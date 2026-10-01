using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;
using Microsoft.Extensions.Logging;

namespace Bosun.Tests.Supervisor;

/// <summary>
/// bs-x57: <see cref="MountSupervisor.RunAsync"/> is the channel's single consumer, so if it ever
/// ends, every timer enqueues into a channel nobody reads and every command waits forever -- with
/// nothing left running to log it. These tests run the REAL loop (unlike the rest of the suite,
/// which pumps via <c>DrainAsync</c>) and assert that nothing except cancellation of its own token
/// can end it, that ending any other way is logged at Critical, and that the liveness signals a
/// future watchdog will consume (<see cref="MountSupervisor.IsLoopRunning"/>,
/// <see cref="MountSupervisor.LastLoopActivityUtc"/>) are accurate.
///
/// Deterministic: time is a fake clock, and every "wait for the loop" is an await on a task the
/// loop completes (a barrier command through the same FIFO channel), never a sleep.
/// <c>WaitAsync</c> is only a guard so a regression fails fast instead of hanging the run.
/// </summary>
public sealed class SupervisorLoopSurvivalTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    /// <summary>A command that completes only after every action queued before it has finished --
    /// the channel is FIFO with a single consumer, so this is a deterministic "loop has caught up".</summary>
    private static Task Barrier(MountSupervisor supervisor) =>
        supervisor.SetMountingAvailabilityAsync(MountingAvailability.Available).WaitAsync(Guard);

    private static SupervisorHarness NewHarness() =>
        new(HostFixtures.Build(HostFixtures.Global(), HostFixtures.OnDemand("archive", drive: "Q:")));

    [Fact]
    public async Task Loop_keeps_running_after_an_action_throws_an_arbitrary_exception()
    {
        var harness = NewHarness();
        using var cts = new CancellationTokenSource();
        var loop = harness.Supervisor.RunAsync(cts.Token);

        harness.Supervisor.Enqueue(_ => throw new InvalidOperationException("boom (synchronous throw)"));
        harness.Supervisor.Enqueue(_ => Task.FromException(new InvalidOperationException("boom (faulted task)")));
        await Barrier(harness.Supervisor);

        Assert.False(loop.IsCompleted);
        Assert.True(harness.Supervisor.IsLoopRunning);
        Assert.Equal(2, harness.Log.Entries.Count(e => e.Level == LogLevel.Error && e.Exception is InvalidOperationException));

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop.WaitAsync(Guard));
    }

    [Fact]
    public async Task Loop_keeps_running_after_a_non_shutdown_OperationCanceledException()
    {
        var harness = NewHarness();
        using var cts = new CancellationTokenSource();
        var loop = harness.Supervisor.RunAsync(cts.Token);

        using var someOtherSource = new CancellationTokenSource();
        someOtherSource.Cancel();

        // An rc/HTTP timeout, a bare OCE, and an OCE carrying a token that is NOT the supervisor's:
        // none of them is shutdown.
        harness.Supervisor.Enqueue(_ => throw new TaskCanceledException("HttpClient.Timeout elapsed"));
        harness.Supervisor.Enqueue(_ => throw new OperationCanceledException());
        harness.Supervisor.Enqueue(_ => throw new OperationCanceledException(someOtherSource.Token));
        await Barrier(harness.Supervisor);

        Assert.False(loop.IsCompleted);
        Assert.True(harness.Supervisor.IsLoopRunning);
        Assert.Equal(3, harness.Log.Entries.Count(e => e.Level == LogLevel.Error && e.Exception is OperationCanceledException));

        // And it still processes ordinary work afterwards.
        await harness.Supervisor.StartAsync().WaitAsync(Guard);
        Assert.Equal(MountState.Ready, harness.Snapshot("archive").State);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop.WaitAsync(Guard));
    }

    [Fact]
    public async Task Loop_exits_cleanly_only_when_its_own_token_is_cancelled()
    {
        var harness = NewHarness();
        using var cts = new CancellationTokenSource();
        var loop = harness.Supervisor.RunAsync(cts.Token);
        await Barrier(harness.Supervisor);
        Assert.False(loop.IsCompleted);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop.WaitAsync(Guard));
        Assert.True(loop.IsCanceled);
        Assert.False(harness.Supervisor.IsLoopRunning);
        Assert.DoesNotContain(harness.Log.Entries, e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task An_action_interrupted_by_shutdown_ends_the_loop_without_an_error_log()
    {
        var harness = NewHarness();
        using var cts = new CancellationTokenSource();
        var loop = harness.Supervisor.RunAsync(cts.Token);

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Supervisor.Enqueue(async ct =>
        {
            started.SetResult();
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct);
        });
        await started.Task.WaitAsync(Guard);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop.WaitAsync(Guard));
        Assert.False(harness.Supervisor.IsLoopRunning);
        Assert.DoesNotContain(harness.Log.Entries, e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task Loop_logs_Critical_if_it_exits_without_shutdown_being_requested()
    {
        var harness = NewHarness();
        using var cts = new CancellationTokenSource();
        var loop = harness.Supervisor.RunAsync(cts.Token);
        await Barrier(harness.Supervisor);

        // Completing the command channel is the one non-cancellation way the loop can end.
        await harness.Supervisor.DisposeAsync();
        await loop.WaitAsync(Guard);

        Assert.False(harness.Supervisor.IsLoopRunning);
        Assert.Contains(harness.Log.Entries, e =>
            e.Level == LogLevel.Critical && e.Message.Contains("without shutdown being requested", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_real_loop_keeps_retrying_a_drain_through_repeated_unmount_timeouts()
    {
        // The 2026-09-28 incident, end to end through the production loop and fake clock.
        var harness = new SupervisorHarness(HostFixtures.Build(
            HostFixtures.Global(),
            HostFixtures.OnDemand("archive", drive: "Q:"),
            HostFixtures.OnDemand("other", drive: "R:")));
        using var cts = new CancellationTokenSource();
        var loop = harness.Supervisor.RunAsync(cts.Token);
        await harness.Supervisor.StartAsync().WaitAsync(Guard);
        await harness.Supervisor.RequestMountAsync("archive").WaitAsync(Guard);
        harness.Rclone.MakeUnmountThrow(new TaskCanceledException("HttpClient.Timeout of 100 seconds elapsing"));

        await harness.Supervisor.RequestUnmountAsync("archive").WaitAsync(Guard);
        for (var i = 0; i < 3; i++)
        {
            harness.Time.Advance(TimeSpan.FromSeconds(5)); // the drain's retry timer enqueues an attempt
            await Barrier(harness.Supervisor);
        }

        Assert.False(loop.IsCompleted, "the supervisor loop died");
        Assert.Equal(MountState.Draining, harness.Snapshot("archive").State);
        Assert.True(harness.Rclone.UnmountCallCountFor("Q:") >= 4);

        // Commands for other hosts are still served.
        await harness.Supervisor.RequestMountAsync("other").WaitAsync(Guard);
        Assert.Equal(MountState.Mounted, harness.Snapshot("other").State);

        harness.Rclone.StopThrowingFromUnmount();
        harness.Time.Advance(TimeSpan.FromSeconds(5));
        await Barrier(harness.Supervisor);
        Assert.NotEqual(MountState.Draining, harness.Snapshot("archive").State);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop.WaitAsync(Guard));
    }

    // ----------------------------------------------------------------------------------------
    // Liveness signals (consumed by a future watchdog)
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void Before_the_loop_starts_it_reports_not_running_and_no_activity()
    {
        var harness = NewHarness();

        Assert.False(harness.Supervisor.IsLoopRunning);
        Assert.Equal(DateTimeOffset.MinValue, harness.Supervisor.LastLoopActivityUtc);
    }

    [Fact]
    public async Task Starting_the_loop_marks_it_running_and_stamps_activity_with_the_injected_clock()
    {
        var harness = NewHarness();
        using var cts = new CancellationTokenSource();
        var startTime = harness.Time.GetUtcNow();

        var loop = harness.Supervisor.RunAsync(cts.Token);

        Assert.True(harness.Supervisor.IsLoopRunning);
        Assert.Equal(startTime, harness.Supervisor.LastLoopActivityUtc);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop.WaitAsync(Guard));
    }

    [Fact]
    public async Task Activity_is_stamped_when_an_action_begins_and_again_when_it_finishes()
    {
        var harness = NewHarness();
        using var cts = new CancellationTokenSource();
        var loop = harness.Supervisor.RunAsync(cts.Token);
        var t0 = harness.Time.GetUtcNow();

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Supervisor.Enqueue(async _ =>
        {
            started.SetResult();
            await release.Task;
        });

        await started.Task.WaitAsync(Guard);

        // The stamp is taken from the injected clock when the action BEGAN, so a wedged action
        // leaves it stale while IsLoopRunning stays true -- the signature a watchdog looks for.
        Assert.Equal(t0, harness.Supervisor.LastLoopActivityUtc);
        Assert.True(harness.Supervisor.IsLoopRunning);

        harness.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(t0, harness.Supervisor.LastLoopActivityUtc);

        release.SetResult();
        await Barrier(harness.Supervisor);

        Assert.Equal(t0 + TimeSpan.FromSeconds(30), harness.Supervisor.LastLoopActivityUtc);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop.WaitAsync(Guard));
    }

    [Fact]
    public async Task A_failing_action_still_counts_as_loop_activity()
    {
        var harness = NewHarness();
        using var cts = new CancellationTokenSource();
        var loop = harness.Supervisor.RunAsync(cts.Token);
        await Barrier(harness.Supervisor);

        harness.Time.Advance(TimeSpan.FromSeconds(11));
        harness.Supervisor.Enqueue(_ => throw new TaskCanceledException("timeout"));
        await Barrier(harness.Supervisor);

        Assert.Equal(harness.Time.GetUtcNow(), harness.Supervisor.LastLoopActivityUtc);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop.WaitAsync(Guard));
    }

    [Fact]
    public async Task Exiting_marks_the_loop_not_running_and_stamps_the_exit()
    {
        var harness = NewHarness();
        using var cts = new CancellationTokenSource();
        var loop = harness.Supervisor.RunAsync(cts.Token);
        await Barrier(harness.Supervisor);

        harness.Time.Advance(TimeSpan.FromSeconds(42));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop.WaitAsync(Guard));

        Assert.False(harness.Supervisor.IsLoopRunning);
        Assert.Equal(harness.Time.GetUtcNow(), harness.Supervisor.LastLoopActivityUtc);
    }

    [Fact]
    public async Task The_DrainAsync_test_pump_does_not_claim_the_loop_is_running()
    {
        var harness = NewHarness();

        await harness.StartAsync();

        Assert.False(harness.Supervisor.IsLoopRunning);
    }
}
