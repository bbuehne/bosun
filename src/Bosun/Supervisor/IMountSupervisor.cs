namespace Bosun.Supervisor;

/// <summary>
/// Owns the per-host mount state machine specified in docs/ARCHITECTURE.md §4 (bs-psq). The
/// only component permitted to call <c>mount/mount</c> and <c>mount/unmount</c>
/// (<see cref="Rclone.IRcloneClient.MountAsync"/>, <see cref="Rclone.IRcloneClient.UnmountAsync"/>)
/// -- see the architectural-boundary remarks on <see cref="Rclone.IRcloneClient"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>One instance of the machine per configured host.</b> <see cref="StartAsync"/> builds the
/// host set from <see cref="Configuration.IHostConfigStore.Current"/> at the moment it is called;
/// config reloads while running are not yet handled (a new/removed/changed host requires an app
/// restart to take effect) -- flagged as discovered work, not silently guessed at. See the
/// delivery report.
/// </para>
/// <para>
/// <b>System-event entry points.</b> <see cref="SuspendAsync"/>, <see cref="ResumeAsync"/>, and
/// <see cref="NetworkChangedAsync"/> implement the state-machine SIDE of docs/ARCHITECTURE.md §4
/// rule 5 and the Backoff section's reset triggers. Wiring the actual OS event sources
/// (<c>PowerModeChanged</c>, <c>NetworkAddressChanged</c>) to call these is E6's job
/// (<c>ISystemEventSource</c>), not this epic's -- these methods are the seam E6 wires into.
/// Likewise <see cref="OnRcloneRestartedAsync"/> is the seam for whichever DI wiring subscribes
/// to <c>RcloneProcessService.StatusChanged</c> (see its remarks on why every transition to
/// <c>Healthy</c> must reach this method).
/// </para>
/// </remarks>
public interface IMountSupervisor
{
    /// <summary>
    /// Builds the host set from the current config, adopts-or-clears any mounts already present
    /// (crash recovery, docs/ARCHITECTURE.md §6, run BEFORE anything else), then begins probing
    /// every administratively-enabled host. Safe to call once; a second call is a no-op.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops scheduling further work. Does not proactively unmount -- ADR-004: mounts
    /// exist only while Bosun (and therefore the <c>rclone rcd</c> child it owns) is running, so
    /// process exit already tears them down; see the delivery report for the reasoning.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>A point-in-time, read-only snapshot for the UI. Never mutate through this --
    /// commands are the only mutation path (docs/ARCHITECTURE.md §5: "UI reads a snapshot; the UI
    /// never mutates state directly, it posts commands to the same channel").</summary>
    IReadOnlyList<HostMountSnapshot> GetSnapshot();

    /// <summary>
    /// User- or persistent-tier-triggered mount request. No-op (logged) unless the host is
    /// currently <see cref="MountState.Ready"/> or <see cref="MountState.Unreachable"/> -- Invariant
    /// I1, docs/ARCHITECTURE.md §4 rule 1: nothing here bypasses "Mounting only from Ready".
    /// </summary>
    /// <remarks>
    /// Against an <see cref="MountState.Unreachable"/> host, this is never a silent no-op
    /// (docs/ARCHITECTURE.md §4 rule 8 / ADR-014): it resets the backoff ladder and probes
    /// immediately, mounting only if that probe passes. Throws <see cref="MountRequestRefusedException"/>
    /// if the probe it triggers fails, or if the system is currently suspended (refused outright,
    /// without probing -- Invariant I8 in reverse). Throws <see cref="MountingUnavailableException"/>
    /// if mounting is currently unavailable process-wide (bs-yvw.1), checked before any of the above.
    /// </remarks>
    Task RequestMountAsync(string hostKey, CancellationToken cancellationToken = default);

    /// <summary>User-triggered unmount. No-op (logged) unless the host is currently
    /// <see cref="MountState.Mounting"/> or <see cref="MountState.Mounted"/>.</summary>
    Task RequestUnmountAsync(string hostKey, CancellationToken cancellationToken = default);

    /// <summary>Explicit "retry now" (Backoff section reset trigger): resets backoff and forces
    /// an immediate probe for a host currently <see cref="MountState.Ready"/> or
    /// <see cref="MountState.Unreachable"/>. No-op (logged) otherwise.</summary>
    Task RequestRetryNowAsync(string hostKey, CancellationToken cancellationToken = default);

    /// <summary>Resets the idle-unmount clock for an on-demand host (docs/ARCHITECTURE.md §4 rule
    /// 7). No-op unless the host is currently <see cref="MountState.Mounted"/>. See the delivery
    /// report for the caveat that nothing in this codebase yet calls this -- there is no
    /// filesystem-activity signal wired up.</summary>
    Task RecordActivityAsync(string hostKey, CancellationToken cancellationToken = default);

