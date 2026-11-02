using Bosun.Health;
using Bosun.Hosting;
using Bosun.Rclone.Process;
using Bosun.Tests.Configuration.Fakes;
using Bosun.UI.Tray;

namespace Bosun.Tests.UI.Tray;

/// <summary>
/// bs-ww9.1.1: one toast when Bosun enters a Faulted state (ADR-012's channel table). The first
/// group drives the notifier from a fake <see cref="IAppHealth"/>, so every case is exactly the
/// snapshot sequence it names. The second group uses the real <see cref="AppHealthService"/> on a
/// <see cref="FakeTimeProvider"/>, because "the startup grace period" is that model's definition
/// (<see cref="AppHealth.IsStarting"/>) and the notifier must follow it rather than copy it.
/// </summary>
public sealed class DegradedStateNotifierTests
{
    private sealed class FakeAppHealth : IAppHealth
    {
        public AppHealth Current { get; private set; } = AppHealth.Healthy;

        public event EventHandler<AppHealthChangedEventArgs>? Changed;

        public int SubscriberCount => Changed?.GetInvocationList().Length ?? 0;

        public void Publish(AppHealth health)
        {
            Current = health;
            Changed?.Invoke(this, new AppHealthChangedEventArgs(health));
        }

        public void Publish(bool isStarting = false, params HealthIssue[] issues) => Publish(new AppHealth
        {
            Level = issues.Length == 0 ? HealthLevel.Ok : issues.Max(i => i.Severity),
            Issues = issues,
            IsStarting = isStarting,
        });
    }

    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    private static HealthIssue Faulted(string code, string title = "Something is wrong") =>
        new(code, HealthLevel.Faulted, title, "detail that must not reach the toast", T0);

    private static HealthIssue Degraded(string code, string title = "Something is impaired") =>
        new(code, HealthLevel.Degraded, title, "detail", T0);

    private static (DegradedStateNotifier Notifier, List<(string Title, string Text)> Toasts) Watch(IAppHealth health)
    {
        var toasts = new List<(string Title, string Text)>();
        return (new DegradedStateNotifier(health, (title, text) => toasts.Add((title, text))), toasts);
    }

    // ------------------------------------------------------------------------------------------
    // The rule, against a fake health source
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void A_new_Faulted_issue_raises_one_toast_titled_Bosun_with_the_issue_title()
    {
        var health = new FakeAppHealth();
        var (_, toasts) = Watch(health);

        health.Publish(false, Faulted("startup.winfsp-missing", "WinFsp is not installed"));

        var toast = Assert.Single(toasts);
        Assert.Equal("Bosun", toast.Title);
        Assert.Equal("WinFsp is not installed", toast.Text);
    }

    [Fact]
    public void The_toast_text_is_the_title_alone_not_the_detail()
    {
        var health = new FakeAppHealth();
        var (_, toasts) = Watch(health);

        health.Publish(false, Faulted("x.y", "rclone was not found"));

        var toast = Assert.Single(toasts);
        Assert.DoesNotContain("detail", toast.Text);
    }

    [Fact]
    public void A_Degraded_issue_raises_no_toast()
    {
        var health = new FakeAppHealth();
        var (_, toasts) = Watch(health);

        health.Publish(false, Degraded("startup.terminal-fragment"));

        Assert.Empty(toasts);
    }

    [Fact]
    public void A_healthy_snapshot_raises_no_toast()
    {
        var health = new FakeAppHealth();
        var (_, toasts) = Watch(health);

        health.Publish(false);

        Assert.Empty(toasts);
    }

    [Fact]
    public void A_fault_already_present_when_the_notifier_is_created_is_toasted_once()
    {
        var health = new FakeAppHealth();
        health.Publish(false, Faulted("rclone.port-held", "Another program is using rclone's control port"));

        var (_, toasts) = Watch(health);

        Assert.Equal("Another program is using rclone's control port", Assert.Single(toasts).Text);
    }

    [Fact]
    public void A_fault_that_stays_does_not_toast_again_even_when_its_title_or_the_other_issues_change()
    {
        var health = new FakeAppHealth();
        var (_, toasts) = Watch(health);

        health.Publish(false, Faulted("rclone.not-running", "rclone is not running"));
        health.Publish(false, Faulted("rclone.not-running", "rclone is not running (still)"));
        health.Publish(false, Faulted("rclone.not-running"), Degraded("startup.terminal-fragment"));

        Assert.Single(toasts);
    }

