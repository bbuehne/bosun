using Bosun.Rclone;
using Bosun.Rclone.Process;
using Bosun.Tests.Configuration.Fakes;
using Bosun.Tests.Rclone.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bosun.Tests.Rclone.Process.Independent;

/// <summary>
/// Independent tests for <see cref="RcloneProcessService.RestartAsync"/> ("Restart rclone",
/// bs-aoz), written from ADR-020's bs-aoz amendment and docs/ARCHITECTURE.md §6 -- not from the
/// implementation or its own tests.
/// </summary>
/// <remarks>
/// <para>
/// No real process, port, or HTTP call: <see cref="TrackingLauncher"/>,
/// <see cref="GatedPortGuard"/> and <see cref="FakeRcloneClient"/> stand in for all three, and
/// every service delay runs on a <see cref="FakeTimeProvider"/>.
/// </para>
/// <para>
/// <b>Real time is used for one thing only: waiting for background continuations.</b> The
/// service's supervise loop is a detached task whose continuations run on the thread pool, so a
/// test has to wait for them to happen. <see cref="WaitUntilAsync"/> polls the asserted condition
/// with a 10-second ceiling that exists only so a failure is a red test rather than a hang.
/// <see cref="AssertHoldsAsync"/> is the negative form: it watches a condition for a short window
/// and fails if it ever breaks. A slow machine can only make it miss a bug, never report a false
/// one. No business timing depends on the wall clock.
/// </para>
/// </remarks>
public sealed class RestartSpecTests
{
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(5);

    // -- Spec 1: ordering ---------------------------------------------------------------------------

    /// <summary>
    /// The amendment's order: status Starting (the mounting gate closes) before the old rcd is
    /// touched; the old rcd killed AND its exit confirmed before the port guard is consulted; the
    /// port guard before the new launch. The old process here does not exit on Kill; it exits
    /// only when the test says so. Bug classes: launching while the old rcd still holds the port
    /// (the new one then fails to bind, or two rcds run), and killing while mounts can still be
    /// started against the dying process.
    /// </summary>
    [Fact]
    public async Task Restart_goes_Starting_then_kills_and_waits_for_the_exit_then_checks_the_port_then_launches()
    {
        var f = new Fixture();
        f.Launcher.EnqueueHandle(exitOnKill: false);
        await f.Service.StartAsync(CancellationToken.None);
        Assert.Equal(RcloneProcessStatus.Healthy, f.Service.Status);
        var old = f.Launcher.Handles.Single();
        var mark = f.Log.Count;

        var restart = f.Service.RestartAsync();
        await WaitUntilAsync(() => old.KillCount > 0, "the old rcd was never killed", f, mark);

        // The old process has not exited. Nothing may touch the port or launch until it does.
        await AssertHoldsAsync(() => f.PortGuard.Calls == 1 && f.Launcher.Attempts == 1,
            "the port guard was consulted or a new rcd launched before the old one confirmed exit", f, mark);

        old.Exit(1);
        Assert.True(await restart.WaitAsync(TimeSpan.FromSeconds(10)), $"RestartAsync reported failure.{Environment.NewLine}{f.Log.Dump(mark)}");

        var log = f.Log.Since(mark).ToList();
        var starting = log.IndexOf("status:Starting:None");
        var kill = log.IndexOf("kill#1");
        var exited = log.IndexOf("exited#1");
        var guard = log.IndexOf("portguard#2");
        var launch = log.IndexOf("launch#2");
        var healthy = log.IndexOf("status:Healthy:None");
        var order = $"{Environment.NewLine}{f.Log.Dump(mark)}";

        Assert.True(starting >= 0 && kill >= 0 && exited >= 0 && guard >= 0 && launch >= 0 && healthy >= 0, order);
        Assert.True(starting < kill, "status must go to Starting before the old rcd is killed" + order);
        Assert.True(kill < exited && exited < guard, "the old rcd's exit must be confirmed before the port guard" + order);
        Assert.True(guard < launch, "the port guard must be consulted before the new rcd is launched" + order);
        Assert.True(launch < healthy, order);
    }

    // -- Spec 2: the deliberate kill is not a crash ------------------------------------------------

