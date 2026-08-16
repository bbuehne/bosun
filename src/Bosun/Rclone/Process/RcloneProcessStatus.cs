namespace Bosun.Rclone.Process;

/// <summary>Lifecycle status of the supervised <c>rclone rcd</c> child process.</summary>
public enum RcloneProcessStatus
{
    /// <summary>Not started yet, or stopped cleanly (host shutdown).</summary>
    Stopped,

    /// <summary>Process launched; waiting for <c>core/version</c> to confirm it is answering.</summary>
    Starting,

    /// <summary>
    /// Running and <c>core/version</c> responded. This is the "reconcile from scratch" signal:
    /// it fires on the very first successful start AND after every restart following an
    /// unexpected death, because a brand-new <c>rclone rcd</c> process never knows about any of
    /// Bosun's intended mounts (<c>mount/listmounts</c> on a fresh process returns empty).
    /// Anything downstream that owns mount state (E5's <c>IMountSupervisor</c>) MUST treat every
    /// transition into this status as "reconcile every host from scratch", not merely "the
    /// process is back" (docs/ARCHITECTURE.md §6, "rclone rcd dies" row; Invariant I2).
    /// </summary>
    Healthy,

    /// <summary>
    /// Not running and not healthy. See <see cref="RcloneProcessFaultKind"/> for why, and
    /// <see cref="RcloneProcessService"/>'s remarks for which fault kinds retry automatically.
    /// </summary>
    Faulted,
}

/// <summary>Why <see cref="RcloneProcessStatus.Faulted"/>. Kept as a typed enum rather than only
/// a message string so callers (and tests) can branch on it without parsing text -- mirrors
/// <c>ShallowProbeOutcome</c>/<c>DeepProbeOutcome</c>'s style in <c>Bosun.Probe</c>.</summary>
public enum RcloneProcessFaultKind
{
    /// <summary>Not faulted (used alongside <see cref="RcloneProcessStatus.Starting"/> and
    /// <see cref="RcloneProcessStatus.Healthy"/>).</summary>
    None,

    /// <summary>
    /// The rclone executable could not be found at all (e.g. not on PATH). Per the E3 brief,
    /// this is distinct from <see cref="LaunchFailed"/> ("present but failed to start") -- and
    /// it is the one that will actually fire on the maintainer's first run, since rclone is not
    /// yet installed on his machine. Terminal: <see cref="RcloneProcessService"/> does NOT retry
    /// automatically for this fault kind, because retrying will not fix a missing executable --
    /// see the class remarks.
    /// </summary>
    ExecutableNotFound,

    /// <summary>The executable exists but the process could not be started. Retried automatically.</summary>
    LaunchFailed,

    /// <summary>The process started but never answered <c>core/version</c> within
    /// <see cref="RcloneProcessServiceOptions.HealthCheckTimeout"/>. Retried automatically.</summary>
    HealthCheckFailed,

    /// <summary>The process was <see cref="RcloneProcessStatus.Healthy"/> and then exited on its
    /// own (not because <see cref="RcloneProcessService"/> killed it). Retried automatically --
    /// this is "restart on death" (docs/ARCHITECTURE.md §6).</summary>
    ProcessExitedUnexpectedly,
}

public sealed class RcloneProcessStatusChangedEventArgs(
    RcloneProcessStatus status, RcloneProcessFaultKind faultKind, string? detail) : EventArgs
{
    public RcloneProcessStatus Status { get; } = status;
    public RcloneProcessFaultKind FaultKind { get; } = faultKind;

    /// <summary>Actionable detail when <see cref="Status"/> is <see cref="RcloneProcessStatus.Faulted"/>;
    /// <see langword="null"/> otherwise.</summary>
    public string? Detail { get; } = detail;
}
