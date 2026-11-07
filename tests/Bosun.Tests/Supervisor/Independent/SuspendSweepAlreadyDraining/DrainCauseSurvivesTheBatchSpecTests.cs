using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Independent.SuspendPreempt;
using static Bosun.Tests.Supervisor.Independent.SuspendSweep.SweepSpec;
using static Bosun.Tests.Supervisor.Independent.SuspendSweepAlreadyDraining.AlreadyDrainingSpec;

namespace Bosun.Tests.Supervisor.Independent.SuspendSweepAlreadyDraining;

/// <summary>
/// bs-mua.1, "What is unchanged: each host's drain cause (a user unmount still parks it, a removed host is still
/// evicted, an idle timeout stays an idle timeout, a probe-failure drain stays Automatic), its single Draining
/// transition-log entry, its once-only escalation flag" (ADR-023 amendment), followed through to resume, where
/// the cause is what decides whether the host comes back (rule 5's resume paragraph, rules 6, 9 and 9a).
/// Written from the spec.
/// </summary>
public sealed class DrainCauseSurvivesTheBatchSpecTests
{
    /// <summary>
    /// <b>Protects:</b> every ordinary cause at once beside two swept hosts: each already-Draining host is
    /// re-issued at arrival, after the swept hosts; the batch has one verification; with a healthy rcd no drive is
    /// left (I8); nothing mounts while suspended (I1/I8); and on resume each host does what its OWN cause says:
    /// the persistent swept host and the probe-failure host remount by tier, the on-demand swept host remounts by
    /// ADR-022's record, the user-unmounted host stays parked, and the removed host stays gone.
    /// <b>Catches:</b> a batch that relabels re-driven hosts as suspend drains (a parked drive returns on wake; a
    /// removed host is re-enabled), one that records them for ADR-022, a re-driven host given a second Draining
    /// entry, a re-issue omitted for one cause, and any mount between suspend and resume.
    /// </summary>
    [Fact]
    public async Task Each_reissued_host_keeps_its_own_cause_through_the_batch_and_the_resume()
    {
        var alpha = Persistent("alpha", "M:");
        var bravo = OnDemand("bravo", "N:");
        var charlie = Persistent("charlie", "O:");
        var delta = OnDemand("delta", "P:");
        var echo = Persistent("echo", "R:");
        var h = NewHarness(alpha, bravo, charlie, delta, echo);
        await h.StartAsync();
        await UserMountAsync(h, "bravo");
        await UserMountAsync(h, "delta");
        foreach (var drive in new[] { "O:", "P:", "R:" })
        {
            h.Rclone.ConfirmUnmountAtCall(drive, 2); // the drain's own unmount does nothing; the re-issue works
        }

        // charlie: the mounted-probe failure threshold (rule 3) drains it, cause Automatic.
        h.Probe.SetShallow(PreemptSpecHarness.HostnameOf("charlie"), ShallowProbeOutcome.ConnectionRefused);
        for (var i = 0; i < 600 && h.State("charlie") != MountState.Draining; i++)
        {
            await h.AdvanceAsync(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        Assert.Equal(MountState.Draining, h.State("charlie"));

        // delta: the user unmounts it. echo: removed from config. Both at once.
        await h.RunAsync(() => h.Supervisor.RequestUnmountAsync("delta"));
        await ReloadAsync(h, Config(alpha, bravo, charlie, delta));
        Assert.Equal(MountState.Draining, h.State("delta"));
        Assert.Equal(MountState.Draining, h.State("echo"));
        var mountsBefore = h.TotalMountCalls;

        await h.AdvanceAsync(TimeSpan.FromSeconds(1), Fine);
        var mark = Mark(h);
        var arrival = h.Now;
        await h.SuspendAsync();

        // The batch: every drive once, at arrival, swept hosts first; one verification; nothing left mounted.
        foreach (var drive in new[] { "M:", "N:", "O:", "P:", "R:" })
        {
            Assert.Equal(1, UnmountsAt(h, drive, arrival));
            Assert.False(h.IsMountedInRclone(drive), $"I8: {drive} is still mounted after the suspend with a healthy rcd");
        }

        var order = UnmountTargetsSince(h, mark);
        Assert.True(
            order.Take(2).OrderBy(d => d).SequenceEqual(["M:", "N:"]),
            $"the swept hosts' requests go first; saw [{string.Join(", ", order)}]");
        Assert.Equal(1, ListMountsSince(h, mark));

        // Each cause, exactly one Draining entry, unchanged.
        Assert.Equal([DrainCause.Suspend], h.DrainCauses("alpha"));
        Assert.Equal([DrainCause.Suspend], h.DrainCauses("bravo"));
        Assert.Equal([DrainCause.Automatic], h.DrainCauses("charlie"));
        Assert.Equal([DrainCause.UserUnmount], h.DrainCauses("delta"));
        Assert.Equal([DrainCause.ConfigRemoved], h.DrainCauses("echo"));
        Assert.False(IsSupervised(h, "echo"), "a removed host is still evicted when its drain completes");
        Assert.True(h.Snapshot("delta").UserParked);

        // The night: nothing mounts while suspended, whatever the probes now say.
        h.Probe.SetShallow(PreemptSpecHarness.HostnameOf("charlie"), ShallowProbeOutcome.Success);
        await h.AdvanceAsync(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(1));
        Assert.Equal(mountsBefore, h.TotalMountCalls);
        Assert.DoesNotContain(h.Supervisor.GetTransitionHistory(), t => t.To == MountState.Mounting && t.TimestampUtc >= arrival);

        await h.ResumeAsync();

        Assert.Equal(MountState.Mounted, h.State("alpha"));
        Assert.Equal(MountState.Mounted, h.State("bravo"));
        Assert.Equal(MountState.Mounted, h.State("charlie"));
        Assert.NotEqual(MountState.Mounted, h.State("delta"));
        Assert.True(h.Snapshot("delta").UserParked);
        Assert.Equal(1, h.MountCalls("P:"));
        Assert.False(IsSupervised(h, "echo"));
        Assert.Equal(1, h.MountCalls("R:"));
        AssertMountingOnlyFromReady(h);
        Assert.Empty(LoopFaults(h));
    }

    /// <summary>
    /// <b>Protects:</b> "an idle timeout stays an idle timeout" (bs-mua.1) together with rule 9a: "A host already
    /// Draining or Disabled when suspend arrives is not noted", so an on-demand drive the idle timeout was already
    /// taking down is NOT brought back after resume, though it is re-issued in the batch like any other.
    /// <b>Catches:</b> folding re-driven hosts into the sweep's per-host bookkeeping, which notes on-demand hosts
    /// for ADR-022's remount: the drive the configuration asked to unmount reappears on wake. Also a re-issue
    /// skipped for an idle-timeout drain.
    /// </summary>
    [Fact]
    public async Task An_idle_timeout_drain_is_reissued_in_the_batch_and_its_drive_does_not_come_back_on_resume()
    {
        var h = NewHarness(Persistent("alpha", "M:"), OnDemandIdle("hotel", "H:", idleSeconds: 120));
        await h.StartAsync();
        await UserMountAsync(h, "hotel");
        h.Rclone.ConfirmUnmountAtCall("H:", 2);

        for (var i = 0; i < 600 && h.State("hotel") != MountState.Draining; i++)
        {
            await h.AdvanceAsync(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        Assert.Equal(MountState.Draining, h.State("hotel"));
        Assert.Equal([DrainCause.IdleTimeout], h.DrainCauses("hotel"));

        await h.AdvanceAsync(TimeSpan.FromSeconds(1), Fine);
        var arrival = h.Now;
        await h.SuspendAsync();

        Assert.Equal(1, UnmountsAt(h, "H:", arrival));
        Assert.Equal(1, UnmountsAt(h, "M:", arrival));
        Assert.False(h.IsMountedInRclone("H:"));
        Assert.Equal([DrainCause.IdleTimeout], h.DrainCauses("hotel"));

        await h.AdvanceAsync(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(1));
        await h.ResumeAsync();
        await h.AdvanceAsync(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(1));

        Assert.Equal(MountState.Mounted, h.State("alpha"));
        Assert.NotEqual(MountState.Mounted, h.State("hotel"));
        Assert.Equal(1, h.MountCalls("H:"));
        AssertMountingOnlyFromReady(h);
    }

    /// <summary>
    /// <b>Protects:</b> a repair interrupted by a suspend. "Unmount all & re-probe" drains one host at a time and
    /// stops before the next drain when a suspend is pending (bs-5ga.1); the host it had begun draining is then
    /// already <c>Draining</c> (cause Repair) when the suspend runs, so it is re-issued in the batch after the host
    /// the repair never reached, which the suspend sweeps. Each keeps its cause, and on resume both persistent
    /// hosts remount (a repair does not park, rule 9).
    /// <b>Catches:</b> the repair's host relabelled Suspend or left out of the batch; the repair carrying on to
    /// the next host while the suspend waits; the repair's drain treated as parking.
    /// </summary>
    [Fact]
    public async Task A_host_the_interrupted_repair_was_draining_is_reissued_after_the_host_it_never_reached()
    {
        var h = NewHarness(Persistent("alpha", "M:"), Persistent("bravo", "N:"));
        await h.StartAsync();

        var repairCall = h.HRclone.HoldNext(OnCancel.Ignore, "unmount");
        var repair = h.Supervisor.RepairAllAsync();
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(repairCall, "the repair's first unmount");
        var first = repairCall.Target!;
        var (firstKey, otherKey, otherDrive) = first == "M:" ? ("alpha", "bravo", "N:") : ("bravo", "alpha", "M:");
        h.Rclone.ConfirmUnmountAtCall(first, 2); // the repair's unmount does nothing; the re-issue works

        var mark = Mark(h);
        var arrival = h.Now;
        var suspend = SuspendCaller.Call(h.Supervisor);
        repairCall.Release();
        await h.FinishAsync(pump, "the repair or the suspend never finished", repair, suspend);

        // bs-5ga.2: a repair cut short by the suspend reports the stop instead of success.
        var stopped = await Assert.ThrowsAsync<RepairStoppedException>(() => repair);
        Assert.Equal("the computer is going to sleep", stopped.Reason);

        Assert.Equal([DrainCause.Repair], h.DrainCauses(firstKey));
        Assert.Equal([DrainCause.Suspend], h.DrainCauses(otherKey));
        Assert.Equal([otherDrive, first], UnmountTargetsSince(h, mark));
        Assert.Equal(1, UnmountsAt(h, otherDrive, arrival));
        Assert.False(h.IsMountedInRclone("M:"));
        Assert.False(h.IsMountedInRclone("N:"));
        Assert.False(h.Snapshot(firstKey).UserParked);

        await h.AdvanceAsync(TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(100));
        Assert.Equal(1, h.MountCalls("M:"));
        Assert.Equal(1, h.MountCalls("N:"));

        await h.ResumeAsync();
        Assert.Equal(MountState.Mounted, h.State("alpha"));
        Assert.Equal(MountState.Mounted, h.State("bravo"));
        AssertMountingOnlyFromReady(h);
        Assert.Empty(LoopFaults(h));
    }

    /// <summary>
    /// <b>Protects:</b> a resume queued behind a suspend whose batch is held in rcd. The channel is serial, so the
    /// resume runs only after the batch: no <c>mount/mount</c> while the swept host's unmount is hung (I8), the
    /// resume's mount comes after every batch host's drain has finished, the user-unmounted host re-issued in the
    /// batch stays parked, and Mounting is entered only from Ready (I1).
    /// <b>Catches:</b> a resume that jumps the queue or runs beside the suspend (remounting while the batch is
    /// still unmounting), and a re-driven user unmount un-parked by that resume.
    /// </summary>
    [Fact]
    public async Task A_resume_queued_while_the_batch_is_held_runs_after_it_and_leaves_the_parked_host_parked()
    {
        var h = NewHarness(Persistent("alpha", "M:"), OnDemand("delta", "P:"));
        await h.StartAsync();
        await UserMountAsync(h, "delta");
        await UserUnmountUnconfirmedAsync(h, "delta", "P:", 2);
        await h.AdvanceAsync(TimeSpan.FromSeconds(1), Fine);

        var hung = h.HRclone.HoldNextAt(OnCancel.Ignore, "unmount", "M:");
        var mountsBefore = h.TotalMountCalls;
        var suspend = SuspendCaller.Call(h.Supervisor);
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(hung, "alpha's swept unmount");
        var resume = h.Supervisor.ResumeAsync();

        await PreemptSpecHarness.WaitForAsync(() => h.HRclone.Count("unmount", "P:") == 2, "delta's re-issue never left");
        Assert.Equal(mountsBefore, h.TotalMountCalls);
        Assert.Equal(MountState.Draining, h.State("alpha"));

        hung.Release();
        await h.FinishAsync(pump, "the suspend or the queued resume never finished", suspend, resume);

        Assert.Equal(MountState.Mounted, h.State("alpha"));
        Assert.Equal(2, h.MountCalls("M:"));
        Assert.Equal(1, h.MountCalls("P:"));
        Assert.True(h.Snapshot("delta").UserParked);
        Assert.NotEqual(MountState.Mounted, h.State("delta"));

        var history = h.Supervisor.GetTransitionHistory().Reverse().ToList();
        var remount = history.FindLastIndex(t => t.HostKey == "alpha" && t.To == MountState.Mounting);
        var alphaDrained = history.FindLastIndex(t => t.HostKey == "alpha" && t.From == MountState.Draining);
        var deltaDrained = history.FindLastIndex(t => t.HostKey == "delta" && t.From == MountState.Draining);
        Assert.True(alphaDrained >= 0 && deltaDrained >= 0, "both batch hosts must have finished their drains");
        Assert.True(
            remount > alphaDrained && remount > deltaDrained,
            "the resume's mount came before the suspend batch had finished: " + Describe(history));
        AssertMountingOnlyFromReady(h);
        Assert.Empty(LoopFaults(h));
    }
}
