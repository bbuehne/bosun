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
        // A StartAsync that walks a host needs several rc calls (listmounts, then mount). The loop's
        // activity stamp must advance as EACH returns, not only when the whole action begins and ends
        // -- otherwise a many-host start would look stalled to the watchdog while working.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero));
        var rclone = new FakeRcloneMountClient();
        MountSupervisor? supervisor = null;
        var observed = new List<(DateTimeOffset Now, DateTimeOffset LastActivity)>();
        var slow = new SlowRcloneClient(rclone, () =>
        {
            observed.Add((time.GetUtcNow(), supervisor!.LastLoopActivityUtc));
            time.Advance(TimeSpan.FromSeconds(100));
        });
        supervisor = new MountSupervisor(
            new FakeHostConfigStore(HostFixtures.Build(HostFixtures.Global(), HostFixtures.Persistent("alpha", drive: "P:"))),
            slow, new FakeProbe(), time, new CapturingLogger<MountSupervisor>());

        var start = supervisor.StartAsync();
        await supervisor.DrainAsync();
        await start;

        Assert.True(observed.Count >= 2, $"expected at least two rc calls in StartAsync, saw {observed.Count}");

        // At every rc call after the first, the stamp was the moment the previous call returned --
        // 100 s ago would be the action's start.
        foreach (var (now, lastActivity) in observed.Skip(1))
        {
            Assert.Equal(now, lastActivity);
        }
    }

    /// <summary>Delegates to a fake and runs a hook at the start of each call the supervisor makes.</summary>
    private sealed class SlowRcloneClient(IRcloneClient inner, Action onCall) : IRcloneClient
    {
        public Task<RcloneVersionInfo> GetVersionAsync(CancellationToken ct) { onCall(); return inner.GetVersionAsync(ct); }

        public Task CreateConfigAsync(string remoteName, string type, IReadOnlyDictionary<string, string> parameters, CancellationToken ct) =>
            inner.CreateConfigAsync(remoteName, type, parameters, ct);

        public Task<IReadOnlyDictionary<string, string>?> GetConfigAsync(string remoteName, CancellationToken ct) =>
            inner.GetConfigAsync(remoteName, ct);

        public Task<RcloneMountResult> MountAsync(RcloneMountRequest request, CancellationToken ct) { onCall(); return inner.MountAsync(request, ct); }

        public Task UnmountAsync(string mountPoint, CancellationToken ct) { onCall(); return inner.UnmountAsync(mountPoint, ct); }

        public Task<IReadOnlyList<RcloneMountInfo>> ListMountsAsync(CancellationToken ct) { onCall(); return inner.ListMountsAsync(ct); }

        public Task<IReadOnlyList<RcloneListItem>> ListAsync(string fs, string remote, CancellationToken ct) =>
            inner.ListAsync(fs, remote, ct);
    }
}
