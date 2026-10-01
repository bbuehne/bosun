using Bosun.Configuration;
using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Configuration.Fakes;
using Bosun.Tests.Supervisor.Support;
using Rc = Bosun.Tests.Supervisor.Independent.RcTimeout.FaultInjectingRcloneDouble;

namespace Bosun.Tests.Supervisor.Independent.RcTimeout;

/// <summary>
/// bs-x57, item 3, tested through the real production loop, <see cref="MountSupervisor.RunAsync"/>,
/// not the test-only <c>DrainAsync</c> pump. These tests ask whether RunAsync keeps processing
/// after an action throws an arbitrary exception, and after a cancellation-shaped exception that
/// did not come from its own token. They also check that it ends only once its own token is
/// cancelled.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these are separate from the <c>DrainAsync</c> tests.</b> The incident was the loop
/// dying. <c>DrainAsync</c> is a different method. A fix could make every action handle its own
/// rc failures, so the DrainAsync tests pass, and still leave <c>RunAsync</c> one unhandled
/// exception away from dying for good. Only these tests catch that.
/// </para>
/// <para>
/// <b>Determinism without sleeps.</b> The loop runs on a thread-pool thread, but the tests never
/// guess at timing:
/// </para>
/// <list type="bullet">
/// <item>Time is still the injected <see cref="FakeTimeProvider"/>. Advancing it only QUEUES
/// timer work onto the channel.</item>
/// <item>"Has the loop processed everything queued so far?" is answered by a barrier command,
/// <c>RecordActivityAsync</c> on a <c>mode = "none"</c> host, which does nothing. The channel is
/// FIFO with one consumer, so when the barrier completes, everything queued before it has run.</item>
/// <item>"Has this particular call happened?" is answered by a <see cref="TaskCompletionSource"/>
/// that the fake completes.</item>
/// <item>Every wait races the awaited task against the loop task itself and a 10-second upper
/// bound. A dead loop fails the test immediately, with the loop's status in the message. The
/// bound is only reached if something truly hangs. It is never slept through on a passing
/// run.</item>
/// </list>
/// </remarks>
public sealed class SupervisorLoopSurvivalTests
{
    private const string Barrier = "quiet";

    /// <summary>
    /// The incident, through the real loop. Two periodic deep-probe failures drain alpha from a
    /// timer action. alpha's <c>mount/unmount</c> fails with the row's exception. Bug caught: the
    /// failure escapes the action and ends <c>RunAsync</c>, so bravo's later mount request is
    /// never processed. The control row passes on any build and shows the scenario itself is
    /// sound.
    /// </summary>
    [Theory]
    [MemberData(nameof(RcFaults.All), MemberType = typeof(RcFaults))]
    public async Task RunAsync_keeps_serving_other_hosts_after_a_timer_driven_drain_unmount_fails(string kind)
    {
        await using var h = new LoopHarness(TwoHostConfig());
        await h.WithinAsync(h.Supervisor.StartAsync(), "StartAsync");
        Assert.Equal(MountState.Mounted, h.State("alpha"));
        Assert.Equal(MountState.Ready, h.State("bravo"));

        var unmountFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Rclone.OnCall = c =>
        {
            if (c is { Operation: Rc.Unmount, MountPoint: "P:", Faulted: true })
            {
                unmountFailed.TrySetResult();
            }
        };
        h.Rclone.FailUnmount("P:", RcFaults.Factory(kind), times: int.MaxValue);
        h.Probe.Scripted.SetDeep("alpha", DeepProbeOutcome.Failed, times: 2);

        h.Time.Advance(TimeSpan.FromSeconds(300));
        await h.BarrierAsync("alpha's first deep-probe failure");
        h.Time.Advance(TimeSpan.FromSeconds(300));
        await h.WithinAsync(unmountFailed.Task, "alpha's drain attempting mount/unmount");

        await h.WithinAsync(h.Supervisor.RequestMountAsync("bravo"), "bravo's mount request, queued after alpha's failed unmount");

        Assert.Equal(MountState.Mounted, h.State("bravo"));
        Assert.Equal(MountState.Draining, h.State("alpha"));
        h.AssertLoopStillRunning();
        await h.StopAndAssertEndedAsync();
    }