    /// <summary>Every host in <see cref="MountState.Mounted"/> or <see cref="MountState.Mounting"/>
    /// drains, then parks in <see cref="MountState.Disabled"/> until <see cref="ResumeAsync"/>
    /// (docs/ARCHITECTURE.md §4 rule 5). Returns promptly -- ISystemEventSource's suspend handler
    /// must complete quickly (docs/ARCHITECTURE.md §3); a drain that does not confirm immediately
    /// keeps retrying on its own schedule rather than blocking this call.</summary>
    Task SuspendAsync(CancellationToken cancellationToken = default);

    /// <summary>Every previously-enabled host re-probes immediately, bypassing backoff
    /// (docs/ARCHITECTURE.md §4 rule 5, Backoff section). Exception: an on-demand host sitting in
    /// <see cref="MountState.Unreachable"/> is left dark, exactly as it is for the recurring ladder
    /// (ADR-014) -- resume is a passive system event, not the user acting on this host.</summary>
    Task ResumeAsync(CancellationToken cancellationToken = default);

    /// <summary>Backoff resets and an immediate re-probe for every idle <see cref="MountState.Ready"/>
    /// host, and for every idle <see cref="MountState.Unreachable"/> host whose mount mode is
    /// <c>persistent</c>; an immediate forced probe for every <see cref="MountState.Mounted"/> host
    /// too (E6 bullet: "force-probe mounted hosts"). Does NOT touch <see cref="MountState.Disabled"/>
    /// hosts -- a network change should not reawaken a host the user or config explicitly disabled.
    /// An on-demand host in <see cref="MountState.Unreachable"/> is left dark for the same reason as
    /// <see cref="ResumeAsync"/> (ADR-014).</summary>
    Task NetworkChangedAsync(CancellationToken cancellationToken = default);

    /// <summary>Call whenever <c>RcloneProcessService.StatusChanged</c> reports
    /// <c>RcloneProcessStatus.Healthy</c> -- including the very first start. A restarted
    /// <c>rclone rcd</c> knows nothing about mounts this supervisor believes are up
    /// (<c>mount/listmounts</c> on the fresh process returns empty), so this reconciles every
    /// host against reality immediately rather than waiting for the next tick.</summary>
    Task OnRcloneRestartedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gates whether ANY host may enter <see cref="MountState.Mounting"/> (bs-yvw.1). Set by
    /// <see cref="Hosting.StartupOrchestrator"/> from WinFsp detection and <c>rclone rcd</c>
    /// health. Hosts still probe normally while unavailable, and never enter <c>Mounting</c> until
    /// this reports <see cref="MountingAvailability.IsAvailable"/> -- see the type's remarks. A
    /// transition from unavailable to available immediately attempts to mount every
    /// administratively-enabled, non-parked persistent host sitting in <see cref="MountState.Ready"/>,
    /// so recovery (e.g. <c>rclone rcd</c> restarting) does not require an app restart.
    /// </summary>
    Task SetMountingAvailabilityAsync(MountingAvailability availability, CancellationToken cancellationToken = default);
}

/// <summary>The seven states of docs/ARCHITECTURE.md §4, one instance per configured host.</summary>
public enum MountState
{
    Disabled,
    Probing,
    Unreachable,
    Ready,
    Mounting,
    Mounted,
    Draining,
}

/// <summary>Read-only point-in-time view of one host's mount state, for
/// <see cref="IMountSupervisor.GetSnapshot"/>.</summary>
public sealed record HostMountSnapshot
{
    public required string HostKey { get; init; }
    public required MountState State { get; init; }
    public string? Drive { get; init; }
    public required bool AdministrativelyEnabled { get; init; }
    public int ConsecutiveMountedFailures { get; init; }
    public int ConsecutiveIdleFailures { get; init; }
    public DateTimeOffset? LastTransitionUtc { get; init; }
    public string? LastTransitionTrigger { get; init; }

    /// <summary>True if the user explicitly unmounted this host and it has not since been
    /// explicitly remounted, had its config reloaded, or restarted with the app (bs-fix-e5). A
    /// persistent host can be <see cref="MountState.Ready"/> (or cycling through
    /// <see cref="MountState.Unreachable"/>/<see cref="MountState.Probing"/>) with this <c>true</c>
    /// -- that is deliberate parking, not a fault, and the tray UI should render it as such rather
    /// than as an unexplained "why hasn't this remounted" state.</summary>
    public bool UserParked { get; init; }

    /// <summary>
    /// Causal reason ANY host cannot currently mount for a process-wide reason (bs-yvw.1) -- e.g.
    /// "WinFsp is not installed" -- or <see langword="null"/> when mounting is available. Not
    /// about this particular host's own reachability; see <see cref="MountingAvailability"/>.
    /// </summary>
    public string? MountUnavailableReason { get; init; }
}
