using System.Net.Http;
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
/// Every rc call in this file is POST with a JSON body, matching every worked example on
/// https://rclone.org/rc/ (e.g. <c>curl http://localhost:5572/core/version</c> via HTTP POST).
/// A non-2xx response, or a 2xx response whose body cannot be parsed as the expected shape,
/// becomes an <see cref="RcloneRcException"/>; anything that means the HTTP call itself could
/// not be made (rcd unreachable, timeout, DNS) is left as whatever <see cref="HttpClient"/>
/// throws (<see cref="HttpRequestException"/>, <see cref="TaskCanceledException"/>) -- see the
/// remarks on <see cref="RcloneRcException"/> for why that distinction is preserved rather than
/// collapsed into one exception type.
/// </remarks>
public sealed class RcloneClient(HttpClient httpClient) : IRcloneClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Cache modes at or above the Invariant I6 floor. Anything else -- including
    /// <see langword="null"/>, empty, "off", "minimal", or a typo -- is clamped to "writes" by
    /// <see cref="MountAsync"/>.</summary>
    private static readonly HashSet<string> CacheModesAtOrAboveFloor =
        new(StringComparer.OrdinalIgnoreCase) { "writes", "full" };

    public async Task<RcloneVersionInfo> GetVersionAsync(CancellationToken cancellationToken)
    {
        var body = await PostAsync("core/version", body: null, cancellationToken).ConfigureAwait(false);

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

        await PostAsync("config/create", requestBody, cancellationToken).ConfigureAwait(false);
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
            body = await PostAsync("config/get", new JsonObject { ["name"] = remoteName }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RcloneRcException)
        {
            // Deliberately not distinguishing "remote does not exist" from any other rc-level
            // failure here -- see the remarks on IRcloneClient.GetConfigAsync for why.
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
        var body = await PostAsync("mount/mount", requestBody, cancellationToken).ConfigureAwait(false);

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
        await PostAsync("mount/unmount", new JsonObject { ["mountPoint"] = mountPoint }, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RcloneMountInfo>> ListMountsAsync(CancellationToken cancellationToken)
    {
        // No parameters: https://rclone.org/rc/#mount-listmounts.
        var body = await PostAsync("mount/listmounts", body: null, cancellationToken).ConfigureAwait(false);

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

        var body = await PostAsync("operations/list", requestBody, cancellationToken).ConfigureAwait(false);

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
    /// JSON object.
    /// </summary>
    private async Task<JsonObject> PostAsync(string endpoint, JsonNode? body, CancellationToken cancellationToken)
    {
        var requestJson = (body ?? new JsonObject()).ToJsonString(JsonOptions);
        using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
        using var response = await httpClient.PostAsync(endpoint, content, cancellationToken).ConfigureAwait(false);

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


