namespace Bosun.Rclone;

/// <summary>
/// Typed wrapper over the rclone rc HTTP API (bs-tg9; docs/ARCHITECTURE.md §3, ADR-003,
/// Invariant I4). Every endpoint below was verified against rclone's current documentation
/// rather than recalled from memory -- CLAUDE.md flags rc parameter names as one of two areas
/// where secondhand information is routinely wrong. The source URL is cited next to each member
/// and, again, next to the exact JSON body construction in <see cref="RcloneClient"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Architectural boundary (read before adding a caller):</b> this interface may EXPOSE
/// <see cref="MountAsync"/> and <see cref="UnmountAsync"/> -- bs-tg9 requires it -- but only
/// <c>IMountSupervisor</c> (E5, not yet built) is permitted to CALL them. Nothing in E3 calls
/// either method. If a future change needs another component to mount or unmount, that is an
/// architecture question to raise, not a convenience wrapper to add here.
/// </para>
/// <para>
/// <b>Invariants I6/I7</b> are enforced inside <see cref="RcloneClient.MountAsync"/> at the
/// point the JSON body is built, not left to callers: <c>network_mode</c> is hardcoded to
/// <see langword="true"/> in every mount request (there is no way to ask for anything else --
/// see <see cref="RcloneMountRequest"/>, which has no network-mode field at all), and
/// <c>vfs_cache_mode</c> is clamped to the "writes" floor regardless of what is requested.
/// </para>
/// </remarks>
public interface IRcloneClient
{
    /// <summary>
    /// <c>core/version</c> -- the startup/liveness health check <see cref="Process.RcloneProcessService"/>
    /// uses to confirm a freshly spawned <c>rclone rcd</c> is actually answering.
    /// Source: https://rclone.org/rc/#core-version ("Authentication is not required for this
    /// call.", no parameters).
    /// </summary>
    Task<RcloneVersionInfo> GetVersionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// <c>config/create</c> -- creates (or, per rclone's <c>CreateRemote</c> implementation,
    /// authoritatively OVERWRITES) the named remote's section in <c>rclone.conf</c>. Only the
    /// named section is touched; every other remote in the file, including ones Bosun did not
    /// create, is left alone -- verified against rclone source
    /// (fs/config/config.go: <c>CreateRemote</c> calls <c>LoadedData().DeleteSection(name)</c>
    /// then <c>SetValue(name, "type", Type)</c> before applying <paramref name="parameters"/>,
    /// touching nothing else). Source for the parameter names (<c>name</c>, <c>type</c>,
    /// <c>parameters</c>): https://rclone.org/rc/#config-create.
    /// </summary>
    Task CreateConfigAsync(
        string remoteName, string type, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken);

    /// <summary>
    /// <c>config/get</c> -- retrieves the named remote's current parameters, or
    /// <see langword="null"/> if the remote is not configured (or the call otherwise failed --
    /// see remarks). Source for the parameter name (<c>name</c>):
    /// https://rclone.org/rc/#config-get. The exact response shape for a single remote is not
    /// shown with a worked example on that page; it was inferred from the documented shape of
    /// the sibling <c>config/dump</c> call ("Returns a JSON object: key: value" where "keys are
    /// remote names and values are the config parameters") -- i.e. a flat JSON object of that
    /// one remote's parameters. Flagged as not independently confirmed with a literal example;
    /// see the E3 delivery report.
    /// </summary>
    /// <remarks>
    /// Deliberately returns <see langword="null"/> rather than throwing for "remote does not
    /// exist", because rclone's exact error text/shape for that case was not verified (see
    /// remarks on the interface). <see cref="RcloneClient"/> currently treats ANY
    /// <see cref="RcloneRcException"/> from this call the same way -- as "could not confirm the
    /// remote is configured correctly" -- rather than pattern-matching error text, so a real
    /// "not found" and an unexpected rc-level error are handled identically (both mean
    /// <see cref="RcloneRemoteProvisioner"/> falls back to <see cref="CreateConfigAsync"/>,
    /// which is safe because it is an authoritative overwrite of only the named section).
    /// </remarks>
    Task<IReadOnlyDictionary<string, string>?> GetConfigAsync(string remoteName, CancellationToken cancellationToken);

