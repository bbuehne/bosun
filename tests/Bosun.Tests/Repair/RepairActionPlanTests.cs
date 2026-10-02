using System.Windows.Input;
using Bosun.Health;
using Bosun.Repair;
using Bosun.Status;
using Bosun.UI.Banner;

namespace Bosun.Tests.Repair;

/// <summary>
/// Which repair buttons the health banner shows for which issue, and in what order (bs-aoz). Pure: no
/// WPF, no dialogs, no supervisor.
/// </summary>
public sealed class RepairActionPlanTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 14, 3, TimeSpan.Zero);

    private static readonly RepairActionKind[] RcloneFirst =
        [RepairActionKind.RestartRclone, RepairActionKind.UnmountAllAndReprobe, RepairActionKind.RestartBosun];

    private static readonly RepairActionKind[] BosunFirst =
        [RepairActionKind.RestartBosun, RepairActionKind.RestartRclone, RepairActionKind.UnmountAllAndReprobe];

    [Theory]
    [InlineData(HealthIssueCodes.RcloneExecutableNotFound)]
    [InlineData(HealthIssueCodes.RcloneLaunchFailed)]
    [InlineData(HealthIssueCodes.RcloneHealthCheckFailed)]
    [InlineData(HealthIssueCodes.RcloneUnauthorized)]
    [InlineData(HealthIssueCodes.RcloneExitedBeforeHealthy)]
    [InlineData(HealthIssueCodes.RclonePortHeld)]
    [InlineData(HealthIssueCodes.RcloneExitedUnexpectedly)]
    [InlineData(HealthIssueCodes.RcloneNotRunning)]
    public void Rclone_issues_lead_with_Restart_rclone(string code)
    {
        Assert.Equal(RcloneFirst, RepairActionPlan.ForIssue(code));
    }

    [Theory]
    [InlineData(HealthIssueCodes.SupervisorLoopStopped)]
    [InlineData(HealthIssueCodes.WatchdogSupervisorStalled)]
    [InlineData(HealthIssueCodes.WatchdogRestartLimit)]
    [InlineData(HealthIssueCodes.StartupConfigInvalid)]
    [InlineData(HealthIssueCodes.StartupWinFspMissing)]
    [InlineData(HealthIssueCodes.StartupTerminalFragment)]
    [InlineData(HealthIssueCodes.StartupSupervisorFailed)]
    [InlineData("some.future-code")]
    public void Supervisor_watchdog_startup_and_unknown_issues_lead_with_Restart_Bosun(string code)
    {
        Assert.Equal(BosunFirst, RepairActionPlan.ForIssue(code));
    }

    [Fact]
    public void The_restart_notice_leads_with_Dismiss_and_still_offers_every_repair()
    {
        Assert.Equal(
            [
                RepairActionKind.DismissNotice,
                RepairActionKind.RestartBosun,
                RepairActionKind.RestartRclone,
                RepairActionKind.UnmountAllAndReprobe,
            ],
            RepairActionPlan.ForIssue(HealthIssueCodes.WatchdogRestarted));
    }

    [Theory]
    [InlineData(HealthIssueCodes.RclonePortHeld)]
    [InlineData(HealthIssueCodes.SupervisorLoopStopped)]
    [InlineData(HealthIssueCodes.WatchdogRestarted)]
    [InlineData("some.future-code")]
    public void Every_issue_offers_all_three_repairs_whatever_the_order(string code)
    {
        var plan = RepairActionPlan.ForIssue(code);

        Assert.Contains(RepairActionKind.RestartRclone, plan);
        Assert.Contains(RepairActionKind.UnmountAllAndReprobe, plan);
        Assert.Contains(RepairActionKind.RestartBosun, plan);
        Assert.Equal(plan.Count, plan.Distinct().Count());
    }

    [Fact]
    public void Dismiss_is_offered_only_for_the_restart_notice()
    {
        foreach (var code in new[] { HealthIssueCodes.RclonePortHeld, HealthIssueCodes.SupervisorLoopStopped, HealthIssueCodes.WatchdogSupervisorStalled })
        {
            Assert.DoesNotContain(RepairActionKind.DismissNotice, RepairActionPlan.ForIssue(code));
        }
    }

    [Fact]
    public void No_issue_means_no_buttons()
    {
        Assert.Empty(RepairActionPlan.ForIssue(null));
    }

    [Fact]
    public void The_tray_menu_order_is_fixed_and_has_no_Dismiss()
    {
        Assert.Equal(
            [RepairActionKind.RestartRclone, RepairActionKind.UnmountAllAndReprobe, RepairActionKind.RestartBosun],
            RepairActionPlan.TrayOrder);
    }

    [Theory]
    [InlineData(RepairActionKind.RestartRclone, "Restart rclone")]
    [InlineData(RepairActionKind.UnmountAllAndReprobe, "Unmount all & re-probe")]
    [InlineData(RepairActionKind.RestartBosun, "Restart Bosun")]
    [InlineData(RepairActionKind.DismissNotice, "Dismiss")]
    public void Labels_name_the_action(RepairActionKind kind, string label)
    {
        Assert.Equal(label, RepairActionPlan.Label(kind));
    }

    // -- the banner view model applies the plan --------------------------------------------------

    private static HealthIssue Issue(string code, HealthLevel severity = HealthLevel.Faulted) =>
        new(code, severity, $"title of {code}", "detail", T0);

    private static AppHealth Health(params HealthIssue[] issues) => new()
    {
        Level = issues.Length == 0 ? HealthLevel.Ok : issues.Max(i => i.Severity),
        Issues = issues,
    };

    private static (HealthBannerViewModel ViewModel, List<RepairActionKind> Executed) NewViewModel()
    {
        var executed = new List<RepairActionKind>();
        var vm = new HealthBannerViewModel(TimeZoneInfo.Utc);
        vm.UseActions(kind => new RelayCommand(() => executed.Add(kind)));
        return (vm, executed);
    }

    [Fact]
    public void The_banner_lists_the_buttons_for_the_top_issue_in_order()
    {
        var (vm, _) = NewViewModel();

        vm.Update(Health(Issue(HealthIssueCodes.RclonePortHeld)), AggregateHealth.Healthy);

        Assert.Equal(["Restart rclone", "Unmount all & re-probe", "Restart Bosun"], vm.Actions.Select(a => a.Label));
    }

    [Fact]
    public void A_different_top_issue_reorders_the_buttons()
    {
        var (vm, _) = NewViewModel();
        vm.Update(Health(Issue(HealthIssueCodes.RclonePortHeld)), AggregateHealth.Healthy);

        vm.Update(Health(Issue(HealthIssueCodes.SupervisorLoopStopped)), AggregateHealth.Healthy);

        Assert.Equal(["Restart Bosun", "Restart rclone", "Unmount all & re-probe"], vm.Actions.Select(a => a.Label));
    }

    [Fact]
    public void The_buttons_are_always_there_while_the_banner_is_visible_and_gone_when_it_is_not()
    {
        var (vm, _) = NewViewModel();

        vm.Update(Health(Issue(HealthIssueCodes.RclonePortHeld)), AggregateHealth.Healthy);
        Assert.True(vm.IsVisible);
        Assert.Equal(3, vm.Actions.Count);

        vm.Update(AppHealth.Healthy, AggregateHealth.Healthy);
        Assert.False(vm.IsVisible);
        Assert.Empty(vm.Actions);
    }

    [Fact]
    public void Each_button_runs_its_own_action()
    {
        var (vm, executed) = NewViewModel();
        vm.Update(Health(Issue(HealthIssueCodes.WatchdogRestarted, HealthLevel.Degraded)), AggregateHealth.Healthy);

        foreach (var action in vm.Actions)
        {
            action.Command.Execute(null);
        }

        Assert.Equal(
            [
                RepairActionKind.DismissNotice,
                RepairActionKind.RestartBosun,
                RepairActionKind.RestartRclone,
                RepairActionKind.UnmountAllAndReprobe,
            ],
            executed);
    }

    [Fact]
    public void An_unchanged_refresh_keeps_the_same_buttons_so_they_do_not_flicker()
    {
        var (vm, _) = NewViewModel();
        var health = Health(Issue(HealthIssueCodes.RclonePortHeld));
        vm.Update(health, AggregateHealth.Healthy);
        var first = vm.Actions.ToList();
        var changes = 0;
        vm.Actions.CollectionChanged += (_, _) => changes++;

        vm.Update(health, AggregateHealth.Healthy);
        vm.Update(health, AggregateHealth.Healthy);

        Assert.Equal(0, changes);
        Assert.Equal(first, vm.Actions.ToList());
    }

    [Fact]
    public void A_changed_second_issue_does_not_rebuild_the_buttons()
    {
        var (vm, _) = NewViewModel();
        vm.Update(Health(Issue(HealthIssueCodes.RclonePortHeld), Issue(HealthIssueCodes.StartupWinFspMissing)), AggregateHealth.Healthy);
        var changes = 0;
        vm.Actions.CollectionChanged += (_, _) => changes++;

        vm.Update(Health(Issue(HealthIssueCodes.RclonePortHeld), Issue(HealthIssueCodes.StartupTerminalFragment, HealthLevel.Degraded)), AggregateHealth.Healthy);

        Assert.Equal(0, changes);
    }

    [Fact]
    public void Without_a_command_source_there_are_no_buttons()
    {
        var vm = new HealthBannerViewModel(TimeZoneInfo.Utc);

        vm.Update(Health(Issue(HealthIssueCodes.RclonePortHeld)), AggregateHealth.Healthy);

        Assert.Empty(vm.Actions);
    }

    [Fact]
    public void Supplying_commands_after_the_banner_is_already_showing_fills_the_buttons()
    {
        var vm = new HealthBannerViewModel(TimeZoneInfo.Utc);
        vm.Update(Health(Issue(HealthIssueCodes.RclonePortHeld)), AggregateHealth.Healthy);

        vm.UseActions(_ => new RelayCommand(() => { }));

        Assert.Equal(3, vm.Actions.Count);
        ICommand command = vm.Actions[0].Command;
        Assert.True(command.CanExecute(null));
    }
}
