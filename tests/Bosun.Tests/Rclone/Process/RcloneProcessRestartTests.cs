using Bosun.Rclone;
using Bosun.Rclone.Process;
using Bosun.Tests.Configuration.Fakes;
using Bosun.Tests.Rclone.Fakes;
using Bosun.Tests.Rclone.Process.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bosun.Tests.Rclone.Process;

/// <summary>
/// <see cref="RcloneProcessService.RestartAsync"/> (bs-aoz, "Restart rclone"): stop the current rcd
/// and start a fresh one through the ordinary launch path. No real process, no real HTTP, no real
/// wall-clock wait -- fakes and <see cref="FakeTimeProvider"/> throughout.
/// </summary>
public sealed class RcloneProcessRestartTests
{
    [Fact]
    public async Task The_old_process_is_killed_before_the_port_is_checked_and_the_new_one_launched()
    {
        var h = Harness.Create();
        await h.Service.StartAsync(CancellationToken.None);
        h.Order.Clear();

        var restarted = await h.Service.RestartAsync();

        Assert.True(restarted);

        // The status goes to Starting first (closing the mounting gate before anything is touched),
        // then the old process dies, THEN the port guard is consulted and the new process launched.
        Assert.Equal(
            ["status:Starting", "kill:gen0", "port-guard", "launch", "status:Healthy"],
            h.Order);
    }

    [Fact]
    public async Task The_port_guard_is_consulted_on_a_restart_exactly_as_on_a_start()
    {
        var h = Harness.Create();
        await h.Service.StartAsync(CancellationToken.None);
        Assert.Equal(1, h.PortGuard.EnsureFreeCalls);

        await h.Service.RestartAsync();

        Assert.Equal(2, h.PortGuard.EnsureFreeCalls);
    }

    [Fact]
    public async Task A_port_held_by_another_program_blocks_the_restart_and_reports_the_real_cause()
    {
        var h = Harness.Create();
        await h.Service.StartAsync(CancellationToken.None);
        h.PortGuard.Enqueue(new RcPortCheck(RcPortCheckOutcome.HeldByOtherProcess, "port 5572 is held by PID 4242 (other.exe)"));

        var restarted = await h.Service.RestartAsync();

        Assert.False(restarted);
        Assert.Equal(RcloneProcessStatus.Faulted, h.Service.Status);
        Assert.Equal(RcloneProcessFaultKind.PortHeldByOtherProcess, h.Service.FaultKind);
        Assert.Contains("PID 4242", h.Service.LastFaultMessage);
        Assert.Single(h.Launcher.StartCalls); // the original launch only: nothing launched into a held port
    }

    [Fact]
    public async Task Every_healthy_transition_after_a_restart_is_a_distinct_event_for_the_reconcile_signal()
    {
        var h = Harness.Create();
        await h.Service.StartAsync(CancellationToken.None);

        await h.Service.RestartAsync();

        // StartupOrchestrator turns each Healthy event into IMountSupervisor.OnRcloneRestartedAsync.
        Assert.Equal(2, h.Statuses.Count(s => s == RcloneProcessStatus.Healthy));
    }

    [Fact]
    public async Task The_supervise_loop_does_not_mistake_the_kill_for_a_crash_and_launch_a_second_process()
    {
        var h = Harness.Create(restartDelay: TimeSpan.FromSeconds(2));
        await h.Service.StartAsync(CancellationToken.None);

        await h.Service.RestartAsync();
        h.Time.Advance(TimeSpan.FromMinutes(1));
        await PumpAsync();

        // A loop that saw the kill as an unexpected exit would have reported this fault and
        // launched again after the restart delay.
        Assert.DoesNotContain(RcloneProcessStatus.Faulted, h.Statuses);
        Assert.Equal(2, h.Launcher.StartCalls.Count);
    }