    /// <summary>
    /// Bug class: the supervise loop seeing the restart's own kill as a crash -- a spurious
    /// "exited unexpectedly" fault on the banner, and a second relaunch racing the restart's.
    /// </summary>
    [Fact]
    public async Task The_deliberate_kill_is_not_reported_as_an_unexpected_exit_and_causes_no_extra_launch()
    {
        var f = new Fixture();
        await f.Service.StartAsync(CancellationToken.None);
        var mark = f.Log.Count;

        Assert.True(await f.Service.RestartAsync());

        // Give a wrongly-surviving loop every chance to act on the old process's exit.
        for (var i = 0; i < 5; i++)
        {
            f.Time.Advance(RestartDelay);
            await AssertHoldsAsync(() => f.Launcher.Attempts == 2, "an extra rcd was launched after the restart", f, mark);
        }

        Assert.DoesNotContain(f.Log.Since(mark), e => e.StartsWith("status:Faulted", StringComparison.Ordinal));
        Assert.Equal(RcloneProcessStatus.Healthy, f.Service.Status);
        Assert.Equal(RcloneProcessFaultKind.None, f.Service.FaultKind);
        Assert.Single(f.Launcher.Alive);
    }

    // -- Spec 3: exactly one Healthy -----------------------------------------------------------------

    /// <summary>
    /// The Healthy transition is the supervisor's reconcile-from-scratch signal
    /// (<c>OnRcloneRestartedAsync</c>). Bug classes: no Healthy at all (the supervisor never
    /// learns its mounts died with the old rcd), or two (a duplicate reconcile, or a hidden
    /// second launch).
    /// </summary>
    [Fact]
    public async Task A_successful_restart_raises_Healthy_exactly_once()
    {
        var f = new Fixture();
        await f.Service.StartAsync(CancellationToken.None);
        var mark = f.Log.Count;

        Assert.True(await f.Service.RestartAsync());
        f.Time.Advance(RestartDelay * 3);
        await AssertHoldsAsync(() => f.Log.Since(mark).Count(e => e.StartsWith("status:Healthy", StringComparison.Ordinal)) <= 1,
            "Healthy was raised more than once for one restart", f, mark);

        Assert.Single(f.Log.Since(mark), e => e.StartsWith("status:Healthy", StringComparison.Ordinal));
        Assert.Equal(RcloneProcessStatus.Healthy, f.Service.Status);
    }

    /// <summary>
    /// After a restart the new rcd is supervised exactly like the first one: if it dies, it is
    /// restarted. Bug class: a restart that stops the supervise loop to do its work and never
    /// starts it again, leaving the next crash unrecovered (docs/ARCHITECTURE.md §6).
    /// </summary>
    [Fact]
    public async Task After_a_restart_a_crash_of_the_new_rcd_is_still_detected_and_recovered()
    {
        var f = new Fixture();
        await f.Service.StartAsync(CancellationToken.None);
        Assert.True(await f.Service.RestartAsync());
        var replacement = f.Launcher.Handles.Last();
        var mark = f.Log.Count;

        replacement.Exit(3);
        await WaitUntilAsync(() => f.Service.FaultKind == RcloneProcessFaultKind.ProcessExitedUnexpectedly,
            "a crash of the restarted rcd was not detected", f, mark);

        await AdvanceUntilAsync(f, () => f.Service.Status == RcloneProcessStatus.Healthy && f.Launcher.Attempts == 3,
            "the crashed rcd was not relaunched", mark);
        Assert.Single(f.Launcher.Alive);
    }

    // -- Spec 4: failure modes ----------------------------------------------------------------------

    /// <summary>
    /// A foreign process grabs the rc port while rcd is being restarted. Bug classes: a generic
    /// or wrong fault kind (the banner then names the wrong cause), launching anyway, or giving up
    /// for good on a cause ADR-020 says may go away.
    /// </summary>
    [Fact]
    public async Task A_port_held_by_a_foreign_process_faults_accurately_and_the_retry_loop_resumes()
    {
        var f = new Fixture();
        await f.Service.StartAsync(CancellationToken.None);
        f.PortGuard.Enqueue(new RcPortCheck(RcPortCheckOutcome.HeldByOtherProcess, "rc port 5572 is held by PID 4242 (C:\\other\\thing.exe)"));
        var mark = f.Log.Count;

        Assert.False(await f.Service.RestartAsync());
        Assert.Equal(RcloneProcessStatus.Faulted, f.Service.Status);
        Assert.Equal(RcloneProcessFaultKind.PortHeldByOtherProcess, f.Service.FaultKind);
        Assert.Equal(1, f.Launcher.Attempts);
        Assert.Empty(f.Launcher.Alive);

        await AdvanceUntilAsync(f, () => f.Service.Status == RcloneProcessStatus.Healthy,
            "the retry loop did not resume after the port was freed", mark);
        Assert.Equal(2, f.Launcher.Attempts);
        Assert.Single(f.Launcher.Alive);
    }

