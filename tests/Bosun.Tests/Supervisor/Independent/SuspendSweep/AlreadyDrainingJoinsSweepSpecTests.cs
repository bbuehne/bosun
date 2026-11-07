using Bosun.Configuration;
using Bosun.Rclone;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Independent.SuspendPreempt;
using Bosun.Tests.Supervisor.Support;
using static Bosun.Tests.Supervisor.Independent.SuspendSweep.SweepSpec;

namespace Bosun.Tests.Supervisor.Independent.SuspendSweep;

/// <summary>
/// bs-mua.1 (the maintainer's decision of 2026-11-07, option B), written from docs/ARCHITECTURE.md §4 rule 5
/// and ADR-023's amendment of the same date rather than from the implementation: a host that is ALREADY
/// <c>Draining</c> when a suspend arrives, and whose forced-unmount deadline the suspend budget shortens, has
/// its unmount re-issued in the sweep's arrival batch, together with the <c>Mounted</c>/<c>Mounting</c> hosts'
/// unmounts, and shares the sweep's verification and escalation. Before this it was re-driven after the sweep,
/// one host's whole step at a time (about 80 s each against a stuck rcd).
/// </summary>
/// <remarks>
/// <para>
/// <b>What the tests observe.</b> Request ARRIVAL, not execution. <c>rclone rcd</c> runs <c>mount/*</c> calls
/// one at a time under one lock (bs-6lu), and <see cref="HoldableRclone"/> models that, so an arrival-time
/// assertion is the only thing this change can move: it removes Bosun-side serialisation, it does not make rcd
/// faster. A held call is "a stuck rcd"; <see cref="Hold.WhenArrived"/> is "the request left Bosun".
/// </para>
/// <para>
/// <b>The rule for a drain request already in flight.</b> Every rc call a drain makes is awaited on the
/// supervisor's channel, which runs one action at a time, so when the suspend action starts no drain request of
/// its own is outstanding: a step that was stuck on a slow rcd finishes (or times out) first, and the suspend
/// waits behind it (ADR-023 decision 4: drains are not preempted). The sweep then sends exactly one re-issue for
/// that host, after the earlier call has returned, so two unmounts for one drive are never in flight from Bosun
/// together. The last test here holds that open.
/// </para>
/// <para>
/// Nothing here reaches a real drive letter, rclone, WinFsp or a host. Time moves only when a test moves it.
/// </para>
/// </remarks>
public sealed class AlreadyDrainingJoinsSweepSpecTests
{
    private static RcloneRcTimeoutException Timeout() => new("mount/unmount", TimeSpan.FromSeconds(30));

    private static BosunConfig ConfigOf(params HostConfig[] hosts) =>
        HostFixtures.Build(HostFixtures.Global(failuresBeforeUnmount: 3, suspendUnmountTimeoutSeconds: Budget), hosts);

    /// <summary>Mounts an on-demand host, then has the user unmount it with an unmount that never takes effect,
    /// so it is <c>Draining</c> (cause <see cref="DrainCause.UserUnmount"/>) and stays so.</summary>
    private static async Task UserUnmountedAndStuckAsync(PreemptSpecHarness h, string key, string drive)
    {
        await h.RunAsync(() => h.Supervisor.RequestMountAsync(key));
        h.Rclone.ConfirmUnmountAtCall(drive, int.MaxValue);
        await h.RunAsync(() => h.Supervisor.RequestUnmountAsync(key));
        Assert.Equal(MountState.Draining, h.State(key));
        Assert.Equal([DrainCause.UserUnmount], h.DrainCauses(key));
    }