    [Fact]
    public void A_second_Faulted_code_appearing_while_the_first_remains_toasts_only_the_second()
    {
        var health = new FakeAppHealth();
        var (_, toasts) = Watch(health);

        health.Publish(false, Faulted("startup.winfsp-missing", "WinFsp is not installed"));
        health.Publish(false, Faulted("startup.winfsp-missing", "WinFsp is not installed"), Faulted("rclone.not-running", "rclone is not running"));

        Assert.Equal(
            ["WinFsp is not installed", "rclone is not running"],
            toasts.Select(t => t.Text));
    }

    [Fact]
    public void A_fault_that_clears_and_comes_back_toasts_again()
    {
        var health = new FakeAppHealth();
        var (_, toasts) = Watch(health);

        health.Publish(false, Faulted("rclone.exited-unexpectedly"));
        health.Publish(false);
        health.Publish(false, Faulted("rclone.exited-unexpectedly"));

        Assert.Equal(2, toasts.Count);
    }

    [Fact]
    public void A_fault_that_drops_to_Degraded_and_returns_to_Faulted_toasts_again()
    {
        var health = new FakeAppHealth();
        var (_, toasts) = Watch(health);

        health.Publish(false, Faulted("watchdog.supervisor-stalled"));
        health.Publish(false, Degraded("watchdog.supervisor-stalled"));
        health.Publish(false, Faulted("watchdog.supervisor-stalled"));

        Assert.Equal(2, toasts.Count);
    }

    [Fact]
    public void A_Faulted_issue_during_the_startup_grace_period_raises_nothing_yet()
    {
        var health = new FakeAppHealth();
        var (_, toasts) = Watch(health);

        health.Publish(true, Faulted("startup.winfsp-missing", "WinFsp is not installed"));

        Assert.Empty(toasts);
    }

    [Fact]
    public void A_fault_seen_during_the_grace_period_is_toasted_once_when_the_grace_period_ends()
    {
        var health = new FakeAppHealth();
        var (_, toasts) = Watch(health);

        health.Publish(true, Faulted("startup.winfsp-missing", "WinFsp is not installed"));
        health.Publish(false, Faulted("startup.winfsp-missing", "WinFsp is not installed"));
        health.Publish(false, Faulted("startup.winfsp-missing", "WinFsp is not installed"), Degraded("startup.terminal-fragment"));

        Assert.Equal("WinFsp is not installed", Assert.Single(toasts).Text);
    }

    [Fact]
    public void A_fault_that_cleared_during_the_grace_period_is_never_toasted()
    {
        var health = new FakeAppHealth();
        var (_, toasts) = Watch(health);

        health.Publish(true, Faulted("rclone.launch-failed"));
        health.Publish(true);
        health.Publish(false);

        Assert.Empty(toasts);
    }

    [Fact]
    public void After_Dispose_nothing_is_raised_and_the_subscription_is_gone()
    {
        var health = new FakeAppHealth();
        var (notifier, toasts) = Watch(health);

        notifier.Dispose();
        health.Publish(false, Faulted("rclone.port-held"));

        Assert.Empty(toasts);
        Assert.Equal(0, health.SubscriberCount);
    }

    [Fact]
    public void Constructor_rejects_nulls()
    {
        Assert.Throws<ArgumentNullException>(() => new DegradedStateNotifier(null!, (_, _) => { }));
        Assert.Throws<ArgumentNullException>(() => new DegradedStateNotifier(new FakeAppHealth(), null!));
    }

    // ------------------------------------------------------------------------------------------
    // The real health model on a fake clock: the grace period is the model's, not the notifier's
    // ------------------------------------------------------------------------------------------

    private sealed class Rig : IDisposable
    {
        public FakeTimeProvider Time { get; } = new(T0);
        public AppHealthService Health { get; }
        public DegradedStateNotifier Notifier { get; }
        public List<(string Title, string Text)> Toasts { get; } = [];

        public Rig()
        {
            Health = new AppHealthService(Time, new AppHealthOptions { StartupGracePeriod = TimeSpan.FromSeconds(30) });
            Notifier = new DegradedStateNotifier(Health, (title, text) => Toasts.Add((title, text)));
        }

        public void Dispose()
        {
            Notifier.Dispose();
            Health.Dispose();
        }
    }

    private static StartupReadiness WinFspMissing() => new()
    {
        ConfigState = ConfigReadinessState.Loaded,
        ConfigErrors = [],
        WinFspInstalled = false,
        WinFspMessage = "WinFsp is not installed (looked in the registry).",
        TerminalFragmentWritten = true,
        TerminalFragmentFaultMessage = null,
        RcloneHealthy = false,
        RcloneFaultMessage = null,
        SupervisorRunning = false,
        SupervisorFaultMessage = null,
    };

