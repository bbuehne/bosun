using Bosun.Health;
using Bosun.Hosting;
using Bosun.Rclone.Process;
using Bosun.Tests.Configuration.Fakes;

namespace Bosun.Tests.Health;

/// <summary>
/// The application-health model (bs-yyg, ADR-020 Decision 4): each source maps to the expected issue,
/// code and level; recovery clears the issue; the startup grace period is honoured. All time is the
/// injected <see cref="FakeTimeProvider"/> -- nothing here waits on a real clock.
/// </summary>
public sealed class AppHealthServiceTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

    private sealed class Harness : IDisposable
    {
        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero));
        public AppHealthService Health { get; }
        public List<AppHealth> Published { get; } = [];

        public Harness(TimeSpan? grace = null)
        {
            Health = new AppHealthService(Time, new AppHealthOptions { StartupGracePeriod = grace ?? Grace });
            Health.Changed += (_, e) => Published.Add(e.Health);
        }

        public AppHealth Now => Health.Current;

        public void Dispose() => Health.Dispose();
    }

    private static StartupReadiness Readiness(
        ConfigReadinessState config = ConfigReadinessState.Loaded,
        IReadOnlyList<string>? configErrors = null,
        bool winFsp = true,
        bool fragmentWritten = true,
        string? fragmentFault = null,
        bool rcloneHealthy = true,
        string? rcloneFault = null) => new()
    {
        ConfigState = config,
        ConfigErrors = configErrors ?? [],
        WinFspInstalled = winFsp,
        WinFspMessage = winFsp ? "WinFsp found." : "WinFsp is not installed (looked in the registry).",
        TerminalFragmentWritten = fragmentWritten,
        TerminalFragmentFaultMessage = fragmentFault,
        RcloneHealthy = rcloneHealthy,
        RcloneFaultMessage = rcloneFault,
        SupervisorRunning = true,
    };

    // ------------------------------------------------------------------------------------------
    // Startup grace period
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void A_fresh_service_is_Ok_and_starting_not_an_issue()
    {
        using var h = new Harness();

        Assert.Equal(HealthLevel.Ok, h.Now.Level);
        Assert.True(h.Now.IsStarting);
        Assert.Empty(h.Now.Issues);
        Assert.Null(h.Now.TopIssue);
    }

    [Fact]
    public void Rclone_not_up_inside_the_grace_period_is_still_just_starting()
    {
        using var h = new Harness();
        h.Health.ObserveRclone(RcloneProcessStatus.Starting, RcloneProcessFaultKind.None, null);

        h.Time.Advance(Grace - TimeSpan.FromMilliseconds(1));

        Assert.True(h.Now.IsOk);
        Assert.True(h.Now.IsStarting);
        Assert.Empty(h.Published);
    }

    [Fact]
    public void Rclone_still_not_up_when_the_grace_period_ends_becomes_a_Faulted_issue_by_itself()
    {
        using var h = new Harness();
        h.Health.ObserveRclone(RcloneProcessStatus.Starting, RcloneProcessFaultKind.None, null);

        // Nobody reports anything: the model's own timer is what notices.
        h.Time.Advance(Grace);

        var issue = Assert.Single(h.Now.Issues);
        Assert.Equal(HealthIssueCodes.RcloneNotRunning, issue.Code);
        Assert.Equal(HealthLevel.Faulted, issue.Severity);
        Assert.Equal(HealthLevel.Faulted, h.Now.Level);
        Assert.False(h.Now.IsStarting);
        Assert.Single(h.Published);
    }

    [Fact]
    public void The_grace_period_is_measured_from_when_the_service_was_created()
    {
        using var h = new Harness();
        var created = h.Time.GetUtcNow();
        h.Time.Advance(Grace);

        Assert.Equal(created + Grace, Assert.Single(h.Now.Issues).FirstSeenUtc);
    }

    [Fact]
    public void Rclone_becoming_healthy_inside_the_grace_period_ends_starting_without_ever_raising_an_issue()
    {
        using var h = new Harness();

        h.Health.ObserveRclone(RcloneProcessStatus.Healthy, RcloneProcessFaultKind.None, null);

        Assert.True(h.Now.IsOk);
        Assert.False(h.Now.IsStarting);

        h.Time.Advance(Grace * 2);
        Assert.Empty(h.Now.Issues);
    }

    [Fact]
    public void An_explicit_rclone_fault_does_not_wait_for_the_grace_period()
    {
        using var h = new Harness();

        h.Health.ObserveRclone(RcloneProcessStatus.Faulted, RcloneProcessFaultKind.ExecutableNotFound, "rclone is not installed");

        Assert.Equal(HealthLevel.Faulted, h.Now.Level);
        Assert.False(h.Now.IsStarting);
    }

    // ------------------------------------------------------------------------------------------
    // rclone
    // ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(RcloneProcessFaultKind.ExecutableNotFound, HealthIssueCodes.RcloneExecutableNotFound)]
    [InlineData(RcloneProcessFaultKind.LaunchFailed, HealthIssueCodes.RcloneLaunchFailed)]
    [InlineData(RcloneProcessFaultKind.HealthCheckFailed, HealthIssueCodes.RcloneHealthCheckFailed)]
    [InlineData(RcloneProcessFaultKind.HealthCheckUnauthorized, HealthIssueCodes.RcloneUnauthorized)]
    [InlineData(RcloneProcessFaultKind.ProcessExitedBeforeHealthy, HealthIssueCodes.RcloneExitedBeforeHealthy)]
    [InlineData(RcloneProcessFaultKind.PortHeldByOtherProcess, HealthIssueCodes.RclonePortHeld)]
    [InlineData(RcloneProcessFaultKind.ProcessExitedUnexpectedly, HealthIssueCodes.RcloneExitedUnexpectedly)]
    public void Each_rclone_fault_kind_maps_to_its_own_Faulted_issue_carrying_the_real_message(
        RcloneProcessFaultKind kind, string expectedCode)
    {
        using var h = new Harness();
        const string message = "core/version on 127.0.0.1:5572 returned HTTP 401 (PID 4242, C:\\tools\\rclone.exe)";

        h.Health.ObserveRclone(RcloneProcessStatus.Faulted, kind, message);

        var issue = Assert.Single(h.Now.Issues);
        Assert.Equal(expectedCode, issue.Code);
        Assert.Equal(HealthLevel.Faulted, issue.Severity);
        Assert.Equal(message, issue.Detail);
        Assert.False(string.IsNullOrWhiteSpace(issue.Title));
        Assert.DoesNotContain(message, issue.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void A_fault_with_no_message_still_produces_a_readable_detail()
    {
        using var h = new Harness();

        h.Health.ObserveRclone(RcloneProcessStatus.Faulted, RcloneProcessFaultKind.HealthCheckFailed, null);

        Assert.False(string.IsNullOrWhiteSpace(Assert.Single(h.Now.Issues).Detail));
    }

    [Fact]
    public void Rclone_recovering_clears_the_issue_and_publishes_the_change()
    {
        using var h = new Harness();
        h.Health.ObserveRclone(RcloneProcessStatus.Faulted, RcloneProcessFaultKind.HealthCheckUnauthorized, "401");
        Assert.False(h.Now.IsOk);

        h.Health.ObserveRclone(RcloneProcessStatus.Healthy, RcloneProcessFaultKind.None, null);

        Assert.True(h.Now.IsOk);
        Assert.Empty(h.Now.Issues);
        Assert.True(h.Published[^1].IsOk);
    }

    [Fact]
    public void Rclone_restarting_after_a_fault_keeps_showing_the_real_cause_instead_of_a_generic_one()
    {
        using var h = new Harness();
        h.Health.ObserveRclone(RcloneProcessStatus.Faulted, RcloneProcessFaultKind.PortHeldByOtherProcess, "port held by PID 7");
        h.Time.Advance(Grace * 2);

        h.Health.ObserveRclone(RcloneProcessStatus.Starting, RcloneProcessFaultKind.None, null);

        var issue = Assert.Single(h.Now.Issues);
        Assert.Equal(HealthIssueCodes.RclonePortHeld, issue.Code);
        Assert.Equal("port held by PID 7", issue.Detail);
    }

    [Fact]
    public void The_fault_changing_kind_replaces_the_issue_so_only_one_rclone_issue_exists()
    {
        using var h = new Harness();
        h.Health.ObserveRclone(RcloneProcessStatus.Faulted, RcloneProcessFaultKind.HealthCheckFailed, "no answer");

        h.Health.ObserveRclone(RcloneProcessStatus.Faulted, RcloneProcessFaultKind.HealthCheckUnauthorized, "HTTP 401");

        var issue = Assert.Single(h.Now.Issues);
        Assert.Equal(HealthIssueCodes.RcloneUnauthorized, issue.Code);
    }

    [Fact]
    public void FirstSeen_is_kept_while_an_issue_stays_present_and_reset_after_it_clears()
    {
        using var h = new Harness();
        var t0 = h.Time.GetUtcNow();
        h.Health.ObserveRclone(RcloneProcessStatus.Faulted, RcloneProcessFaultKind.HealthCheckFailed, "first message");

        h.Time.Advance(TimeSpan.FromMinutes(10));
        h.Health.ObserveRclone(RcloneProcessStatus.Faulted, RcloneProcessFaultKind.HealthCheckFailed, "a different message");
        var updated = Assert.Single(h.Now.Issues);
        Assert.Equal("a different message", updated.Detail);
        Assert.Equal(t0, updated.FirstSeenUtc);

        h.Health.ObserveRclone(RcloneProcessStatus.Healthy, RcloneProcessFaultKind.None, null);
        h.Time.Advance(TimeSpan.FromMinutes(5));
        h.Health.ObserveRclone(RcloneProcessStatus.Faulted, RcloneProcessFaultKind.HealthCheckFailed, "again");

        Assert.Equal(t0 + TimeSpan.FromMinutes(15), Assert.Single(h.Now.Issues).FirstSeenUtc);
    }

    [Fact]
    public void Reporting_the_same_thing_again_does_not_publish_a_change()
    {
        using var h = new Harness();
        h.Health.ObserveRclone(RcloneProcessStatus.Faulted, RcloneProcessFaultKind.LaunchFailed, "boom");
        var count = h.Published.Count;

        h.Time.Advance(TimeSpan.FromSeconds(1));
        h.Health.ObserveRclone(RcloneProcessStatus.Faulted, RcloneProcessFaultKind.LaunchFailed, "boom");

        Assert.Equal(count, h.Published.Count);
    }

    // ------------------------------------------------------------------------------------------
    // Startup readiness
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Invalid_config_is_a_Faulted_issue_listing_the_errors()
    {
        using var h = new Harness();

        h.Health.ObserveStartup(Readiness(ConfigReadinessState.Invalid, configErrors: ["line 3: bad drive", "line 9: dup key"]));

        var issue = Assert.Single(h.Now.Issues);
        Assert.Equal(HealthIssueCodes.StartupConfigInvalid, issue.Code);
        Assert.Equal(HealthLevel.Faulted, issue.Severity);
        Assert.Contains("line 3: bad drive", issue.Detail, StringComparison.Ordinal);
        Assert.Contains("line 9: dup key", issue.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_config_suppresses_the_rclone_never_started_issue_and_the_starting_state()
    {
        using var h = new Harness();
        h.Health.ObserveStartup(Readiness(ConfigReadinessState.Invalid, configErrors: ["bad"]));
        Assert.False(h.Now.IsStarting);

        h.Time.Advance(Grace * 4);

        // rclone was never started because nothing could be configured: one cause, shown once.
        Assert.Equal(HealthIssueCodes.StartupConfigInvalid, Assert.Single(h.Now.Issues).Code);
    }

    [Fact]
    public void WinFsp_missing_is_a_Faulted_issue_carrying_the_detector_message()
    {
        using var h = new Harness();

        h.Health.ObserveStartup(Readiness(winFsp: false));

        var issue = Assert.Single(h.Now.Issues);
        Assert.Equal(HealthIssueCodes.StartupWinFspMissing, issue.Code);
        Assert.Equal(HealthLevel.Faulted, issue.Severity);
        Assert.Equal("WinFsp is not installed (looked in the registry).", issue.Detail);
    }

    [Fact]
    public void A_failed_Terminal_fragment_write_is_only_Degraded()
    {
        using var h = new Harness();

        h.Health.ObserveStartup(Readiness(fragmentWritten: false, fragmentFault: "disk full"));

        var issue = Assert.Single(h.Now.Issues);
        Assert.Equal(HealthIssueCodes.StartupTerminalFragment, issue.Code);
        Assert.Equal(HealthLevel.Degraded, issue.Severity);
        Assert.Equal(HealthLevel.Degraded, h.Now.Level);
        Assert.Equal("disk full", issue.Detail);
    }

    [Fact]
    public void First_run_with_no_config_is_not_an_issue()
    {
        using var h = new Harness();

        h.Health.ObserveStartup(Readiness(ConfigReadinessState.AwaitingFirstRun));

        Assert.Empty(h.Now.Issues);
    }

    [Fact]
    public void The_Initial_readiness_placeholder_is_ignored()
    {
        using var h = new Harness();

        h.Health.ObserveStartup(StartupReadiness.Initial);

        Assert.Empty(h.Now.Issues);
    }

    [Fact]
    public void The_rclone_fields_of_readiness_are_not_a_second_source_for_rclone()
    {
        using var h = new Harness();
        h.Health.ObserveRclone(RcloneProcessStatus.Faulted, RcloneProcessFaultKind.HealthCheckUnauthorized, "HTTP 401");

        // StartupReadiness says RcloneHealthy=false with its own message; reading it too would show
        // one cause twice, under a code that could not say which kind it was.
        h.Health.ObserveStartup(Readiness(rcloneHealthy: false, rcloneFault: "HTTP 401"));

        Assert.Equal(HealthIssueCodes.RcloneUnauthorized, Assert.Single(h.Now.Issues).Code);
    }

    [Fact]
    public void A_later_readiness_without_the_problem_clears_the_startup_issue()
    {
        using var h = new Harness();
        h.Health.ObserveStartup(Readiness(winFsp: false));

        h.Health.ObserveStartup(Readiness(winFsp: true));

        Assert.Empty(h.Now.Issues);
    }

    // ------------------------------------------------------------------------------------------
    // Supervisor loop
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void A_started_loop_that_is_not_running_is_a_Faulted_issue()
    {
        using var h = new Harness();

        h.Health.ObserveSupervisorLoop(started: true, isRunning: false);

        var issue = Assert.Single(h.Now.Issues);
        Assert.Equal(HealthIssueCodes.SupervisorLoopStopped, issue.Code);
        Assert.Equal(HealthLevel.Faulted, issue.Severity);
    }

    [Fact]
    public void A_loop_that_was_never_started_is_not_reported_as_dead()
    {
        using var h = new Harness();

        h.Health.ObserveSupervisorLoop(started: false, isRunning: false);

        Assert.Empty(h.Now.Issues);
    }

    [Fact]
    public void A_running_loop_has_no_issue_and_a_restarted_loop_clears_it()
    {
        using var h = new Harness();
        h.Health.ObserveSupervisorLoop(started: true, isRunning: false);

        h.Health.ObserveSupervisorLoop(started: true, isRunning: true);

        Assert.Empty(h.Now.Issues);
    }

    // ------------------------------------------------------------------------------------------
    // ReportIssue / ClearIssue: the watchdog's hook
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void ReportIssue_adds_an_issue_and_ClearIssue_removes_it()
    {
        using var h = new Harness();

        h.Health.ReportIssue("watchdog.loop-stalled", HealthLevel.Faulted, "Supervision stalled", "No activity for 3 minutes");
        var issue = Assert.Single(h.Now.Issues);
        Assert.Equal("watchdog.loop-stalled", issue.Code);
        Assert.Equal("Supervision stalled", issue.Title);
        Assert.Equal("No activity for 3 minutes", issue.Detail);

        h.Health.ClearIssue("watchdog.loop-stalled");
        Assert.Empty(h.Now.Issues);
    }

    [Fact]
    public void Re_reporting_updates_the_text_but_keeps_FirstSeen()
    {
        using var h = new Harness();
        h.Health.ObserveRclone(RcloneProcessStatus.Healthy, RcloneProcessFaultKind.None, null);
        var t0 = h.Time.GetUtcNow();
        h.Health.ReportIssue("watchdog.restart-limit", HealthLevel.Degraded, "Restarted once", "1 of 3");

        h.Time.Advance(TimeSpan.FromMinutes(20));
        h.Health.ReportIssue("watchdog.restart-limit", HealthLevel.Faulted, "Restart limit reached", "3 of 3");

        var issue = Assert.Single(h.Now.Issues);
        Assert.Equal(HealthLevel.Faulted, issue.Severity);
        Assert.Equal("3 of 3", issue.Detail);
        Assert.Equal(t0, issue.FirstSeenUtc);
    }

    [Fact]
    public void Clearing_a_code_that_was_never_reported_is_a_no_op()
    {
        using var h = new Harness();
        var count = h.Published.Count;

        h.Health.ClearIssue("nothing.here");

        Assert.Equal(count, h.Published.Count);
    }

    [Fact]
    public void ClearIssue_does_not_remove_a_derived_issue()
    {
        using var h = new Harness();
        h.Health.ObserveRclone(RcloneProcessStatus.Faulted, RcloneProcessFaultKind.LaunchFailed, "boom");

        h.Health.ClearIssue(HealthIssueCodes.RcloneLaunchFailed);

        Assert.Single(h.Now.Issues);
    }

    [Fact]
    public void ReportIssue_rejects_an_Ok_severity()
    {
        using var h = new Harness();

        Assert.Throws<ArgumentOutOfRangeException>(() => h.Health.ReportIssue("x.y", HealthLevel.Ok, "t", "d"));
    }

    // ------------------------------------------------------------------------------------------
    // Aggregation and publication
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Level_is_the_worst_severity_and_issues_are_ordered_most_severe_then_oldest()
    {
        using var h = new Harness();
        h.Health.ObserveRclone(RcloneProcessStatus.Healthy, RcloneProcessFaultKind.None, null);
        h.Health.ReportIssue("a.degraded-old", HealthLevel.Degraded, "old degraded", "d");
        h.Time.Advance(TimeSpan.FromMinutes(1));
        h.Health.ReportIssue("a.faulted-new", HealthLevel.Faulted, "new faulted", "d");
        h.Time.Advance(TimeSpan.FromMinutes(1));
        h.Health.ReportIssue("a.faulted-newest", HealthLevel.Faulted, "newest faulted", "d");

        Assert.Equal(HealthLevel.Faulted, h.Now.Level);
        Assert.Equal(new[] { "a.faulted-new", "a.faulted-newest", "a.degraded-old" }, h.Now.Issues.Select(i => i.Code).ToArray());
        Assert.Equal("a.faulted-new", h.Now.TopIssue!.Code);
    }

    [Fact]
    public void Only_Degraded_issues_give_a_Degraded_level()
    {
        using var h = new Harness();

        h.Health.ReportIssue("a.b", HealthLevel.Degraded, "t", "d");

        Assert.Equal(HealthLevel.Degraded, h.Now.Level);
    }

    [Fact]
    public void Issues_from_different_sources_coexist()
    {
        using var h = new Harness();

        h.Health.ObserveRclone(RcloneProcessStatus.Faulted, RcloneProcessFaultKind.LaunchFailed, "boom");
        h.Health.ObserveSupervisorLoop(started: true, isRunning: false);
        h.Health.ObserveStartup(Readiness(fragmentWritten: false, fragmentFault: "disk full"));

        Assert.Equal(3, h.Now.Issues.Count);
        Assert.Equal(HealthLevel.Faulted, h.Now.Level);
        Assert.Equal(HealthLevel.Degraded, h.Now.Issues[^1].Severity);
    }

    [Fact]
    public void A_throwing_subscriber_does_not_stop_the_others_or_the_model()
    {
        using var h = new Harness();
        var secondSaw = false;
        h.Health.Changed += (_, _) => throw new InvalidOperationException("subscriber bug");
        h.Health.Changed += (_, _) => secondSaw = true;

        h.Health.ReportIssue("a.b", HealthLevel.Degraded, "t", "d");

        Assert.True(secondSaw);
        Assert.Single(h.Now.Issues);
    }

    [Fact]
    public void A_zero_grace_period_means_there_is_no_starting_state()
    {
        using var h = new Harness(grace: TimeSpan.Zero);

        Assert.False(h.Now.IsStarting);
        Assert.Equal(HealthIssueCodes.RcloneNotRunning, Assert.Single(h.Now.Issues).Code);
    }

    [Fact]
    public void BeginShutdown_stops_rclone_and_the_loop_ending_from_being_issues()
    {
        // bs-6to: on a normal exit rclone stops and the loop ends. That is the shutdown, not a fault.
        using var h = new Harness(grace: TimeSpan.Zero);
        h.Health.ObserveRclone(RcloneProcessStatus.Healthy, RcloneProcessFaultKind.None, null);
        h.Health.ObserveSupervisorLoop(started: true, isRunning: true);
        Assert.True(h.Now.IsOk);

        h.Health.BeginShutdown();
        h.Health.ObserveRclone(RcloneProcessStatus.Stopped, RcloneProcessFaultKind.None, null);
        h.Health.ObserveSupervisorLoop(started: true, isRunning: false);

        Assert.True(h.Now.IsOk, string.Join(", ", h.Now.Issues.Select(i => i.Code)));
    }

    [Fact]
    public void BeginShutdown_leaves_explicitly_reported_issues_alone()
    {
        using var h = new Harness(grace: TimeSpan.Zero);
        h.Health.ReportIssue(HealthIssueCodes.WatchdogSupervisorStalled, HealthLevel.Faulted, "stalled", "detail");

        h.Health.BeginShutdown();

        Assert.Contains(h.Now.Issues, i => i.Code == HealthIssueCodes.WatchdogSupervisorStalled);
    }

    [Fact]
    public void Without_BeginShutdown_rclone_stopping_is_an_issue()
    {
        // The counterpart that makes the two tests above mean something.
        using var h = new Harness(grace: TimeSpan.Zero);
        h.Health.ObserveRclone(RcloneProcessStatus.Stopped, RcloneProcessFaultKind.None, null);

        Assert.Equal(HealthIssueCodes.RcloneNotRunning, Assert.Single(h.Now.Issues).Code);
    }
}
