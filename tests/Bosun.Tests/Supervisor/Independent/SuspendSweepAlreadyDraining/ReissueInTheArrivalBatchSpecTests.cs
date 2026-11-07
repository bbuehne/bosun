using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Independent.SuspendPreempt;
using static Bosun.Tests.Supervisor.Independent.SuspendSweep.SweepSpec;
using static Bosun.Tests.Supervisor.Independent.SuspendSweepAlreadyDraining.AlreadyDrainingSpec;

namespace Bosun.Tests.Supervisor.Independent.SuspendSweepAlreadyDraining;

/// <summary>
/// bs-mua.1, which hosts get a re-issue and when it leaves Bosun. docs/ARCHITECTURE.md §4 rule 5: "The suspend
/// sweeps the Mounted and Mounting hosts, counts the budget for the already-Draining hosts, and then sends ONE
/// batch: first the swept hosts' unmounts, then the re-issue for each already-Draining host whose deadline the
/// budget shortened ... Every request is started before any is awaited, so with a stuck rcd a later
/// already-Draining host's request no longer waits behind an earlier host's whole step." Written from the spec.
/// </summary>
public sealed class ReissueInTheArrivalBatchSpecTests
{
    /// <summary>
    /// <b>Protects:</b> the decision itself (I8): every already-Draining host's re-issue leaves Bosun at the
    /// suspend's arrival, together with the swept host's, and in the stated order (swept first).
    /// <b>Catches:</b> the pre-bs-mua.1 behaviour, where already-Draining hosts were re-driven one whole drain step
    /// at a time AFTER the sweep: with the swept host's unmount hung in rcd, no re-issue ever arrives. Also catches
    /// a batch that awaits the first re-issue before sending the second, and a batch that sends re-issues before
    /// the swept hosts' first requests.
    /// </summary>
    [Fact]
    public async Task Every_reissue_is_on_the_wire_at_arrival_while_the_swept_hosts_unmount_is_still_hung()
    {
        var h = NewHarness(Persistent("alpha", "M:"), OnDemand("delta", "P:"), OnDemand("foxtrot", "R:"));
        await h.StartAsync();
        await UserMountAsync(h, "delta");
        await UserMountAsync(h, "foxtrot");
        await UserUnmountUnconfirmedAsync(h, "delta", "P:", int.MaxValue);
        await UserUnmountUnconfirmedAsync(h, "foxtrot", "R:", int.MaxValue);
        await h.AdvanceAsync(TimeSpan.FromSeconds(1), Fine);

        var swept = h.HRclone.HoldNextAt(OnCancel.Ignore, "unmount", "M:");
        var reissues = new Dictionary<string, Hold>
        {
            ["P:"] = h.HRclone.HoldNextAt(OnCancel.Ignore, "unmount", "P:"),
            ["R:"] = h.HRclone.HoldNextAt(OnCancel.Ignore, "unmount", "R:"),
        };
        var mark = Mark(h);
        var arrival = h.Now;

        var suspend = SuspendCaller.Call(h.Supervisor);
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(swept, "alpha's swept unmount");
        await AllArrivedAsync(reissues, "an already-Draining host's re-issue did not leave while alpha's unmount was hung");

        Assert.True(swept.IsPending, "setup: alpha's unmount must still be hung when the re-issues arrive");
        Assert.All(reissues.Values, r => Assert.True(r.IsPending, "setup: no re-issue may have returned yet"));
        Assert.Equal(1, UnmountsAt(h, "M:", arrival));
        Assert.Equal(1, UnmountsAt(h, "P:", arrival));
        Assert.Equal(1, UnmountsAt(h, "R:", arrival));
        var order = UnmountTargetsSince(h, mark);
        Assert.True(
            order.Count == 3 && order[0] == "M:",
            $"the swept host's request goes first, then the re-issues; saw [{string.Join(", ", order)}]");

        swept.Release();
        foreach (var r in reissues.Values)
        {
            r.Release();
        }

        await h.FinishAsync(pump, "the suspend never finished after its requests were released", suspend);

        Assert.Equal([DrainCause.UserUnmount], h.DrainCauses("delta"));
        Assert.Equal([DrainCause.UserUnmount], h.DrainCauses("foxtrot"));
        Assert.Empty(LoopFaults(h));
    }

