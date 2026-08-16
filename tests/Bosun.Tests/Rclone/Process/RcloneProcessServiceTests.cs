using Bosun.Rclone;
using Bosun.Rclone.Process;
using Bosun.Tests.Configuration.Fakes;
using Bosun.Tests.Rclone.Fakes;
using Bosun.Tests.Rclone.Process.Fakes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bosun.Tests.Rclone.Process;

/// <summary>
/// Covers bs-5mt's <see cref="RcloneProcessService"/>: start, health check, restart-on-death,
/// the ExecutableNotFound/LaunchFailed/HealthCheckFailed/ProcessExitedUnexpectedly distinctions,
/// and clean stop. No real process, no real HTTP, no real wall-clock wait anywhere (CLAUDE.md
/// worktree-safety rules) -- <see cref="FakeRcloneProcessLauncher"/>,
/// <see cref="FakeRcloneClient"/>, and <see cref="FakeTimeProvider"/> back everything.
/// </summary>
public sealed class RcloneProcessServiceTests
{
    [Fact]
    public async Task StartAsync_becomes_Healthy_after_a_successful_launch_and_health_check()
    {
        var (service, launcher, client, time, statusHistory) = Create();
        launcher.EnqueueSuccess(new FakeRcloneProcessHandle());

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(RcloneProcessStatus.Healthy, service.Status);
        Assert.Equal(RcloneProcessFaultKind.None, service.FaultKind);
        Assert.Contains(RcloneProcessStatus.Healthy, statusHistory);
        Assert.Single(client.GetVersionCalls);
    }

    [Fact]
    public async Task StartAsync_passes_rcd_with_loopback_addr_and_config_path()
    {
        var (service, launcher, _, _, _) = Create(port: 5573, configPath: @"C:\fixture\rclone.conf");
        launcher.EnqueueSuccess(new FakeRcloneProcessHandle());

        await service.StartAsync(CancellationToken.None);

        var args = Assert.Single(launcher.StartCalls).Arguments;
        Assert.Equal(["rcd", "--rc-addr", "127.0.0.1:5573", "--config", @"C:\fixture\rclone.conf"], args);
    }

    // -- bs-ard: the rc credential reaches the child via environment, never the command line ----