    [Fact]
    public async Task After_a_restart_the_loop_is_running_again_and_a_later_crash_is_still_restarted()
    {
        var h = Harness.Create(restartDelay: TimeSpan.FromSeconds(2));
        await h.Service.StartAsync(CancellationToken.None);
        await h.Service.RestartAsync();
        var newest = h.Launcher.Handles[^1];

        newest.SimulateExit(1);
        await PumpAsync();
        Assert.Equal(RcloneProcessFaultKind.ProcessExitedUnexpectedly, h.Service.FaultKind);
        await AdvanceAndWaitUntilAsync(h.Time, TimeSpan.FromSeconds(2), () => h.Service.Status == RcloneProcessStatus.Healthy);

        Assert.Equal(3, h.Launcher.StartCalls.Count);
        Assert.Equal(RcloneProcessStatus.Healthy, h.Service.Status);
    }

    [Fact]
    public async Task A_failed_launch_during_a_restart_returns_false_and_the_loop_retries_it()
    {
        var h = Harness.Create(restartDelay: TimeSpan.FromSeconds(5));
        await h.Service.StartAsync(CancellationToken.None);
        h.Launcher.EnqueueLaunchFailure();

        var restarted = await h.Service.RestartAsync();

        Assert.False(restarted);
        Assert.Equal(RcloneProcessFaultKind.LaunchFailed, h.Service.FaultKind);

        // The launcher then hands out a healthy handle: the loop's retry is what recovers.
        await AdvanceAndWaitUntilAsync(h.Time, TimeSpan.FromSeconds(5), () => h.Service.Status == RcloneProcessStatus.Healthy);
        Assert.Equal(RcloneProcessStatus.Healthy, h.Service.Status);
        Assert.Equal(3, h.Launcher.StartCalls.Count);
    }

    [Fact]
    public async Task A_missing_executable_is_terminal_for_a_restart_too_and_is_not_retried()
    {
        var h = Harness.Create();
        await h.Service.StartAsync(CancellationToken.None);
        h.Launcher.EnqueueExecutableNotFound();

        var restarted = await h.Service.RestartAsync();

        Assert.False(restarted);
        Assert.Equal(RcloneProcessFaultKind.ExecutableNotFound, h.Service.FaultKind);
        h.Time.Advance(TimeSpan.FromHours(1));
        await PumpAsync();
        Assert.Equal(2, h.Launcher.StartCalls.Count); // the original and the failed restart; no retry loop
    }

    [Fact]
    public async Task Restart_before_the_service_has_started_does_nothing()
    {
        var h = Harness.Create();

        var restarted = await h.Service.RestartAsync();

        Assert.False(restarted);
        Assert.Empty(h.Launcher.StartCalls);
        Assert.Equal(0, h.PortGuard.EnsureFreeCalls);
    }

    [Fact]
    public async Task Restart_after_the_service_has_been_stopped_does_nothing()
    {
        var h = Harness.Create();
        await h.Service.StartAsync(CancellationToken.None);
        await h.Service.StopAsync(CancellationToken.None);

        var restarted = await h.Service.RestartAsync();

        Assert.False(restarted);
        Assert.Single(h.Launcher.StartCalls);
        Assert.Equal(RcloneProcessStatus.Stopped, h.Service.Status);
    }

    [Fact]
    public async Task Two_overlapping_restarts_run_one_after_the_other_and_never_leave_two_live_processes()
    {
        var h = Harness.Create();
        await h.Service.StartAsync(CancellationToken.None);

        var first = h.Service.RestartAsync();
        var second = h.Service.RestartAsync();
        await Task.WhenAll(first, second);

        Assert.Equal(3, h.Launcher.StartCalls.Count);
        var live = Assert.Single(h.Launcher.Handles, handle => !handle.HasExited);
        Assert.Same(h.Launcher.Handles[^1], live);
    }

    [Fact]
    public async Task Stopping_after_a_restart_kills_the_new_process()
    {
        var h = Harness.Create();
        await h.Service.StartAsync(CancellationToken.None);
        await h.Service.RestartAsync();

        await h.Service.StopAsync(CancellationToken.None);

        Assert.All(h.Launcher.Handles, handle => Assert.True(handle.HasExited));
        Assert.Equal(RcloneProcessStatus.Stopped, h.Service.Status);
    }