    /// <summary>
    /// <b>Protects:</b> the amendment's core claim: an already-<c>Draining</c> host's re-issue leaves Bosun in
    /// the same batch as a <c>Mounted</c> host's unmount, at the suspend's arrival, while rcd is stuck on the
    /// <c>Mounted</c> host's call.
    /// <b>Catches:</b> the old order, where the already-draining host is re-driven only after the sweep has
    /// finished, so with a stuck rcd its request waits for the sweep's whole outcome (and for the hosts before it)
    /// and never reaches rcd before the machine sleeps (I8). The request is observed arriving while the
    /// <c>Mounted</c> host's call is still outstanding. The re-issue must also not be cancelled by the suspend
    /// that sent it (the drain is not preempted, ADR-023 decision 4).
    /// </summary>
    [Fact]
    public async Task An_already_draining_hosts_reissue_arrives_with_the_mounted_hosts_unmount_while_rcd_is_stuck()
    {
        var h = New(Persistent("alpha", "M:"), OnDemand("yankee", "Y:"));
        await h.StartAsync();
        await UserUnmountedAndStuckAsync(h, "yankee", "Y:");
        await h.AdvanceAsync(TimeSpan.FromSeconds(1), Step);

        var stuckAlpha = h.HRclone.HoldNextAt(OnCancel.Ignore, "unmount", "M:");
        var yankeeReissue = h.HRclone.HoldNextAt(OnCancel.Ignore, "unmount", "Y:");
        var yankeeBefore = h.HRclone.Count("unmount", "Y:");
        var arrival = h.Now;

        var suspend = SuspendCaller.Call(h.Supervisor);
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(stuckAlpha, "alpha's unmount (swept)");
        await PreemptSpecHarness.ArrivedAsync(
            yankeeReissue, "yankee's re-issue while alpha's unmount is still outstanding (bs-mua.1)");

        // Both requests are on the wire at the arrival instant, alpha's still unanswered.
        Assert.True(stuckAlpha.IsPending);
        Assert.Equal(yankeeBefore + 1, h.HRclone.Count("unmount", "Y:"));
        Assert.Equal(1, UnmountsAt(h, "M:", arrival));
        Assert.Equal(1, UnmountsAt(h, "Y:", arrival));
        Assert.False(yankeeReissue.CancellationRequested, "the suspend cancelled the drain request it sent");

        stuckAlpha.Release();
        yankeeReissue.Release();
        await h.FinishAsync(pump, "the sweep never finished", suspend);

        Assert.Equal(MountState.Disabled, h.State("alpha"));
        Assert.Equal(MountState.Draining, h.State("yankee")); // its drive never left: still being drained
        Assert.Equal(1, h.HRclone.Count("unmount", "M:"));
        Assert.Empty(LoopFaults(h));
    }

    /// <summary>
    /// <b>Protects:</b> with a stuck rcd, a SECOND already-<c>Draining</c> host no longer waits behind the
    /// first's whole step on Bosun's side: both re-issues have left Bosun while both calls are outstanding.
    /// <b>Catches:</b> the old loop, which sent one host's unmount, awaited its verification and forced
    /// unmount (up to about 80 s), and only then the next host's. Both holds are armed so the result does
    /// not depend on which host the sweep happens to reach first.
    /// </summary>
    [Fact]
    public async Task With_a_stuck_rcd_a_second_already_draining_host_does_not_wait_behind_the_first_hosts_step()
    {
        var h = New(OnDemand("yankee", "Y:"), OnDemand("tango", "T:"));
        await h.StartAsync();
        await UserUnmountedAndStuckAsync(h, "yankee", "Y:");
        await UserUnmountedAndStuckAsync(h, "tango", "T:");
        await h.AdvanceAsync(TimeSpan.FromSeconds(1), Step);

        var yankeeHold = h.HRclone.HoldNextAt(OnCancel.Ignore, "unmount", "Y:");
        var tangoHold = h.HRclone.HoldNextAt(OnCancel.Ignore, "unmount", "T:");
        var arrival = h.Now;

        var suspend = SuspendCaller.Call(h.Supervisor);
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(yankeeHold, "yankee's re-issue");
        await PreemptSpecHarness.ArrivedAsync(
            tangoHold, "tango's re-issue while yankee's is outstanding: it waited behind yankee's step (bs-mua.1)");

        Assert.True(yankeeHold.IsPending);
        Assert.True(tangoHold.IsPending);
        Assert.Equal(1, UnmountsAt(h, "Y:", arrival));
        Assert.Equal(1, UnmountsAt(h, "T:", arrival));

        // rcd's lock holds one of them; the client gives up on that one (30 s), rcd carries on, and then
        // the other runs. The sweep still finishes, with both hosts left Draining and verified together.
        yankeeHold.Abandon(Timeout());
        tangoHold.Release();
        await new[] { yankeeHold, tangoHold }.FinishInRcdInTurnAsync();
        await h.FinishAsync(pump, "the sweep never finished", suspend);

        Assert.Equal(MountState.Draining, h.State("yankee"));
        Assert.Equal(MountState.Draining, h.State("tango"));
        Assert.Empty(LoopFaults(h));
    }

