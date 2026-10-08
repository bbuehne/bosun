using Bosun.Repair;
using Bosun.Tests.Configuration.Fakes;
using Bosun.Tests.Supervisor.Support;
using Bosun.Tests.UI.Tray.Fakes;
using Bosun.Tests.Watchdog;
using Bosun.UI.Tray;
using Bosun.Watchdog;

namespace Bosun.Tests.UI.Tray;

/// <summary>
/// The tray's Repair menu and the health banner both call <see cref="HostActionDispatcher.Repair"/>
/// (ADR-018: one command path), and the dispatcher reaches the supervisor only through
/// <c>IMountSupervisor</c> (Invariant I4). Bs-aoz.
/// </summary>
public sealed class RepairDispatchTests
{
    private sealed class Prompt : IRepairPrompt
    {
        public Task<RepairConfirmation> ConfirmAsync(string title, string message) =>
            Task.FromResult(RepairConfirmation.Confirmed);

        public void ShowError(string title, string message)
        {
        }
    }

    private sealed class RcloneRestarter : Bosun.Rclone.Process.IRcloneProcessRestarter
    {
        public int Calls { get; private set; }

        public Task<bool> RestartAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(true);
        }
    }

    private sealed class Notice : IRestartNotice
    {
        public int Dismissals { get; private set; }

        public void Dismiss() => Dismissals++;
    }

    private static (HostActionDispatcher Dispatcher, FakeMountSupervisor Supervisor, RcloneRestarter Rclone, FakeRestarter App, Notice Notice) Create()
    {
        var supervisor = new FakeMountSupervisor();
        var rclone = new RcloneRestarter();
        var app = new FakeRestarter();
        var notice = new Notice();
        var commands = new RepairCommands(supervisor, () => rclone, () => app, new Prompt(), new FakeTimeProvider(), health: null, new CapturingLogger<RepairCommands>());
        var dispatcher = new HostActionDispatcher(
            supervisor, new FakeExternalLauncher(), logger: null, diagnostics: null, repair: commands, restartNotice: notice);
        return (dispatcher, supervisor, rclone, app, notice);
    }

    [Fact]
    public void Restart_rclone_reaches_the_rclone_restarter_and_nothing_on_the_supervisor()
    {
        var (dispatcher, supervisor, rclone, app, _) = Create();

        dispatcher.Repair(RepairActionKind.RestartRclone);

        Assert.Equal(1, rclone.Calls);
        Assert.Equal(0, supervisor.RepairAllCalls);
        Assert.Empty(supervisor.MountRequests);
        Assert.Empty(supervisor.UnmountRequests);
        Assert.Empty(app.Reasons);
    }

    [Fact]
    public void Unmount_all_reaches_the_supervisor_through_RepairAllAsync_only()
    {
        var (dispatcher, supervisor, rclone, app, _) = Create();

        dispatcher.Repair(RepairActionKind.UnmountAllAndReprobe);

        Assert.Equal(1, supervisor.RepairAllCalls);
        Assert.Empty(supervisor.UnmountRequests);
        Assert.Equal(0, rclone.Calls);
        Assert.Empty(app.Reasons);
    }

    [Fact]
    public void Restart_Bosun_asks_for_a_manual_restart()
    {
        var (dispatcher, _, _, app, _) = Create();

        dispatcher.Repair(RepairActionKind.RestartBosun);

        Assert.Equal([RestartKind.Manual], app.Kinds);
    }

    [Fact]
    public void Dismiss_is_not_a_repair_and_dismisses_the_notice()
    {
        var (dispatcher, supervisor, rclone, app, notice) = Create();

        dispatcher.Repair(RepairActionKind.DismissNotice);

        Assert.Equal(1, notice.Dismissals);
        Assert.Equal(0, supervisor.RepairAllCalls);
        Assert.Equal(0, rclone.Calls);
        Assert.Empty(app.Reasons);
    }

    [Fact]
    public void Without_repair_commands_a_repair_click_does_nothing_and_does_not_throw()
    {
        var dispatcher = new HostActionDispatcher(new FakeMountSupervisor(), new FakeExternalLauncher());

        dispatcher.Repair(RepairActionKind.RestartBosun);
        dispatcher.Repair(RepairActionKind.DismissNotice);
    }

    [Fact]
    public void A_double_click_on_the_banner_button_runs_the_repair_once()
    {
        // The banner button and the tray item both end here: the in-flight guard lives in the command,
        // so it holds whichever surface the clicks come from.
        var (dispatcher, supervisor, _, _, _) = Create();
        var gate = new TaskCompletionSource();
        supervisor.RepairAllBlocker = gate.Task;

        dispatcher.Repair(RepairActionKind.UnmountAllAndReprobe);
        dispatcher.Repair(RepairActionKind.UnmountAllAndReprobe);
        dispatcher.Repair(RepairActionKind.UnmountAllAndReprobe);

        Assert.Equal(1, supervisor.RepairAllCalls);
        gate.SetResult();
    }
}
