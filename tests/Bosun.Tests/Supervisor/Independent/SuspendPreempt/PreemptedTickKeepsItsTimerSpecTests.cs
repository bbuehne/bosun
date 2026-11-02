using Bosun.Configuration;
using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor.Independent.SuspendPreempt;

/// <summary>
/// ADR-023 decision 3 ("The tick re-arms its own timer") against ADR-011 and Invariant I2: after any
/// preempted tick, the host -- and the reconciliation loop -- keep their timers. Each test preempts one of
/// the five recurring ticks, resumes, runs a long post-resume window on the fake clock, and checks that the
/// polling the spec requires is still happening and still able to drain a dead host. Written from the ADR,
/// not from the implementation.
/// </summary>
/// <remarks>
/// ADR-023 names the hazard: "Using one token for both would let a cancellation escape and strand a
/// Mounted host with no probe timer (ADR-011, I2)." A stranded mounted host never accumulates failures and
/// never drains, so its drive letter outlives its host -- the failure this project exists to prevent.
/// </remarks>
public sealed class PreemptedTickKeepsItsTimerSpecTests
{
    private static PreemptSpecHarness NewHarness(params HostConfig[] hosts) =>
        new(HostFixtures.Build(
            HostFixtures.Global(failuresBeforeUnmount: 3, suspendUnmountTimeoutSeconds: 3), hosts));

    private static async Task PreemptAndResumeAsync(PreemptSpecHarness h, Hold hold, TimeSpan fire, string what)
    {
        h.FireTimers(fire);
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(hold, what);
        var suspend = SuspendCaller.Call(h.Supervisor);
        if (hold.OnCancel == OnCancel.Ignore)
        {
            hold.Release();
        }

        await h.FinishAsync(pump, $"the suspend waited for {what}", suspend);
        Assert.True(hold.TokenCancelled, $"the suspend did not preempt {what}");
        Assert.Empty(h.Errors);
        await h.ResumeAsync();
    }

    /// <summary>Over 30 minutes after resume a mounted persistent host is shallow-probed every 60 s and
    /// deep-probed every 300 s, and three shallow failures then drain it as <c>Automatic</c>.</summary>
    private static async Task AssertMountedHostIsStillSupervisedAsync(PreemptSpecHarness h, string hostKey, string drive)
    {
        Assert.Equal(MountState.Mounted, h.State(hostKey));
        var shallow = h.ShallowCalls(hostKey);
        var deep = h.HProbe.DeepCalls(hostKey);

        await h.AdvanceAsync(TimeSpan.FromMinutes(30));
        Assert.True(h.ShallowCalls(hostKey) - shallow >= 29, $"mounted shallow probing stopped: {h.ShallowCalls(hostKey) - shallow} in 30 min");
        Assert.True(h.HProbe.DeepCalls(hostKey) - deep >= 5, $"mounted deep probing stopped: {h.HProbe.DeepCalls(hostKey) - deep} in 30 min");

        h.Probe.SetShallow(PreemptSpecHarness.HostnameOf(hostKey), ShallowProbeOutcome.ConnectionRefused);
        await h.AdvanceAsync(TimeSpan.FromSeconds(185));
        Assert.Contains(DrainCause.Automatic, h.DrainCauses(hostKey));
        Assert.False(h.IsMountedInRclone(drive), "a dead host's drive survived three probe intervals after a preempted tick");
    }

    /// <summary>
    /// <b>Protects:</b> ADR-011 / I2 after the mounted shallow tick is preempted.
    /// <b>Catches:</b> the preempted tick returning without re-arming, or its cancellation escaping the tick.
    /// </summary>
    [Fact]
    public async Task After_a_preempted_mounted_shallow_tick_the_remounted_host_is_still_probed_and_drains_on_failure()
    {
        var h = NewHarness(HostFixtures.Persistent("alpha", probeIntervalSeconds: 60, drive: "A:"));
        await h.StartAsync();
        var hold = h.HProbe.HoldNextShallow(PreemptSpecHarness.HostnameOf("alpha"));

        await PreemptAndResumeAsync(h, hold, TimeSpan.FromSeconds(60), "the mounted shallow probe");

        await AssertMountedHostIsStillSupervisedAsync(h, "alpha", "A:");
    }

    /// <summary>
    /// <b>Protects:</b> ADR-011 / ADR-016 after the mounted deep tick is preempted.
    /// <b>Catches:</b> as above, for the deep probe's own timer.
    /// </summary>
    [Fact]
    public async Task After_a_preempted_mounted_deep_tick_the_remounted_host_is_still_probed_and_drains_on_failure()
    {
        var h = NewHarness(HostFixtures.Persistent("alpha", probeIntervalSeconds: 60, drive: "A:"));
        await h.StartAsync();
        await h.AdvanceAsync(TimeSpan.FromSeconds(299));
        var hold = h.HProbe.HoldNextDeep("alpha");

        await PreemptAndResumeAsync(h, hold, TimeSpan.FromSeconds(1), "the mounted deep probe");

        await AssertMountedHostIsStillSupervisedAsync(h, "alpha", "A:");
    }

