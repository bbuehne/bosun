using Bosun.Tests.Configuration.Fakes;
using Bosun.Tests.Supervisor.Support;
using Bosun.Watchdog;
using Microsoft.Extensions.Logging;

namespace Bosun.Tests.Watchdog;

/// <summary>
/// bs-6to: the restart itself (what is launched, in what order) and the new instance's half of the
/// single-instance handoff. Everything is faked: no process is started, none is inspected, no
/// application is shut down.
/// </summary>
public sealed class RestartMechanicsTests
{
    private const string Exe = @"C:\Apps\Bosun\Bosun.exe";

    // ------------------------------------------------------------------------------------------
    // The launch
    // ------------------------------------------------------------------------------------------

    private sealed class RestarterHarness
    {
        public FakeLauncher Launcher { get; } = new();
        public FakeApplicationShutdown AppShutdown { get; } = new();
        public FakeProcessExiter Exiter { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public ShutdownGuard Guard { get; }
        public ProcessAppRestarter Restarter { get; }
        public CapturingLogger<ProcessAppRestarter> Log { get; } = new();

        public RestarterHarness(IReadOnlyList<string>? args = null)
        {
            Guard = new ShutdownGuard(Time, Exiter, () => null);
            Restarter = new ProcessAppRestarter(
                new RestartContext(Exe, args ?? ["--autostart"], ProcessId: 4242),
                Launcher, AppShutdown, Guard, Log);
        }
    }

    [Fact]
    public async Task The_restart_launches_the_same_exe_with_the_same_arguments_plus_the_handoff_flag()
    {
        var h = new RestarterHarness(["--autostart"]);

        var restarted = await h.Restarter.RestartAsync("supervisor stalled", RestartKind.Watchdog, CancellationToken.None);

        Assert.True(restarted);
        var launch = Assert.Single(h.Launcher.Launches);
        Assert.Equal(Exe, launch.Exe);
        Assert.Equal(["--autostart", "--restarted-by-watchdog", "4242"], launch.Args);
    }

    [Fact]
    public async Task The_replacement_is_launched_before_this_instance_is_asked_to_shut_down()
    {
        var h = new RestarterHarness();
        var shutdownRequestedWhenLaunching = -1;
        h.Launcher.Throws = null;
        var launcher = new OrderRecordingLauncher(h.AppShutdown, count => shutdownRequestedWhenLaunching = count);
        var restarter = new ProcessAppRestarter(
            new RestartContext(Exe, [], 1), launcher, h.AppShutdown, h.Guard, h.Log);

        await restarter.RestartAsync("x", RestartKind.Watchdog, CancellationToken.None);

        Assert.Equal(0, shutdownRequestedWhenLaunching);
        Assert.Equal(1, h.AppShutdown.Requests);
        Assert.True(h.Guard.IsShuttingDown);
    }

    private sealed class OrderRecordingLauncher(FakeApplicationShutdown shutdown, Action<int> onLaunch) : IBosunProcessLauncher
    {
        public void Start(string exePath, IReadOnlyList<string> arguments) => onLaunch(shutdown.Requests);
    }

    [Fact]
    public async Task A_launch_that_fails_leaves_this_instance_running()
    {
        var h = new RestarterHarness();
        h.Launcher.Throws = new InvalidOperationException("cannot start");

        var restarted = await h.Restarter.RestartAsync("x", RestartKind.Watchdog, CancellationToken.None);

        Assert.False(restarted);
        Assert.Equal(0, h.AppShutdown.Requests);
        Assert.False(h.Guard.IsShuttingDown);
        Assert.Contains(h.Log.Entries, e => e.Level == LogLevel.Error && e.Exception is InvalidOperationException);
    }

    [Fact]
    public async Task Nothing_is_launched_when_the_application_is_already_exiting()
    {
        var h = new RestarterHarness();
        h.Guard.BeginShutdown("user chose Exit");

        var restarted = await h.Restarter.RestartAsync("x", RestartKind.Watchdog, CancellationToken.None);

        Assert.False(restarted);
        Assert.Empty(h.Launcher.Launches);
        Assert.Equal(0, h.AppShutdown.Requests);
    }

    [Fact]
    public async Task A_restart_arms_the_exit_deadline_so_a_wedged_instance_cannot_outlive_it()
    {
        var h = new RestarterHarness();
        await h.Restarter.RestartAsync("x", RestartKind.Watchdog, CancellationToken.None);

        // The orderly shutdown never finishes (nothing in this test completes it).
        h.Time.Advance(ShutdownGuard.DefaultBound);

        Assert.Equal([ShutdownGuard.ForcedExitCode], h.Exiter.ExitCodes);
    }

