namespace Bosun.Health;

/// <summary>
/// How bad the application's own health is (bs-yyg, ADR-020 Decision 4). Deliberately separate from
/// <c>Bosun.Status.AggregateHealth</c>: that rolls up what the HOSTS are doing (unreachable, mount
/// failing); this describes whether BOSUN ITSELF is working -- rclone, the supervisor loop, startup.
/// A host-level problem never appears here, so a host row is never asked to carry an app-wide fault
/// and the banner is never asked to carry a host's.
/// </summary>
public enum HealthLevel
{
    /// <summary>Nothing is wrong with Bosun itself.</summary>
    Ok,

    /// <summary>Something Bosun relies on is impaired but its core job still runs -- e.g. the
    /// Windows Terminal fragment could not be written, while mounting is unaffected.</summary>
    Degraded,

    /// <summary>Bosun cannot do its core job (mount and supervise drives) until this is fixed --
    /// rclone is not running, the supervisor loop is dead, the config is invalid.</summary>
    Faulted,
}

/// <summary>
/// One specific thing that is wrong with Bosun. Immutable. Two issues with the same
/// <see cref="Code"/> are the same issue seen at different times -- <see cref="AppHealthService"/>
/// keeps <see cref="FirstSeenUtc"/> from the first sighting for as long as the issue stays present.
/// </summary>
/// <param name="Code">Stable identifier, e.g. <c>rclone.port-held</c>. Safe to key repair actions,
/// log lines and tests on; never shown to the user as text. See <see cref="HealthIssueCodes"/>.</param>
/// <param name="Severity"><see cref="HealthLevel.Degraded"/> or <see cref="HealthLevel.Faulted"/>;
/// never <see cref="HealthLevel.Ok"/> (an issue by definition is not OK).</param>
/// <param name="Title">One line a person can read at a glance.</param>
/// <param name="Detail">The specific cause, in full -- e.g. the real rclone fault message.</param>
/// <param name="FirstSeenUtc">When this issue was first reported, from the injected clock.</param>
public sealed record HealthIssue(
    string Code,
    HealthLevel Severity,
    string Title,
    string Detail,
    DateTimeOffset FirstSeenUtc);

/// <summary>
/// Immutable snapshot of the application's health (bs-yyg). Produced only by
/// <see cref="AppHealthService"/>. No UI-framework types: the window banner, the tray tooltip, the
/// watchdog (bs-6to) and the diagnostics bundle (bs-ds3) all read the same value.
/// </summary>
public sealed record AppHealth
{
    /// <summary>OK, not starting, no issues.</summary>
    public static readonly AppHealth Healthy = new() { Level = HealthLevel.Ok, Issues = [] };

    /// <summary>The worst severity among <see cref="Issues"/>; <see cref="HealthLevel.Ok"/> when
    /// there are none.</summary>
    public required HealthLevel Level { get; init; }

    /// <summary>Every current issue, most severe first; among equals, oldest first, then by code.
    /// <see cref="TopIssue"/> is the first.</summary>
    public required IReadOnlyList<HealthIssue> Issues { get; init; }

    /// <summary>
    /// True while Bosun is still inside its startup grace period and something it needs (rclone) has
    /// simply not come up yet. Not an issue and does not raise <see cref="Level"/>: starting up is
    /// not a fault. When true, <see cref="Level"/> is <see cref="HealthLevel.Ok"/> or at most
    /// the level of unrelated issues -- never because of the thing that is still starting.
    /// </summary>
    public bool IsStarting { get; init; }

    public bool IsOk => Level == HealthLevel.Ok;

    /// <summary>The most severe issue, or <see langword="null"/> when <see cref="Level"/> is OK.</summary>
    public HealthIssue? TopIssue => Issues.Count > 0 ? Issues[0] : null;

    /// <summary>Value comparison (a record's own equality would compare <see cref="Issues"/> by
    /// reference).</summary>
    internal bool SameContentAs(AppHealth other) =>
        Level == other.Level && IsStarting == other.IsStarting && Issues.SequenceEqual(other.Issues);
}

/// <summary>
/// The stable issue codes Bosun's own sources use (bs-yyg). Namespaced by source so the watchdog
/// (bs-6to) and any later publisher pick their own prefix (<c>watchdog.*</c>) without colliding.
/// </summary>
public static class HealthIssueCodes
{
    // rclone: one code per real cause (RcloneProcessFaultKind), so a repair action can target the
    // right one. Only one rclone issue exists at a time.
    public const string RcloneExecutableNotFound = "rclone.executable-not-found";
    public const string RcloneLaunchFailed = "rclone.launch-failed";
    public const string RcloneHealthCheckFailed = "rclone.health-check-failed";
    public const string RcloneUnauthorized = "rclone.unauthorized";
    public const string RcloneExitedBeforeHealthy = "rclone.exited-before-healthy";
    public const string RclonePortHeld = "rclone.port-held";
    public const string RcloneExitedUnexpectedly = "rclone.exited-unexpectedly";

    /// <summary>rclone never reported healthy and the startup grace period has run out, but no
    /// specific fault was ever reported.</summary>
    public const string RcloneNotRunning = "rclone.not-running";

    // Startup (StartupReadiness).
    public const string StartupConfigInvalid = "startup.config-invalid";
    public const string StartupWinFspMissing = "startup.winfsp-missing";
    public const string StartupTerminalFragment = "startup.terminal-fragment";
    public const string StartupSupervisorFailed = "startup.supervisor-failed";

    // Supervisor loop.
    public const string SupervisorLoopStopped = "supervisor.loop-stopped";

    // Watchdog (bs-6to). Reported by SupervisorWatchdog, not derived.

    /// <summary>The supervisor loop is dead or has made no progress past the stall threshold. Shown
    /// while the watchdog restarts Bosun, and kept showing if it is not allowed to.</summary>
    public const string WatchdogSupervisorStalled = "watchdog.supervisor-stalled";

    /// <summary>The watchdog has used its restart allowance for the hour and has stopped restarting
    /// Bosun automatically. A person has to act.</summary>
    public const string WatchdogRestartLimit = "watchdog.restart-limit";
}