    /// <summary>
    /// <b>Protects:</b> decision 2 (the reconciliation tick is preemptible) with §4 "Reconciliation": every
    /// tick compares intended state against <c>mount/listmounts</c>. The reconciliation timer is global, so
    /// nothing a resume does for a host would re-arm it if the preempted tick did not.
    /// <b>Catches:</b> reconciliation that stops for good after the first suspend that caught it running --
    /// drift (a mount rclone lost, a Bosun crash leftover) is then never corrected.
    /// </summary>
    [Fact]
    public async Task After_a_preempted_reconciliation_tick_reconciliation_keeps_running()
    {
        var h = NewHarness(HostFixtures.Persistent("alpha", probeIntervalSeconds: 60, drive: "A:"));
        await h.StartAsync();
        await h.AdvanceAsync(TimeSpan.FromSeconds(29.9));
        var hold = h.HRclone.HoldNext(OnCancel.LikeProduction, "version", "listmounts");

        await PreemptAndResumeAsync(h, hold, TimeSpan.FromSeconds(0.2), "the reconciliation tick");

        var before = h.HRclone.Count("listmounts");
        await h.AdvanceAsync(TimeSpan.FromMinutes(10));
        Assert.True(h.HRclone.Count("listmounts") - before >= 18, $"reconciliation stopped: {h.HRclone.Count("listmounts") - before} listmounts in 10 min");
    }

    /// <summary>
    /// <b>Protects:</b> ADR-011 / the Backoff ladder after the idle probe tick of an unreachable persistent host
    /// is preempted (docs/OPERATIONS.md T1: the drive returns on its own once the network does).
    /// <b>Catches:</b> a preempted idle tick that does not re-arm, so the host stays Unreachable forever.
    /// </summary>
    [Fact]
    public async Task After_a_preempted_idle_tick_the_ladder_keeps_polling_and_the_host_mounts_when_it_returns()
    {
        var h = NewHarness(HostFixtures.Persistent("pz", probeIntervalSeconds: 60, drive: "P:"));
        h.Probe.SetShallow(PreemptSpecHarness.HostnameOf("pz"), ShallowProbeOutcome.ConnectionRefused);
        await h.StartAsync();
        var hold = h.HProbe.HoldNextShallow(PreemptSpecHarness.HostnameOf("pz"));

        await PreemptAndResumeAsync(h, hold, TimeSpan.FromSeconds(6), "the idle probe");
        Assert.Equal(MountState.Unreachable, h.State("pz"));

        var before = h.ShallowCalls("pz");
        await h.AdvanceAsync(TimeSpan.FromMinutes(30));
        Assert.True(h.ShallowCalls("pz") - before >= 8, $"the ladder stopped: {h.ShallowCalls("pz") - before} probes in 30 min");

        h.Probe.SetShallow(PreemptSpecHarness.HostnameOf("pz"), ShallowProbeOutcome.Success);
        await h.AdvanceAsync(TimeSpan.FromMinutes(6));
        Assert.Equal(MountState.Mounted, h.State("pz"));
    }

    /// <summary>
    /// <b>Protects:</b> decision 2 for the idle-unmount fire, decision 3 (a preempted tick discards what its
    /// I/O returned), ADR-022 (an on-demand drive the user had mounted comes back after the sleep) and rule
    /// 9a's "The idle timer starts fresh".
    /// <b>Catches:</b> an idle fire whose <c>vfs/*</c> reads return a clean, idle VFS after the suspend
    /// cancelled the tick, and which then unmounts as <c>IdleTimeout</c>. The suspend then finds the host
    /// already draining, keeps the idle cause, sets no ADR-022 record, and the user's drive is gone after wake.
    /// Also catches an idle clock that is never re-armed after the remount.
    /// </summary>
    [Fact]
    public async Task A_preempted_idle_unmount_fire_is_not_an_idle_unmount_and_the_drive_returns_with_a_fresh_clock()
    {
        var h = NewHarness(HostFixtures.OnDemand("od", idleUnmountSeconds: 90, drive: "Q:"));
        await h.StartAsync();
        await h.RunAsync(() => h.Supervisor.RequestMountAsync("od"));
        await h.AdvanceAsync(TimeSpan.FromSeconds(89.9));
        Assert.Equal(MountState.Mounted, h.State("od"));
        var hold = h.HRclone.HoldNext(OnCancel.Ignore, "vfslist", "vfsstats", "vfsqueue");

        await PreemptAndResumeAsync(h, hold, TimeSpan.FromSeconds(0.2), "the idle-unmount fire");

        Assert.Equal([DrainCause.Suspend], h.DrainCauses("od"));
        Assert.Equal(MountState.Mounted, h.State("od"));
        Assert.Equal(2, h.MountCalls("Q:"));

        await h.AdvanceAsync(TimeSpan.FromSeconds(200));
        Assert.Equal([DrainCause.Suspend, DrainCause.IdleTimeout], h.DrainCauses("od"));
    }
}