    [Fact]
    public void While_rclone_is_still_starting_inside_the_grace_period_there_is_no_toast_and_none_when_it_comes_up()
    {
        using var rig = new Rig();

        rig.Health.ObserveRclone(RcloneProcessStatus.Starting, RcloneProcessFaultKind.None, null);
        rig.Time.Advance(TimeSpan.FromSeconds(10));
        rig.Health.ObserveRclone(RcloneProcessStatus.Healthy, RcloneProcessFaultKind.None, null);
        rig.Time.Advance(TimeSpan.FromSeconds(60));

        Assert.Empty(rig.Toasts);
    }

    [Fact]
    public void Rclone_that_never_reports_toasts_once_when_the_grace_period_runs_out()
    {
        using var rig = new Rig();

        rig.Health.ObserveRclone(RcloneProcessStatus.Starting, RcloneProcessFaultKind.None, null);
        rig.Time.Advance(TimeSpan.FromSeconds(29));
        Assert.Empty(rig.Toasts);

        rig.Time.Advance(TimeSpan.FromSeconds(2));
        rig.Time.Advance(TimeSpan.FromSeconds(120));

        var toast = Assert.Single(rig.Toasts);
        Assert.Equal(("Bosun", "rclone is not running"), toast);
    }

    [Fact]
    public void A_fault_rclone_reports_explicitly_toasts_at_once_without_waiting_for_the_grace_period()
    {
        using var rig = new Rig();

        rig.Health.ObserveRclone(RcloneProcessStatus.Faulted, RcloneProcessFaultKind.ExecutableNotFound, "rclone is not installed");

        Assert.Equal(("Bosun", "rclone was not found"), Assert.Single(rig.Toasts));
    }

    [Fact]
    public void A_missing_WinFsp_reported_while_rclone_starts_is_toasted_when_the_grace_state_ends()
    {
        using var rig = new Rig();

        rig.Health.ObserveRclone(RcloneProcessStatus.Starting, RcloneProcessFaultKind.None, null);
        rig.Health.ObserveStartup(WinFspMissing());
        Assert.True(rig.Health.Current.IsStarting);
        Assert.Empty(rig.Toasts);

        rig.Health.ObserveRclone(RcloneProcessStatus.Healthy, RcloneProcessFaultKind.None, null);

        Assert.Equal(("Bosun", "WinFsp is not installed"), Assert.Single(rig.Toasts));
    }

    [Fact]
    public void A_repeated_identical_rclone_report_does_not_toast_again_but_a_new_episode_does()
    {
        using var rig = new Rig();

        rig.Health.ObserveRclone(RcloneProcessStatus.Faulted, RcloneProcessFaultKind.ProcessExitedUnexpectedly, "exit 1");
        rig.Health.ObserveRclone(RcloneProcessStatus.Faulted, RcloneProcessFaultKind.ProcessExitedUnexpectedly, "exit 1");
        rig.Health.ObserveRclone(RcloneProcessStatus.Faulted, RcloneProcessFaultKind.ProcessExitedUnexpectedly, "exit 2");
        Assert.Single(rig.Toasts);

        rig.Health.ObserveRclone(RcloneProcessStatus.Healthy, RcloneProcessFaultKind.None, null);
        rig.Health.ObserveRclone(RcloneProcessStatus.Faulted, RcloneProcessFaultKind.ProcessExitedUnexpectedly, "exit 3");

        Assert.Equal(2, rig.Toasts.Count);
        Assert.All(rig.Toasts, t => Assert.Equal(("Bosun", "rclone stopped unexpectedly"), t));
    }

    [Fact]
    public void A_Degraded_reported_issue_does_not_toast_but_a_Faulted_one_does()
    {
        using var rig = new Rig();
        rig.Health.ObserveRclone(RcloneProcessStatus.Healthy, RcloneProcessFaultKind.None, null);

        rig.Health.ReportIssue("watchdog.restarted", HealthLevel.Degraded, "Bosun restarted itself", "detail");
        Assert.Empty(rig.Toasts);

        rig.Health.ReportIssue("ui.unhandled-exception", HealthLevel.Faulted, "Bosun hit an unexpected error", "detail");

        Assert.Equal(("Bosun", "Bosun hit an unexpected error"), Assert.Single(rig.Toasts));
    }

    [Fact]
    public void A_reported_Faulted_issue_inside_the_grace_period_waits_for_it_to_end()
    {
        using var rig = new Rig();
        rig.Health.ObserveRclone(RcloneProcessStatus.Starting, RcloneProcessFaultKind.None, null);

        rig.Health.ReportIssue("ui.unhandled-exception", HealthLevel.Faulted, "Bosun hit an unexpected error", "detail");
        Assert.Empty(rig.Toasts);

        rig.Health.ObserveRclone(RcloneProcessStatus.Healthy, RcloneProcessFaultKind.None, null);

        Assert.Equal(("Bosun", "Bosun hit an unexpected error"), Assert.Single(rig.Toasts));
    }
}
