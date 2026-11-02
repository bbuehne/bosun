namespace Bosun.Supervisor;

/// <summary>
/// Why a drain was begun (bs-fix-e5; made public and recorded on
/// <see cref="MountTransitionEntry.DrainCause"/> by bs-ynb). The supervisor uses it to decide what
/// happens when the drain completes: whether the host is parked rather than auto re-enabled
/// (<see cref="UserUnmount"/>), whether the post-drain re-enable is paced through
/// <c>MountSupervisor.ArmMountRetryTimer</c> instead of firing immediately
/// (<see cref="MountFailure"/>), and whether the host is evicted (<see cref="ConfigRemoved"/>).
/// It also selects which timeout the drain escalates against (<see cref="Suspend"/> uses
/// <c>global.suspend_unmount_timeout_seconds</c>; everything else uses the fixed ordinary drain
/// confirm timeout).
/// </summary>
/// <remarks>
/// Public so UI-layer consumers (<c>UnexpectedUnmountDetector</c>) can classify a drain by value
/// rather than by matching the free-text <see cref="MountTransitionEntry.Trigger"/>, which is for
/// humans and may be reworded at any time.
/// </remarks>
public enum DrainCause
{
    /// <summary>Reconciliation drift, a mounted shallow-probe-failure threshold, or a deep-probe
    /// failure -- a drive lost without anyone having asked for it. The host should come back
    /// exactly as it did before bs-fix-e5: re-enabled immediately once the drain completes. This
    /// is the one cause that raises the "unexpectedly unmounted" toast (bs-p97): an idle timeout
    /// and a config change used to be <see cref="Automatic"/> too and now have their own values
    /// because the maintainer decided those are not unexpected.</summary>
    Automatic,

    /// <summary>System suspend (Invariant I8). Escalates on the suspend budget, not the
    /// ordinary drain timeout; never re-enabled while the supervisor is suspended (that is
    /// <c>ResumeAsync</c>'s job).</summary>
    Suspend,

    /// <summary>An explicit <c>RequestUnmountAsync</c>. Parks the host so it does not
    /// auto-remount itself the moment the drain completes -- see the defect-3 discussion in the
    /// bs-fix-e5 delivery report.</summary>
    UserUnmount,

    /// <summary>A deep-probe or <c>mount/mount</c> failure while entering <c>Mounting</c>. Paced
    /// via <c>MountSupervisor.ArmMountRetryTimer</c> instead of an immediate re-enable -- see the
    /// defect-1 discussion in the bs-fix-e5 delivery report (ADR-005 / docs/ARCHITECTURE.md §6
    /// "drive letter already in use: refuse the mount... do not retry forever").</summary>
    MountFailure,

    /// <summary>A host removed entirely from config while Mounting/Mounted (bs-7ck, Invariant
    /// I2). Drains exactly like <see cref="Automatic"/>, but on completion the host is evicted
    /// from supervision rather than re-enabled -- nothing in config describes this host any more,
    /// so nothing should keep probing or remounting it. Deliberately distinct from a host
    /// reconfigured to <c>mount.mode = "none"</c> (which stays in the snapshot, parked
    /// <see cref="MountState.Disabled"/>, and drains as <see cref="Automatic"/>): that host's KEY
    /// still exists in config, whereas a REMOVED key has nothing left to describe.</summary>
    ConfigRemoved,

    /// <summary>"Unmount all and re-probe" (<c>RepairAllAsync</c>, bs-aoz). Drains exactly like
    /// <see cref="Automatic"/> -- the host is re-enabled, so it re-probes and a persistent host
    /// remounts -- and deliberately NOT like <see cref="UserUnmount"/>: the host is not parked. A
    /// repair means "drop everything and start clean", not "keep these hosts down". Kept as its
    /// own cause (rather than reusing <see cref="Automatic"/>) so the log says why, and so a
    /// future change to <see cref="Automatic"/> cannot silently change a repair.</summary>
    Repair,

    /// <summary>An on-demand host's idle-unmount timeout (<c>mount.idle_unmount_seconds</c>). Drains
    /// and re-enables exactly like <see cref="Automatic"/>; separate only so the tray does not
    /// toast an unmount the configuration asked for (bs-p97).</summary>
    IdleTimeout,

    /// <summary>A mount-affecting edit of a host that stays in config (changed hostname, drive,
    /// remote path, mode set to <c>none</c>, ...). Drains and re-enables exactly like
    /// <see cref="Automatic"/>; separate only so the tray does not toast an unmount the user's own
    /// edit caused (bs-p97). A host REMOVED from config is <see cref="ConfigRemoved"/>.</summary>
    ConfigChanged,
}