    /// <summary>
    /// Regression guard. Passes on current main, and must keep passing. A timer-driven action
    /// throws an ordinary exception: here a deep probe breaking its own contract. The loop must
    /// log it and keep going. Bug caught: a bs-x57 fix that narrows the loop's catch-all, or
    /// rethrows from it, and so makes ANY action bug fatal to every host.
    /// </summary>
    [Fact]
    public async Task RunAsync_keeps_processing_after_a_timer_driven_action_throws_an_arbitrary_exception()
    {
        await using var h = new LoopHarness(TwoHostConfig());
        await h.WithinAsync(h.Supervisor.StartAsync(), "StartAsync");

        var thrown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Probe.OnDeepThrow = _ => thrown.TrySetResult();
        h.Probe.ThrowOnDeep("alpha", () => new InvalidOperationException("simulated: a supervisor action blew up"), times: 1);

        h.Time.Advance(TimeSpan.FromSeconds(300));
        await h.WithinAsync(thrown.Task, "alpha's deep probe (scripted to throw)");

        await h.WithinAsync(h.Supervisor.RequestMountAsync("bravo"), "bravo's mount request, queued after the throwing action");
        Assert.Equal(MountState.Mounted, h.State("bravo"));
        h.AssertLoopStillRunning();
        await h.StopAndAssertEndedAsync();
    }

    /// <summary>
    /// The same as the arbitrary-exception test, but the action throws a cancellation-shaped
    /// exception while RunAsync's own token is NOT cancelled. Bug caught: the loop's catch
    /// filters on exception TYPE (<c>ex is not OperationCanceledException</c>) instead of on its
    /// own token. That reads a stray timeout as shutdown and ends RunAsync. This test hits the
    /// loop's own guard directly, separately from the rc call sites, so a fix that patches only
    /// the call sites still fails here.
    /// </summary>
    [Theory]
    [MemberData(nameof(RcFaults.CancellationShaped), MemberType = typeof(RcFaults))]
    public async Task RunAsync_keeps_processing_after_an_action_throws_a_cancellation_its_own_token_did_not_cause(string kind)
    {
        await using var h = new LoopHarness(TwoHostConfig());
        await h.WithinAsync(h.Supervisor.StartAsync(), "StartAsync");

        var thrown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Probe.OnDeepThrow = _ => thrown.TrySetResult();
        h.Probe.ThrowOnDeep("alpha", RcFaults.Factory(kind), times: 1);

        h.Time.Advance(TimeSpan.FromSeconds(300));
        await h.WithinAsync(thrown.Task, "alpha's deep probe (scripted to throw)");

        await h.WithinAsync(h.Supervisor.RequestMountAsync("bravo"), "bravo's mount request, queued after the throwing action");
        Assert.Equal(MountState.Mounted, h.State("bravo"));
        h.AssertLoopStillRunning();
        await h.StopAndAssertEndedAsync();
    }

