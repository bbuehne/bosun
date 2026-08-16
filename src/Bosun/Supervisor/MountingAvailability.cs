namespace Bosun.Supervisor;

/// <summary>
/// Why mounting is unavailable process-wide (bs-yvw.1). One gate --
/// <see cref="MountSupervisor"/> refuses to let ANY host leave <see cref="MountState.Ready"/> for
/// <see cref="MountState.Mounting"/> while unavailable -- with several possible causes. Adding a
/// new cause (e.g. a future prerequisite) is a new enum value, never a new gate.
/// </summary>
public enum MountingUnavailableCause
{
    /// <summary>WinFsp is not installed -- ADR-002's hard mount prerequisite.</summary>
    WinFspMissing,

    /// <summary><c>rclone rcd</c> is not currently healthy (never started, crashed, or mid-restart).</summary>
    RcloneUnhealthy,
}

/// <summary>
/// Process-wide gate on whether ANY host may enter <see cref="MountState.Mounting"/>
/// (<see cref="MountSupervisor"/>), independent of any single host's own probe/backoff state.
/// </summary>
/// <remarks>
/// Hosts keep shallow-probing while unavailable -- ADR-012 Decision 2's fail-soft principle, and
/// docs/ARCHITECTURE.md §6's "WinFsp not installed: ... leave terminal features working" reading
/// extended to "leave reachability visible too". None may leave <see cref="MountState.Ready"/> for
/// <see cref="MountState.Mounting"/> until this reports <see cref="IsAvailable"/>, though --
/// otherwise a deep probe that needs neither WinFsp nor rclone (<c>operations/list</c> over SFTP)
/// keeps passing, <c>mount/mount</c> keeps failing, and the host retries forever against a
/// condition only installing software can fix (bs-yvw.1; the shape of bs-k4k, paced rather than
/// spinning, but still endless and still noisy in the target server's auth log).
/// </remarks>
public readonly record struct MountingAvailability
{
    public bool IsAvailable { get; private init; }

    public MountingUnavailableCause? Cause { get; private init; }

    /// <summary>
    /// The causal sentence ADR-012 Decision 3 requires -- e.g. "WinFsp is not installed" -- never
    /// "mounting unavailable". Null when <see cref="IsAvailable"/>. Ready to be composed with a
    /// host's drive letter by whatever renders it (E9, not built yet) into ADR-012's own worked
    /// example: "P: is not mounted -- WinFsp is not installed".
    /// </summary>
    public string? Reason { get; private init; }

    public static readonly MountingAvailability Available = new() { IsAvailable = true };

    public static MountingAvailability Unavailable(MountingUnavailableCause cause, string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        return new MountingAvailability { IsAvailable = false, Cause = cause, Reason = reason };
    }
}

/// <summary>
/// Thrown by <see cref="IMountSupervisor.RequestMountAsync"/> when mounting is currently
/// unavailable process-wide (bs-yvw.1). ADR-014 rule 8 established that a tray action must never
/// be a silent no-op for the "click Mount on an Unreachable host" case; the same reasoning applies
/// here -- the click did something observable (failed, loudly, with the causal reason) rather than
/// nothing at all.
/// </summary>
public sealed class MountingUnavailableException(string hostKey, MountingUnavailableCause cause, string reason)
    : InvalidOperationException($"Cannot mount '{hostKey}': {reason}")
{
    public string HostKey { get; } = hostKey;

    public MountingUnavailableCause Cause { get; } = cause;

    public string Reason { get; } = reason;
}
