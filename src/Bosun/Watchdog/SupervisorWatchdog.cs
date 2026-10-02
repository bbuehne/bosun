using Bosun.Health;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bosun.Watchdog;

/// <summary>
/// Notices when the mount supervisor's loop has died or stalled and restarts Bosun (bs-6to,
/// ADR-020 Decision 3). Exists because on 2026-09-28 the loop died and for 2.5 days nothing probed,
/// drained or remounted anything, and nothing said so.
/// </summary>
/// <remarks>
/// <para>
/// <b>Independent of what it watches.</b> It runs on its own timer, on the injected
/// <see cref="TimeProvider"/>, and reads <see cref="ISupervisorLiveness"/> -- three fields, no
/// calls into the supervisor and nothing queued on the supervisor's channel. A watchdog that had to
/// ask the loop whether it was alive would hang with it.
/// </para>
/// <para>
/// <b>What a stall is.</b> The supervisor is started, and the loop has shown no sign of life for
/// <see cref="WatchdogOptions.StallThreshold"/> -- whether it has exited or is alive and blocked.
/// Both are measured the same way, from the loop's last activity stamp: a loop that exited stamped
/// that moment on its way out, so a dead loop is acted on after the same threshold, not faster. That
/// is deliberate: it makes the two cases indistinguishable to the rest of the design and keeps a
/// loop that is merely between shutdown steps from being restarted on sight.
/// </para>
/// <para>
/// <b>What it does.</b> Logs at Error, reports <c>watchdog.supervisor-stalled</c> (Faulted) to the
/// health model, and asks <see cref="IAppRestarter"/> to restart the process. At most
/// <see cref="WatchdogOptions.MaxRestartsInWindow"/> times per rolling
/// <see cref="WatchdogOptions.RestartWindow"/>. Past that it does not restart; it reports
/// <c>watchdog.restart-limit</c> as well and keeps the stall issue up, so a fault that survives a
/// restart cannot become a restart loop that also hides its own banner. The history is persisted
/// (<see cref="IRestartHistoryStore"/>) and is written BEFORE the restart is requested: the process
/// that exits cannot be relied on to write it afterwards, and the one that starts must find it.
/// </para>
/// <para>
/// <b>Waking from sleep is not a stall.</b> The loop's last stamp is hours old by the wall clock
/// after a sleep, and the watchdog's timer can fire before the loop's own tick does. A gap between
/// two checks far larger than the interval means the machine was asleep (or the process starved), so
/// the watchdog restarts its own clock instead of judging by the loop's old stamp
/// (<see cref="WatchdogOptions.MaxCheckGap"/>).
/// </para>
/// <para>
/// <b>Shutdown is not a stall either.</b> Once the application is exiting
/// (<see cref="IShutdownState"/>) it does nothing, and this service is registered after
/// <c>StartupOrchestrator</c> so the host stops it first.
/// </para>
/// </remarks>
public sealed class SupervisorWatchdog : IHostedService, IDisposable
{
    private readonly ISupervisorLiveness liveness;
    private readonly IAppHealthReporter health;
    private readonly IAppRestarter restarter;
    private readonly IRestartHistoryStore history;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<SupervisorWatchdog> logger;
    private readonly IShutdownState? shutdown;
    private readonly WatchdogOptions options;

    private ITimer? timer;
    private List<DateTimeOffset> restarts = [];
    private DateTimeOffset baselineUtc;
    private DateTimeOffset lastCheckUtc;
    private bool stallReported;
    private bool limitReported;
    private volatile bool restartInProgress;
    private volatile bool stopped;
    private int checkBusy;

