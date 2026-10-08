using Bosun.Health;
using Bosun.Rclone.Process;
using Bosun.Repair;
using Bosun.Supervisor;
using Bosun.Tests.Configuration.Fakes;
using Bosun.Tests.Supervisor.Support;
using Bosun.Tests.UI.Tray.Fakes;
using Bosun.Tests.Watchdog;
using Bosun.Watchdog;
using Microsoft.Extensions.Logging;

namespace Bosun.Tests.Repair;

/// <summary>
/// The three one-click repairs (bs-aoz): what each calls, when each confirms, that a second click
/// while one is running is ignored, and that Restart Bosun is a manual restart that never touches the
/// watchdog's history. Everything is faked: no process, no mount, no dialog.
/// </summary>
public sealed class RepairCommandsTests
{
    private static HostMountSnapshot Mounted(string key = "nas", string drive = "P:") => new()
    {
        HostKey = key,
        State = MountState.Mounted,
        Drive = drive,
        AdministrativelyEnabled = true,
    };

    private sealed class Harness
    {
        public FakeMountSupervisor Supervisor { get; } = new();
        public FakeRcloneRestarter Rclone { get; } = new();
        public FakeRestarter AppRestarter { get; } = new();
        public FakePrompt Prompt { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public CapturingLogger<RepairCommands> Log { get; } = new();
        public bool RcloneAvailable { get; set; } = true;
        public bool AppRestarterAvailable { get; set; } = true;
        public RepairCommands Commands { get; }

        public Harness()
        {
            Commands = new RepairCommands(
                Supervisor,
                () => RcloneAvailable ? Rclone : null,
                () => AppRestarterAvailable ? AppRestarter : null,
                Prompt,
                Time,
                health: null,
                Log);
        }

        public bool LoggedInformation(string fragment) =>
            Log.Entries.Any(e => e.Level == LogLevel.Information && e.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    // -- Restart rclone -------------------------------------------------------------------------

    [Fact]
    public async Task Restart_rclone_asks_the_rclone_restarter_once()
    {
        var h = new Harness();

        await h.Commands.RestartRcloneAsync();

        Assert.Equal(1, h.Rclone.Calls);
        Assert.True(h.LoggedInformation("restarting rclone"));
    }

    [Fact]
    public async Task Restart_rclone_confirms_only_when_mounted_drives_would_be_dropped()
    {
        var quiet = new Harness();
        await quiet.Commands.RestartRcloneAsync();
        Assert.Empty(quiet.Prompt.Confirmations); // nothing mounted: nothing to warn about

        var busy = new Harness();
        busy.Supervisor.Snapshot = [Mounted("nas", "P:"), Mounted("backup", "R:")];
        await busy.Commands.RestartRcloneAsync();
        var confirmation = Assert.Single(busy.Prompt.Confirmations);
        Assert.Contains("P:", confirmation.Message, StringComparison.Ordinal);
        Assert.Contains("R:", confirmation.Message, StringComparison.Ordinal);
        Assert.Equal(1, busy.Rclone.Calls);
    }

    [Fact]
    public async Task Declining_the_confirmation_restarts_nothing()
    {
        var h = new Harness();
        h.Supervisor.Snapshot = [Mounted()];
        h.Prompt.Answer = false;

        await h.Commands.RestartRcloneAsync();

        Assert.Equal(0, h.Rclone.Calls);
        Assert.True(h.LoggedInformation("declined"));
        Assert.True(h.LoggedInformation("the repair did not run"));
    }

    // -- What the log says when a confirmation did not lead to a repair (bs-3hx) ---------------

    public static TheoryData<string> AllRepairs() => ["rclone", "unmount", "bosun"];

    private static Task RunRepair(Harness h, string which) => which switch
    {
        "rclone" => h.Commands.RestartRcloneAsync(),
        "unmount" => h.Commands.UnmountAllAsync(),
        _ => h.Commands.RestartBosunAsync(),
    };

    private static int RepairCalls(Harness h, string which) => which switch
    {
        "rclone" => h.Rclone.Calls,
        "unmount" => h.Supervisor.RepairAllCalls,
        _ => h.AppRestarter.Reasons.Count,
    };

    [Theory]
    [MemberData(nameof(AllRepairs))]
    public async Task An_explicit_No_is_logged_as_declined_and_the_repair_does_not_run(string which)
    {
        var h = new Harness();
        h.Supervisor.Snapshot = [Mounted()];
        h.Prompt.Result = RepairConfirmation.Declined;

        await RunRepair(h, which);

        Assert.Equal(0, RepairCalls(h, which));
        var entry = Assert.Single(h.Log.Entries, e => e.Message.Contains("declined", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("answered No", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(h.Log.Entries, e => e.Message.Contains("cancelled by the user", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [MemberData(nameof(AllRepairs))]
    public async Task A_dialog_that_closed_without_an_answer_is_never_logged_as_the_users_decision(string which)
    {
        var h = new Harness();
        h.Supervisor.Snapshot = [Mounted()];
        h.Prompt.Result = RepairConfirmation.NoAnswer;

        await RunRepair(h, which);

        Assert.Equal(0, RepairCalls(h, which));
        var entry = Assert.Single(h.Log.Entries, e => e.Message.Contains("not confirmed", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, entry.Level); // worth seeing: the user may think they asked for it
        Assert.Contains("closed without an answer", entry.Message, StringComparison.Ordinal);
        Assert.Contains("the repair did not run", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(h.Log.Entries, e =>
            e.Message.Contains("by the user", StringComparison.OrdinalIgnoreCase)
            || e.Message.Contains("cancelled", StringComparison.OrdinalIgnoreCase)
            || e.Message.Contains("declined", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [MemberData(nameof(AllRepairs))]
    public async Task Yes_leads_to_the_repair_running_and_no_not_confirmed_line(string which)
    {
        var h = new Harness();
        h.Supervisor.Snapshot = [Mounted()];
        h.Prompt.Result = RepairConfirmation.Confirmed;

        await RunRepair(h, which);

        Assert.Equal(1, RepairCalls(h, which));
        Assert.DoesNotContain(h.Log.Entries, e =>
            e.Message.Contains("not confirmed", StringComparison.Ordinal) || e.Message.Contains("declined", StringComparison.Ordinal));
        Assert.Contains(h.Log.Entries, e => e.Message.StartsWith("Repair:", StringComparison.Ordinal) && e.Message.Contains("at the user's request", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Restart_rclone_before_rclone_was_ever_started_says_so_and_does_nothing()
    {
        var h = new Harness { RcloneAvailable = false };

        await h.Commands.RestartRcloneAsync();

        Assert.Equal(0, h.Rclone.Calls);
        Assert.Single(h.Prompt.Errors);
    }

    [Fact]
    public async Task A_restart_that_does_not_become_healthy_is_logged_and_not_a_dialog()
    {
        // The health banner already names the real cause (port held, 401, ...); a second dialog would
        // only repeat it, less accurately.
        var h = new Harness();
        h.Rclone.Result = false;

        await h.Commands.RestartRcloneAsync();

        Assert.Empty(h.Prompt.Errors);
        Assert.Contains(h.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("did not become healthy", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_second_Restart_rclone_click_while_one_is_running_is_ignored()
    {
        var h = new Harness();
        var gate = new TaskCompletionSource<bool>();
        h.Rclone.Blocker = gate.Task;

        var first = h.Commands.RestartRcloneAsync();
        Assert.True(h.Commands.IsRestartingRclone);
        await h.Commands.RestartRcloneAsync(); // the double click
        await h.Commands.RestartRcloneAsync();

        Assert.Equal(1, h.Rclone.Calls);

        gate.SetResult(true);
        await first;

        Assert.False(h.Commands.IsRestartingRclone);
        await h.Commands.RestartRcloneAsync(); // after it finished, a new request is honoured
        Assert.Equal(2, h.Rclone.Calls);
    }

    [Fact]
    public async Task A_throwing_restart_releases_the_guard_and_tells_the_user()
    {
        var h = new Harness();
        h.Rclone.Throws = new InvalidOperationException("boom");

        await h.Commands.RestartRcloneAsync();

        Assert.False(h.Commands.IsRestartingRclone);
        Assert.Contains("boom", Assert.Single(h.Prompt.Errors).Message, StringComparison.Ordinal);
    }

    // -- Unmount all & re-probe -----------------------------------------------------------------

    [Fact]
    public async Task Unmount_all_goes_through_the_supervisor_and_calls_nothing_else_on_it()
    {
        var h = new Harness();
        h.Supervisor.Snapshot = [Mounted()];

        await h.Commands.UnmountAllAsync();

        Assert.Equal(1, h.Supervisor.RepairAllCalls);
        Assert.Empty(h.Supervisor.UnmountRequests); // not 'unmount each host': that would park them (ADR-015)
        Assert.Empty(h.Supervisor.MountRequests);
        Assert.Equal(0, h.Rclone.Calls);
        Assert.True(h.LoggedInformation("unmount all and re-probe"));
    }

    [Fact]
    public async Task Unmount_all_confirms_when_a_drive_is_mounted_and_not_otherwise()
    {
        var quiet = new Harness();
        await quiet.Commands.UnmountAllAsync();
        Assert.Empty(quiet.Prompt.Confirmations);
        Assert.Equal(1, quiet.Supervisor.RepairAllCalls); // still re-probes

        var busy = new Harness();
        busy.Supervisor.Snapshot = [Mounted()];
        busy.Prompt.Answer = false;
        await busy.Commands.UnmountAllAsync();
        Assert.Single(busy.Prompt.Confirmations);
        Assert.Equal(0, busy.Supervisor.RepairAllCalls);
    }

    [Fact]
    public async Task A_second_Unmount_all_click_while_one_is_running_is_ignored()
    {
        var h = new Harness();
        var gate = new TaskCompletionSource();
        h.Supervisor.RepairAllBlocker = gate.Task;

        var first = h.Commands.UnmountAllAsync();
        await h.Commands.UnmountAllAsync();
        await h.Commands.UnmountAllAsync();

        Assert.Equal(1, h.Supervisor.RepairAllCalls);
        Assert.True(h.Commands.IsUnmountingAll);

        gate.SetResult();
        await first;
        Assert.False(h.Commands.IsUnmountingAll);
    }

    [Fact]
    public async Task A_supervisor_that_never_answers_is_given_up_on_after_the_timeout_and_the_user_is_told()
    {
        var h = new Harness();
        h.Supervisor.RepairAllBlocker = new TaskCompletionSource().Task; // never completes

        var command = h.Commands.UnmountAllAsync();
        h.Time.Advance(RepairCommands.SupervisorTimeout);
        await command;

        Assert.False(h.Commands.IsUnmountingAll); // the guard is released: Restart Bosun is the way out, and the button works again
        var error = Assert.Single(h.Prompt.Errors);
        Assert.Contains("Restart Bosun", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stopped_supervisor_is_reported_not_swallowed()
    {
        var h = new Harness();
        h.Supervisor.RepairAllException = new SupervisorStoppedException();

        await h.Commands.UnmountAllAsync();

        Assert.Single(h.Prompt.Errors);
        Assert.False(h.Commands.IsUnmountingAll);
    }

    [Fact]
    public async Task A_stuck_Unmount_all_does_not_stop_the_user_restarting_Bosun()
    {
        var h = new Harness();
        h.Supervisor.RepairAllBlocker = new TaskCompletionSource().Task;

        var stuck = h.Commands.UnmountAllAsync();
        await h.Commands.RestartBosunAsync();

        Assert.Single(h.AppRestarter.Reasons);
        Assert.True(h.Commands.IsUnmountingAll);
        GC.KeepAlive(stuck);
    }

    // -- Restart Bosun --------------------------------------------------------------------------

    [Fact]
    public async Task Restart_Bosun_always_confirms_and_asks_for_a_manual_restart()
    {
        var h = new Harness();

        await h.Commands.RestartBosunAsync();

        Assert.Single(h.Prompt.Confirmations);
        Assert.Equal([RestartKind.Manual], h.AppRestarter.Kinds);
        Assert.True(h.LoggedInformation("not counted against the watchdog's limit"));
    }

    [Fact]
    public async Task Declining_Restart_Bosun_restarts_nothing()
    {
        var h = new Harness();
        h.Prompt.Answer = false;

        await h.Commands.RestartBosunAsync();

        Assert.Empty(h.AppRestarter.Reasons);
    }

    [Fact]
    public async Task A_restart_that_could_not_launch_a_replacement_tells_the_user_and_releases_the_guard()
    {
        var h = new Harness();
        h.AppRestarter.Result = false;

        await h.Commands.RestartBosunAsync();

        Assert.Single(h.Prompt.Errors);
        Assert.False(h.Commands.IsRestartingBosun);
    }

    [Fact]
    public async Task A_second_Restart_Bosun_click_while_one_is_running_is_ignored()
    {
        var h = new Harness();
        var gate = new TaskCompletionSource<bool>();
        h.AppRestarter.OnRequest = () => { };
        var blockingRestarter = new BlockingAppRestarter(gate.Task);
        var commands = new RepairCommands(
            h.Supervisor, () => h.Rclone, () => blockingRestarter, h.Prompt, h.Time, health: null, h.Log);

        var first = commands.RestartBosunAsync();
        await commands.RestartBosunAsync();
        await commands.RestartBosunAsync();

        Assert.Equal(1, blockingRestarter.Calls);
        Assert.Single(h.Prompt.Confirmations); // the ignored clicks did not even ask again

        gate.SetResult(true);
        await first;
    }

    [Fact]
    public async Task A_manual_restart_through_the_real_restarter_launches_the_user_flag_and_never_writes_the_watchdog_history()
    {
        // The real ProcessAppRestarter, over fakes. The watchdog's allowance is already used up: a
        // manual restart must go ahead anyway and must not be recorded, or a person restarting Bosun
        // could lock themselves out of (or use up) the protection against a restart loop.
        var history = new InMemoryRestartHistory { Stored = [DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch] };
        var launcher = new FakeLauncher();
        var shutdown = new FakeApplicationShutdown();
        var time = new FakeTimeProvider();
        var guard = new ShutdownGuard(time, new FakeProcessExiter(), () => null);
        var restarter = new ProcessAppRestarter(
            new RestartContext(@"C:\Apps\Bosun\Bosun.exe", ["--autostart"], ProcessId: 777),
            launcher, shutdown, guard, new CapturingLogger<ProcessAppRestarter>());
        var h = new Harness();
        var commands = new RepairCommands(h.Supervisor, () => h.Rclone, () => restarter, h.Prompt, h.Time, health: null, h.Log);

        await commands.RestartBosunAsync();

        var launch = Assert.Single(launcher.Launches);
        Assert.Equal(["--restarted-by-user", "777"], launch.Args); // --autostart dropped: the window will show
        Assert.Equal(1, shutdown.Requests);
        Assert.Equal(0, history.SaveCount);
        Assert.Equal(3, history.Stored.Count);
    }

    // -- Every repair says what it did and why --------------------------------------------------

    [Fact]
    public async Task Every_repair_logs_at_Information_what_it_did()
    {
        var h = new Harness();

        await h.Commands.RestartRcloneAsync();
        await h.Commands.UnmountAllAsync();
        await h.Commands.RestartBosunAsync();

        Assert.True(h.LoggedInformation("restarting rclone at the user's request"));
        Assert.True(h.LoggedInformation("unmount all and re-probe at the user's request"));
        Assert.True(h.LoggedInformation("restarting Bosun at the user's request"));
    }

    [Fact]
    public async Task The_log_line_includes_the_health_the_user_was_looking_at()
    {
        var health = new AppHealthService(new FakeTimeProvider(), new AppHealthOptions { StartupGracePeriod = TimeSpan.Zero });
        health.ReportIssue(HealthIssueCodes.RclonePortHeld, HealthLevel.Faulted, "port held", "PID 1");
        var h = new Harness();
        var commands = new RepairCommands(h.Supervisor, () => h.Rclone, () => h.AppRestarter, h.Prompt, h.Time, health, h.Log);

        await commands.RestartRcloneAsync();

        Assert.True(h.LoggedInformation(HealthIssueCodes.RclonePortHeld));
        health.Dispose();
    }

    // -- fakes ----------------------------------------------------------------------------------

    private sealed class FakeRcloneRestarter : IRcloneProcessRestarter
    {
        public int Calls { get; private set; }

        public bool Result { get; set; } = true;

        public Exception? Throws { get; set; }

        public Task<bool>? Blocker { get; set; }

        public Task<bool> RestartAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Throws is not null)
            {
                return Task.FromException<bool>(Throws);
            }

            return Blocker ?? Task.FromResult(Result);
        }
    }

    private sealed class BlockingAppRestarter(Task<bool> blocker) : IAppRestarter
    {
        public int Calls { get; private set; }

        public Task<bool> RestartAsync(string reason, RestartKind kind, CancellationToken cancellationToken)
        {
            Calls++;
            return blocker;
        }
    }

    private sealed class FakePrompt : IRepairPrompt
    {
        public RepairConfirmation Result { get; set; } = RepairConfirmation.Confirmed;

        /// <summary>Shorthand kept from before the dialog had three outcomes: false is an explicit No.</summary>
        public bool Answer
        {
            set => Result = value ? RepairConfirmation.Confirmed : RepairConfirmation.Declined;
        }

        public List<(string Title, string Message)> Confirmations { get; } = [];

        public List<(string Title, string Message)> Errors { get; } = [];

        public Task<RepairConfirmation> ConfirmAsync(string title, string message)
        {
            Confirmations.Add((title, message));
            return Task.FromResult(Result);
        }

        public void ShowError(string title, string message) => Errors.Add((title, message));
    }
}
