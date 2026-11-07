using Bosun.Rclone;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Independent.SuspendPreempt;
using static Bosun.Tests.Supervisor.Independent.SuspendSweep.SweepSpec;
using static Bosun.Tests.Supervisor.Independent.SuspendSweepAlreadyDraining.AlreadyDrainingSpec;

namespace Bosun.Tests.Supervisor.Independent.SuspendSweepAlreadyDraining;

/// <summary>
/// bs-mua.1, after the requests: the re-driven hosts "then share the sweep's verification (one
/// mount/listmounts for all) and its forced-unmount phase (all together, one more mount/listmounts), and a host
/// still unconfirmed retries on its own timer, exactly as a swept host does" (docs/ARCHITECTURE.md §4 rule 5),
/// with "its once-only escalation flag" unchanged (ADR-023, bs-mua.1 amendment). Written from the spec.
/// </summary>
public sealed class SharedVerificationAndEscalationSpecTests
{
    /// <summary>
    /// <b>Protects:</b> "one mount/listmounts for all" (the shared verification) and Invariant I8 with a healthy
    /// rcd: once the suspend action has run, no drive is still mounted, and each re-driven host completes its drain
    /// on the verification, not on the unmount call's own result (rule 4).
    /// <b>Catches:</b> re-driven hosts verified one at a time after the sweep (a listmounts per host, the pre-
    /// bs-mua.1 shape), and a re-driven host whose confirmed drive is left Draining (and later escalated) because
    /// the shared verification only looked at the swept hosts.
    /// </summary>
    [Fact]
    public async Task Swept_and_reissued_hosts_are_verified_by_one_listmounts_and_no_drive_is_left_mounted()
    {
        var h = NewHarness(
            Persistent("alpha", "M:"), Persistent("bravo", "N:"), OnDemand("delta", "P:"), OnDemand("foxtrot", "R:"));
        await h.StartAsync();
        await UserMountAsync(h, "delta");
        await UserMountAsync(h, "foxtrot");
        await UserUnmountUnconfirmedAsync(h, "delta", "P:", 2);   // the re-issue takes
        await UserUnmountUnconfirmedAsync(h, "foxtrot", "R:", 2);
        await h.AdvanceAsync(TimeSpan.FromSeconds(1), Fine);

        var mark = Mark(h);
        var arrival = h.Now;
        await h.SuspendAsync();

        Assert.Equal(1, ListMountsSince(h, mark));
        foreach (var drive in new[] { "M:", "N:", "P:", "R:" })
        {
            Assert.Equal(1, UnmountsAt(h, drive, arrival));
            Assert.False(h.IsMountedInRclone(drive), $"I8: {drive} is still mounted after the suspend with a healthy rcd");
        }

        Assert.Equal(MountState.Disabled, h.State("alpha"));
        Assert.Equal(MountState.Disabled, h.State("bravo"));
        foreach (var key in new[] { "delta", "foxtrot" })
        {
            Assert.NotEqual(MountState.Draining, h.State(key));
            Assert.True(h.Snapshot(key).UserParked, $"{key}: the user's unmount must still park the host");
        }

        await h.AdvanceAsync(TimeSpan.FromSeconds(Budget + 10), TimeSpan.FromMilliseconds(100));
        foreach (var key in new[] { "alpha", "bravo", "delta", "foxtrot" })
        {
            Assert.Empty(h.EscalationTimes(key));
        }
    }