    /// <summary>
    /// <b>Protects:</b> the three promises that batching must not weaken, for hosts with different causes:
    /// (1) each already-Draining host keeps its ORIGINAL cause (no second <c>Draining</c> entry with
    /// <see cref="DrainCause.Suspend"/>), so a user unmount still parks the host and a removed host is still
    /// evicted; (2) the sweep's ONE <c>mount/listmounts</c> verifies them together with the swept host; (3)
    /// each escalates to a forced unmount EXACTLY once, at arrival + budget.
    /// <b>Catches:</b> a batch that gives the re-driven hosts the Suspend cause (the user-unmounted host is
    /// unparked and resume remounts a drive the user took down); per-host verification (three
    /// <c>listmounts</c> where one will do, which is the serial cost this change removes); an escalation sent
    /// twice for a host (once by the batch, once by its retry timer's step), or none.
    /// </summary>
    [Fact]
    public async Task Re_driven_hosts_keep_their_cause_share_one_verification_and_escalate_once_each()
    {
        var h = New(Persistent("alpha", "M:"), Persistent("romeo", "R:"), OnDemand("yankee", "Y:"));
        await h.StartAsync();
        await UserUnmountedAndStuckAsync(h, "yankee", "Y:");

        // romeo is removed from the config while Mounted, and its unmount never takes effect.
        h.Rclone.ConfirmUnmountAtCall("R:", int.MaxValue);
        var withoutRomeo = ConfigOf(Persistent("alpha", "M:"), OnDemand("yankee", "Y:"));
        h.Store.Publish(withoutRomeo);
        await h.RunAsync(() => h.Supervisor.ConfigChangedAsync(withoutRomeo));
        Assert.Equal([DrainCause.ConfigRemoved], h.DrainCauses("romeo"));
        Assert.Equal(MountState.Draining, h.State("romeo"));

        await h.AdvanceAsync(TimeSpan.FromSeconds(1), Step);
        var alphaBefore = h.Transitions("alpha").Count;
        var mark = Mark(h);
        var arrival = h.Now;

        await h.SuspendAsync();

        // One suspend drain for the swept host; the others' causes are untouched.
        AssertOneSuspendDrainThenDisabled(h, "alpha", alphaBefore);
        Assert.Equal([DrainCause.UserUnmount], h.DrainCauses("yankee"));
        Assert.Equal([DrainCause.ConfigRemoved], h.DrainCauses("romeo"));
        Assert.Equal(1, UnmountsAt(h, "M:", arrival));
        Assert.Equal(1, UnmountsAt(h, "Y:", arrival));
        Assert.Equal(1, UnmountsAt(h, "R:", arrival));

        // One listmounts verified all three: the two re-driven hosts did not each run a step of their own.
        Assert.Equal(1, ListMountsSince(h, mark));

        await h.AdvanceAsync(TimeSpan.FromSeconds(Budget + 1), Step);

        // Exactly one escalation each, on the suspend budget counted from arrival.
        foreach (var key in new[] { "yankee", "romeo" })
        {
            var escalations = h.EscalationTimes(key);
            Assert.Single(escalations);
            Assert.InRange(escalations[0], arrival.AddSeconds(Budget) - Step, arrival.AddSeconds(Budget) + Step);
        }

        // Their drives go; each completes its OWN kind of drain: parked, evicted.
        h.Rclone.DropMountSilently("Y:");
        h.Rclone.DropMountSilently("R:");
        await h.AdvanceAsync(TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(100));
        var yankee = h.Snapshot("yankee");
        Assert.Equal(MountState.Disabled, yankee.State);
        Assert.True(yankee.UserParked);
        Assert.DoesNotContain(h.Supervisor.GetSnapshot(), s => s.HostKey == "romeo");
        Assert.Single(h.EscalationTimes("yankee"));
        Assert.Single(h.EscalationTimes("romeo"));

        await h.ResumeAsync();
        await h.AdvanceAsync(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(100));
        Assert.Equal(MountState.Mounted, h.State("alpha"));
        Assert.NotEqual(MountState.Mounted, h.State("yankee")); // still parked
        Assert.Equal(1, h.MountCalls("Y:"));
        Assert.Equal(1, h.MountCalls("R:")); // evicted, not remounted
        Assert.Empty(LoopFaults(h));
    }

