using System.Text.Json;
using Bosun.Health;
using Bosun.Tests.Configuration.Fakes;
using Bosun.Tests.Supervisor.Support;
using Bosun.Watchdog;

namespace Bosun.Tests.Watchdog;

/// <summary>
/// bs-aoz: after a watchdog restart, the new instance says so (<c>watchdog.restarted</c>, Degraded) and
/// the notice goes away when the user dismisses it or after 24 hours. Fake clock, fake history, no
/// process.
/// </summary>
public sealed class WatchdogRestartNoticeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 2, 3, 12, 0, TimeSpan.Zero);

    private sealed class Harness : IDisposable
    {
        public FakeTimeProvider Time { get; }
        public InMemoryRestartHistory History { get; } = new();
        public AppHealthService Health { get; }
        public WatchdogRestartNotice Notice { get; }
        public CapturingLogger<WatchdogRestartNotice> Log { get; } = new();

        public Harness(RestartLaunchInfo launch, LastRestart? last = null, TimeSpan? launchedAfterRecord = null)
        {
            // The old instance wrote the record at T0; this one starts a few seconds later.
            Time = new FakeTimeProvider(T0 + (launchedAfterRecord ?? TimeSpan.FromSeconds(20)));
            History.Last = last;
            Health = new AppHealthService(Time, new AppHealthOptions { StartupGracePeriod = TimeSpan.Zero });
            Health.ObserveRclone(Bosun.Rclone.Process.RcloneProcessStatus.Healthy, Bosun.Rclone.Process.RcloneProcessFaultKind.None, null); // so rclone is not itself an issue
            Notice = new WatchdogRestartNotice(launch, History, Health, Time, Log, TimeZoneInfo.Utc);
        }

        public HealthIssue? Issue => Health.Current.Issues.SingleOrDefault(i => i.Code == HealthIssueCodes.WatchdogRestarted);

        public void Dispose()
        {
            Notice.Dispose();
            Health.Dispose();
        }
    }

    private static readonly RestartLaunchInfo ByWatchdog = new(RestartKind.Watchdog);

    [Fact]
    public async Task A_watchdog_launch_reports_a_Degraded_notice_with_the_time_and_the_reason()
    {
        using var h = new Harness(ByWatchdog, new LastRestart(T0, "supervisor loop stalled: exited, no activity for 3 min 0 s"));

        await h.Notice.StartAsync(CancellationToken.None);

        var issue = h.Issue;
        Assert.NotNull(issue);
        Assert.Equal(HealthLevel.Degraded, issue.Severity);
        Assert.Equal("Bosun restarted itself", issue.Title);
        Assert.Contains("at 03:12", issue.Detail, StringComparison.Ordinal);
        Assert.Contains("mount supervision stalled", issue.Detail, StringComparison.Ordinal);
        Assert.Contains("supervisor loop stalled: exited, no activity for 3 min 0 s", issue.Detail, StringComparison.Ordinal);
        Assert.Contains("Copy diagnostics", issue.Detail, StringComparison.Ordinal);
        Assert.Equal(HealthLevel.Degraded, h.Health.Current.Level);
    }

    [Theory]
    [MemberData(nameof(NonWatchdogLaunches))]
    public async Task Any_other_launch_reports_nothing(RestartLaunchInfo launch)
    {
        using var h = new Harness(launch, new LastRestart(T0, "stale record from an earlier restart"));

        await h.Notice.StartAsync(CancellationToken.None);

        Assert.Null(h.Issue);
        Assert.True(h.Health.Current.IsOk);
    }

    public static TheoryData<RestartLaunchInfo> NonWatchdogLaunches => new()
    {
        RestartLaunchInfo.Ordinary,
        new RestartLaunchInfo(RestartKind.Manual),
    };

    [Fact]
    public async Task A_record_from_an_earlier_restart_is_not_presented_as_the_cause_of_this_one()
    {
        // The record is older than any handoff could take: it belongs to a previous restart.
        using var h = new Harness(
            ByWatchdog,
            new LastRestart(T0 - TimeSpan.FromHours(5), "an old reason"),
            launchedAfterRecord: TimeSpan.Zero);

        await h.Notice.StartAsync(CancellationToken.None);

        var issue = h.Issue;
        Assert.NotNull(issue);
        Assert.DoesNotContain("an old reason", issue.Detail, StringComparison.Ordinal);
        Assert.Contains("not recorded", issue.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_no_record_at_all_the_notice_still_appears_and_says_the_reason_was_not_recorded()
    {
        using var h = new Harness(ByWatchdog, last: null);

        await h.Notice.StartAsync(CancellationToken.None);

        var issue = h.Issue;
        Assert.NotNull(issue);
        Assert.Contains("03:12", issue.Detail, StringComparison.Ordinal); // this launch's time
        Assert.Contains("not recorded", issue.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dismissing_clears_it_and_only_it()
    {
        using var h = new Harness(ByWatchdog, new LastRestart(T0, "r"));
        h.Health.ReportIssue(HealthIssueCodes.WatchdogRestartLimit, HealthLevel.Faulted, "limit", "d");
        await h.Notice.StartAsync(CancellationToken.None);

        h.Notice.Dismiss();

        Assert.Null(h.Issue);
        Assert.Contains(h.Health.Current.Issues, i => i.Code == HealthIssueCodes.WatchdogRestartLimit);
    }

    [Fact]
    public async Task Dismissing_when_nothing_is_showing_does_nothing()
    {
        using var h = new Harness(RestartLaunchInfo.Ordinary);
        await h.Notice.StartAsync(CancellationToken.None);

        h.Notice.Dismiss();

        Assert.True(h.Health.Current.IsOk);
    }

    [Fact]
    public async Task It_clears_itself_after_24_hours_and_not_before()
    {
        using var h = new Harness(ByWatchdog, new LastRestart(T0, "r"));
        await h.Notice.StartAsync(CancellationToken.None);

        h.Time.Advance(WatchdogRestartNotice.Lifetime - TimeSpan.FromSeconds(1));
        Assert.NotNull(h.Issue);

        h.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(h.Issue);
        Assert.True(h.Health.Current.IsOk);
    }

    [Fact]
    public async Task A_dismissed_notice_does_not_come_back_when_the_24_hours_pass()
    {
        using var h = new Harness(ByWatchdog, new LastRestart(T0, "r"));
        await h.Notice.StartAsync(CancellationToken.None);
        h.Notice.Dismiss();

        h.Time.Advance(WatchdogRestartNotice.Lifetime + TimeSpan.FromHours(1));

        Assert.Null(h.Issue);
    }

    [Fact]
    public void The_launch_info_reads_the_restart_flag_from_the_command_line()
    {
        Assert.True(RestartLaunchInfo.FromArguments(["--autostart", "--restarted-by-watchdog", "12"]).RestartedByWatchdog);
        Assert.False(RestartLaunchInfo.FromArguments(["--autostart"]).RestartedByWatchdog);
        Assert.False(RestartLaunchInfo.FromArguments(["--restarted-by-user", "12"]).RestartedByWatchdog);
        Assert.Equal(RestartKind.Manual, RestartLaunchInfo.FromArguments(["--restarted-by-user", "12"]).Kind);
    }

    // -- the history file the reason travels in --------------------------------------------------

    [Fact]
    public void The_reason_survives_the_round_trip_through_the_history_file()
    {
        var path = TempFile();
        try
        {
            var store = new JsonRestartHistoryStore(path);

            store.Save([T0], new LastRestart(T0, "supervisor loop stalled: exited"));

            var read = new JsonRestartHistoryStore(path);
            Assert.Equal([T0], read.Load());
            Assert.Equal(new LastRestart(T0, "supervisor loop stalled: exited"), read.LoadLast());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_history_file_written_before_reasons_were_kept_still_loads_with_no_reason()
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, """{ "Restarts": [ "2026-10-01T09:00:00+00:00", "2026-10-01T09:30:00+00:00" ] }""");
            var store = new JsonRestartHistoryStore(path);

            Assert.Equal(2, store.Load().Count);
            Assert.Null(store.LoadLast());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void An_older_build_reading_a_newer_file_still_sees_the_restarts_that_enforce_the_limit()
    {
        var path = TempFile();
        try
        {
            new JsonRestartHistoryStore(path).Save([T0, T0 + TimeSpan.FromMinutes(5)], new LastRestart(T0, "why"));

            // The pre-bs-aoz reader: a document with only Restarts, deserialized the same way.
            var old = JsonSerializer.Deserialize<LegacyDocument>(File.ReadAllText(path));

            Assert.NotNull(old);
            Assert.Equal([T0, T0 + TimeSpan.FromMinutes(5)], old.Restarts);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_saved_restart_with_no_reason_leaves_no_reason_behind()
    {
        var path = TempFile();
        try
        {
            var store = new JsonRestartHistoryStore(path);
            store.Save([T0], new LastRestart(T0, "old reason"));

            store.Save([T0, T0 + TimeSpan.FromMinutes(1)]);

            Assert.Null(store.LoadLast());
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"bosun-restart-history-{Guid.NewGuid():N}.json");

    private sealed class LegacyDocument
    {
        public List<DateTimeOffset> Restarts { get; set; } = [];
    }
}
