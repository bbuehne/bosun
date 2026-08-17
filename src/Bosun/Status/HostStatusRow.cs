using Bosun.Configuration;
using Bosun.Supervisor;

namespace Bosun.Status;

/// <summary>
/// The causal bucket a host's current status falls into (bs-ww9.6, ADR-012 Decision 3). Exists so
/// a UI can style/group/filter rows without re-deriving the same distinctions
/// <see cref="StatusDerivation"/> already made from the raw snapshot -- e.g. never render
/// <see cref="Parked"/> or <see cref="OnDemandIdle"/> with fault styling (ADR-015).
/// </summary>
public enum StatusCategory
{
    /// <summary>Mounted and working.</summary>
    Healthy,

    /// <summary>An on-demand host resting in <see cref="MountState.Ready"/> (docs/ARCHITECTURE.md
    /// §4 rule 6). Not a fault -- it is exactly where an on-demand host is supposed to sit until
    /// the user asks for it.</summary>
    OnDemandIdle,

    /// <summary>The user explicitly unmounted this host and it has not been explicitly remounted,
    /// reloaded, or restarted since (ADR-015). Not a fault -- rendering it as one is precisely the
    /// confusion ADR-015 exists to avoid.</summary>
    Parked,

    /// <summary>Mounting is unavailable process-wide -- no WinFsp, no healthy <c>rclone rcd</c>
    /// (bs-yvw.1). Affects every mount-capable host identically; see
    /// <see cref="HostMountSnapshot.MountUnavailableReason"/>.</summary>
    MountingUnavailable,

    /// <summary>The host answers reachability probes fine but repeated mount attempts fail --
    /// expired key, disabled SFTP subsystem, drive letter already claimed by something else
    /// (bs-ww9.4). Previously invisible; see <see cref="HostMountSnapshot.ConsecutiveMountFailures"/>.</summary>
    MountFailing,

    /// <summary>The host is not responding to reachability probes at all.</summary>
    Unreachable,

    /// <summary><c>mount.mode = "none"</c> -- this host was never configured to have a mount.</summary>
    NotConfigured,

    /// <summary>A transitional/administrative state not otherwise categorised above -- probing,
    /// mounting, draining, or waiting to be (re-)enabled. Not a fault; it is expected to resolve on
    /// its own within one probe/mount cycle.</summary>
    Pending,
}

/// <summary>
/// Aggregate health across every configured host, for the tray icon (bs-ww9.6, ADR-012 Decision
/// 3: "a degraded Bosun must not look identical to a healthy one at a glance"). Derived from the
/// exact same <see cref="HostStatusRow"/> list the status window renders, via
/// <see cref="StatusDerivation.DeriveAggregateHealth"/>, so the icon and the window can never
/// disagree.
/// </summary>
public enum AggregateHealth
{
    /// <summary>Nothing wrong: every host is mounted, resting as its tier and state expect, or
    /// parked at the user's own request.</summary>
    Healthy,

    /// <summary>Something the user probably wants to know about but that may well resolve itself
    /// -- e.g. a persistent host currently unreachable, or a mounted host accumulating probe
    /// failures on its way toward an automatic unmount.</summary>
    Degraded,

    /// <summary>Something that will NOT resolve itself without the user's intervention -- mounting
    /// unavailable process-wide, or a persistent host repeatedly failing to mount (bs-ww9.4 item 3:
    /// "the user asked for that drive at every login and is not getting it").</summary>
    Error,
}

/// <summary>
/// One host's derived, UI-ready status row (bs-ww9.6). Pure data -- no WPF types, no formatting
/// tied to any particular control. Produced by <see cref="StatusDerivation.DeriveRow"/> from a
/// <see cref="HostMountSnapshot"/>, that host's <see cref="HostConfig"/>, and its live session
/// count (<see cref="SessionMonitor.ISessionMonitor"/>).
/// </summary>
public sealed record HostStatusRow
{
    /// <summary>The stable config-key identity (never <see cref="DisplayName"/>) -- see
    /// <see cref="HostConfig.Key"/>'s remarks.</summary>
    public required string HostKey { get; init; }

    public required string DisplayName { get; init; }

    public required MountState State { get; init; }

    public required MountMode Mode { get; init; }

    /// <summary>The configured drive letter (e.g. <c>"P:"</c>), or <see langword="null"/> for a
    /// <see cref="MountMode.None"/> host, which has none.</summary>
    public string? Drive { get; init; }

    public required bool UserParked { get; init; }

    /// <summary>Live <c>ssh.exe</c> session count for this host, from
    /// <see cref="SessionMonitor.ISessionMonitor.GetActiveSessions"/> at the moment this row was
    /// derived. Independent of mount state -- a host can have live terminal sessions with no mount
    /// at all, or vice versa (docs/ARCHITECTURE.md §1: three independent axes, ADR-008).</summary>
    public required int SessionCount { get; init; }

    public DateTimeOffset? LastTransitionUtc { get; init; }

    public string? LastTransitionTrigger { get; init; }

    /// <summary>The causal bucket this row falls into. Drives styling; never re-derive this from
    /// <see cref="StatusText"/> -- the text is for humans, this is for logic.</summary>
    public required StatusCategory Category { get; init; }

    /// <summary>
    /// The causal, human-readable sentence ADR-012 Decision 3 requires -- e.g. "P: is not mounted
    /// -- WinFsp is not installed", never "some features unavailable". Ready to render directly;
    /// no further composition needed.
    /// </summary>
    public required string StatusText { get; init; }

    /// <summary>Pass-through diagnostic counters, kept on the row so a UI (or a test) can build
    /// richer detail than <see cref="StatusText"/> alone without going back to the raw snapshot.
    /// Never used to re-derive <see cref="Category"/> -- see <see cref="StatusDerivation"/>.</summary>
    public int ConsecutiveMountedFailures { get; init; }

    public int ConsecutiveDeepProbeFailures { get; init; }

    public int ConsecutiveIdleFailures { get; init; }

    /// <summary>See <see cref="HostMountSnapshot.ConsecutiveMountFailures"/> (bs-ww9.4).</summary>
    public int ConsecutiveMountFailures { get; init; }

    /// <summary>See <see cref="HostMountSnapshot.LastMountFailureReason"/> (bs-ww9.4).</summary>
    public string? LastMountFailureReason { get; init; }
}