    [Fact]
    public async Task StartAsync_passes_the_rc_credential_via_environment_variables_not_arguments()
    {
        var credential = new RcloneRcCredential("bosun-test-user", "bosun-test-pass");
        var (service, launcher, _, _, _) = Create(credential: credential);
        launcher.EnqueueSuccess(new FakeRcloneProcessHandle());

        await service.StartAsync(CancellationToken.None);

        var startInfo = Assert.Single(launcher.StartCalls);
        Assert.Equal("bosun-test-user", startInfo.EnvironmentVariables["RCLONE_RC_USER"]);
        Assert.Equal("bosun-test-pass", startInfo.EnvironmentVariables["RCLONE_RC_PASS"]);

        // The secret must never appear on the command line -- a command line is readable by any
        // same-user process (Win32_Process.CommandLine).
        Assert.DoesNotContain(startInfo.Arguments, a => a.Contains("bosun-test-pass"));
        Assert.DoesNotContain(startInfo.Arguments, a => a.Contains("bosun-test-user"));
        Assert.DoesNotContain(startInfo.Arguments, a => a.Contains("rc-user", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(startInfo.Arguments, a => a.Contains("rc-pass", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Restart_after_an_unexpected_exit_reuses_the_same_credential()
    {
        // The per-Bosun-process-lifetime decision (see RcloneRcCredential's class remarks):
        // every launch attempt -- including a restart -- must carry the SAME credential, since
        // RcloneClient's Authorization header was set once, at construction, and is never
        // refreshed.
        var credential = new RcloneRcCredential("bosun-test-user", "bosun-test-pass");
        var (service, launcher, _, time, _) = Create(credential: credential, restartDelay: TimeSpan.FromSeconds(2));
        var firstHandle = new FakeRcloneProcessHandle();
        var secondHandle = new FakeRcloneProcessHandle();
        launcher.EnqueueSuccess(firstHandle);
        launcher.EnqueueSuccess(secondHandle);

        await service.StartAsync(CancellationToken.None);
        firstHandle.SimulateExit(1);
        await PumpAsync();
        await AdvanceAndWaitUntilAsync(time, TimeSpan.FromSeconds(2), () => service.Status == RcloneProcessStatus.Healthy);

        Assert.Equal(2, launcher.StartCalls.Count);
        Assert.All(launcher.StartCalls, s =>
        {
            Assert.Equal("bosun-test-user", s.EnvironmentVariables["RCLONE_RC_USER"]);
            Assert.Equal("bosun-test-pass", s.EnvironmentVariables["RCLONE_RC_PASS"]);
        });
    }

    [Fact]
    public async Task The_rc_password_never_appears_in_any_logged_output()
    {
        // A long, high-entropy, near-impossible-to-coincidentally-match marker in place of a
        // real random password -- if THIS ever shows up in a captured log line, something logged
        // the secret (or the whole credential via an unguarded {Credential} template).
        const string secretMarker = "SECRET-MARKER-3f9a7c2e-91b6-4d8a-b7ee-do-not-log-me";
        var credential = new RcloneRcCredential("bosun-test-user", secretMarker);
        var logger = new RecordingLogger<RcloneProcessService>();

        var (service, launcher, _, time, _) = Create(
            restartDelay: TimeSpan.FromSeconds(1),
            healthCheckTimeout: TimeSpan.FromMilliseconds(100),
            healthCheckPollInterval: TimeSpan.FromSeconds(1),
            stopTimeout: TimeSpan.FromMilliseconds(50),
            credential: credential,
            logger: logger);

        // Exercise several of RcloneProcessService's logging paths: a launch failure
        // (LogError), a successful start (LogInformation), an unexpected exit and restart
        // (LogWarning), and a stop (which itself calls Kill, generating no additional log line
        // in the happy path) -- see the class's own logger.Log* call sites.
        launcher.EnqueueLaunchFailure();
        var neverHealthyHandle = new FakeRcloneProcessHandle();
        launcher.EnqueueSuccess(neverHealthyHandle);

        await service.StartAsync(CancellationToken.None);
        Assert.Equal(RcloneProcessFaultKind.LaunchFailed, service.FaultKind);

        await AdvanceAndWaitUntilAsync(time, TimeSpan.FromSeconds(1), () => service.Status == RcloneProcessStatus.Healthy);
        Assert.Equal(RcloneProcessStatus.Healthy, service.Status);

        neverHealthyHandle.SimulateExit(1);
        await PumpAsync();
        var restartedHandle = new FakeRcloneProcessHandle();
        launcher.EnqueueSuccess(restartedHandle);
        await AdvanceAndWaitUntilAsync(time, TimeSpan.FromSeconds(1), () => launcher.StartCalls.Count == 3);

        var stopTask = service.StopAsync(CancellationToken.None);
        await AdvanceUntilCompleteAsync(stopTask, time, TimeSpan.FromMilliseconds(50));
        await stopTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotEmpty(logger.CapturedText);
        Assert.DoesNotContain(logger.CapturedText, line => line.Contains(secretMarker, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecutableNotFound_is_terminal_and_never_retries()
    {
        var (service, launcher, _, time, statusHistory) = Create();
        launcher.EnqueueExecutableNotFound();

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(RcloneProcessStatus.Faulted, service.Status);
        Assert.Equal(RcloneProcessFaultKind.ExecutableNotFound, service.FaultKind);
        Assert.Contains("PATH", service.LastFaultMessage);
        Assert.Single(statusHistory, s => s == RcloneProcessStatus.Faulted);

        // No retry loop was spun off -- advancing time cannot cause a second Start call.
        time.Advance(TimeSpan.FromHours(1));
        await PumpAsync();
        Assert.Single(launcher.StartCalls);
    }

    [Fact]
    public async Task LaunchFailed_retries_after_RestartDelay_and_can_then_become_Healthy()
    {
        var (service, launcher, _, time, statusHistory) = Create(restartDelay: TimeSpan.FromSeconds(5));
        launcher.EnqueueLaunchFailure();
        launcher.EnqueueSuccess(new FakeRcloneProcessHandle());

        await service.StartAsync(CancellationToken.None);
        Assert.Equal(RcloneProcessStatus.Faulted, service.Status);
        Assert.Equal(RcloneProcessFaultKind.LaunchFailed, service.FaultKind);
        Assert.Single(launcher.StartCalls);

        await AdvanceAndWaitUntilAsync(time, TimeSpan.FromSeconds(5), () => service.Status == RcloneProcessStatus.Healthy);

        Assert.Equal(2, launcher.StartCalls.Count);
        Assert.Equal(RcloneProcessStatus.Healthy, service.Status);
        Assert.Equal([RcloneProcessStatus.Starting, RcloneProcessStatus.Faulted, RcloneProcessStatus.Starting, RcloneProcessStatus.Healthy], statusHistory);
    }

    [Fact]
    public async Task HealthCheckFailed_kills_the_unresponsive_process_and_faults()
    {
        // pollInterval > timeout so the very first failed poll already exceeds the deadline --
        // no timer advancing needed, keeping this test single-hop and simple.
        var (service, launcher, client, _, _) = Create(
            healthCheckTimeout: TimeSpan.FromMilliseconds(100),
            healthCheckPollInterval: TimeSpan.FromSeconds(1));
        var handle = new FakeRcloneProcessHandle();
        launcher.EnqueueSuccess(handle);
        client.EnqueueVersionFailure(new InvalidOperationException("rcd not answering yet"));

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(RcloneProcessStatus.Faulted, service.Status);
        Assert.Equal(RcloneProcessFaultKind.HealthCheckFailed, service.FaultKind);
        Assert.Equal(1, handle.KillCallCount);
    }

    [Fact]
    public async Task An_unexpected_exit_while_Healthy_triggers_restart_and_reconciliation_signal()
    {
        var (service, launcher, _, time, statusHistory) = Create(restartDelay: TimeSpan.FromSeconds(2));
        var firstHandle = new FakeRcloneProcessHandle();
        var secondHandle = new FakeRcloneProcessHandle();
        launcher.EnqueueSuccess(firstHandle);
        launcher.EnqueueSuccess(secondHandle);

        await service.StartAsync(CancellationToken.None);
        Assert.Equal(RcloneProcessStatus.Healthy, service.Status);

        firstHandle.SimulateExit(1);
        await PumpAsync();
        Assert.Equal(RcloneProcessFaultKind.ProcessExitedUnexpectedly, service.FaultKind);

        await AdvanceAndWaitUntilAsync(time, TimeSpan.FromSeconds(2), () => service.Status == RcloneProcessStatus.Healthy);

        Assert.Equal(2, launcher.StartCalls.Count);
        Assert.Equal(RcloneProcessStatus.Healthy, service.Status);
        // The "reconcile from scratch" contract: every Healthy transition is a distinct event a
        // future IMountSupervisor can subscribe to and treat as "the process is new, mount state
        // must be reconciled" -- not merely "we are healthy again".
        Assert.Equal(2, statusHistory.Count(s => s == RcloneProcessStatus.Healthy));
    }

    [Fact]
    public async Task StopAsync_kills_the_process_and_does_not_trigger_a_restart()
    {
        var (service, launcher, _, time, _) = Create(restartDelay: TimeSpan.FromSeconds(1));
        var handle = new FakeRcloneProcessHandle();
        launcher.EnqueueSuccess(handle);
        await service.StartAsync(CancellationToken.None);

        await service.StopAsync(CancellationToken.None);

        Assert.Equal(1, handle.KillCallCount);
        Assert.Equal(RcloneProcessStatus.Stopped, service.Status);

        // Advancing time after Stop must not resurrect the loop.
        time.Advance(TimeSpan.FromMinutes(10));
        await PumpAsync();
        Assert.Single(launcher.StartCalls);
    }

    [Fact]
    public async Task StopAsync_does_not_orphan_the_process_even_if_it_never_confirms_exit()
    {
        var handle = new NeverExitsHandle();
        var (service, launcher, _, time, _) = Create(stopTimeout: TimeSpan.FromMilliseconds(50));
        launcher.EnqueueSuccess(handle);
        await service.StartAsync(CancellationToken.None);

        // Must return (not hang) even though the handle never confirms HasExited -- StopAsync's
        // own TimeProvider-driven timer (in KillAndConfirmExitAsync) bounds the wait.
        var stopTask = service.StopAsync(CancellationToken.None);
        await AdvanceUntilCompleteAsync(stopTask, time, TimeSpan.FromMilliseconds(50));
        await stopTask.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, handle.KillCallCount);
        Assert.Equal(RcloneProcessStatus.Stopped, service.Status);
    }

    /// <summary>
    /// Repeatedly advances <paramref name="time"/> by <paramref name="step"/>, ceding real
    /// thread-scheduling time between attempts, until <paramref name="task"/> completes or a
    /// bounded real-time budget expires.
    /// </summary>
    /// <remarks>
    /// Exists for exactly one reason: <c>RcloneProcessService.KillAndConfirmExitAsync</c>'s
    /// TimeProvider-driven timeout timer is registered on whichever thread eventually resumes
    /// <c>StopAsync</c>'s wait for the supervise loop to unwind, and that resumption is not
    /// guaranteed to have happened on the calling thread before a single, immediate
    /// <c>Advance()</c> call -- reliable when this test runs in isolation, intermittent under a
    /// contended ThreadPool (observed empirically running the full 234-test suite in parallel).
    /// A single premature <c>Advance()</c> can never retroactively catch a timer that did not
    /// exist yet: <c>FakeTimeProvider</c> computes a new timer's due time from its clock AT
    /// REGISTRATION, so advancing before registration only pushes that future due time further
    /// out. The <c>Task.Delay</c> here is test-harness synchronization for that BCL scheduling
    /// detail, not simulated business timing -- the production timeout duration itself remains
    /// entirely driven by <paramref name="time"/>; this loop's only job is to keep offering the
    /// timer a chance to fire once it exists.
    /// </remarks>
    /// <summary>
    /// Advances <paramref name="time"/> by <paramref name="delta"/>, then waits -- on a bounded
    /// real-time budget, polling the actual asserted condition rather than guessing how long to
    /// wait -- until <paramref name="condition"/> is true.
    /// </summary>
    /// <remarks>
    /// Exists for the same underlying reason as <see cref="AdvanceUntilCompleteAsync"/>, but for
    /// state driven off <c>RcloneProcessService.SuperviseAsync</c>'s
    /// <c>Task.Delay(TimeSpan, TimeProvider, CancellationToken)</c> await, which has no
    /// caller-visible <see cref="Task"/> to await directly (<c>SuperviseAsync</c> is a detached
    /// background loop). The BCL's TimeProvider-flavoured <c>Task.Delay</c> promise does not
    /// guarantee its continuation resumes inline on the thread that fires the timer: firing the
    /// timer synchronously inside <see cref="FakeTimeProvider.Advance"/> (as this test's
    /// <c>Advance</c> call does) only queues the awaiting method's continuation onto the
    /// <see cref="ThreadPool"/> -- it does not guarantee that continuation has actually run by the
    /// time <c>Advance</c> returns. A fixed count of <c>Task.Yield()</c>s (the old
    /// <c>PumpAsync</c>-based pattern this replaced) is a bet on how quickly the pool schedules
    /// that already-queued work item, and loses that bet under a cold or contended pool --
    /// reproduced empirically on the very first isolated run of
    /// <see cref="LaunchFailed_retries_after_RestartDelay_and_can_then_become_Healthy"/> while
    /// diagnosing bs-6oz. Polling the real asserted condition removes the bet without weakening
    /// what is asserted: the assertions after this call are unchanged, only how long the test is
    /// willing to wait for them to become true before failing on its own.
    /// </remarks>
    private static async Task AdvanceAndWaitUntilAsync(FakeTimeProvider time, TimeSpan delta, Func<bool> condition)
    {
        time.Advance(delta);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5).ConfigureAwait(false);
        }
    }

    private static async Task AdvanceUntilCompleteAsync(Task task, FakeTimeProvider time, TimeSpan step)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            time.Advance(step);
            await Task.Delay(5).ConfigureAwait(false);
        }
    }

    private static (RcloneProcessService Service, FakeRcloneProcessLauncher Launcher, FakeRcloneClient Client, FakeTimeProvider Time, List<RcloneProcessStatus> StatusHistory) Create(
        int port = 5572,
        string configPath = @"C:\fixture\rclone.conf",
        TimeSpan? restartDelay = null,
        TimeSpan? healthCheckTimeout = null,
        TimeSpan? healthCheckPollInterval = null,
        TimeSpan? stopTimeout = null,
        RcloneRcCredential? credential = null,
        ILogger<RcloneProcessService>? logger = null)
    {
        var launcher = new FakeRcloneProcessLauncher();
        var client = new FakeRcloneClient();
        var time = new FakeTimeProvider();
        var options = new RcloneProcessServiceOptions
        {
            RcloneRcPort = port,
            RcloneConfigPath = configPath,
            RestartDelay = restartDelay ?? TimeSpan.FromSeconds(5),
            HealthCheckTimeout = healthCheckTimeout ?? TimeSpan.FromSeconds(10),
            HealthCheckPollInterval = healthCheckPollInterval ?? TimeSpan.FromMilliseconds(100),
            StopTimeout = stopTimeout ?? TimeSpan.FromSeconds(5),
        };

        var service = new RcloneProcessService(
            launcher,
            client,
            options,
            time,
            logger ?? NullLogger<RcloneProcessService>.Instance,
            credential ?? new RcloneRcCredential("bosun-test-user", "bosun-test-pass"));
        var statusHistory = new List<RcloneProcessStatus>();
        service.StatusChanged += (_, e) => statusHistory.Add(e.Status);

        return (service, launcher, client, time, statusHistory);
    }

    /// <summary>Yields control repeatedly so any continuation scheduled onto the thread pool by a
    /// fired <see cref="FakeTimeProvider"/> timer gets a chance to run before assertions --
    /// belt-and-braces alongside FakeTimeProvider's own synchronous-firing design; not a real
    /// wait (no timer, no clock, bounded iteration count).</summary>
    private static async Task PumpAsync()
    {
        for (var i = 0; i < 64; i++)
        {
            await Task.Yield();
        }
    }

    /// <summary>A handle whose process never confirms exit, even after <see cref="Kill"/> --
    /// exercises <see cref="RcloneProcessService.StopAsync"/>'s bounded wait.</summary>
    private sealed class NeverExitsHandle : IRcloneProcessHandle
    {
        public bool HasExited => false;
        public int? ExitCode => null;
        public int KillCallCount { get; private set; }
        public event EventHandler? Exited { add { } remove { } }
        public void Kill() => KillCallCount++;
        public void Dispose() { }
    }
}