    // ------------------------------------------------------------------------------------------
    // The argument contract
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void An_earlier_handoff_flag_is_replaced_not_accumulated()
    {
        var args = RestartHandoffArguments.ForRestart(["--autostart", "--restarted-by-watchdog", "111", "--other"], 222);

        Assert.Equal(["--autostart", "--other", "--restarted-by-watchdog", "222"], args);
    }

    [Theory]
    [InlineData(new[] { "--restarted-by-watchdog", "123" }, 123)]
    [InlineData(new[] { "--autostart", "--restarted-by-watchdog", "123" }, 123)]
    [InlineData(new[] { "--RESTARTED-BY-WATCHDOG=77" }, 77)]
    public void The_old_process_id_is_read_from_the_flag(string[] args, int expected)
    {
        Assert.True(RestartHandoffArguments.TryGetOldProcessId(args, out var pid));
        Assert.Equal(expected, pid);
    }

    public static TheoryData<string[]> MalformedOrAbsentFlags => new()
    {
        Array.Empty<string>(),
        new[] { "--autostart" },
        new[] { "--restarted-by-watchdog" },
        new[] { "--restarted-by-watchdog", "abc" },
        new[] { "--restarted-by-watchdog", "0" },
        new[] { "--restarted-by-watchdog", "-5" },
    };

    [Theory]
    [MemberData(nameof(MalformedOrAbsentFlags))]
    public void No_old_process_id_is_reported_when_the_flag_is_absent_or_malformed(string[] args)
    {
        Assert.False(RestartHandoffArguments.TryGetOldProcessId(args, out var pid));
        Assert.Equal(0, pid);
    }

    // ------------------------------------------------------------------------------------------
    // The new instance's handoff wait
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_new_instance_proceeds_at_once_when_the_old_one_is_already_gone()
    {
        var time = new FakeTimeProvider();
        var probe = new FakeProcessExitProbe(time) { ExitsAt = time.GetUtcNow() };
        var waiter = new OldInstanceWaiter(probe, time);

        var result = await waiter.WaitForExitAsync(4242, OldInstanceWaiter.DefaultTimeout);

        Assert.Equal(HandoffWaitResult.OldInstanceExited, result);
        Assert.Equal(1, probe.Polls);
    }

    [Fact]
    public async Task The_new_instance_waits_for_the_old_one_to_exit()
    {
        var time = new TimerCountingTimeProvider();
        var probe = new FakeProcessExitProbe(time) { ExitsAt = time.GetUtcNow() + TimeSpan.FromSeconds(10) };
        var waiter = new OldInstanceWaiter(probe, time);

        var waiting = waiter.WaitForExitAsync(4242, OldInstanceWaiter.DefaultTimeout);
        Assert.False(waiting.IsCompleted);

        await AdvanceUntilCompletedAsync(time, waiting, TimeSpan.FromSeconds(60));

        Assert.Equal(HandoffWaitResult.OldInstanceExited, await waiting);
        Assert.True(probe.Polls > 1);
    }

    [Fact]
    public async Task The_wait_is_bounded_and_kills_nothing_when_the_old_instance_never_exits()
    {
        var time = new TimerCountingTimeProvider();
        var probe = new FakeProcessExitProbe(time); // never exits
        var log = new CapturingLogger<RestartMechanicsTests>();
        var waiter = new OldInstanceWaiter(probe, time, log);
        var start = time.GetUtcNow();

        var waiting = waiter.WaitForExitAsync(4242, OldInstanceWaiter.DefaultTimeout);
        await AdvanceUntilCompletedAsync(time, waiting, TimeSpan.FromMinutes(5));

        Assert.Equal(HandoffWaitResult.TimedOut, await waiting);

        // It gave up at the bound -- not before, and not long after.
        var waited = time.GetUtcNow() - start;
        Assert.True(waited >= OldInstanceWaiter.DefaultTimeout, $"gave up after only {waited}");
        Assert.True(waited < OldInstanceWaiter.DefaultTimeout + TimeSpan.FromSeconds(1), $"waited {waited}");
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("still running", StringComparison.Ordinal));
    }

    /// <summary>
    /// Steps the clock one poll at a time, in lockstep with the waiter: after each step it waits
    /// until the waiter has either finished or armed its next delay, so no step can be skipped
    /// because the waiter had not got round to arming its timer yet.
    /// </summary>
    private static async Task AdvanceUntilCompletedAsync(TimerCountingTimeProvider time, Task task, TimeSpan limit)
    {
        var advanced = TimeSpan.Zero;
        while (!task.IsCompleted && advanced < limit)
        {
            var timersBefore = time.TimersCreated;
            time.Advance(OldInstanceWaiter.PollInterval);
            advanced += OldInstanceWaiter.PollInterval;

            while (!task.IsCompleted && time.TimersCreated == timersBefore)
            {
                await Task.Yield();
            }
        }

        Assert.True(task.IsCompleted, "the handoff wait did not finish within the limit");
    }

