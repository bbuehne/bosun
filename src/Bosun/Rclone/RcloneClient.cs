using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bosun.Rclone;

/// <summary>
/// Real <see cref="IRcloneClient"/>: <see cref="HttpClient"/> + <see cref="System.Text.Json"/>
/// over the rclone rc HTTP API (bs-tg9). <paramref name="httpClient"/>'s <c>BaseAddress</c> must
/// already point at the loopback rc endpoint (e.g. <c>http://127.0.0.1:5572/</c>) -- wiring that
/// up from <c>global.rclone_rc_port</c> is the DI registration's job
/// (<see cref="Hosting.BosunHostFactory"/>), not this class's.
/// </summary>
/// <remarks>
/// <para>
/// Every rc call in this file is POST with a JSON body, matching every worked example on
/// https://rclone.org/rc/ (e.g. <c>curl http://localhost:5572/core/version</c> via HTTP POST).
/// A non-2xx response, or a 2xx response whose body cannot be parsed as the expected shape,
/// becomes an <see cref="RcloneRcException"/>; anything that means the HTTP call itself could
/// not be made (rcd unreachable, DNS) is left as whatever <see cref="HttpClient"/> throws
/// (<see cref="HttpRequestException"/>) -- see the remarks on <see cref="RcloneRcException"/> for
/// why that distinction is preserved rather than collapsed into one exception type.
/// </para>
/// <para>
/// <b>Per-call timeouts (bs-x57).</b> Every call carries its own explicit timeout (the
/// <c>*Timeout</c> constants below), enforced here with a <see cref="TimeProvider"/>-driven timer
/// on a linked token -- the same shape as <see cref="Probe.HostProbe"/> -- rather than by relying
/// on <see cref="HttpClient.Timeout"/>'s 100 s default. A call that exceeds its timeout surfaces
/// as <see cref="RcloneRcTimeoutException"/> (an <see cref="RcloneRcException"/>), never as a bare
/// <see cref="TaskCanceledException"/>: callers that treat any
/// <see cref="OperationCanceledException"/> as "shutting down" would otherwise misread a hung rc
/// call as shutdown, which is exactly how the 2026-09-28 incident silently killed the supervisor
/// loop. Only cancellation of the CALLER's token still surfaces as
/// <see cref="OperationCanceledException"/>.
/// </para>
/// <para>
/// <b>Authentication (bs-ard).</b> Verified against a real rclone v1.75.0 binary: every rc
/// endpoint except <c>core/version</c> returns HTTP 403 unless the rcd process was started with
/// rc credentials. <paramref name="credential"/>'s value is used to set the
/// <c>Authorization: Basic ...</c> header on <paramref name="httpClient"/> once, here in the
/// constructor -- <see cref="HttpClient"/> applies its <c>DefaultRequestHeaders</c> to every
/// request it sends, so this covers every call this class makes, including
/// <see cref="GetVersionAsync"/>. rclone's own docs say <c>core/version</c> does not require
/// auth, but that was observed to be true only when rc auth is not configured at all -- with a
/// real credential configured (which <see cref="Process.RcloneProcessService"/> always does, per
/// bs-ard), a real v1.75.0 binary returns 401 for an unauthenticated <c>core/version</c> call
/// too, so sending auth here is not just uniformity, it is required for the health check
/// (<see cref="Process.RcloneProcessService.WaitUntilHealthyAsync"/>) to ever succeed. See the
/// remarks on <see cref="RcloneRcCredential"/> for why one credential, generated once for the
/// whole Bosun process lifetime, stays valid across every <c>rclone rcd</c> restart.
/// </para>
/// </remarks>
public sealed class RcloneClient : IRcloneClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Per-endpoint timeouts (bs-x57). All calls go to rcd on loopback, so a call only takes long
    // when rcd itself is blocked on something slower behind it (an SFTP session, a dead SSH
    // channel, a VFS flush). The values below balance bounding how long ONE stuck call can hold
    // MountSupervisor's single, globally-serialised channel loop (every other host waits behind
    // it) against not abandoning a call that is merely slow but would succeed.

    /// <summary>core/version: answered from rcd's memory without touching any remote. It is the
    /// health-check poll and ADR-017's "is rcd up" gate, so a slow answer means "not healthy" --
    /// there is no value in waiting longer.</summary>
    internal static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(5);

    /// <summary>config/get and config/create: edit rcd's in-memory config (no remote I/O), so they
    /// are quick, but they are not health pings and may serialise behind other config work.</summary>
    internal static readonly TimeSpan ConfigTimeout = TimeSpan.FromSeconds(10);

    /// <summary>mount/listmounts: rcd's in-memory mount table, no remote I/O. It is the supervisor's
    /// ground truth after every unmount attempt and is called on every drain retry, so it must be
    /// short -- but it can queue behind rcd's mount-table lock while a mount/unmount is in flight,
    /// hence not as short as core/version.</summary>
    internal static readonly TimeSpan ListMountsTimeout = TimeSpan.FromSeconds(10);

    /// <summary>mount/unmount: the call most likely to stall -- over a dead SSH channel rclone
    /// blocks flushing/closing the VFS against a peer that never answers. Deliberately longer than
    /// the metadata calls so a slow-but-working unmount (large dirty cache) can finish, yet bounded:
    /// the drain retries on its own cadence and verifies against listmounts, so giving up on one
    /// attempt is cheap, while holding the global loop for minutes is not.</summary>
    internal static readonly TimeSpan UnmountTimeout = TimeSpan.FromSeconds(30);

    /// <summary>mount/mount: connects the SFTP session and registers the WinFsp mount, so it does
    /// real remote I/O plus WinFsp setup and is the slowest legitimate call here. A timed-out
    /// mount/mount may nevertheless have taken effect inside rcd; that is safe, because the
    /// supervisor treats the timeout as a mount failure and drains, and the drain's unmount +
    /// listmounts verification clears any mount that did land.</summary>
    internal static readonly TimeSpan MountTimeout = TimeSpan.FromSeconds(60);

    /// <summary>operations/list: the deep probe's call, a live SFTP round-trip. Callers that need a
    /// tight bound (HostProbe, global.probe_timeout_seconds, default 5 s) impose their own via the
    /// token and fire first; this is only the backstop for a caller that does not, and is well
    /// above any sane probe timeout so it never pre-empts one.</summary>
    internal static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;

    public RcloneClient(HttpClient httpClient, RcloneRcCredential credential, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(credential);

        _httpClient = httpClient;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", credential.ToBasicAuthHeaderValue());
    }

    /// <summary>Cache modes at or above the Invariant I6 floor. Anything else -- including
    /// <see langword="null"/>, empty, "off", "minimal", or a typo -- is clamped to "writes" by
    /// <see cref="MountAsync"/>.
    ///
    /// Kept as defence-in-depth after bs-lrd / bs-mb2 closed the underlying gap:
    /// <see cref="Bosun.Configuration.ConfigValidator"/> now rejects any <c>mount.vfs_cache_mode</c>
    /// outside this same allow-list ("invalid-vfs-cache-mode"), and
    /// <see cref="Bosun.Configuration.ConfigParser"/> defaults an absent value to "writes" at
    /// bind time, so this clamp should be unreachable --
    /// every request built from a validated config already carries "writes" or "full". It stays
    /// because this is the one place I6 is actually enforced on the wire (per the E3 brief), and
    /// a caller that somehow bypasses config validation must still not be able to mount with a
    /// cache mode below the floor.</summary>
    private static readonly HashSet<string> CacheModesAtOrAboveFloor =
        new(StringComparer.OrdinalIgnoreCase) { "writes", "full" };

    public async Task<RcloneVersionInfo> GetVersionAsync(CancellationToken cancellationToken)
    {
        var body = await PostAsync("core/version", body: null, VersionTimeout, cancellationToken).ConfigureAwait(false);

        return new RcloneVersionInfo
        {
            Version = RequireString(body, "version", "core/version"),
            Os = TryGetString(body, "os"),
            Arch = TryGetString(body, "arch"),
        };
    }

    public async Task CreateConfigAsync(
        string remoteName, string type, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(remoteName);
        ArgumentException.ThrowIfNullOrEmpty(type);
        ArgumentNullException.ThrowIfNull(parameters);

        // Parameter names verified: https://rclone.org/rc/#config-create.
        var requestBody = new JsonObject
        {
            ["name"] = remoteName,
            ["type"] = type,
            ["parameters"] = JsonSerializer.SerializeToNode(parameters, JsonOptions),
        };

        await PostAsync("config/create", requestBody, ConfigTimeout, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, string>?> GetConfigAsync(string remoteName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(remoteName);

        // Parameter name verified: https://rclone.org/rc/#config-get. Response shape is a flat
        // object of that remote's parameters -- see the "not independently confirmed with a
        // literal example" note on IRcloneClient.GetConfigAsync.
        JsonObject body;
        try
        {
            body = await PostAsync("config/get", new JsonObject { ["name"] = remoteName }, ConfigTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RcloneRcException ex) when (ex is not RcloneRcTimeoutException)
        {
            // Deliberately not distinguishing "remote does not exist" from any other rc-level
            // failure here -- see the remarks on IRcloneClient.GetConfigAsync for why. A TIMEOUT is
            // the one exception (bs-x57): rcd not answering says nothing about whether the remote
            // exists, so reporting "absent" would make the provisioner overwrite a good remote.
            return null;
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in body)
        {
            if (value is null)
            {
                continue;
            }

            result[key] = value.GetValueKind() == JsonValueKind.String
                ? value.GetValue<string>()
                : value.ToJsonString();
        }

        return result;
    }

    public async Task<RcloneMountResult> MountAsync(RcloneMountRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.Fs);
        ArgumentException.ThrowIfNullOrEmpty(request.MountPoint);

        var requestBody = BuildMountBody(request);
        var body = await PostAsync("mount/mount", requestBody, MountTimeout, cancellationToken).ConfigureAwait(false);

        return new RcloneMountResult
        {
            MountPoint = RequireString(body, "mountPoint", "mount/mount"),
        };
    }

    /// <summary>
    /// Builds the <c>mount/mount</c> JSON body. This is the single point where Invariants I6
    /// and I7 are enforced (per the E3 brief: "Enforce I6/I7 at the point mount parameters are
    /// built"), not somewhere upstream that a future caller could bypass.
    /// </summary>
    /// <remarks>
    /// Parameter names (<c>fs</c>, <c>mountPoint</c>, <c>mountType</c>) and the flat
    /// top-level option mechanism (CLI flag name, dashes to underscores --
    /// <c>vfs_cache_mode</c>, <c>network_mode</c>) are verified against
    /// https://rclone.org/rc/#mount-mount, which shows both the nested
    /// <c>vfsOpt='{"CacheMode": 2}'</c> form and the flat
    /// <c>vfs_cache_mode=writes</c> form as equivalent, and states flat parameters use "the same
    /// [names] as their CLI flags without '--' and with '-' replaced by '_'". <c>network_mode</c>
    /// additionally verified as the exact config-tag name of <c>mountlib.Options.NetworkMode</c>
    /// (<c>config:"network_mode"</c>) via pkg.go.dev's rendering of
    /// github.com/rclone/rclone/cmd/mountlib -- the flat name was not shown in a literal
    /// worked example the way <c>vfs_cache_mode</c> was, so it is inferred from that config tag
    /// plus the documented flat-naming rule, not from a literal example. Flagged in the E3
    /// delivery report.
    /// </remarks>
    private static JsonObject BuildMountBody(RcloneMountRequest request)
    {
        var requestBody = new JsonObject
        {
            ["fs"] = request.Fs,
            ["mountPoint"] = request.MountPoint,
            ["vfs_cache_mode"] = EnforceVfsCacheModeFloor(request.VfsCacheMode),

            // Invariant I7: --network-mode on every mount, unconditionally. There is no request
            // field that could set this to false -- see the remarks on RcloneMountRequest.
            ["network_mode"] = true,
        };

        if (!string.IsNullOrEmpty(request.MountType))
        {
            requestBody["mountType"] = request.MountType;
        }

        return requestBody;
    }

    private static string EnforceVfsCacheModeFloor(string? requested) =>
        requested is not null && CacheModesAtOrAboveFloor.Contains(requested) ? requested : "writes";

    public async Task UnmountAsync(string mountPoint, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(mountPoint);

        // Parameter name verified: https://rclone.org/rc/#mount-unmount.
        await PostAsync("mount/unmount", new JsonObject { ["mountPoint"] = mountPoint }, UnmountTimeout, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RcloneMountInfo>> ListMountsAsync(CancellationToken cancellationToken)
    {
        // No parameters: https://rclone.org/rc/#mount-listmounts.
        var body = await PostAsync("mount/listmounts", body: null, ListMountsTimeout, cancellationToken).ConfigureAwait(false);

        if (!body.TryGetPropertyValue("mountPoints", out var node) || node is not JsonArray array)
        {
            return [];
        }

        var result = new List<RcloneMountInfo>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonObject entry)
            {
                continue;
            }

            result.Add(new RcloneMountInfo
            {
                Fs = RequireString(entry, "Fs", "mount/listmounts"),
                MountPoint = RequireString(entry, "MountPoint", "mount/listmounts"),
                MountedOn = TryGetDateTimeOffset(entry, "MountedOn"),
            });
        }

        return result;
    }

    public async Task<IReadOnlyList<RcloneListItem>> ListAsync(string fs, string remote, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(fs);
        ArgumentNullException.ThrowIfNull(remote);

        // Parameter names verified: https://rclone.org/rc/#operations-list. opt.recurse omitted
        // (defaults to false/absent) so this is depth 1, matching docs/ARCHITECTURE.md §3's deep
        // probe description.
        var requestBody = new JsonObject
        {
            ["fs"] = fs,
            ["remote"] = remote,
        };

        var body = await PostAsync("operations/list", requestBody, ListTimeout, cancellationToken).ConfigureAwait(false);

        if (!body.TryGetPropertyValue("list", out var node) || node is not JsonArray array)
        {
            return [];
        }

        var result = new List<RcloneListItem>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonObject entry)
            {
                continue;
            }

            result.Add(new RcloneListItem
            {
                Path = RequireString(entry, "Path", "operations/list"),
                Name = RequireString(entry, "Name", "operations/list"),
                IsDir = entry.TryGetPropertyValue("IsDir", out var isDirNode) && isDirNode is not null && isDirNode.GetValue<bool>(),
                Size = entry.TryGetPropertyValue("Size", out var sizeNode) && sizeNode is not null ? sizeNode.GetValue<long>() : null,
            });
        }

        return result;
    }

    /// <summary>
    /// POSTs <paramref name="body"/> (or <c>{}</c> when <see langword="null"/>) as JSON to
    /// <paramref name="endpoint"/> and returns the parsed response object. Throws
    /// <see cref="RcloneRcException"/> for a non-2xx response or a response body that is not a
    /// JSON object, and <see cref="RcloneRcTimeoutException"/> if the whole exchange (request plus
    /// reading the response) takes longer than <paramref name="timeout"/>. Cancellation of
    /// <paramref name="cancellationToken"/> itself still throws
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    private async Task<JsonObject> PostAsync(string endpoint, JsonNode? body, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = new CancellationTokenSource();
        using var timer = _timeProvider.CreateTimer(
            static state => ((CancellationTokenSource)state!).Cancel(), timeoutSource, timeout, Timeout.InfiniteTimeSpan);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        try
        {
            return await SendAsync(endpoint, body, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // The caller did not cancel, so this is a timeout: ours (timeoutSource) or, if
            // HttpClient.Timeout is shorter than ours, HttpClient's own. Either way it must not
            // leave here as a bare OperationCanceledException (see RcloneRcTimeoutException).
            throw new RcloneRcTimeoutException(
                endpoint, timeoutSource.IsCancellationRequested ? timeout : _httpClient.Timeout, ex);
        }
    }

    private async Task<JsonObject> SendAsync(string endpoint, JsonNode? body, CancellationToken cancellationToken)
    {
        var requestJson = (body ?? new JsonObject()).ToJsonString(JsonOptions);
        using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(endpoint, content, cancellationToken).ConfigureAwait(false);

        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new RcloneRcException(endpoint, (int)response.StatusCode, DescribeError(endpoint, text));
        }

        JsonNode? parsed;
        try
        {
            parsed = string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new RcloneRcException(endpoint, (int)response.StatusCode, $"rc call to '{endpoint}' returned a body that was not valid JSON: {ex.Message}", ex);
        }

        if (parsed is not JsonObject obj)
        {
            throw new RcloneRcException(endpoint, (int)response.StatusCode, $"rc call to '{endpoint}' returned a non-object JSON body: {text}");
        }

        return obj;
    }

    /// <summary>rc error responses are documented (https://rclone.org/rc/) to carry an "error"
    /// field with a human-readable message; fall back to the raw body when that shape is not
    /// present so no information is silently dropped.</summary>
    private static string DescribeError(string endpoint, string body)
    {
        try
        {
            if (JsonNode.Parse(body) is JsonObject obj &&
                obj.TryGetPropertyValue("error", out var errorNode) &&
                errorNode is not null &&
                errorNode.GetValueKind() == JsonValueKind.String)
            {
                return $"rc call to '{endpoint}' failed: {errorNode.GetValue<string>()}";
            }
        }
        catch (JsonException)
        {
            // Fall through to the raw-body message below.
        }

        return $"rc call to '{endpoint}' failed with body: {body}";
    }

    private static string RequireString(JsonObject body, string property, string endpoint)
    {
        if (body.TryGetPropertyValue(property, out var node) && node is not null && node.GetValueKind() == JsonValueKind.String)
        {
            return node.GetValue<string>();
        }

        throw new RcloneRcException(endpoint, httpStatusCode: null, $"rc call to '{endpoint}' response was missing expected string field '{property}'");
    }

    private static string? TryGetString(JsonObject body, string property) =>
        body.TryGetPropertyValue(property, out var node) && node is not null && node.GetValueKind() == JsonValueKind.String
            ? node.GetValue<string>()
            : null;

    private static DateTimeOffset? TryGetDateTimeOffset(JsonObject body, string property)
    {
        if (!body.TryGetPropertyValue(property, out var node) || node is null || node.GetValueKind() != JsonValueKind.String)
        {
            return null;
        }

        return DateTimeOffset.TryParse(node.GetValue<string>(), out var parsed) ? parsed : null;
    }
}