    /// <summary>
    /// <b>Protects:</b> "A host whose deadline is not shortened is still not re-issued" (bs-mua.1) and "a drain
    /// whose ordinary deadline is already sooner than suspend arrival plus the budget keeps it (and is not
    /// re-issued)" (rule 5), now that such a host sits beside a sweep.
    /// <b>Catches:</b> a batch that takes EVERY already-Draining host (an unconditional re-issue), and a sweep that
    /// pulls the host into its forced-unmount phase on the suspend's terms instead of leaving it on its own deadline.
    /// </summary>
    [Fact]
    public async Task An_already_draining_host_whose_own_deadline_is_sooner_is_not_in_the_batch()
    {
        var h = NewHarness(Persistent("alpha", "M:"), OnDemand("delta", "P:"));
        await h.StartAsync();
        await UserMountAsync(h, "delta");
        var t0 = await UserUnmountUnconfirmedAsync(h, "delta", "P:", int.MaxValue);

        // Ordinary deadline t0 + 10; the suspend's would be t0 + 8 + 3 = t0 + 11. Nothing to shorten.
        await h.AdvanceAsync(TimeSpan.FromSeconds(8), TimeSpan.FromMilliseconds(100));
        var arrival = h.Now;
        await h.SuspendAsync();

        Assert.Equal(1, UnmountsAt(h, "M:", arrival));
        Assert.Equal(0, UnmountsAt(h, "P:", arrival));

        await h.AdvanceAsync(TimeSpan.FromSeconds(15), Fine);
        AssertEscalatedOnceNear(h, "delta", t0.AddSeconds(OrdinaryDeadlineSeconds), Fine, t0);
        Assert.Equal([DrainCause.UserUnmount], h.DrainCauses("delta"));
    }

    /// <summary>
    /// <b>Protects:</b> "A host already escalated is left alone (it is already on the forced path)" (rule 5) and the
    /// once-only escalation flag (bs-mua.1, "What is unchanged"), with a sweep whose own forced phase runs.
    /// <b>Catches:</b> a batch built from every Draining host regardless of its escalation flag: it would re-issue
    /// the escalated host's unmount at arrival, and could escalate it a second time with the swept host.
    /// </summary>
    [Fact]
    public async Task An_already_escalated_host_gets_no_reissue_and_is_not_escalated_again()
    {
        var h = NewHarness(Persistent("alpha", "M:"), OnDemand("delta", "P:"));
        await h.StartAsync();
        await UserMountAsync(h, "delta");
        var t0 = await UserUnmountUnconfirmedAsync(h, "delta", "P:", int.MaxValue);
        h.Rclone.ConfirmUnmountAtCall("M:", int.MaxValue); // alpha escalates on the budget too

        await h.AdvanceAsync(TimeSpan.FromSeconds(11), TimeSpan.FromMilliseconds(100));
        Assert.Single(h.EscalationTimes("delta")); // precondition: already on the forced path

        var arrival = h.Now;
        await h.SuspendAsync();
        Assert.Equal(1, UnmountsAt(h, "M:", arrival));
        Assert.Equal(0, UnmountsAt(h, "P:", arrival));

        await h.AdvanceAsync(TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(100));
        Assert.Single(h.EscalationTimes("delta"));
        AssertEscalatedOnceNear(h, "alpha", arrival.AddSeconds(Budget), TimeSpan.FromMilliseconds(100), arrival);
        Assert.Equal(MountState.Draining, h.State("delta"));
    }

    /// <summary>
    /// <b>Protects:</b> "a second suspend never moves a deadline later" (rule 5), applied to the batch: once the
    /// first suspend has set a host's deadline to arrival₁ + budget, a second suspend one second later has nothing
    /// to shorten, so it re-issues nothing for that host, and each host still escalates once, on the FIRST
    /// suspend's deadline. (Windows can deliver PBT_APMSUSPEND twice.)
    /// <b>Catches:</b> a batch that re-issues every already-Draining host on every suspend (two unmounts for one
    /// drive a second apart, and the second suspend's verification and escalation stacked on the first), and a
    /// deadline recomputed from the latest suspend.
    /// </summary>
    [Fact]
    public async Task A_second_suspend_a_moment_later_reissues_nothing_and_moves_no_deadline()
    {
        var h = NewHarness(Persistent("alpha", "M:"), OnDemand("delta", "P:"));
        await h.StartAsync();
        await UserMountAsync(h, "delta");
        await UserUnmountUnconfirmedAsync(h, "delta", "P:", int.MaxValue);
        h.Rclone.ConfirmUnmountAtCall("M:", int.MaxValue);

        await h.AdvanceAsync(TimeSpan.FromSeconds(1), Fine);
        var first = h.Now;
        await h.SuspendAsync();
        Assert.Equal(1, UnmountsAt(h, "P:", first)); // the first suspend did re-issue

        await h.AdvanceAsync(TimeSpan.FromSeconds(1), Fine);
        var second = h.Now;
        await h.SuspendAsync();

        Assert.Equal(0, UnmountsAt(h, "P:", second));
        await h.AdvanceAsync(TimeSpan.FromSeconds(15), Fine);
        AssertEscalatedOnceNear(h, "delta", first.AddSeconds(Budget), Fine, first);
        AssertEscalatedOnceNear(h, "alpha", first.AddSeconds(Budget), Fine, first);
        Assert.Equal([DrainCause.UserUnmount], h.DrainCauses("delta"));
        Assert.Equal([DrainCause.Suspend], h.DrainCauses("alpha"));
    }

