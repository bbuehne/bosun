using Bosun.Configuration;
using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor.Independent.SuspendPreempt;

/// <summary>
/// bs-7d7 / ADR-023 decision 7 and §4 rule 5: the suspend budget counts "from the moment suspend arrives --
/// the instant SuspendAsync is called, not when the channel gets round to it". A suspend that waited in the
/// queue gets less time, never more. Written from the ADR, not from the implementation.
/// </summary>
/// <remarks>
/// <para>
/// In each test a command that is not preemptible (a "retry now" whose probe is held) occupies the channel
/// when the suspend is called, and the fake clock moves while the suspend waits. Only then is the blocker
/// released, so the suspend is dequeued <c>wait</c> seconds after it arrived. The existing
/// <c>SuspendWhileDrainingSpecTests</c> cannot see this defect: their clock never moves while the suspend
/// is queued (bs-7d7's description).
/// </para>
/// <para>
/// Escalation is observed the same way <c>SuspendBudgetHarness</c> observes it: the warning-level log line
/// naming the host and containing "escalat". Every unmount here is scripted never to confirm, so the only
/// way a drain ends is the escalation being timed.
/// </para>
/// </remarks>
public sealed class BudgetFromArrivalSpecTests
{
    private const int Budget = 3;
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(10);

    private static PreemptSpecHarness NewHarness(params HostConfig[] hosts) =>
        new(HostFixtures.Build(
            HostFixtures.Global(failuresBeforeUnmount: 3, suspendUnmountTimeoutSeconds: Budget),
            [HostFixtures.Persistent("slow", probeIntervalSeconds: 60, drive: "S:"), .. hosts]));

    /// <summary>Calls SuspendAsync while a held command occupies the channel, moves the clock
    /// <paramref name="queuedFor"/>, then lets the suspend run. Returns the arrival instant.</summary>
    private static async Task<DateTimeOffset> SuspendThatWaitedInTheQueueAsync(PreemptSpecHarness h, TimeSpan queuedFor)
    {
        var hold = h.HProbe.HoldNextShallow(PreemptSpecHarness.HostnameOf("slow"), OnCancel.Ignore);
        var blocker = h.Supervisor.RequestRetryNowAsync("slow");
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(hold, "the blocker's probe");

        var arrival = h.Now;
        var suspend = SuspendCaller.Call(h.Supervisor);
        h.FireTimers(queuedFor);
        hold.Release(ShallowProbeOutcome.ConnectionRefused);
        await h.FinishAsync(pump, "the blocker never finished", blocker, suspend);
        Assert.Equal(arrival + queuedFor, h.Now);
        return arrival;
    }

    private static async Task<PreemptSpecHarness> StartedAsync(params HostConfig[] hosts)
    {
        var h = NewHarness(hosts);
        h.Probe.SetShallow(PreemptSpecHarness.HostnameOf("slow"), ShallowProbeOutcome.ConnectionRefused);
        await h.StartAsync();
        return h;
    }

    private static void AssertEscalatedOnceWithin(PreemptSpecHarness h, string hostKey, DateTimeOffset expected)
    {
        var escalations = h.EscalationTimes(hostKey);
        Assert.True(escalations.Count == 1, $"expected one escalation for {hostKey}, saw {escalations.Count}");
        var at = escalations[0];
        Assert.True(
            at >= expected - Step && at <= expected + Step,
            $"{hostKey} escalated at +{(at - expected).TotalMilliseconds:0} ms from the expected instant {expected:HH:mm:ss.fff}");
    }

    /// <summary>
    /// <b>Protects:</b> decision 7 for a host that is <c>Mounted</c> when the suspend arrives: "suspend drains
    /// start their clock from" the arrival time.
    /// <b>Catches:</b> the suspend drain's clock started at dequeue -- here escalation at arrival + 2 + 3
    /// instead of arrival + 3, two seconds after Windows may already have suspended the machine.
    /// </summary>
    [Fact]
    public async Task A_mounted_host_drained_by_a_suspend_that_waited_2s_escalates_at_arrival_plus_budget()
    {
        var h = await StartedAsync(HostFixtures.Persistent("m1", probeIntervalSeconds: 60, drive: "M:"));
        Assert.Equal(MountState.Mounted, h.State("m1"));
        h.Rclone.ConfirmUnmountAtCall("M:", int.MaxValue);

        var arrival = await SuspendThatWaitedInTheQueueAsync(h, TimeSpan.FromSeconds(2));
        await h.AdvanceAsync(TimeSpan.FromSeconds(10), Step);

        AssertEscalatedOnceWithin(h, "m1", arrival.AddSeconds(Budget));
    }

    /// <summary>
    /// <b>Protects:</b> decision 7 / rule 5 for a host ALREADY draining: "a host already draining gets the
    /// deadline arrival + budget".
    /// <b>Catches:</b> the already-draining host's deadline computed as dequeue + budget.
    /// </summary>
    [Fact]
    public async Task An_already_draining_host_escalates_at_arrival_plus_budget_when_the_suspend_waited_in_the_queue()
    {
        var h = await StartedAsync(HostFixtures.OnDemand("od", drive: "Q:"));
        await h.RunAsync(() => h.Supervisor.RequestMountAsync("od"));
        h.Rclone.ConfirmUnmountAtCall("Q:", int.MaxValue);
        await h.RunAsync(() => h.Supervisor.RequestUnmountAsync("od"));
        Assert.Equal(MountState.Draining, h.State("od"));
        var t0 = h.Now;

        await h.AdvanceAsync(TimeSpan.FromSeconds(1), Step);
        var arrival = await SuspendThatWaitedInTheQueueAsync(h, TimeSpan.FromSeconds(1.5));
        await h.AdvanceAsync(TimeSpan.FromSeconds(12), Step);

        Assert.Equal(t0.AddSeconds(1), arrival);
        AssertEscalatedOnceWithin(h, "od", arrival.AddSeconds(Budget)); // t0 + 4, not t0 + 5.5
    }

    /// <summary>
    /// <b>Protects:</b> "gets less time, never more" at its extreme: a suspend that waited longer than the
    /// whole budget has none left.
    /// <b>Catches:</b> a fresh budget granted at dequeue: escalation at arrival + 5 + 3 although Windows' window
    /// closed at about arrival + 2.
    /// </summary>
    [Fact]
    public async Task A_suspend_that_waited_longer_than_the_budget_escalates_as_soon_as_it_runs()
    {
        var h = await StartedAsync(HostFixtures.Persistent("m1", probeIntervalSeconds: 60, drive: "M:"));
        h.Rclone.ConfirmUnmountAtCall("M:", int.MaxValue);

        var arrival = await SuspendThatWaitedInTheQueueAsync(h, TimeSpan.FromSeconds(5));
        var dequeued = h.Now;
        await h.AdvanceAsync(TimeSpan.FromSeconds(10), Step);

        Assert.Contains(dequeued, h.HRclone.Times("unmount", "M:"));
        var escalations = h.EscalationTimes("m1");
        Assert.NotEmpty(escalations);
        Assert.True(
            escalations[0] <= dequeued + Step,
            $"escalated {(escalations[0] - arrival).TotalSeconds:0.###} s after arrival; the budget was {Budget} s and the suspend ran at +5 s");
    }
}
