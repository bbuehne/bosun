namespace Bosun.UI.Tray;

/// <summary>
/// Process-wide health as the tray icon must show it at a glance (ADR-012 Decision 3; bs-ww9.5).
/// Produced by <see cref="IStatusReadModel.GetAggregateHealth"/>.
/// </summary>
/// <remarks>
/// These are the three states the bs-ww9.5 brief names as the minimum that must be distinguished:
/// "healthy (all enabled hosts mounted or intentionally parked), degraded (something the user
/// asked for is not happening), and mounting-unavailable (WinFsp/rclone missing --
/// <c>HostMountSnapshot.MountUnavailableReason</c> is non-null)". The real derivation from a
/// supervisor snapshot into one of these three values is bs-ww9.6's job (E9c) -- this enum is the
/// seam bs-ww9.6's output plugs into; see <see cref="IStatusReadModel"/>'s remarks.
/// </remarks>
public enum AggregateHealth
{
    /// <summary>Every administratively-enabled host is mounted, on-demand-and-resting, or
    /// intentionally parked by the user. Nothing needs attention.</summary>
    Healthy,

    /// <summary>Something the user asked for is not happening -- a host is unreachable, a mount
    /// keeps failing, a deep probe is failing -- but mounting itself is possible in principle.</summary>
    Degraded,

    /// <summary>Mounting cannot succeed for ANY host right now (WinFsp missing, rclone rcd
    /// unhealthy) -- <c>HostMountSnapshot.MountUnavailableReason</c> is non-null for at least one
    /// administratively-enabled host. The single most severe state: worse than any one host being
    /// merely unreachable, because nothing at all can mount.</summary>
    MountingUnavailable,
}
