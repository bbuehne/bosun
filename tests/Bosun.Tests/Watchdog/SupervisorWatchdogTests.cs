using Bosun.Health;
using Bosun.Tests.Configuration.Fakes;
using Bosun.Tests.Supervisor.Support;
using Bosun.Watchdog;
using Microsoft.Extensions.Logging;

namespace Bosun.Tests.Watchdog;

/// <summary>
/// bs-6to / ADR-020 Decision 3: the watchdog restarts Bosun when the supervisor loop has died or
/// stalled, no sooner than the threshold, at most 3 times per rolling hour. Time is the injected
/// fake; the restart goes to a fake that launches nothing. The default suite never restarts, spawns
/// or kills a real process.
/// </summary>
public sealed class SupervisorWatchdogTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly WatchdogOptions Defaults = new();

    private sealed class Harness : IDisposable
    {
        public FakeTimeProvider Fake { get; } = new(T0);
        public SleepableTimeProvider Time { get; }
        public FakeLiveness Liveness { get; } = new() { LastLoopActivityUtc = T0 };
        public FakeRestarter Restarter { get; } = new();
        public InMemoryRestartHistory History { get; } = new();
        public FakeShutdownState Shutdown { get; } = new();
        public AppHealthService Health { get; }
        public CapturingLogger<SupervisorWatchdog> Log { get; } = new();
        public SupervisorWatchdog Dog { get; }

        public Harness(IEnumerable<DateTimeOffset>? previousRestarts = null, WatchdogOptions? options = null, InMemoryRestartHistory? sharedHistory = null)
        {
            Time = new SleepableTimeProvider(Fake);
            Health = new AppHealthService(Time, new AppHealthOptions { StartupGracePeriod = TimeSpan.Zero });
            if (sharedHistory is not null)
            {
                History = sharedHistory;
            }

            if (previousRestarts is not null)
            {
                History.Stored = [.. previousRestarts];
            }

            Dog = new SupervisorWatchdog(Liveness, Health, Restarter, History, Time, Log, Shutdown, options);
        }

        public Task StartAsync() => Dog.StartAsync(CancellationToken.None);

        public void Advance(TimeSpan delta) => Time.Advance(delta);

        public IReadOnlyList<string> WatchdogIssueCodes =>
            Health.Current.Issues.Select(i => i.Code).Where(c => c.StartsWith("watchdog.", StringComparison.Ordinal)).ToList();

        public HealthIssue? Issue(string code) => Health.Current.Issues.SingleOrDefault(i => i.Code == code);

        public void Dispose()
        {
            Dog.Dispose();
            Health.Dispose();
        }
    }

    // ------------------------------------------------------------------------------------------
    // Detection and the threshold
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task No_restart_before_the_threshold_and_one_at_it_when_activity_is_stale()
    {
        using var h = new Harness();
        await h.StartAsync();

        // One check interval short of the threshold: 165 s of silence against a 180 s threshold.
        h.Advance(Defaults.StallThreshold - Defaults.CheckInterval);
        Assert.Empty(h.Restarter.Reasons);
        Assert.Empty(h.WatchdogIssueCodes);

        h.Advance(Defaults.CheckInterval);

        var reason = Assert.Single(h.Restarter.Reasons);
        Assert.Contains("alive but not progressing", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_restart_before_the_threshold_and_one_at_it_when_the_loop_is_not_running()
    {
        using var h = new Harness();
        h.Liveness.IsLoopRunning = false;
        await h.StartAsync();

        h.Advance(Defaults.StallThreshold - Defaults.CheckInterval);
        Assert.Empty(h.Restarter.Reasons);

        h.Advance(Defaults.CheckInterval);

        var reason = Assert.Single(h.Restarter.Reasons);
        Assert.Contains("exited", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_action_when_the_supervisor_is_not_started_however_long_it_is_quiet()
    {
        using var h = new Harness();
        h.Liveness.IsStarted = false;
        h.Liveness.IsLoopRunning = false;
        await h.StartAsync();

        h.Advance(TimeSpan.FromHours(6));

        Assert.Empty(h.Restarter.Reasons);
        Assert.Empty(h.WatchdogIssueCodes);
        Assert.Equal(0, h.History.SaveCount);
    }

    [Fact]
    public async Task A_loop_that_keeps_showing_activity_is_never_restarted()
    {
        using var h = new Harness();
        await h.StartAsync();

        for (var i = 0; i < 40; i++)
        {
            h.Advance(TimeSpan.FromSeconds(30));
            h.Liveness.LastLoopActivityUtc = h.Time.GetUtcNow();
        }

        Assert.Empty(h.Restarter.Reasons);
        Assert.Empty(h.WatchdogIssueCodes);
    }

    [Fact]
    public async Task A_stale_stamp_from_before_the_watchdog_started_is_not_held_against_the_loop()
    {
        using var h = new Harness();
        h.Liveness.LastLoopActivityUtc = DateTimeOffset.MinValue;
        await h.StartAsync();

        h.Advance(Defaults.StallThreshold - Defaults.CheckInterval);

        Assert.Empty(h.Restarter.Reasons);
    }

    [Fact]
    public async Task A_stall_logs_Error_with_the_facts_and_reports_a_Faulted_issue()
    {
        using var h = new Harness();
        await h.StartAsync();

        h.Advance(Defaults.StallThreshold);

        Assert.Contains(h.Log.Entries, e =>
            e.Level == LogLevel.Error
            && e.Message.Contains("Supervisor stalled", StringComparison.Ordinal)
            && e.Message.Contains("alive but not progressing", StringComparison.Ordinal));

        var issue = h.Issue(HealthIssueCodes.WatchdogSupervisorStalled);
        Assert.NotNull(issue);
        Assert.Equal(HealthLevel.Faulted, issue.Severity);
        Assert.Equal("watchdog.supervisor-stalled", issue.Code);
        Assert.Contains("restarting itself", issue.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_restart_is_recorded_in_the_history_BEFORE_it_is_requested()
    {
        using var h = new Harness();
        IReadOnlyList<DateTimeOffset>? storedWhenRequested = null;
        h.Restarter.OnRequest = () => storedWhenRequested = [.. h.History.Stored];
        await h.StartAsync();

        h.Advance(Defaults.StallThreshold);

        Assert.NotNull(storedWhenRequested);
        Assert.Equal(h.Time.GetUtcNow(), Assert.Single(storedWhenRequested));
    }

    [Fact]
    public async Task No_second_restart_is_requested_while_the_first_is_under_way()
    {
        using var h = new Harness();
        await h.StartAsync();

        h.Advance(Defaults.StallThreshold);
        h.Advance(TimeSpan.FromMinutes(10));

        Assert.Single(h.Restarter.Reasons);
        Assert.Equal(1, h.History.SaveCount);
    }

    [Fact]
    public async Task Nothing_happens_once_the_application_is_shutting_down()
    {
        using var h = new Harness();
        h.Shutdown.IsShuttingDown = true;
        await h.StartAsync();

        h.Advance(TimeSpan.FromMinutes(30));

        Assert.Empty(h.Restarter.Reasons);
        Assert.Empty(h.WatchdogIssueCodes);
    }

    [Fact]
    public async Task A_stopped_watchdog_does_not_check_any_more()
    {
        using var h = new Harness();
        await h.StartAsync();
        await h.Dog.StopAsync(CancellationToken.None);

        h.Advance(TimeSpan.FromMinutes(30));

        Assert.Empty(h.Restarter.Reasons);
    }

    [Fact]
    public async Task A_loop_that_resumes_clears_the_reported_issues()
    {
        using var h = new Harness(previousRestarts: [T0 - TimeSpan.FromMinutes(5), T0 - TimeSpan.FromMinutes(4), T0 - TimeSpan.FromMinutes(3)]);
        await h.StartAsync();
        h.Advance(Defaults.StallThreshold);
        Assert.Equal(2, h.WatchdogIssueCodes.Count);

        h.Liveness.LastLoopActivityUtc = h.Time.GetUtcNow();
        h.Advance(Defaults.CheckInterval);

        Assert.Empty(h.WatchdogIssueCodes);
    }

    // ------------------------------------------------------------------------------------------
    // Waking from sleep is not a stall
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Waking_from_sleep_does_not_restart_Bosun_but_a_stall_after_waking_still_does()
    {
        using var h = new Harness();
        await h.StartAsync();
        h.Advance(TimeSpan.FromSeconds(30));
        h.Liveness.LastLoopActivityUtc = h.Time.GetUtcNow();

        // The machine sleeps overnight: the wall clock moves 8 hours, no timer fires meanwhile, and
        // the loop's last stamp is now 8 hours old.
        h.Time.Sleep(TimeSpan.FromHours(8));
        h.Advance(Defaults.CheckInterval);

        Assert.Empty(h.Restarter.Reasons);

        // It is still judged by what happens after waking: silence for a full threshold restarts.
        h.Advance(Defaults.StallThreshold - Defaults.CheckInterval * 2);
        Assert.Empty(h.Restarter.Reasons);
        h.Advance(Defaults.CheckInterval * 2);
        Assert.Single(h.Restarter.Reasons);
    }

    // ------------------------------------------------------------------------------------------
    // The rate limit
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_fourth_stall_within_an_hour_is_refused_and_reports_restart_limit()
    {
        // Four generations of Bosun in a row, sharing the history file the way real restarts do.
        var history = new InMemoryRestartHistory();
        var now = T0;
        var restartsLaunched = 0;
        Harness? last = null;

        for (var generation = 1; generation <= 4; generation++)
        {
            last?.Dispose();
            var h = new Harness(sharedHistory: history);
            h.Fake.Advance(now - T0);
            h.Liveness.LastLoopActivityUtc = h.Time.GetUtcNow();
            await h.StartAsync();

            h.Advance(Defaults.StallThreshold);
            restartsLaunched += h.Restarter.Reasons.Count;
            now = h.Time.GetUtcNow() + TimeSpan.FromMinutes(5);
            last = h;
        }

        Assert.NotNull(last);
        Assert.Equal(3, restartsLaunched);
        Assert.Empty(last.Restarter.Reasons);

        var limit = last.Issue(HealthIssueCodes.WatchdogRestartLimit);
        Assert.NotNull(limit);
        Assert.Equal(HealthLevel.Faulted, limit.Severity);
        Assert.Equal("watchdog.restart-limit", limit.Code);
        Assert.Contains("stopped", limit.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Quit Bosun", limit.Detail, StringComparison.Ordinal);

        // The stall issue keeps showing alongside it.
        Assert.NotNull(last.Issue(HealthIssueCodes.WatchdogSupervisorStalled));
        Assert.Contains(last.Log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("restart limit reached", StringComparison.Ordinal));
        Assert.Equal(3, history.Stored.Count);

        last.Dispose();
    }

    [Fact]
    public async Task The_limit_window_rolls_so_a_restart_is_allowed_again_after_an_hour()
    {
        // Three earlier restarts, the oldest 50 minutes ago: refused now, allowed once it is an hour old.
        using var h = new Harness(previousRestarts:
            [T0 - TimeSpan.FromMinutes(50), T0 - TimeSpan.FromMinutes(40), T0 - TimeSpan.FromMinutes(30)]);
        await h.StartAsync();

        h.Advance(Defaults.StallThreshold);

        Assert.Empty(h.Restarter.Reasons);
        Assert.NotNull(h.Issue(HealthIssueCodes.WatchdogRestartLimit));

        // The oldest entry turns 60 minutes old at T0 + 10 min; the next check after that restarts.
        h.Advance(TimeSpan.FromMinutes(10));

        Assert.Single(h.Restarter.Reasons);
        Assert.Equal(3, h.History.Stored.Count);
    }

    [Fact]
    public async Task History_loaded_from_the_file_is_honoured_by_the_next_process()
    {
        var path = Path.Combine(Path.GetTempPath(), "bosun-tests", Guid.NewGuid().ToString("N"), "watchdog-restarts.json");
        try
        {
            var store = new JsonRestartHistoryStore(path);
            store.Save([T0 - TimeSpan.FromMinutes(20), T0 - TimeSpan.FromMinutes(10), T0 - TimeSpan.FromMinutes(5)]);

            var fake = new FakeTimeProvider(T0);
            var restarter = new FakeRestarter();
            using var health = new AppHealthService(fake, new AppHealthOptions { StartupGracePeriod = TimeSpan.Zero });
            using var dog = new SupervisorWatchdog(
                new FakeLiveness { LastLoopActivityUtc = T0 }, health, restarter, new JsonRestartHistoryStore(path),
                fake, new CapturingLogger<SupervisorWatchdog>());
            await dog.StartAsync(CancellationToken.None);

            fake.Advance(Defaults.StallThreshold);

            Assert.Empty(restarter.Reasons);
            Assert.Contains(health.Current.Issues, i => i.Code == HealthIssueCodes.WatchdogRestartLimit);
        }
        finally
        {
            var directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task A_restart_that_could_not_be_recorded_is_not_attempted()
    {
        using var h = new Harness();
        h.History.SaveThrows = new IOException("disk full");
        await h.StartAsync();

        h.Advance(Defaults.StallThreshold);

        Assert.Empty(h.Restarter.Reasons);
        Assert.NotNull(h.Issue(HealthIssueCodes.WatchdogSupervisorStalled));
        var limit = h.Issue(HealthIssueCodes.WatchdogRestartLimit);
        Assert.NotNull(limit);
        Assert.Contains("disk full", limit.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_restart_that_fails_to_launch_is_retried_at_the_next_check_and_counts_against_the_limit()
    {
        using var h = new Harness();
        h.Restarter.Result = false;
        await h.StartAsync();

        h.Advance(Defaults.StallThreshold);
        Assert.Single(h.Restarter.Reasons);

        h.Advance(Defaults.CheckInterval);
        h.Advance(Defaults.CheckInterval);
        Assert.Equal(3, h.Restarter.Reasons.Count);

        // The fourth attempt is over the limit.
        h.Advance(Defaults.CheckInterval);
        Assert.Equal(3, h.Restarter.Reasons.Count);
        Assert.NotNull(h.Issue(HealthIssueCodes.WatchdogRestartLimit));
        Assert.NotNull(h.Issue(HealthIssueCodes.WatchdogSupervisorStalled));
    }

    [Fact]
    public async Task A_restarter_that_throws_does_not_take_the_watchdog_down()
    {
        using var h = new Harness();
        h.Restarter.Throws = new InvalidOperationException("boom");
        await h.StartAsync();

        h.Advance(Defaults.StallThreshold);
        h.Advance(Defaults.CheckInterval);

        Assert.Equal(2, h.Restarter.Reasons.Count);
        Assert.Contains(h.Log.Entries, e => e.Level == LogLevel.Error && e.Exception is InvalidOperationException);
    }
}
