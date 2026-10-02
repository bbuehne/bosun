using Bosun.Hosting;
using Bosun.Rclone.Process;

namespace Bosun.Health;

/// <summary>Raised by <see cref="IAppHealth.Changed"/>.</summary>
public sealed class AppHealthChangedEventArgs(AppHealth health) : EventArgs
{
    public AppHealth Health { get; } = health;
}

/// <summary>
/// Read side of the application-health model (bs-yyg, ADR-020 Decision 4). Anything that shows or
/// records health -- the window banner, the tray, the diagnostics bundle (bs-ds3) -- depends on
/// this and nothing else.
/// </summary>
public interface IAppHealth
{
    /// <summary>The latest snapshot. Never null; safe to read from any thread.</summary>
    AppHealth Current { get; }

    /// <summary>
    /// Raised after <see cref="Current"/> changes in content (a repeated identical report does not
    /// raise it). Fired on whichever thread caused the change -- including a timer thread when the
    /// startup grace period ends -- so a WPF subscriber must marshal to its dispatcher itself, as
    /// with <c>IStatusReadModel.Changed</c>. The shipped UI polls <see cref="Current"/> on its own
    /// timer instead.
    /// </summary>
    event EventHandler<AppHealthChangedEventArgs>? Changed;
}

/// <summary>
/// Write side (bs-yyg). Sources push what they know; the service derives issues from it. Everything
/// is idempotent -- reporting the same thing twice changes nothing.
/// </summary>
public interface IAppHealthReporter
{
    /// <summary>
    /// The hook for sources the model knows nothing about (the watchdog, bs-6to). Adds the issue, or
    /// updates its severity/title/detail if <paramref name="code"/> is already reported -- keeping
    /// the original <see cref="HealthIssue.FirstSeenUtc"/>. Stays until <see cref="ClearIssue"/>.
    /// A reported issue overrides a derived one with the same code.
    /// </summary>
    /// <param name="severity">Must be Degraded or Faulted.</param>
    void ReportIssue(string code, HealthLevel severity, string title, string detail);

    /// <summary>Removes a reported issue. A no-op if <paramref name="code"/> is not reported. Does not
    /// touch derived issues (rclone, startup, supervisor loop): those clear when their source says
    /// the problem is gone.</summary>
    void ClearIssue(string code);

    /// <summary>Latest <c>RcloneProcessService</c> status. <paramref name="message"/> is its
    /// <c>LastFaultMessage</c> / the event's <c>Detail</c>, used verbatim as the issue detail.</summary>
    void ObserveRclone(RcloneProcessStatus status, RcloneProcessFaultKind faultKind, string? message);

    /// <summary>Latest <see cref="StartupReadiness"/>: config, WinFsp and Terminal-fragment
    /// outcomes. Its rclone fields are ignored on purpose -- <see cref="ObserveRclone"/> carries the
    /// real fault kind, and reading both would show one cause twice.</summary>
    void ObserveStartup(StartupReadiness readiness);

    /// <summary>The supervisor loop's liveness. <paramref name="started"/> is whether Bosun launched
    /// the loop; <paramref name="isRunning"/> is <c>MountSupervisor.IsLoopRunning</c>. Started but not
    /// running is a Faulted issue. This reports death only: a loop that is alive but stalled is the
    /// watchdog's call (bs-6to, via <see cref="ReportIssue"/>), because judging staleness needs the
    /// watchdog's thresholds.</summary>
    void ObserveSupervisorLoop(bool started, bool isRunning);

    /// <summary>
    /// Bosun is exiting on purpose (bs-6to). From here on, "rclone is not running" and "the supervisor
    /// loop has stopped" are the shutdown itself, not faults, so they stop being derived as issues;
    /// otherwise a normal exit flashes a Faulted banner while the children are being stopped. Issues
    /// a source reported explicitly (<see cref="ReportIssue"/>) are untouched. One-way: there is no
    /// un-shutdown.
    /// </summary>
    void BeginShutdown();
}

/// <summary>Options for <see cref="AppHealthService"/>.</summary>
public sealed record AppHealthOptions
{
    /// <summary>
    /// How long after the service is created that "rclone has not come up yet" counts as starting
    /// rather than as an issue. 30 seconds.
    /// </summary>
    /// <remarks>
    /// The slowest legitimate first start is the stale-rcd kill (<c>StaleKillTimeout</c>, 5 s) plus
    /// the health check (<c>HealthCheckTimeout</c>, 15 s) = 20 s, after config load, the Terminal
    /// fragment write and WinFsp detection (all fast). 30 s leaves 10 s of margin over that. Past it
    /// the health check has overrun its own timeout, which is a real anomaly worth a banner.
    /// A fault that rclone reports explicitly (not found, 401, exited, port held) does not wait for
    /// the grace period -- it is an issue immediately, because the cause is already known.
    /// </remarks>
    public TimeSpan StartupGracePeriod { get; init; } = TimeSpan.FromSeconds(30);
}