    /// <summary>
    /// <b>Protects:</b> the shared forced-unmount phase. With the swept host's unmount hung in rcd past the budget,
    /// every first request (the swept one and the re-issue) has been sent; once rcd is free, ONE listmounts
    /// verifies them, both still-listed hosts are escalated together, and ONE more listmounts verifies the forced
    /// unmounts: two listmounts for the whole suspend. Each host is escalated exactly once, and the re-driven host
    /// keeps its own cause.
    /// <b>Catches:</b> re-driven hosts given their own drain step after the sweep (four listmounts: the sweep's two,
    /// then the host's own two), a re-driven host escalated twice (once in the shared phase and again on its own
    /// timer), or a re-driven host never escalated because the forced phase only looks at swept hosts.
    /// </summary>
    [Fact]
    public async Task With_rcd_stuck_past_the_budget_a_reissued_host_is_escalated_once_in_the_sweeps_forced_phase()
    {
        var h = NewHarness(Persistent("alpha", "M:"), OnDemand("delta", "P:"));
        await h.StartAsync();
        await UserMountAsync(h, "delta");
        await UserUnmountUnconfirmedAsync(h, "delta", "P:", 3); // original, re-issue: no; forced: yes
        h.Rclone.ConfirmUnmountAtCall("M:", 2);                 // swept: no; forced: yes
        await h.AdvanceAsync(TimeSpan.FromSeconds(1), Fine);

        var hung = h.HRclone.HoldNextAt(OnCancel.Ignore, "unmount", "M:");
        var mark = Mark(h);
        var arrival = h.Now;

        var suspend = SuspendCaller.Call(h.Supervisor);
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(hung, "alpha's swept unmount");
        await PreemptSpecHarness.WaitForAsync(() => UnmountsSince(h, mark, "P:") == 1, "delta's re-issue never left at arrival");

        // rcd stays stuck on alpha's call past the budget. The channel is busy with the suspend, so nothing else
        // runs; the clock is moved without a second consumer.
        var until = arrival.AddSeconds(Budget + 1);
        while (h.Now < until)
        {
            h.FireTimers(TimeSpan.FromMilliseconds(100));
            Assert.False(pump.IsCompleted, "setup: the suspend finished while alpha's unmount was still hung");
        }

        hung.Abandon(new RcloneRcTimeoutException("mount/unmount", TimeSpan.FromSeconds(30)));
        hung.FinishInRcd();
        await h.FinishAsync(pump, "the suspend never finished after rcd was free again", suspend);

        var freedAt = h.Now;
        Assert.Equal(2, ListMountsSince(h, mark));
        Assert.Equal(2, UnmountsSince(h, mark, "P:"));
        Assert.Equal(2, UnmountsSince(h, mark, "M:"));
        AssertEscalatedOnceNear(h, "alpha", freedAt, TimeSpan.Zero, arrival);
        AssertEscalatedOnceNear(h, "delta", freedAt, TimeSpan.Zero, arrival);
        Assert.False(h.IsMountedInRclone("M:"));
        Assert.False(h.IsMountedInRclone("P:"));
        Assert.True(h.Snapshot("delta").UserParked);
        Assert.NotEqual(MountState.Draining, h.State("delta"));

        await h.AdvanceAsync(TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(100));
        Assert.Single(h.EscalationTimes("alpha"));
        Assert.Single(h.EscalationTimes("delta"));
        Assert.Equal([DrainCause.UserUnmount], h.DrainCauses("delta"));
        Assert.Empty(LoopFaults(h));
    }

    /// <summary>
    /// <b>Protects:</b> "a host still unconfirmed retries on its own timer, exactly as a swept host does", aimed at
    /// the deadline the budget set: a re-driven host whose re-issue does not take is escalated once, at
    /// arrival + budget, the same instant as the swept host beside it, and then stays <c>Draining</c> (I2: never
    /// believed gone while rclone lists it).
    /// <b>Catches:</b> a re-driven host dropped from the retry path after the batch (no escalation before the
    /// machine sleeps), a host left on its ordinary 10 s clock, or one completed on the batch's say-so.
    /// </summary>
    [Fact]
    public async Task An_unconfirmed_reissued_host_escalates_once_at_arrival_plus_budget_and_stays_draining()
    {
        var h = NewHarness(Persistent("alpha", "M:"), OnDemand("delta", "P:"));
        await h.StartAsync();
        await UserMountAsync(h, "delta");
        await UserUnmountUnconfirmedAsync(h, "delta", "P:", int.MaxValue);
        h.Rclone.ConfirmUnmountAtCall("M:", int.MaxValue);
        await h.AdvanceAsync(TimeSpan.FromSeconds(1), Fine);

        var arrival = h.Now;
        await h.SuspendAsync();
        Assert.Equal(1, UnmountsAt(h, "P:", arrival));

        await h.AdvanceAsync(TimeSpan.FromSeconds(Budget + 1), Fine);
        AssertEscalatedOnceNear(h, "alpha", arrival.AddSeconds(Budget), Fine, arrival);
        AssertEscalatedOnceNear(h, "delta", arrival.AddSeconds(Budget), Fine, arrival);

        await h.AdvanceAsync(TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(100));
        Assert.Equal(MountState.Draining, h.State("delta"));
        Assert.Equal(MountState.Draining, h.State("alpha"));
        Assert.Single(h.EscalationTimes("delta"));
        Assert.Equal([DrainCause.UserUnmount], h.DrainCauses("delta"));
    }
}
