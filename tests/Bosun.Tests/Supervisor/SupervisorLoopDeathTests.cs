using Bosun.Probe;
using Bosun.Rclone;
using Bosun.Supervisor;
using Bosun.Tests.Configuration.Fakes;
using Bosun.Tests.SessionMonitor.Fakes;
using Bosun.Tests.Supervisor.Fakes;
using Bosun.Tests.Supervisor.Support;
using Bosun.Watchdog;

namespace Bosun.Tests.Supervisor;

/// <summary>
/// bs-6to / ADR-020 Decision 7. On 2026-10-01 every <c>EnqueueAndWait</c> caller (shutdown, UI
/// commands) waited on a completion that only the dead loop would have set, so the UI hung. Whenever
/// the loop exits -- for any reason -- every waiting caller must be failed, and later callers must
/// fail at once. These tests run the REAL loop; every wait is on a task the loop completes, with
/// <c>WaitAsync</c> only as a guard so a regression fails fast instead of hanging the run.
/// </summary>
public sealed class SupervisorLoopDeathTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    private static SupervisorHarness NewHarness() =>
        new(HostFixtures.Build(HostFixtures.Global(), HostFixtures.OnDemand("archive", drive: "Q:")));

    /// <summary>Parks the loop inside an action that ends only when the loop is cancelled, and
    /// returns once the loop is demonstrably inside it.</summary>
    private static async Task ParkLoopAsync(MountSupervisor supervisor)
    {
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        supervisor.Enqueue(async ct =>
        {
            parked.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        });
        await parked.Task.WaitAsync(Guard);
    }

    [Fact]
    public async Task A_command_waiting_when_the_loop_exits_is_failed_not_left_waiting()
    {
        var harness = NewHarness();
        using var cts = new CancellationTokenSource();
        var loop = harness.Supervisor.RunAsync(cts.Token);
        await ParkLoopAsync(harness.Supervisor);

        // Queued behind the parked action: nothing will ever reach it.
        var pending = harness.Supervisor.SetMountingAvailabilityAsync(MountingAvailability.Available);
        Assert.False(pending.IsCompleted);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop.WaitAsync(Guard));

        var failure = await Assert.ThrowsAsync<SupervisorStoppedException>(() => pending.WaitAsync(Guard));
        Assert.Contains("not running", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_waiting_command_is_failed_not_just_the_first()
    {
        var harness = NewHarness();
        using var cts = new CancellationTokenSource();
        var loop = harness.Supervisor.RunAsync(cts.Token);
        await ParkLoopAsync(harness.Supervisor);

        var first = harness.Supervisor.SetMountingAvailabilityAsync(MountingAvailability.Available);
        var second = harness.Supervisor.StartAsync();
        var third = harness.Supervisor.NetworkChangedAsync();

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop.WaitAsync(Guard));

        await Assert.ThrowsAsync<SupervisorStoppedException>(() => first.WaitAsync(Guard));
        await Assert.ThrowsAsync<SupervisorStoppedException>(() => second.WaitAsync(Guard));
        await Assert.ThrowsAsync<SupervisorStoppedException>(() => third.WaitAsync(Guard));
    }

    [Fact]
    public async Task A_command_issued_after_the_loop_is_dead_fails_fast()
    {
        var harness = NewHarness();
        using var cts = new CancellationTokenSource();
        var loop = harness.Supervisor.RunAsync(cts.Token);
        await Task.Yield();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop.WaitAsync(Guard));

        var late = harness.Supervisor.StartAsync();

        // Already faulted: no loop, no wait. (No WaitAsync: it must not need one.)
        Assert.True(late.IsFaulted);
        Assert.IsType<SupervisorStoppedException>(late.Exception!.InnerException);
    }

    [Fact]
    public async Task The_loop_exiting_because_its_channel_was_completed_also_fails_late_commands()
    {
        var harness = NewHarness();
        using var cts = new CancellationTokenSource();
        var loop = harness.Supervisor.RunAsync(cts.Token);

        // The non-cancellation way out (see SupervisorLoopSurvivalTests): the channel is completed.
        await harness.Supervisor.DisposeAsync();
        await loop.WaitAsync(Guard);

        var late = harness.Supervisor.SetMountingAvailabilityAsync(MountingAvailability.Available);

        Assert.True(late.IsFaulted);
        Assert.IsType<SupervisorStoppedException>(late.Exception!.InnerException);
    }

    [Fact]
    public async Task A_supervisor_whose_loop_has_not_started_yet_still_queues_commands()
    {
        // Fail-fast applies to a loop that has DIED, not one that has not begun: StartupOrchestrator
        // legitimately issues commands as the loop is starting, and tests pump with DrainAsync.
        var harness = NewHarness();

        await harness.StartAsync();

        Assert.Equal(MountState.Ready, harness.Snapshot("archive").State);
    }

    [Fact]
    public async Task A_caller_whose_token_is_cancelled_stops_waiting_even_though_the_loop_is_wedged()
    {
        var harness = NewHarness();
        using var cts = new CancellationTokenSource();
        var loop = harness.Supervisor.RunAsync(cts.Token);
        await ParkLoopAsync(harness.Supervisor);

        using var callerCancel = new CancellationTokenSource();
        var stuck = harness.Supervisor.StopAsync(callerCancel.Token);
        Assert.False(stuck.IsCompleted);

        callerCancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stuck.WaitAsync(Guard));

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop.WaitAsync(Guard));
    }

    // ------------------------------------------------------------------------------------------
    // Liveness signals the watchdog reads
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task IsStarted_follows_StartAsync_and_StopAsync()
    {
        var harness = NewHarness();
        ISupervisorLiveness liveness = harness.Supervisor;
        Assert.False(liveness.IsStarted);

        await harness.StartAsync();
        Assert.True(liveness.IsStarted);

        await harness.RunAsync(() => harness.Supervisor.StopAsync());
        Assert.False(liveness.IsStarted);
    }

    [Fact]
    public async Task Each_rc_call_stamps_loop_activity_so_a_long_action_keeps_proving_it_is_alive()
    {
        // One action -- unmounting a host -- makes two rc calls back to back (unmount, then
        // listmounts to verify). The loop's activity stamp must advance as EACH returns, not only
        // when the whole action begins and ends; otherwise a start or resume that walks many hosts
        // would look stalled to the watchdog while it is working.
        // A clock that moves forward as rc calls "take time" without firing any timer: advancing a
        // real fake clock would fire the reconciliation timer, whose action makes more rc calls.
        var time = new Bosun.Tests.Watchdog.SleepableTimeProvider(new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero)));
        var observing = false;
        var observed = new List<(string Call, DateTimeOffset Now, DateTimeOffset LastActivity)>();
        MountSupervisor? supervisor = null;
        var slow = new SlowRcloneClient(new FakeRcloneMountClient(), call =>
        {
            if (!observing)
            {
                return;
            }

            observed.Add((call, time.GetUtcNow(), supervisor!.LastLoopActivityUtc));
            time.Sleep(TimeSpan.FromSeconds(100)); // each rc call takes 100 s
        });
        supervisor = new MountSupervisor(
            new FakeHostConfigStore(HostFixtures.Build(HostFixtures.Global(), HostFixtures.OnDemand("archive", drive: "Q:"))),
            slow, new FakeProbe(), time, new CapturingLogger<MountSupervisor>());

        await Run(supervisor, supervisor.StartAsync());
        await Run(supervisor, supervisor.RequestMountAsync("archive"));
        observing = true;
        await Run(supervisor, supervisor.RequestUnmountAsync("archive"));

        var unmountIndex = observed.FindIndex(o => o.Call == nameof(IRcloneClient.UnmountAsync));
        Assert.True(unmountIndex >= 0 && unmountIndex + 1 < observed.Count, "expected an unmount followed by another rc call");
        var next = observed[unmountIndex + 1];
        Assert.Equal(nameof(IRcloneClient.ListMountsAsync), next.Call);

        // When the second call began, the stamp was the instant the first returned (== now), not
        // 100 s earlier when the action began.
        Assert.Equal(next.Now, next.LastActivity);
    }

    private static async Task Run(MountSupervisor supervisor, Task command)
    {
        await supervisor.DrainAsync();
        await command;
    }

    [Fact]
    public async Task The_stamping_decorator_stamps_after_every_call_including_a_failing_one()
    {
        var stamps = 0;
        var inner = new FakeRcloneMountClient();
        var client = new ActivityStampingRcloneClient(inner, () => stamps++);

        await client.GetVersionAsync(CancellationToken.None);
        await client.ListMountsAsync(CancellationToken.None);
        inner.MakeListMountsThrow(new InvalidOperationException("boom"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListMountsAsync(CancellationToken.None));

        Assert.Equal(3, stamps);
    }

    /// <summary>Delegates to a fake and runs a hook, told which call it is, at the start of each call
    /// the supervisor makes.</summary>
    private sealed class SlowRcloneClient(IRcloneClient inner, Action<string> onCall) : IRcloneClient
    {
        public Task<RcloneVersionInfo> GetVersionAsync(CancellationToken ct)
        {
            onCall(nameof(GetVersionAsync));
            return inner.GetVersionAsync(ct);
        }

        public Task CreateConfigAsync(string remoteName, string type, IReadOnlyDictionary<string, string> parameters, CancellationToken ct) =>
            inner.CreateConfigAsync(remoteName, type, parameters, ct);

        public Task<IReadOnlyDictionary<string, string>?> GetConfigAsync(string remoteName, CancellationToken ct) =>
            inner.GetConfigAsync(remoteName, ct);

        public Task<RcloneMountResult> MountAsync(RcloneMountRequest request, CancellationToken ct)
        {
            onCall(nameof(MountAsync));
            return inner.MountAsync(request, ct);
        }

        public Task UnmountAsync(string mountPoint, CancellationToken ct)
        {
            onCall(nameof(UnmountAsync));
            return inner.UnmountAsync(mountPoint, ct);
        }

        public Task<IReadOnlyList<RcloneMountInfo>> ListMountsAsync(CancellationToken ct)
        {
            onCall(nameof(ListMountsAsync));
            return inner.ListMountsAsync(ct);
        }

        public Task<IReadOnlyList<RcloneListItem>> ListAsync(string fs, string remote, CancellationToken ct) =>
            inner.ListAsync(fs, remote, ct);
    }
}