    /// <summary>
    /// <c>mount/mount</c> -- creates a mount point via rclone's rc API (never a spawned
    /// <c>rclone mount</c> process -- Invariant I4). See the architectural-boundary remarks on
    /// <see cref="IRcloneClient"/>: only <c>IMountSupervisor</c> may call this.
    /// Source: https://rclone.org/rc/#mount-mount.
    /// </summary>
    Task<RcloneMountResult> MountAsync(RcloneMountRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <c>mount/unmount</c> -- removes the mount at <paramref name="mountPoint"/>. See the
    /// architectural-boundary remarks on <see cref="IRcloneClient"/>: only
    /// <c>IMountSupervisor</c> may call this. Source: https://rclone.org/rc/#mount-unmount
    /// (single required parameter <c>mountPoint</c>).
    /// </summary>
    Task UnmountAsync(string mountPoint, CancellationToken cancellationToken);

    /// <summary>
    /// <c>mount/listmounts</c> -- the reconciliation primitive ADR-003 exists to provide: "what
    /// is actually mounted", independent of what any in-memory state believes.
    /// Source: https://rclone.org/rc/#mount-listmounts (no parameters). Response item field
    /// names (<c>Fs</c>, <c>MountPoint</c>, <c>MountedOn</c>) verified against rclone source,
    /// <c>cmd/mountlib/rc.go</c>'s <c>MountInfo</c> struct
    /// (github.com/rclone/rclone/blob/master/cmd/mountlib/rc.go).
    /// </summary>
    Task<IReadOnlyList<RcloneMountInfo>> ListMountsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// <c>operations/list</c> -- lists <paramref name="remote"/> within <paramref name="fs"/>,
    /// non-recursively (i.e. depth 1 -- <c>opt.recurse</c> is omitted/false), which is what
    /// <c>HostProbe</c>'s deep probe uses to prove authentication and the SFTP subsystem work,
    /// not just TCP reachability (docs/ARCHITECTURE.md §3). Source for parameter names
    /// (<c>fs</c>, <c>remote</c>, <c>opt.recurse</c>): https://rclone.org/rc/#operations-list.
    /// Returned item field names (<c>Path</c>, <c>Name</c>, <c>IsDir</c>, <c>Size</c>) verified
    /// against the worked example on https://rclone.org/commands/rclone_lsjson/, which
    /// <c>operations/list</c>'s own documentation explicitly says its <c>list</c> array matches.
    /// </summary>
    Task<IReadOnlyList<RcloneListItem>> ListAsync(string fs, string remote, CancellationToken cancellationToken);
}

/// <summary>Result of <c>core/version</c>. Only the fields Bosun actually logs are modelled --
/// see the interface's remark about not building a wider seam than needed.</summary>
public sealed record RcloneVersionInfo
{
    public required string Version { get; init; }
    public string? Os { get; init; }
    public string? Arch { get; init; }
}

/// <summary>
/// Request body for <c>mount/mount</c>. Deliberately has NO network-mode field: Invariant I7
/// ("--network-mode on every mount") is enforced by <see cref="RcloneClient"/> hardcoding
/// <c>network_mode: true</c> into every request body, not by a caller-supplied value that could
/// be set to <see langword="false"/> by mistake.
/// </summary>
public sealed record RcloneMountRequest
{
    /// <summary>The rclone fs spec to mount, e.g. <c>bosun-example-nas:/volume1/share</c>. See
    /// <see cref="RcloneRemoteNaming.RemoteFsPath"/>.</summary>
    public required string Fs { get; init; }

    public required string MountPoint { get; init; }

    /// <summary>One of <c>mount</c>, <c>cmount</c>, <c>mount2</c>; <see langword="null"/> lets
    /// rclone pick (mount, then cmount, then mount2, in that priority order per
    /// https://rclone.org/rc/#mount-mount).</summary>
    public string? MountType { get; init; }

    /// <summary>
    /// Caller's requested VFS cache mode. <see cref="RcloneClient.MountAsync"/> clamps this to
    /// the "writes" floor (Invariant I6) regardless of what is supplied here -- <see
    /// langword="null"/>, empty, "off", or "minimal" all become "writes"; only "writes" or
    /// "full" pass through unchanged. This clamp does not depend on upstream config validation
    /// (bs-mb2) already having rejected "off"/"minimal" -- it is a correctness floor enforced
    /// again at the point the mount actually happens.
    /// </summary>
    public string? VfsCacheMode { get; init; }
}

public sealed record RcloneMountResult
{
    public required string MountPoint { get; init; }
}

/// <summary>One entry from <c>mount/listmounts</c>. Field names match rclone's
/// <c>MountInfo</c> struct (see <see cref="IRcloneClient.ListMountsAsync"/> remarks).</summary>
public sealed record RcloneMountInfo
{
    public required string Fs { get; init; }
    public required string MountPoint { get; init; }
    public DateTimeOffset? MountedOn { get; init; }
}

/// <summary>One entry from <c>operations/list</c>'s <c>list</c> array (lsjson shape).</summary>
public sealed record RcloneListItem
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required bool IsDir { get; init; }
    public long? Size { get; init; }
}