    public SupervisorWatchdog(
        ISupervisorLiveness liveness,
        IAppHealthReporter health,
        IAppRestarter restarter,
        IRestartHistoryStore history,
        TimeProvider timeProvider,
        ILogger<SupervisorWatchdog> logger,
        IShutdownState? shutdown = null,
        WatchdogOptions? options = null)
    {
        this.liveness = liveness;
        this.health = health;
        this.restarter = restarter;
        this.history = history;
        this.timeProvider = timeProvider;
        this.logger = logger;
        this.shutdown = shutdown;
        this.options = options ?? new WatchdogOptions();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        // Silence is never measured from before the watchdog existed: a loop stamp from before this
        // process started (or MinValue, before the loop's first run) must not read as a stall.
        baselineUtc = now;
        lastCheckUtc = now;

        // The previous process's restarts, if there were any: the whole point of persisting them.
        restarts = Prune(history.Load(), now);
        if (restarts.Count > 0)
        {
            logger.LogInformation(
                "Supervisor watchdog started; {Count} automatic restart(s) already used in the last {Window}",
                restarts.Count,
                options.RestartWindow);
        }

        timer = timeProvider.CreateTimer(_ => _ = CheckAsync(), null, options.CheckInterval, options.CheckInterval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        stopped = true;
        timer?.Dispose();
        timer = null;
        return Task.CompletedTask;
    }

    public void Dispose() => timer?.Dispose();

    /// <summary>One look at the supervisor. Called by the timer; <c>internal</c> so tests can await
    /// it. Never throws -- it runs on a timer thread, where an escaping exception ends the process.</summary>
    internal async Task CheckAsync()
    {
        // The timer can tick again while a restart request is still in flight.
        if (Interlocked.Exchange(ref checkBusy, 1) == 1)
        {
            return;
        }

        try
        {
            await CheckCoreAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The supervisor watchdog's check failed; it will check again at the next interval");
        }
        finally
        {
            Volatile.Write(ref checkBusy, 0);
        }
    }

    private async Task CheckCoreAsync()
    {
        var now = timeProvider.GetUtcNow();

        if (stopped || restartInProgress || shutdown is { IsShuttingDown: true })
        {
            lastCheckUtc = now;
            return;
        }

        if (now - lastCheckUtc > options.MaxCheckGap)
        {
            baselineUtc = now;
            logger.LogInformation(
                "Supervisor watchdog: {Gap} since the previous check (the machine was asleep, or the process was starved); " +
                "judging the supervisor from now rather than from its pre-gap activity",
                now - lastCheckUtc);
        }

        lastCheckUtc = now;

        if (!liveness.IsStarted)
        {
            // Not started (or stopped on purpose): no activity is expected.
            ClearIfReported();
            return;
        }

        var isRunning = liveness.IsLoopRunning;
        var lastActivity = liveness.LastLoopActivityUtc;
        var since = lastActivity > baselineUtc ? lastActivity : baselineUtc;
        var silentFor = now - since;

        if (silentFor < options.StallThreshold)
        {
            ClearIfReported();
            return;
        }

        await HandleStallAsync(now, isRunning, lastActivity, silentFor).ConfigureAwait(false);
    }

    private async Task HandleStallAsync(DateTimeOffset now, bool isRunning, DateTimeOffset lastActivity, TimeSpan silentFor)
    {
        var state = isRunning ? "alive but not progressing" : "exited";
        var reason = $"supervisor loop stalled: {state}, no activity for {FormatDuration(silentFor)}";

        if (!stallReported)
        {
            logger.LogError(
                "Supervisor stalled: the loop is {State}; last activity {LastActivity:O}, silent for {SilentFor} " +
                "(threshold {Threshold}). Mounts are not being managed.",
                state,
                lastActivity,
                silentFor,
                options.StallThreshold);
        }

        restarts = Prune(restarts, now);

        if (restarts.Count >= options.MaxRestartsInWindow)
        {
            ReportStall(silentFor, state, "Automatic restarts are paused; see the other message.");
            ReportLimit(restarts.Count);
            stallReported = true;
            return;
        }

        List<DateTimeOffset> updated = [.. restarts, now];
        try
        {
            // The reason goes in with the timestamp so the replacement can tell the user why it exists
            // (the watchdog.restarted notice, bs-aoz). Same write, so it cannot be recorded without
            // the restart being counted, or the reverse.
            history.Save(updated, new LastRestart(now, reason));
        }
        catch (Exception ex)
        {
            // Restarting without being able to remember it would let a persistent fault restart
            // Bosun forever, which is exactly what the limit is for.
            logger.LogError(
                ex,
                "Supervisor watchdog: the restart history could not be saved, so the restart limit cannot be " +
                "guaranteed; not restarting automatically");
            ReportStall(silentFor, state, "Automatic restart is unavailable because its history could not be saved.");
            health.ReportIssue(
                HealthIssueCodes.WatchdogRestartLimit,
                HealthLevel.Faulted,
                "Automatic recovery is unavailable",
                "Bosun could not record its restart history under %LOCALAPPDATA%\\Bosun, so it will not restart itself " +
                "(it could not keep to its limit of restarts per hour). Mounts are not being managed. " +
                "Quit Bosun from the tray menu and start it again, and check that folder is writable. " +
                $"Error: {ex.Message}");
            limitReported = true;
            stallReported = true;
            return;
        }

        restarts = updated;
        ReportStall(silentFor, state, "Bosun is restarting itself to recover.");
        stallReported = true;

        // Set before the request, not after: the request is what ends this process, and a second
        // check must not queue a second restart while the first is under way.
        restartInProgress = true;
        var launched = false;
        try
        {
            launched = await restarter.RestartAsync(reason, RestartKind.Watchdog, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Supervisor watchdog: the restart request failed");
        }

        if (!launched)
        {
            restartInProgress = false;
            logger.LogError(
                "Supervisor watchdog: Bosun could not be restarted ({Reason}); will check again in {Interval}. " +
                "{Used} of {Max} automatic restarts used in the last {Window}.",
                reason,
                options.CheckInterval,
                restarts.Count,
                options.MaxRestartsInWindow,
                options.RestartWindow);
            ReportStall(silentFor, state, "Bosun tried to restart itself and could not. It will try again.");
        }
    }

    private void ReportStall(TimeSpan silentFor, string state, string tail) =>
        health.ReportIssue(
            HealthIssueCodes.WatchdogSupervisorStalled,
            HealthLevel.Faulted,
            "Mount supervision has stalled",
            $"The loop that probes, mounts and unmounts hosts has shown no sign of life for {FormatDuration(silentFor)} " +
            $"({state}). No host is being probed, mounted or unmounted. {tail}");

    private void ReportLimit(int used)
    {
        if (!limitReported)
        {
            logger.LogError(
                "Supervisor watchdog: restart limit reached ({Used} automatic restarts in the last {Window}); " +
                "not restarting again. The stall issue stays reported.",
                used,
                options.RestartWindow);
        }

        health.ReportIssue(
            HealthIssueCodes.WatchdogRestartLimit,
            HealthLevel.Faulted,
            "Automatic recovery has stopped",
            $"Bosun has already restarted itself {used} times in the last {FormatDuration(options.RestartWindow)} " +
            "to recover from this, and the supervisor has stalled again. Automatic recovery has stopped so that a " +
            "persistent fault cannot become a restart loop. Mounts are not being managed. Quit Bosun from the tray " +
            "menu and start it again. If it keeps happening, the log in %LOCALAPPDATA%\\Bosun\\logs shows why.");
        limitReported = true;
    }

    private void ClearIfReported()
    {
        if (!stallReported && !limitReported)
        {
            return;
        }

        health.ClearIssue(HealthIssueCodes.WatchdogSupervisorStalled);
        health.ClearIssue(HealthIssueCodes.WatchdogRestartLimit);
        stallReported = false;
        limitReported = false;
        logger.LogInformation("Supervisor watchdog: the supervisor loop is making progress again; cleared the stall report");
    }

    /// <summary>Drops restarts older than the window. Entries dated after <paramref name="now"/>
    /// (the clock was set back) are counted as <paramref name="now"/> so a bad clock cannot block
    /// recovery for longer than one window.</summary>
    private List<DateTimeOffset> Prune(IEnumerable<DateTimeOffset> source, DateTimeOffset now)
    {
        var cutoff = now - options.RestartWindow;
        return source.Select(t => t > now ? now : t).Where(t => t > cutoff).OrderBy(t => t).ToList();
    }

    private static string FormatDuration(TimeSpan span) =>
        span >= TimeSpan.FromHours(1)
            ? $"{(int)span.TotalHours} h {span.Minutes} min"
            : span >= TimeSpan.FromMinutes(1)
                ? $"{(int)span.TotalMinutes} min {span.Seconds} s"
                : $"{(int)span.TotalSeconds} s";
}