    private sealed class TimerCountingTimeProvider : TimeProvider
    {
        private readonly FakeTimeProvider inner = new();
        private int timersCreated;

        public int TimersCreated => Volatile.Read(ref timersCreated);

        public void Advance(TimeSpan delta) => inner.Advance(delta);

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = inner.CreateTimer(callback, state, dueTime, period);
            Interlocked.Increment(ref timersCreated);
            return timer;
        }
    }
}

/// <summary>
/// bs-6to / ADR-020 Decision 7: exit is bounded. With a wedged stop the guard forces the process to
/// exit after the bound; with a stop that finishes in time it never does.
/// </summary>
public sealed class ShutdownGuardTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(15);

    private sealed class GuardHarness
    {
        public FakeTimeProvider Time { get; } = new();
        public FakeProcessExiter Exiter { get; } = new();
        public CapturingLogger<ShutdownGuard> Log { get; } = new();
        public List<string> Unlogged { get; } = [];
        public ShutdownGuard Guard { get; }

        public GuardHarness(bool withLogger = true)
        {
            Guard = new ShutdownGuard(
                Time, Exiter, () => withLogger ? Log : null, message => Unlogged.Add(message), Bound);
        }
    }

    [Fact]
    public async Task A_wedged_stop_is_cut_off_at_the_bound_with_an_Error_log_and_a_forced_exit()
    {
        var h = new GuardHarness();
        var wedged = new TaskCompletionSource(); // the supervisor never answers
        h.Guard.BeginShutdown("application exit");
        var stop = WedgedStopAsync(wedged.Task);

        h.Time.Advance(Bound - TimeSpan.FromSeconds(1));
        Assert.Empty(h.Exiter.ExitCodes);
        Assert.False(stop.IsCompleted);

        h.Time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal([ShutdownGuard.ForcedExitCode], h.Exiter.ExitCodes);
        Assert.Contains(h.Log.Entries, e =>
            e.Level == LogLevel.Error && e.Message.Contains("did not finish", StringComparison.Ordinal));
    }

    private static async Task WedgedStopAsync(Task never) => await never;

    [Fact]
    public void A_stop_that_finishes_in_time_never_forces_an_exit()
    {
        var h = new GuardHarness();
        h.Guard.BeginShutdown("application exit");

        h.Time.Advance(TimeSpan.FromSeconds(2));
        h.Guard.Complete();
        h.Time.Advance(TimeSpan.FromMinutes(5));

        Assert.Empty(h.Exiter.ExitCodes);
        Assert.DoesNotContain(h.Log.Entries, e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public void Beginning_shutdown_again_does_not_extend_the_deadline()
    {
        var h = new GuardHarness();
        h.Guard.BeginShutdown("watchdog restart");

        h.Time.Advance(TimeSpan.FromSeconds(10));
        h.Guard.BeginShutdown("application exit"); // OnExit, a moment later
        h.Time.Advance(TimeSpan.FromSeconds(5));

        Assert.Single(h.Exiter.ExitCodes);
    }

    [Fact]
    public void Nothing_is_armed_until_shutdown_begins()
    {
        var h = new GuardHarness();

        h.Time.Advance(TimeSpan.FromHours(1));

        Assert.False(h.Guard.IsShuttingDown);
        Assert.Empty(h.Exiter.ExitCodes);
    }

    [Fact]
    public void Without_a_logger_the_overrun_is_still_recorded_and_the_exit_still_forced()
    {
        var h = new GuardHarness(withLogger: false);
        h.Guard.BeginShutdown("application exit");

        h.Time.Advance(Bound);

        Assert.Single(h.Exiter.ExitCodes);
        Assert.Contains("did not finish", Assert.Single(h.Unlogged), StringComparison.Ordinal);
    }
}

public sealed class JsonRestartHistoryStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "bosun-tests", Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(directory, "watchdog-restarts.json");

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Saved_restarts_are_loaded_back()
    {
        var store = new JsonRestartHistoryStore(FilePath);
        var times = new[] { new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 1, 9, 30, 0, TimeSpan.Zero) };

        store.Save(times);

        Assert.Equal(times, new JsonRestartHistoryStore(FilePath).Load());
    }

    [Fact]
    public void A_missing_file_is_empty_history()
    {
        Assert.Empty(new JsonRestartHistoryStore(FilePath).Load());
    }

    [Fact]
    public void A_corrupt_file_is_empty_history_not_an_exception()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(FilePath, "{ not json");

        Assert.Empty(new JsonRestartHistoryStore(FilePath).Load());
    }

    [Fact]
    public void Saving_creates_the_directory_and_leaves_no_temp_file()
    {
        new JsonRestartHistoryStore(FilePath).Save([DateTimeOffset.UnixEpoch]);

        Assert.True(File.Exists(FilePath));
        Assert.False(File.Exists(FilePath + ".tmp"));
    }
}