    /// <summary>Bug classes: a launch failure reported under the wrong kind, or ending supervision.</summary>
    [Fact]
    public async Task A_launch_failure_faults_with_LaunchFailed_and_the_retry_loop_resumes()
    {
        var f = new Fixture();
        f.Launcher.EnqueueHandle(exitOnKill: true);
        f.Launcher.EnqueueLaunchFailure();
        await f.Service.StartAsync(CancellationToken.None);
        var mark = f.Log.Count;

        Assert.False(await f.Service.RestartAsync());
        Assert.Equal(RcloneProcessStatus.Faulted, f.Service.Status);
        Assert.Equal(RcloneProcessFaultKind.LaunchFailed, f.Service.FaultKind);

        await AdvanceUntilAsync(f, () => f.Service.Status == RcloneProcessStatus.Healthy,
            "the retry loop did not resume after a failed relaunch", mark);
        Assert.Equal(3, f.Launcher.Attempts);
        Assert.Single(f.Launcher.Alive);
    }

    /// <summary>
    /// ExecutableNotFound is terminal everywhere else (rclone uninstalled will not fix itself);
    /// a restart must not turn it into a retry storm. Bug class: the restart path resuming the
    /// retry loop unconditionally.
    /// </summary>
    [Fact]
    public async Task ExecutableNotFound_during_a_restart_is_terminal()
    {
        var f = new Fixture();
        f.Launcher.EnqueueHandle(exitOnKill: true);
        f.Launcher.EnqueueExecutableNotFound();
        await f.Service.StartAsync(CancellationToken.None);
        var mark = f.Log.Count;

        Assert.False(await f.Service.RestartAsync());
        Assert.Equal(RcloneProcessStatus.Faulted, f.Service.Status);
        Assert.Equal(RcloneProcessFaultKind.ExecutableNotFound, f.Service.FaultKind);

        for (var i = 0; i < 5; i++)
        {
            f.Time.Advance(TimeSpan.FromMinutes(10));
            await AssertHoldsAsync(() => f.Launcher.Attempts == 2, "ExecutableNotFound was retried", f, mark);
        }

        Assert.Equal(RcloneProcessFaultKind.ExecutableNotFound, f.Service.FaultKind);
    }

    // -- Spec 5: overlap and races -------------------------------------------------------------------

    /// <summary>
    /// Two restart clicks (tray and banner, or a double click). The first is parked inside its
    /// launch sequence (in the port check) while the second arrives. Bug classes: two rcd
    /// processes alive at once (the second fails to bind or fights the first for the port), or a
    /// launched process the service no longer tracks (an orphan holding the port and mounts).
    /// </summary>
    [Fact]
    public async Task Overlapping_restarts_never_run_two_rcds_and_leave_exactly_one_tracked_process()
    {
        var f = new Fixture();
        await f.Service.StartAsync(CancellationToken.None);
        f.PortGuard.HoldCall(2);
        var mark = f.Log.Count;

        var first = f.Service.RestartAsync();
        await f.PortGuard.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = f.Service.RestartAsync();
        await AssertHoldsAsync(() => f.Launcher.Attempts == 1, "a second restart launched while the first was mid-sequence", f, mark);

        f.PortGuard.Release();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        await AssertHoldsAsync(() => f.Launcher.Alive.Count <= 1, "more than one rcd alive after both restarts", f, mark);

        Assert.Equal(0, f.Launcher.MaxOthersAliveAtLaunch);
        Assert.Single(f.Launcher.Alive);
        Assert.Equal(RcloneProcessStatus.Healthy, f.Service.Status);

        // The one live process is the one the service tracks: stopping the service ends it.
        await f.Service.StopAsync(CancellationToken.None);
        Assert.Empty(f.Launcher.Alive);
    }