    /// <summary>
    /// Regression guard, the other half of item 3. Passes on current main, and must keep
    /// passing. When RunAsync's OWN token is cancelled while an rc call is in flight (and that
    /// call honours the token), RunAsync must end. Bug caught: a bs-x57 fix that over-corrects
    /// and swallows every <see cref="OperationCanceledException"/>, so the loop never ends and
    /// app shutdown hangs. The incident ended in exactly that kind of hang on close (WER AppHang).
    /// </summary>
    [Fact]
    public async Task RunAsync_ends_when_its_own_token_is_cancelled_even_with_an_rc_call_in_flight()
    {
        await using var h = new LoopHarness(TwoHostConfig());
        await h.WithinAsync(h.Supervisor.StartAsync(), "StartAsync");
        await h.WithinAsync(h.Supervisor.RequestMountAsync("bravo"), "bravo's mount request");

        var inFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Rclone.BeforeUnmount = async (mountPoint, ct) =>
        {
            if (mountPoint == "Q:")
            {
                inFlight.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
        };

        // Deliberately not awaited: the spec does not say what this caller sees once the
        // supervisor shuts down. Its outcome is observed so it cannot surface as unobserved.
        var unmountRequest = h.Supervisor.RequestUnmountAsync("bravo");
        _ = unmountRequest.ContinueWith(t => t.Exception, TaskScheduler.Default);

        await h.WithinAsync(inFlight.Task, "bravo's mount/unmount to be in flight");
        await h.StopAndAssertEndedAsync();
    }

    /// <summary>alpha: persistent, mounted at startup, deep-probed every 300 s, with the shallow
    /// cadence pushed out to an hour so it does not add noise. bravo: on-demand, resting in Ready.
    /// quiet: <c>mode = "none"</c>, the barrier host.</summary>
    private static BosunConfig TwoHostConfig() => HostFixtures.Build(
        HostFixtures.Global(mountedProbeIntervalSeconds: 3600, mountedDeepProbeIntervalSeconds: 300),
        HostFixtures.Persistent("alpha", probeIntervalSeconds: 3600, drive: "P:"),
        HostFixtures.OnDemand("bravo", drive: "Q:"),
        HostFixtures.None(Barrier));

    /// <summary>
    /// Runs <see cref="MountSupervisor.RunAsync"/> on a thread-pool thread for one test, wired to
    /// the same doubles as <see cref="RcTimeoutHarness"/>.
    /// </summary>
    private sealed class LoopHarness : IAsyncDisposable
    {
        private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

        private readonly RcTimeoutHarness inner;
        private readonly CancellationTokenSource cts = new();
        private readonly Task loop;

        public LoopHarness(BosunConfig config)
        {
            inner = new RcTimeoutHarness(config);
            loop = Task.Run(() => inner.Supervisor.RunAsync(cts.Token));
        }

        public MountSupervisor Supervisor => inner.Supervisor;

        public FakeTimeProvider Time => inner.Time;

        public FaultInjectingRcloneDouble Rclone => inner.Rclone;

        public FaultableProbe Probe => inner.Probe;

        public MountState State(string hostKey) => inner.State(hostKey);

        /// <summary>Awaits <paramref name="operation"/>, failing fast if the loop has died and
        /// failing after <see cref="Bound"/> if neither happens.</summary>
        public async Task WithinAsync(Task operation, string what)
        {
            var winner = await Task.WhenAny(operation, loop, Task.Delay(Bound));
            if (operation.IsCompleted)
            {
                await operation;
                return;
            }

            Assert.Fail(winner == loop
                ? $"{what} never ran: {LoopStatus()}"
                : $"{what} did not complete within {Bound.TotalSeconds}s. {LoopStatus()}");
        }

        /// <summary>Returns once everything queued before this call has been processed. See the
        /// class remarks.</summary>
        public Task BarrierAsync(string after) =>
            WithinAsync(Supervisor.RecordActivityAsync(Barrier), $"barrier after {after}");

        public void AssertLoopStillRunning() =>
            Assert.False(loop.IsCompleted, LoopStatus());

        public async Task StopAndAssertEndedAsync()
        {
            await cts.CancelAsync();
            var winner = await Task.WhenAny(loop, Task.Delay(Bound));
            Assert.True(
                winner == loop,
                $"RunAsync did not end within {Bound.TotalSeconds}s of its own token being cancelled: shutdown would hang.");
            Assert.True(
                loop.Status is TaskStatus.RanToCompletion or TaskStatus.Canceled ||
                loop.Exception?.InnerException is OperationCanceledException,
                $"RunAsync ended on cancellation with an unexpected fault: {loop.Exception?.InnerException}");
        }

        public async ValueTask DisposeAsync()
        {
            if (!cts.IsCancellationRequested)
            {
                await cts.CancelAsync();
            }

            await Task.WhenAny(loop, Task.Delay(Bound));
            _ = loop.ContinueWith(t => t.Exception, TaskScheduler.Default);
            cts.Dispose();
        }

        private string LoopStatus()
        {
            if (!loop.IsCompleted)
            {
                return "RunAsync is still running.";
            }

            var fault = loop.Exception?.InnerException;
            return $"RunAsync has ENDED (status {loop.Status}{(fault is null ? string.Empty : $", {PumpOutcome.Describe(fault)}")}) " +
                $"although its own token was {(cts.IsCancellationRequested ? "cancelled" : "NEVER cancelled")}. " +
                "It is the supervisor's only channel consumer, so every host is now permanently silent (bs-x57).";
        }
    }
}