    // ------------------------------------------------------------------------------------------

    private sealed class Harness
    {
        public required RcloneProcessService Service { get; init; }
        public required OrderedLauncher Launcher { get; init; }
        public required FakeRcPortGuard PortGuard { get; init; }
        public required FakeTimeProvider Time { get; init; }

        /// <summary>One list that status events, the port guard, the launcher and every handle's Kill
        /// append to, so a test can assert what happened before what.</summary>
        public required List<string> Order { get; init; }

        public List<RcloneProcessStatus> Statuses { get; } = [];

        public static Harness Create(TimeSpan? restartDelay = null)
        {
            var order = new List<string>();
            var launcher = new OrderedLauncher(order);
            var portGuard = new FakeRcPortGuard { OnEnsureFree = () => order.Add("port-guard") };
            var time = new FakeTimeProvider();
            var service = new RcloneProcessService(
                launcher,
                new FakeRcloneClient(),
                new RcloneProcessServiceOptions
                {
                    RcloneRcPort = 5572,
                    RcloneConfigPath = @"C:\fixture\rclone.conf",
                    RestartDelay = restartDelay ?? TimeSpan.FromSeconds(5),
                    HealthCheckTimeout = TimeSpan.FromSeconds(10),
                    HealthCheckPollInterval = TimeSpan.FromMilliseconds(100),
                    StopTimeout = TimeSpan.FromSeconds(5),
                },
                time,
                NullLogger<RcloneProcessService>.Instance,
                new RcloneRcCredential("bosun-test-user", "bosun-test-pass"),
                portGuard);

            var harness = new Harness { Service = service, Launcher = launcher, PortGuard = portGuard, Time = time, Order = order };
            service.StatusChanged += (_, e) =>
            {
                harness.Statuses.Add(e.Status);
                order.Add($"status:{e.Status}");
            };
            return harness;
        }
    }

    /// <summary>A launcher that records "launch" into the shared order list and keeps every handle it
    /// issued. Hands out a fresh healthy handle once its script is empty.</summary>
    private sealed class OrderedLauncher(List<string> order) : IRcloneProcessLauncher
    {
        private readonly Queue<Func<RecordingHandle>> script = new();

        public List<RecordingHandle> Handles { get; } = [];

        public List<RcloneProcessStartInfo> StartCalls { get; } = [];

        public void EnqueueLaunchFailure() =>
            script.Enqueue(() => throw new RcloneProcessLaunchException("rclone", new InvalidOperationException("boom")));

        public void EnqueueExecutableNotFound() =>
            script.Enqueue(() => throw new RcloneExecutableNotFoundException("rclone", new InvalidOperationException("not found")));

        public IRcloneProcessHandle Start(RcloneProcessStartInfo startInfo)
        {
            StartCalls.Add(startInfo);
            order.Add("launch");
            var handle = script.TryDequeue(out var next) ? next() : new RecordingHandle(order, $"gen{Handles.Count}");
            Handles.Add(handle);
            return handle;
        }
    }

    private sealed class RecordingHandle(List<string> order, string name) : IRcloneProcessHandle
    {
        public bool HasExited { get; private set; }

        public int? ExitCode { get; private set; }

        public event EventHandler? Exited;

        public void SimulateExit(int exitCode)
        {
            if (HasExited)
            {
                return;
            }

            HasExited = true;
            ExitCode = exitCode;
            Exited?.Invoke(this, EventArgs.Empty);
        }

        public void Kill()
        {
            order.Add($"kill:{name}");
            SimulateExit(0);
        }

        public void Dispose()
        {
        }
    }

    private static async Task PumpAsync()
    {
        for (var i = 0; i < 64; i++)
        {
            await Task.Yield();
        }
    }

    private static async Task AdvanceAndWaitUntilAsync(FakeTimeProvider time, TimeSpan delta, Func<bool> condition)
    {
        time.Advance(delta);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5).ConfigureAwait(false);
        }
    }
}
