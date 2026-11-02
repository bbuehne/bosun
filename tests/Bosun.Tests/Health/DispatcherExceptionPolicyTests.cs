using Bosun.Health;
using Bosun.Tests.Configuration.Fakes;
using Bosun.Tests.Supervisor.Support;
using Microsoft.Extensions.Logging;

namespace Bosun.Tests.Health;

/// <summary>
/// bs-twz: once the logger exists, a dispatcher exception is survived and Bosun is marked red; more than
/// five inside 60 s stops being survived. The real <see cref="AppHealthService"/> is used (not a mock) so
/// "the tray goes red" is asserted on the model the tray reads. All time is a fake clock.
/// </summary>
public sealed class DispatcherExceptionPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    private sealed class Harness : IDisposable
    {
        public FakeTimeProvider Time { get; } = new(T0);
        public AppHealthService Health { get; }
        public CapturingLogger<DispatcherExceptionPolicy> Log { get; } = new();
        public DispatcherExceptionPolicy Policy { get; }

        public Harness(IAppHealthReporter? reporter = null, bool useNullReporter = false)
        {
            Health = new AppHealthService(Time, new AppHealthOptions { StartupGracePeriod = TimeSpan.Zero });
            // Healthy rclone, so the only possible issue is the one under test.
            Health.ObserveRclone(Bosun.Rclone.Process.RcloneProcessStatus.Healthy, Bosun.Rclone.Process.RcloneProcessFaultKind.None, null);
            Policy = new DispatcherExceptionPolicy(Time, useNullReporter ? null : reporter ?? Health, Log);
        }

        public HealthIssue? Issue => Health.Current.Issues.SingleOrDefault(i => i.Code == HealthIssueCodes.UiUnhandledException);

        public void Dispose() => Health.Dispose();
    }

    private sealed class ThrowingReporter : IAppHealthReporter
    {
        public int Calls { get; private set; }

        public void ReportIssue(string code, HealthLevel severity, string title, string detail)
        {
            Calls++;
            throw new InvalidOperationException("health model exploded");
        }

        public void ClearIssue(string code) { }
        public void ObserveRclone(Bosun.Rclone.Process.RcloneProcessStatus status, Bosun.Rclone.Process.RcloneProcessFaultKind faultKind, string? message) { }
        public void ObserveStartup(Bosun.Hosting.StartupReadiness readiness) { }
        public void ObserveSupervisorLoop(bool started, bool isRunning) { }
        public void BeginShutdown() { }
    }

    private sealed class ThrowingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            throw new IOException("log sink gone");
    }

    private static InvalidOperationException Boom(string message = "binding exploded") => new(message);

    [Fact]
    public void The_first_exception_is_handled_and_reports_a_Faulted_issue()
    {
        using var h = new Harness();

        var decision = h.Policy.Decide(Boom());

        Assert.Equal(DispatcherExceptionDecision.Handle, decision);
        var issue = Assert.IsType<HealthIssue>(h.Issue);
        Assert.Equal(HealthLevel.Faulted, issue.Severity);
        Assert.Equal("A UI error occurred; see the log", issue.Title);
        Assert.Equal(HealthLevel.Faulted, h.Health.Current.Level);
    }

    [Fact]
    public void The_issue_detail_names_the_exception_type_and_message()
    {
        using var h = new Harness();

        h.Policy.Decide(new ArgumentOutOfRangeException(null, "row index was out of range"));

        var detail = Assert.IsType<HealthIssue>(h.Issue).Detail;
        Assert.Contains("System.ArgumentOutOfRangeException", detail);
        Assert.Contains("row index was out of range", detail);
    }

    [Fact]
    public void An_overlong_message_is_truncated_in_the_detail()
    {
        using var h = new Harness();

        h.Policy.Decide(Boom(new string('x', 5000)));

        Assert.True(h.Issue!.Detail.Length < 1000, $"detail was {h.Issue.Detail.Length} chars");
    }

    [Fact]
    public void The_exception_is_logged_at_Critical_with_the_exception_attached()
    {
        using var h = new Harness();
        var ex = Boom();

        h.Policy.Decide(ex);

        var entry = Assert.Single(h.Log.Entries, e => e.Level == LogLevel.Critical);
        Assert.Same(ex, entry.Exception);
    }

    [Fact]
    public void A_second_exception_updates_the_same_issue_rather_than_adding_one()
    {
        using var h = new Harness();
        h.Policy.Decide(Boom("first"));
        var firstSeen = h.Issue!.FirstSeenUtc;
        h.Time.Advance(TimeSpan.FromSeconds(10));

        h.Policy.Decide(Boom("second"));

        Assert.Single(h.Health.Current.Issues, i => i.Code == HealthIssueCodes.UiUnhandledException);
        Assert.Contains("second", h.Issue!.Detail);
        Assert.Equal(firstSeen, h.Issue.FirstSeenUtc);
    }

    [Fact]
    public void A_later_recovery_elsewhere_does_not_clear_the_issue()
    {
        using var h = new Harness();
        h.Policy.Decide(Boom());

        // Everything the supervision layer can report as "all well again".
        h.Health.ObserveRclone(Bosun.Rclone.Process.RcloneProcessStatus.Healthy, Bosun.Rclone.Process.RcloneProcessFaultKind.None, null);
        h.Health.ObserveSupervisorLoop(started: true, isRunning: true);
        h.Time.Advance(TimeSpan.FromHours(2));

        Assert.NotNull(h.Issue);
        Assert.Equal(HealthLevel.Faulted, h.Health.Current.Level);
    }

    [Fact]
    public void Five_exceptions_inside_the_window_are_all_handled_and_the_sixth_terminates()
    {
        using var h = new Harness();

        for (var i = 1; i <= DispatcherExceptionPolicy.MaxExceptionsPerWindow; i++)
        {
            Assert.Equal(DispatcherExceptionDecision.Handle, h.Policy.Decide(Boom($"e{i}")));
            h.Time.Advance(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(DispatcherExceptionDecision.Terminate, h.Policy.Decide(Boom("e6")));
    }

    [Fact]
    public void Termination_is_explained_in_the_log()
    {
        using var h = new Harness();
        for (var i = 0; i < DispatcherExceptionPolicy.MaxExceptionsPerWindow; i++)
        {
            h.Policy.Decide(Boom());
        }

        h.Policy.Decide(Boom("the last straw"));

        var last = h.Log.Entries[^1];
        Assert.Equal(LogLevel.Critical, last.Level);
        Assert.Contains("exit", last.Message);
        Assert.Equal("the last straw", last.Exception!.Message);
    }

    [Fact]
    public void Once_terminating_it_keeps_terminating_even_after_the_window_passes()
    {
        using var h = new Harness();
        for (var i = 0; i <= DispatcherExceptionPolicy.MaxExceptionsPerWindow; i++)
        {
            h.Policy.Decide(Boom());
        }

        h.Time.Advance(TimeSpan.FromMinutes(10));

        Assert.Equal(DispatcherExceptionDecision.Terminate, h.Policy.Decide(Boom()));
    }

    [Fact]
    public void Exceptions_spread_beyond_the_window_keep_being_handled()
    {
        using var h = new Harness();

        // One every 30 s: never more than three inside any 60 s window (t, t+30, t+60 -> 3), for far
        // more than six exceptions in total.
        for (var i = 0; i < 40; i++)
        {
            Assert.Equal(DispatcherExceptionDecision.Handle, h.Policy.Decide(Boom($"e{i}")));
            h.Time.Advance(TimeSpan.FromSeconds(30));
        }

        Assert.NotNull(h.Issue);
    }

    [Fact]
    public void Old_exceptions_leave_the_window_so_a_later_burst_gets_a_fresh_allowance()
    {
        using var h = new Harness();
        for (var i = 0; i < DispatcherExceptionPolicy.MaxExceptionsPerWindow; i++)
        {
            Assert.Equal(DispatcherExceptionDecision.Handle, h.Policy.Decide(Boom()));
        }

        h.Time.Advance(DispatcherExceptionPolicy.Window + TimeSpan.FromSeconds(1));

        for (var i = 0; i < DispatcherExceptionPolicy.MaxExceptionsPerWindow; i++)
        {
            Assert.Equal(DispatcherExceptionDecision.Handle, h.Policy.Decide(Boom()));
        }
    }

    [Fact]
    public void The_window_boundary_is_inclusive_of_exactly_sixty_seconds()
    {
        // Five at t=0 and a sixth at exactly t=60 s: all six are within a 60 s span, so terminate.
        using var h = new Harness();
        for (var i = 0; i < DispatcherExceptionPolicy.MaxExceptionsPerWindow; i++)
        {
            h.Policy.Decide(Boom());
        }

        h.Time.Advance(DispatcherExceptionPolicy.Window);

        Assert.Equal(DispatcherExceptionDecision.Terminate, h.Policy.Decide(Boom()));
    }

    [Fact]
    public void A_sixth_just_past_the_window_is_still_handled()
    {
        using var h = new Harness();
        for (var i = 0; i < DispatcherExceptionPolicy.MaxExceptionsPerWindow; i++)
        {
            h.Policy.Decide(Boom());
        }

        h.Time.Advance(DispatcherExceptionPolicy.Window + TimeSpan.FromMilliseconds(1));

        Assert.Equal(DispatcherExceptionDecision.Handle, h.Policy.Decide(Boom()));
    }

    [Fact]
    public void A_health_reporter_that_throws_does_not_escape_and_the_exception_is_still_handled()
    {
        var reporter = new ThrowingReporter();
        using var h = new Harness(reporter);

        var decision = h.Policy.Decide(Boom());

        Assert.Equal(DispatcherExceptionDecision.Handle, decision);
        Assert.Equal(1, reporter.Calls);
        Assert.Contains(h.Log.Entries, e => e.Level == LogLevel.Error && e.Exception is InvalidOperationException { Message: "health model exploded" });
    }

    [Fact]
    public void A_missing_reporter_still_handles_and_says_it_could_not_mark_Bosun_red()
    {
        using var h = new Harness(useNullReporter: true);

        var decision = h.Policy.Decide(Boom());

        Assert.Equal(DispatcherExceptionDecision.Handle, decision);
        Assert.Null(h.Issue);
        Assert.Contains(h.Log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("could not be marked faulted"));
    }

    [Fact]
    public void A_logger_that_throws_does_not_escape_and_the_issue_is_still_reported()
    {
        using var health = new AppHealthService(new FakeTimeProvider(T0), new AppHealthOptions { StartupGracePeriod = TimeSpan.Zero });
        var policy = new DispatcherExceptionPolicy(new FakeTimeProvider(T0), health, new ThrowingLogger());

        var decision = policy.Decide(Boom());

        Assert.Equal(DispatcherExceptionDecision.Handle, decision);
        Assert.Contains(health.Current.Issues, i => i.Code == HealthIssueCodes.UiUnhandledException);
    }

    [Fact]
    public void A_dispatcher_exception_raised_while_reporting_is_counted_but_does_not_recurse()
    {
        using var h = new Harness();
        var nested = new ReentrantReporter();
        var policy = new DispatcherExceptionPolicy(h.Time, nested, h.Log);
        nested.OnReport = () => Assert.Equal(DispatcherExceptionDecision.Handle, policy.Decide(Boom("nested")));

        var decision = policy.Decide(Boom("outer"));

        Assert.Equal(DispatcherExceptionDecision.Handle, decision);
        Assert.Equal(1, nested.Calls);
    }

    private sealed class ReentrantReporter : IAppHealthReporter
    {
        public int Calls { get; private set; }
        public Action? OnReport { get; set; }

        public void ReportIssue(string code, HealthLevel severity, string title, string detail)
        {
            Calls++;
            OnReport?.Invoke();
        }

        public void ClearIssue(string code) { }
        public void ObserveRclone(Bosun.Rclone.Process.RcloneProcessStatus status, Bosun.Rclone.Process.RcloneProcessFaultKind faultKind, string? message) { }
        public void ObserveStartup(Bosun.Hosting.StartupReadiness readiness) { }
        public void ObserveSupervisorLoop(bool started, bool isRunning) { }
        public void BeginShutdown() { }
    }
}