    /// <summary>
    /// <b>Protects:</b> the documented rule for a host whose OWN unmount is in flight when the suspend arrives:
    /// the suspend waits behind that call (the channel runs one action at a time; the drain is not
    /// preempted), so the sweep never has two unmounts for one drive outstanding together; and once the earlier
    /// call has returned, the batch sends exactly ONE re-issue for the host, with the swept host's unmount, not
    /// after it.
    /// <b>Catches:</b> a suspend that runs inside the host's step and sends a duplicate unmount while the
    /// first is still in flight; a re-issue lost because the host looked "busy"; and, still, the old order
    /// (the re-issue arrives only after alpha's stuck call is released).
    /// </summary>
    [Fact]
    public async Task A_host_whose_own_unmount_is_in_flight_at_arrival_gets_one_reissue_after_it_returns_in_the_same_batch()
    {
        var h = New(Persistent("alpha", "M:"), OnDemand("yankee", "Y:"));
        await h.StartAsync();
        await UserUnmountedAndStuckAsync(h, "yankee", "Y:");
        var callsAfterUserUnmount = h.HRclone.Count("unmount", "Y:");
        Assert.Equal(1, callsAfterUserUnmount);

        // yankee's own retry step (5 s after its drain began) is now in flight, stuck on rcd.
        var ownStep = h.HRclone.HoldNextAt(OnCancel.Ignore, "unmount", "Y:");
        h.FireTimers(TimeSpan.FromSeconds(5));
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(ownStep, "yankee's own drain step");

        // Armed now, so it catches the NEXT unmount for Y: -- the suspend's re-issue -- and nothing earlier.
        var reissue = h.HRclone.HoldNextAt(OnCancel.Ignore, "unmount", "Y:");
        var stuckAlpha = h.HRclone.HoldNextAt(OnCancel.Ignore, "unmount", "M:");
        var arrival = h.Now;
        var suspend = SuspendCaller.Call(h.Supervisor);

        // The suspend is queued behind the step: no second unmount for Y: is in flight, and the step's own call
        // was not cancelled by the suspend (a drain is not preempted).
        Assert.False(reissue.HasArrived, "a duplicate unmount for Y: was sent while the host's own was in flight");
        Assert.Equal(callsAfterUserUnmount + 1, h.HRclone.Count("unmount", "Y:"));
        Assert.False(ownStep.CancellationRequested, "the suspend cancelled the drain step that was in flight");

        // The call returns (the client's 30 s timeout), rcd finishes it; the step goes on, then the suspend runs.
        ownStep.Abandon(Timeout());
        ownStep.FinishInRcd();
        await PreemptSpecHarness.ArrivedAsync(stuckAlpha, "alpha's unmount (swept)");
        await PreemptSpecHarness.ArrivedAsync(
            reissue, "yankee's single re-issue, in the batch with alpha's still-outstanding unmount (bs-mua.1)");

        Assert.Equal(callsAfterUserUnmount + 2, h.HRclone.Count("unmount", "Y:"));
        Assert.Equal(1, UnmountsAt(h, "M:", arrival));
        Assert.Equal(2, UnmountsAt(h, "Y:", arrival)); // the step's own call and the re-issue: the clock has not moved

        stuckAlpha.Release();
        reissue.Release();
        await h.FinishAsync(pump, "the step or the sweep never finished", suspend);

        // One re-issue, and nothing else was sent for Y: by the suspend.
        Assert.Equal(callsAfterUserUnmount + 2, h.HRclone.Count("unmount", "Y:"));
        Assert.Equal([DrainCause.UserUnmount], h.DrainCauses("yankee"));
        Assert.Equal(MountState.Disabled, h.State("alpha"));
        Assert.Empty(LoopFaults(h));
    }
}