    /// <summary>
    /// Shutdown while a restart is mid-sequence (parked in the port check, just before the
    /// launch). Bug class: the restart completing its launch after StopAsync has torn down,
    /// leaving an rcd running after Bosun believes it stopped -- the orphan ADR-020 was written
    /// about.
    /// </summary>
    /// <remarks>
    /// Two rows. In the first, the port check observes the shutdown token and throws, so the
    /// restart unwinds on its own. In the second it does not (cancellation is cooperative), and
    /// only StopAsync waiting for the restart, or the restart re-checking for shutdown before it
    /// launches, prevents the orphan.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Stop_racing_a_restart_leaves_no_rcd_running_and_nothing_launches_afterwards(bool portCheckHonoursCancellation)
    {
        var f = new Fixture();
        await f.Service.StartAsync(CancellationToken.None);
        f.PortGuard.HoldCall(2, portCheckHonoursCancellation);
        var mark = f.Log.Count;

        var restart = f.Service.RestartAsync();
        await f.PortGuard.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stop = f.Service.StopAsync(CancellationToken.None);
        f.PortGuard.Release();

        await Record.ExceptionAsync(() => restart.WaitAsync(TimeSpan.FromSeconds(10)));
        await AdvanceUntilCompleteAsync(f, stop);
        Assert.True(stop.IsCompletedSuccessfully, $"StopAsync did not complete.{Environment.NewLine}{f.Log.Dump(mark)}");
        Assert.True(restart.IsCompleted, $"RestartAsync did not complete.{Environment.NewLine}{f.Log.Dump(mark)}");

        Assert.Empty(f.Launcher.Alive);
        Assert.Equal(0, f.Launcher.MaxOthersAliveAtLaunch);
        Assert.Equal(RcloneProcessStatus.Stopped, f.Service.Status);

        var attempts = f.Launcher.Attempts;
        for (var i = 0; i < 3; i++)
        {
            f.Time.Advance(TimeSpan.FromMinutes(1));
            await AssertHoldsAsync(() => f.Launcher.Attempts == attempts && f.Launcher.Alive.Count == 0,
                "an rcd was launched after StopAsync completed", f, mark);
        }
    }

    /// <summary>Bug class: a restart after shutdown resurrecting rcd.</summary>
    [Fact]
    public async Task A_restart_after_stop_launches_nothing_and_reports_failure()
    {
        var f = new Fixture();
        await f.Service.StartAsync(CancellationToken.None);
        await f.Service.StopAsync(CancellationToken.None);

        Assert.False(await f.Service.RestartAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, f.Launcher.Attempts);
        Assert.Empty(f.Launcher.Alive);
        Assert.Equal(RcloneProcessStatus.Stopped, f.Service.Status);
    }

    // -- helpers -----------------------------------------------------------------------------------

    private sealed class Fixture
    {
        public Fixture()
        {
            Log = new RestartLog();
            Launcher = new TrackingLauncher(Log);
            PortGuard = new GatedPortGuard(Log);
            Time = new FakeTimeProvider();
            Service = new RcloneProcessService(
                Launcher,
                new FakeRcloneClient(),
                new RcloneProcessServiceOptions
                {
                    RcloneRcPort = 5572,
                    RcloneConfigPath = @"C:\fixture\rclone.conf",
                    RestartDelay = RestartDelay,
                    HealthCheckTimeout = TimeSpan.FromSeconds(10),
                    HealthCheckPollInterval = TimeSpan.FromMilliseconds(100),
                    StopTimeout = TimeSpan.FromSeconds(5),
                },
                Time,
                NullLogger<RcloneProcessService>.Instance,
                new RcloneRcCredential("bosun-test-user", "bosun-test-pass"),
                PortGuard);
            Service.StatusChanged += (_, e) => Log.Add($"status:{e.Status}:{e.FaultKind}");
        }

        public RestartLog Log { get; }

        public TrackingLauncher Launcher { get; }

        public GatedPortGuard PortGuard { get; }

        public FakeTimeProvider Time { get; }

        public RcloneProcessService Service { get; }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string failure, Fixture f, int mark)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"{failure}.{Environment.NewLine}{f.Log.Dump(mark)}");
            }

            await Task.Delay(5);
        }
    }

    /// <summary>Watches <paramref name="condition"/> for a short window and fails if it breaks.</summary>
    private static async Task AssertHoldsAsync(Func<bool> condition, string failure, Fixture f, int mark)
    {
        var until = DateTime.UtcNow + TimeSpan.FromMilliseconds(150);
        do
        {
            if (!condition())
            {
                Assert.Fail($"{failure}.{Environment.NewLine}{f.Log.Dump(mark)}");
            }

            await Task.Delay(5);
        }
        while (DateTime.UtcNow < until);

        Assert.True(condition(), $"{failure}.{Environment.NewLine}{f.Log.Dump(mark)}");
    }

    /// <summary>Advances the fake clock one <see cref="RestartDelay"/> at a time until the
    /// condition holds. The delay timer may be registered after any given advance, so a single
    /// advance is not enough (see RcloneProcessServiceTests.AdvanceUntilCompleteAsync).</summary>
    private static async Task AdvanceUntilAsync(Fixture f, Func<bool> condition, string failure, int mark)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"{failure}.{Environment.NewLine}{f.Log.Dump(mark)}");
            }

            f.Time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5);
        }
    }

    private static async Task AdvanceUntilCompleteAsync(Fixture f, Task task)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            f.Time.Advance(TimeSpan.FromMilliseconds(500));
            await Task.Delay(5);
        }
    }
}
