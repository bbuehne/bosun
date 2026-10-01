using System.Net.Http;
using Bosun.Rclone;
using Bosun.Rclone.Process;
using Bosun.Tests.Configuration.Fakes;
using Bosun.Tests.Rclone.Fakes;
using Bosun.Tests.Rclone.Process.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bosun.Tests.Rclone.Process;

/// <summary>
/// bs-o5x and bs-772 at the service level: the fault text names the REAL cause, our own child
/// exiting ends the health wait at once, and a held rc port is handled through the existing
/// RestartDelay loop. Fakes only (no process, no HTTP, no real clock).
/// </summary>
public sealed class RcloneProcessFaultMessageTests
{
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(15);

    // -- bs-o5x: accurate health-check messages -----------------------------------------------

    [Fact]
    public async Task A_401_during_the_health_wait_is_reported_as_401_not_as_silence()
    {
        var f = new Fixture();
        var handle = new FakeRcloneProcessHandle();
        f.Launcher.EnqueueSuccess(handle);
        // Every poll answers 401, the way the orphaned rcd did on 2026-10-01.
        for (var i = 0; i < 200; i++)
        {
            f.Client.EnqueueVersionFailure(new RcloneRcException("core/version", 401, "rc call to 'core/version' failed: unauthorized"));
        }

        var start = f.Service.StartAsync(CancellationToken.None);
        await f.AdvanceUntilCompleteAsync(start, TimeSpan.FromMilliseconds(250), HealthTimeout + TimeSpan.FromSeconds(5));
        await start.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RcloneProcessStatus.Faulted, f.Service.Status);
        Assert.Equal(RcloneProcessFaultKind.HealthCheckUnauthorized, f.Service.FaultKind);
        Assert.Contains("HTTP 401", f.Service.LastFaultMessage);
        Assert.Contains("127.0.0.1:5572", f.Service.LastFaultMessage);
        Assert.Contains("another rclone", f.Service.LastFaultMessage);
        Assert.DoesNotContain("did not respond", f.Service.LastFaultMessage);
        Assert.Equal(1, handle.KillCallCount);
    }

    [Fact]
    public async Task A_connection_refused_is_reported_as_no_response()
    {
        var f = new Fixture(healthTimeout: TimeSpan.FromMilliseconds(100), pollInterval: TimeSpan.FromSeconds(1));
        f.Launcher.EnqueueSuccess(new FakeRcloneProcessHandle());
        f.Client.EnqueueVersionFailure(new HttpRequestException("No connection could be made because the target machine actively refused it"));

        await f.Service.StartAsync(CancellationToken.None);

        Assert.Equal(RcloneProcessFaultKind.HealthCheckFailed, f.Service.FaultKind);
        Assert.Contains("no response", f.Service.LastFaultMessage);
        Assert.Contains("refused", f.Service.LastFaultMessage);
    }

    [Fact]
    public async Task A_core_version_timeout_is_reported_as_no_response_timed_out()
    {
        var f = new Fixture(healthTimeout: TimeSpan.FromMilliseconds(100), pollInterval: TimeSpan.FromSeconds(1));
        f.Launcher.EnqueueSuccess(new FakeRcloneProcessHandle());
        f.Client.EnqueueVersionFailure(new RcloneRcTimeoutException("core/version", TimeSpan.FromSeconds(5)));

        await f.Service.StartAsync(CancellationToken.None);

        Assert.Equal(RcloneProcessFaultKind.HealthCheckFailed, f.Service.FaultKind);
        Assert.Contains("no response", f.Service.LastFaultMessage);
        Assert.Contains("timed out", f.Service.LastFaultMessage);
    }

    [Fact]
    public async Task Other_http_errors_name_the_status_code()
    {
        var f = new Fixture(healthTimeout: TimeSpan.FromMilliseconds(100), pollInterval: TimeSpan.FromSeconds(1));
        f.Launcher.EnqueueSuccess(new FakeRcloneProcessHandle());
        f.Client.EnqueueVersionFailure(new RcloneRcException("core/version", 403, "forbidden"));

        await f.Service.StartAsync(CancellationToken.None);

        Assert.Equal(RcloneProcessFaultKind.HealthCheckFailed, f.Service.FaultKind);
        Assert.Contains("HTTP 403", f.Service.LastFaultMessage);
    }

    [Fact]
    public async Task A_child_that_exits_during_the_health_wait_faults_with_its_exit_code_and_does_not_wait_out_the_timeout()
    {
        var f = new Fixture();
        var handle = new FakeRcloneProcessHandle();
        f.Launcher.EnqueueSuccess(handle);
        // First poll: the child dies on its bind failure while the orphan answers 401.
        f.Client.EnqueueVersionScript(() =>
        {
            handle.SimulateExit(1);
            return Task.FromException<RcloneVersionInfo>(new RcloneRcException("core/version", 401, "unauthorized"));
        });
        var before = f.Time.GetUtcNow();

        await f.Service.StartAsync(CancellationToken.None);

        Assert.Equal(RcloneProcessStatus.Faulted, f.Service.Status);
        Assert.Equal(RcloneProcessFaultKind.ProcessExitedBeforeHealthy, f.Service.FaultKind);
        Assert.Contains("exited with code 1 before becoming healthy", f.Service.LastFaultMessage);
        Assert.Contains("HTTP 401", f.Service.LastFaultMessage); // the last probe result rides along
        Assert.Single(f.Client.GetVersionCalls);
        // Not a single tick of the injected clock was needed: nowhere near the 15 s timeout.
        Assert.Equal(before, f.Time.GetUtcNow());
    }

    [Fact]
    public async Task A_child_that_is_already_dead_is_reported_before_any_poll()
    {
        var f = new Fixture();
        var handle = new FakeRcloneProcessHandle();
        handle.SimulateExit(2);
        f.Launcher.EnqueueSuccess(handle);

        await f.Service.StartAsync(CancellationToken.None);

        Assert.Equal(RcloneProcessFaultKind.ProcessExitedBeforeHealthy, f.Service.FaultKind);
        Assert.Contains("exited with code 2", f.Service.LastFaultMessage);
        Assert.Empty(f.Client.GetVersionCalls);
    }

    [Fact]
    public async Task A_child_that_exits_while_the_wait_is_sleeping_between_polls_ends_the_wait_without_advancing_time()
    {
        var f = new Fixture();
        var handle = new FakeRcloneProcessHandle();
        f.Launcher.EnqueueSuccess(handle);
        f.Client.EnqueueVersionFailure(new HttpRequestException("refused"));

        var start = f.Service.StartAsync(CancellationToken.None);
        // The first poll has failed and the service is now sleeping in its 250 ms poll delay.
        await WaitUntilAsync(() => f.Client.GetVersionCalls.Count == 1);
        Assert.False(start.IsCompleted);

        handle.SimulateExit(3);
        await start.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RcloneProcessFaultKind.ProcessExitedBeforeHealthy, f.Service.FaultKind);
        Assert.Contains("exited with code 3", f.Service.LastFaultMessage);
        Assert.Contains("no response", f.Service.LastFaultMessage); // last failure included
    }

    [Fact]
    public async Task A_child_exit_names_whoever_holds_the_port()
    {
        var f = new Fixture();
        f.Guard.Holder = @"port 5572 is held by PID 31032 (C:\x\rclone.exe)";
        var handle = new FakeRcloneProcessHandle();
        handle.SimulateExit(1);
        f.Launcher.EnqueueSuccess(handle);

        await f.Service.StartAsync(CancellationToken.None);

        Assert.Contains(@"port 5572 is held by PID 31032 (C:\x\rclone.exe)", f.Service.LastFaultMessage);
    }

    [Fact]
    public async Task Every_new_fault_kind_still_retries_and_a_later_success_clears_the_fault()
    {
        var f = new Fixture(restartDelay: TimeSpan.FromSeconds(5));
        var dead = new FakeRcloneProcessHandle();
        dead.SimulateExit(1);
        f.Launcher.EnqueueSuccess(dead);
        f.Launcher.EnqueueSuccess(new FakeRcloneProcessHandle());

        await f.Service.StartAsync(CancellationToken.None);
        Assert.Equal(RcloneProcessFaultKind.ProcessExitedBeforeHealthy, f.Service.FaultKind);

        await f.AdvanceAndWaitUntilAsync(TimeSpan.FromSeconds(5), () => f.Service.Status == RcloneProcessStatus.Healthy);

        Assert.Equal(RcloneProcessStatus.Healthy, f.Service.Status);
        Assert.Equal(RcloneProcessFaultKind.None, f.Service.FaultKind);
        Assert.Null(f.Service.LastFaultMessage);
        Assert.Equal(2, f.Launcher.StartCalls.Count);
    }

    // -- bs-772: the rc port guard in the launch path ------------------------------------------

    [Fact]
    public async Task A_port_held_by_another_process_faults_without_launching_and_names_the_holder()
    {
        var f = new Fixture();
        const string message = @"Port 5572 is held by PID 4242 (C:\other\thing.exe), which is not a Bosun-launched rclone rcd, so Bosun will not kill it. Stop that process or change global.rclone_rc_port.";
        f.Guard.Enqueue(new RcPortCheck(RcPortCheckOutcome.HeldByOtherProcess, message));

        await f.Service.StartAsync(CancellationToken.None);

        Assert.Equal(RcloneProcessStatus.Faulted, f.Service.Status);
        Assert.Equal(RcloneProcessFaultKind.PortHeldByOtherProcess, f.Service.FaultKind);
        Assert.Equal(message, f.Service.LastFaultMessage);
        Assert.Empty(f.Launcher.StartCalls);
    }

    [Fact]
    public async Task A_held_port_is_retried_on_the_restart_delay_not_in_a_tight_loop_and_recovers_when_it_frees()
    {
        var f = new Fixture(restartDelay: TimeSpan.FromSeconds(5));
        f.Guard.Enqueue(new RcPortCheck(RcPortCheckOutcome.HeldByOtherProcess, "held"));
        // Second check: the holder has gone away (queue exhausted => Free).

        await f.Service.StartAsync(CancellationToken.None);
        Assert.Equal(1, f.Guard.EnsureFreeCalls);

        // Just under the delay: no second check, no launch.
        f.Time.Advance(TimeSpan.FromSeconds(4));
        await Task.Yield();
        Assert.Equal(1, f.Guard.EnsureFreeCalls);
        Assert.Empty(f.Launcher.StartCalls);

        await f.AdvanceAndWaitUntilAsync(TimeSpan.FromSeconds(1), () => f.Service.Status == RcloneProcessStatus.Healthy);

        Assert.Equal(2, f.Guard.EnsureFreeCalls);
        Assert.Single(f.Launcher.StartCalls);
        Assert.Equal(RcloneProcessStatus.Healthy, f.Service.Status);
    }

    [Fact]
    public async Task A_killed_stale_rcd_is_gone_before_Bosun_launches_its_own()
    {
        // Real guard, fake collaborators: the sequence is what matters.
        var order = new List<string>();
        var resolver = new FakePortOwnerResolver();
        resolver.Enqueue(31032);
        resolver.Enqueue(null);
        var inspector = new FakeProcessInspector();
        inspector.Set(StaleRcdMatcherTests.Matching(31032), 31032);
        var terminator = new FakeProcessTerminator { OnKill = _ => order.Add("kill") };
        var options = new RcloneProcessServiceOptions
        {
            RcloneRcPort = 5572,
            RcloneConfigPath = @"C:\Users\Barry\AppData\Roaming\rclone\rclone.conf",
        };
        var guard = new RcPortGuard(
            resolver, inspector, terminator, options, @"DESKTOP\Barry", new FakeTimeProvider(),
            NullLogger<RcPortGuard>.Instance);
        var launcher = new OrderRecordingLauncher(order);
        var service = new RcloneProcessService(
            launcher, new FakeRcloneClient(), options, new FakeTimeProvider(),
            NullLogger<RcloneProcessService>.Instance, new RcloneRcCredential("u", "p"), guard);

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(["kill", "launch"], order);
        Assert.Equal(RcloneProcessStatus.Healthy, service.Status);
    }

    [Fact]
    public async Task A_matching_holder_with_the_wrong_user_is_not_killed_and_the_service_faults()
    {
        var resolver = new FakePortOwnerResolver { Default = 31032 };
        var inspector = new FakeProcessInspector();
        inspector.Set(StaleRcdMatcherTests.Matching(31032) with { Owner = @"DESKTOP\Someone" }, 31032);
        var terminator = new FakeProcessTerminator();
        var options = new RcloneProcessServiceOptions
        {
            RcloneRcPort = 5572,
            RcloneConfigPath = @"C:\Users\Barry\AppData\Roaming\rclone\rclone.conf",
        };
        var guard = new RcPortGuard(
            resolver, inspector, terminator, options, @"DESKTOP\Barry", new FakeTimeProvider(),
            NullLogger<RcPortGuard>.Instance);
        var launcher = new FakeRcloneProcessLauncher();
        var service = new RcloneProcessService(
            launcher, new FakeRcloneClient(), options, new FakeTimeProvider(),
            NullLogger<RcloneProcessService>.Instance, new RcloneRcCredential("u", "p"), guard);

        await service.StartAsync(CancellationToken.None);

        Assert.Empty(terminator.KillCalls);
        Assert.Empty(launcher.StartCalls);
        Assert.Equal(RcloneProcessFaultKind.PortHeldByOtherProcess, service.FaultKind);
        Assert.Contains("PID 31032", service.LastFaultMessage);
    }

    [Fact]
    public async Task ExecutableNotFound_is_still_terminal_with_a_guard_present()
    {
        var f = new Fixture();
        f.Launcher.EnqueueExecutableNotFound();

        await f.Service.StartAsync(CancellationToken.None);
        f.Time.Advance(TimeSpan.FromHours(1));
        await Task.Yield();

        Assert.Equal(RcloneProcessFaultKind.ExecutableNotFound, f.Service.FaultKind);
        Assert.Single(f.Launcher.StartCalls);
        Assert.Equal(1, f.Guard.EnsureFreeCalls);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5).ConfigureAwait(false);
        }
    }

    private sealed class OrderRecordingLauncher(List<string> order) : IRcloneProcessLauncher
    {
        public IRcloneProcessHandle Start(RcloneProcessStartInfo startInfo)
        {
            order.Add("launch");
            return new FakeRcloneProcessHandle();
        }
    }

    private sealed class Fixture
    {
        public Fixture(
            TimeSpan? restartDelay = null, TimeSpan? healthTimeout = null, TimeSpan? pollInterval = null)
        {
            var options = new RcloneProcessServiceOptions
            {
                RcloneRcPort = 5572,
                RcloneConfigPath = @"C:\fixture\rclone.conf",
                RestartDelay = restartDelay ?? TimeSpan.FromSeconds(5),
                HealthCheckTimeout = healthTimeout ?? HealthTimeout,
                HealthCheckPollInterval = pollInterval ?? TimeSpan.FromMilliseconds(250),
            };

            Service = new RcloneProcessService(
                Launcher, Client, options, Time, NullLogger<RcloneProcessService>.Instance,
                new RcloneRcCredential("bosun-test-user", "bosun-test-pass"), Guard);
        }

        public FakeRcloneProcessLauncher Launcher { get; } = new();
        public FakeRcloneClient Client { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public FakeRcPortGuard Guard { get; } = new();
        public RcloneProcessService Service { get; }

        public async Task AdvanceUntilCompleteAsync(Task task, TimeSpan step, TimeSpan maxAdvance)
        {
            var advanced = TimeSpan.Zero;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!task.IsCompleted && advanced < maxAdvance && DateTime.UtcNow < deadline)
            {
                Time.Advance(step);
                advanced += step;
                await Task.Delay(1).ConfigureAwait(false);
            }
        }

        public async Task AdvanceAndWaitUntilAsync(TimeSpan delta, Func<bool> condition)
        {
            Time.Advance(delta);
            await WaitUntilAsync(condition).ConfigureAwait(false);
        }
    }
}