    /// <summary>
    /// <b>Protects:</b> "A request already in flight" (bs-mua.1): an already-Draining host whose own drain step is
    /// stuck in <c>mount/unmount</c> when the suspend is called. The suspend waits behind it (drains are not
    /// preempted, ADR-023 decision 4), and the batch then sends that host exactly one re-issue: "Two unmounts for
    /// one drive are never in flight from Bosun together."
    /// <b>Catches:</b> a suspend prologue (the part that runs off the channel, on the caller's thread) that sends
    /// an rc call or re-drives draining hosts itself; a suspend that cancels the drain step's unmount as if it were
    /// a routine tick; a batch that sends the host two re-issues, or none.
    /// </summary>
    [Fact]
    public async Task A_host_whose_own_unmount_is_in_flight_gets_exactly_one_reissue_after_it_returns()
    {
        var h = NewHarness(Persistent("alpha", "M:"), OnDemand("delta", "P:"));
        await h.StartAsync();
        await UserMountAsync(h, "delta");
        var t0 = await UserUnmountUnconfirmedAsync(h, "delta", "P:", int.MaxValue);

        // Catch delta's own retry step in its mount/unmount, before its 10 s deadline.
        var stepCall = h.HRclone.HoldNextAt(OnCancel.Ignore, "unmount", "P:");
        var pump = Task.CompletedTask;
        while (!stepCall.HasArrived)
        {
            Assert.True(h.Now < t0.AddSeconds(OrdinaryDeadlineSeconds - 1), "setup: delta's drain never retried before its deadline");
            h.FireTimers(TimeSpan.FromMilliseconds(100));
            pump = h.StartPump();
            if (!pump.IsCompleted)
            {
                break; // the channel is busy: with delta's step, checked next
            }
        }

        await PreemptSpecHarness.ArrivedAsync(stepCall, "delta's own drain-retry unmount");
        var arrival = h.Now;
        Assert.True(arrival + TimeSpan.FromSeconds(Budget) < t0.AddSeconds(OrdinaryDeadlineSeconds), "setup: the budget must shorten delta's deadline");
        var mark = Mark(h);

        var suspend = SuspendCaller.Call(h.Supervisor);
        Assert.False(stepCall.CancellationRequested, "the suspend cancelled a drain's unmount in flight (drains are not preempted)");
        Assert.Equal(0, UnmountsSince(h, mark, "P:"));
        Assert.Equal(0, UnmountsSince(h, mark, "M:"));

        var reissue = h.HRclone.HoldNextAt(OnCancel.Ignore, "unmount", "P:");
        stepCall.Release(); // rcd answers; the drive stays (never confirms)
        await PreemptSpecHarness.ArrivedAsync(reissue, "delta's re-issue after its own step returned");
        Assert.Equal(1, UnmountsSince(h, mark, "P:"));
        reissue.Release();
        await h.FinishAsync(pump, "the suspend never ran after delta's step returned", suspend);

        Assert.Equal(1, UnmountsSince(h, mark, "P:"));
        Assert.Equal(1, UnmountsSince(h, mark, "M:"));
        await h.AdvanceAsync(TimeSpan.FromSeconds(Budget + 1), Fine);
        AssertEscalatedOnceNear(h, "delta", arrival.AddSeconds(Budget), Fine, arrival);
        Assert.Equal([DrainCause.UserUnmount], h.DrainCauses("delta"));
        Assert.Empty(LoopFaults(h));
    }
}
