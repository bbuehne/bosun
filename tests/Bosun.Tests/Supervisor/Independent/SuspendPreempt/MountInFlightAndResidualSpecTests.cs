using Bosun.Configuration;
using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor.Independent.SuspendPreempt;

/// <summary>
/// ADR-023 decision 5 (D1: a mount that passed its deep probe is abandoned if a suspend is pending),
/// decision 6 (D5: idle ticks skip while suspended), and the residuals the ADR admits (decision 4 and
/// "What is not preempted, and why"): a <c>mount/mount</c> already sent, the probe of an enable, a command,
/// and the post-mount VFS baseline sample run to completion. Each residual test checks the documented
/// behaviour is the actual behaviour. Written from ADR-023 and §4 rule 5, not from the implementation.
/// </summary>
public sealed class MountInFlightAndResidualSpecTests
{
    private static PreemptSpecHarness NewHarness(params HostConfig[] hosts) =>
        new(HostFixtures.Build(
            HostFixtures.Global(failuresBeforeUnmount: 3, suspendUnmountTimeoutSeconds: 3), hosts));

    // ------------------------------------------------------------------------------------------------
    // D1
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Protects:</b> D1 and rule 5: "If the probe passes and a suspend is pending, mount/mount is not sent:
    /// the mount is abandoned on the existing Mounting -> Draining edge with the suspend drain cause. It is
    /// not a mount failure ... an on-demand host keeps or gets the ADR-022 record"; and "The deep probe of
    /// rule 2 is not cancelled".
    /// <b>Catches:</b> <c>mount/mount</c> sent after the suspend was called (a drive created seconds before
    /// sleep); the abandonment counted as a mount failure (failure row, paced retry); the user's drive not
    /// coming back after resume because the suspend action no longer saw the host as Mounting.
    /// </summary>
    [Fact]
    public async Task A_user_mount_whose_deep_probe_passes_after_the_suspend_was_called_is_abandoned_and_returns_on_resume()
    {
        var h = NewHarness(HostFixtures.OnDemand("od", drive: "Q:"));
        await h.StartAsync();
        var deep = h.HProbe.HoldNextDeep("od", OnCancel.Ignore);
        var request = h.Supervisor.RequestMountAsync("od");
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(deep, "the deep probe entering Mounting");
        Assert.Equal(MountState.Mounting, h.State("od"));

        var suspend = SuspendCaller.Call(h.Supervisor);
        deep.Release(DeepProbeOutcome.Success);
        await h.FinishAsync(pump, "the mount command never finished", request, suspend);

        Assert.False(deep.TokenCancelled, "rule 5: the deep probe of rule 2 is not cancelled by a suspend");
        Assert.Equal(0, h.MountCalls("Q:"));
        Assert.Contains(h.Transitions("od"), t => t.From == MountState.Mounting && t.To == MountState.Draining && t.DrainCause == DrainCause.Suspend);
        Assert.Equal(0, h.Snapshot("od").ConsecutiveMountFailures);
        Assert.Null(h.Snapshot("od").LastMountFailureReason);

        await h.ResumeAsync();
        Assert.Equal(MountState.Mounted, h.State("od"));
        Assert.Equal(1, h.MountCalls("Q:"));
    }

    /// <summary>
    /// <b>Protects:</b> D1 when the mount was begun by a RECURRING tick: the persistent auto-mount on arrival
    /// at <c>Ready</c> through the idle probe. ADR-023 decision 2 makes the idle probe tick preemptible, but
    /// rule 5 says the deep probe of rule 2 is not cancelled, and D1 says the abandonment is not a failure.
    /// <b>Catches:</b> the tick's preemption token linked into the mount's deep probe, so the suspend turns
    /// the deep probe into <c>Cancelled</c>, which the mount path counts as a mount failure (a failure row and a
    /// paced retry after wake); or <c>mount/mount</c> sent anyway.
    /// </summary>
    [Fact]
    public async Task An_auto_mount_begun_by_an_idle_tick_is_abandoned_without_counting_a_mount_failure()
    {
        var h = NewHarness(HostFixtures.Persistent("pz", probeIntervalSeconds: 60, drive: "P:"));
        h.Probe.SetShallow(PreemptSpecHarness.HostnameOf("pz"), ShallowProbeOutcome.ConnectionRefused, times: 1);
        await h.StartAsync();
        Assert.Equal(MountState.Unreachable, h.State("pz"));

        var deep = h.HProbe.HoldNextDeep("pz", OnCancel.Ignore);
        h.FireTimers(TimeSpan.FromSeconds(6)); // first backoff rung
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(deep, "the auto-mount's deep probe, inside the idle tick");
        Assert.Equal(MountState.Mounting, h.State("pz"));

        var suspend = SuspendCaller.Call(h.Supervisor);
        deep.Release(DeepProbeOutcome.Success);
        await h.FinishAsync(pump, "the idle tick never finished", suspend);

        Assert.False(deep.TokenCancelled, "rule 5: the deep probe of rule 2 is not cancelled by a suspend");
        Assert.Equal(0, h.MountCalls("P:"));
        Assert.Equal([DrainCause.Suspend], h.DrainCauses("pz"));
        Assert.Equal(0, h.Snapshot("pz").ConsecutiveMountFailures);
        Assert.Null(h.Snapshot("pz").LastMountFailureReason);

        // Not paced as a failed mount: resume mounts it at once.
        await h.ResumeAsync();
        Assert.Equal(MountState.Mounted, h.State("pz"));
        Assert.Equal(1, h.MountCalls("P:"));
    }

    /// <summary>
    /// <b>Protects:</b> D1: "A deep-probe failure still wins and is still a mount failure."
    /// <b>Catches:</b> the pending-suspend check placed before the outcome check, so a host whose SFTP is
    /// genuinely broken is recorded as a clean suspend abandonment and its failure row vanishes.
    /// </summary>
    [Fact]
    public async Task A_deep_probe_that_fails_while_a_suspend_is_pending_is_still_a_mount_failure()
    {
        var h = NewHarness(HostFixtures.OnDemand("od", drive: "Q:"));
        await h.StartAsync();
        var deep = h.HProbe.HoldNextDeep("od", OnCancel.Ignore);
        var request = h.Supervisor.RequestMountAsync("od");
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(deep, "the deep probe entering Mounting");

        var suspend = SuspendCaller.Call(h.Supervisor);
        deep.Release(DeepProbeOutcome.Failed);
        await h.FinishAsync(pump, "the mount command never finished", request, suspend);

        Assert.Equal(0, h.MountCalls("Q:"));
        Assert.Equal([DrainCause.MountFailure], h.DrainCauses("od"));
        Assert.Equal(1, h.Snapshot("od").ConsecutiveMountFailures);
        Assert.NotNull(h.Snapshot("od").LastMountFailureReason);
    }

    // ------------------------------------------------------------------------------------------------
    // Residuals: what is NOT preempted
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Protects:</b> decision 4 / rule 5: "A mount/mount already sent is never cancelled; the mount
    /// completes and the suspend drains it ... and the post-mount VFS baseline sample (rule 7) is skipped."
    /// <b>Catches:</b> cancelling an in-flight <c>mount/mount</c> (unknown outcome, a half-made mount handed to
    /// a suspend); a suspend that does not then drain it; a VFS baseline poll started while the suspend is
    /// pending (one more rc call that can hang the suspend).
    /// </summary>
    [Fact]
    public async Task A_mount_already_sent_completes_is_drained_by_the_suspend_and_takes_no_VFS_baseline()
    {
        var h = NewHarness(HostFixtures.OnDemand("od", idleUnmountSeconds: 600, drive: "Q:"));
        await h.StartAsync();
        var mount = h.HRclone.HoldNext(OnCancel.LikeProduction, "mount");
        var request = h.Supervisor.RequestMountAsync("od");
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(mount, "mount/mount");

        var suspend = SuspendCaller.Call(h.Supervisor);
        mount.Release();
        await h.FinishAsync(pump, "the mount command never finished", request, suspend);

        Assert.False(mount.TokenCancelled, "the suspend cancelled a mount/mount that was already sent");
        Assert.Equal(1, h.MountCalls("Q:"));
        Assert.Equal([DrainCause.Suspend], h.DrainCauses("od"));
        Assert.False(h.IsMountedInRclone("Q:"));
        Assert.Equal(0, h.HRclone.Count("vfslist") + h.HRclone.Count("vfsstats") + h.HRclone.Count("vfsqueue"));

        // It was Mounted when the suspend ran, so ADR-022 brings it back.
        await h.ResumeAsync();
        Assert.Equal(MountState.Mounted, h.State("od"));
    }

    /// <summary>
    /// <b>Protects:</b> decision 4 ("the probe that EnableHostAsync is already running" is not preempted) and
    /// rule 10's note: "one already in Probing runs to its outcome -- there is no Probing -> Disabled edge".
    /// <b>Catches:</b> an enable probe cancelled by the suspend and then counted as a failure (the host shows
    /// Unreachable for no reason, and an on-demand host would not be polled again), or a new
    /// <c>Probing -> Disabled</c> edge.
    /// </summary>
    [Fact]
    public async Task An_enable_probe_already_running_is_not_preempted_and_ends_in_Ready_without_mounting()
    {
        var h = NewHarness();
        await h.StartAsync();
        var added = HostFixtures.Persistent("nw", probeIntervalSeconds: 60, drive: "N:");
        var next = HostFixtures.Build(h.Store.Current.Global, added);
        var probe = h.HProbe.HoldNextShallow(PreemptSpecHarness.HostnameOf("nw"), OnCancel.LikeProduction);
        h.Store.Publish(next);
        var change = h.Supervisor.ConfigChangedAsync(next);
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(probe, "the enable probe of the added host");
        Assert.Equal(MountState.Probing, h.State("nw"));

        var suspend = SuspendCaller.Call(h.Supervisor);
        probe.Release(ShallowProbeOutcome.Success);
        await h.FinishAsync(pump, "the config change never finished", change, suspend);

        Assert.False(probe.TokenCancelled, "the suspend cancelled an enable's probe");
        Assert.Equal(MountState.Ready, h.State("nw"));
        Assert.DoesNotContain(h.Transitions("nw"), t => t.From == MountState.Probing && t.To == MountState.Disabled);
        Assert.Equal(0, h.MountCalls("N:"));

        await h.ResumeAsync();
        Assert.Equal(MountState.Mounted, h.State("nw"));
    }

    /// <summary>
    /// <b>Protects:</b> decision 4: commands are not preempted -- here a network change's forced probe of a
    /// mounted host (ADR-017). The suspend then drains as usual.
    /// <b>Catches:</b> the suspend cancelling a command's probe, whose <c>Cancelled</c> result the network-change
    /// path would then read as a failure of a host that is fine.
    /// </summary>
    [Fact]
    public async Task A_network_changes_forced_probe_is_not_preempted()
    {
        var h = NewHarness(HostFixtures.Persistent("m1", probeIntervalSeconds: 60, drive: "M:"));
        await h.StartAsync();
        var probe = h.HProbe.HoldNextShallow(PreemptSpecHarness.HostnameOf("m1"), OnCancel.LikeProduction);
        var change = h.Supervisor.NetworkChangedAsync();
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(probe, "the network change's forced shallow probe");

        var suspend = SuspendCaller.Call(h.Supervisor);
        probe.Release(ShallowProbeOutcome.Success);
        await h.FinishAsync(pump, "the network change never finished", change, suspend);

        Assert.False(probe.TokenCancelled, "the suspend cancelled a command's probe");
        Assert.Equal([DrainCause.Suspend], h.DrainCauses("m1"));
        Assert.False(h.IsMountedInRclone("M:"));
    }

    /// <summary>
    /// <b>Protects:</b> ADR-023's admitted residual: "a suspend that arrives during the VFS baseline sample that
    /// follows a mount waits for it (at most one probe timeout)", and rule 7: every such poll runs "under one
    /// probe timeout".
    /// <b>Catches:</b> a baseline poll with no timeout on the injected clock -- with <c>rclone rcd</c> hung, the
    /// suspend would wait forever behind it, not "at most one probe timeout".
    /// </summary>
    [Fact]
    public async Task A_suspend_behind_a_hung_VFS_baseline_sample_is_released_by_the_probe_timeout()
    {
        var h = NewHarness(HostFixtures.OnDemand("od", idleUnmountSeconds: 600, drive: "Q:"));
        await h.StartAsync();
        var sample = h.HRclone.HoldNext(OnCancel.LikeProduction, "vfslist", "vfsstats", "vfsqueue");
        var request = h.Supervisor.RequestMountAsync("od");
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(sample, "the post-mount VFS baseline sample");
        Assert.Equal(1, h.MountCalls("Q:"));

        var suspend = SuspendCaller.Call(h.Supervisor);
        h.FireTimers(TimeSpan.FromSeconds(5)); // global.probe_timeout_seconds
        await h.FinishAsync(pump, "the VFS baseline sample was not bounded by the probe timeout", request, suspend);

        Assert.Equal([DrainCause.Suspend], h.DrainCauses("od"));
        Assert.False(h.IsMountedInRclone("Q:"));
    }

    // ------------------------------------------------------------------------------------------------
    // D5
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Protects:</b> D5 / rule 5: "An idle probe tick that fires while the system is suspended does nothing
    /// and re-arms itself; the forced probes of resume, a network change and 'retry now' are commands and still
    /// probe." Then ADR-011: the ladder still runs after resume.
    /// <b>Catches:</b> idle ticks probing during the sleep window; a skipped tick that does not re-arm (the
    /// host is never polled again after resume); "retry now" wrongly skipped while suspended; a mount while
    /// suspended.
    /// </summary>
    [Fact]
    public async Task Idle_ticks_skip_while_suspended_but_retry_now_still_probes_and_the_ladder_resumes()
    {
        var h = NewHarness(HostFixtures.Persistent("pz", probeIntervalSeconds: 60, drive: "P:"));
        h.Probe.SetShallow(PreemptSpecHarness.HostnameOf("pz"), ShallowProbeOutcome.ConnectionRefused);
        await h.StartAsync();
        Assert.Equal(MountState.Unreachable, h.State("pz"));

        await h.SuspendAsync();
        var atSuspend = h.ShallowCalls("pz");
        await h.AdvanceAsync(TimeSpan.FromHours(2));
        Assert.Equal(atSuspend, h.ShallowCalls("pz"));

        h.Probe.SetShallow(PreemptSpecHarness.HostnameOf("pz"), ShallowProbeOutcome.Success);
        await h.RunAsync(() => h.Supervisor.RequestRetryNowAsync("pz"));
        Assert.Equal(atSuspend + 1, h.ShallowCalls("pz"));
        Assert.Equal(MountState.Ready, h.State("pz"));
        Assert.Equal(0, h.MountCalls("P:"));

        h.Probe.SetShallow(PreemptSpecHarness.HostnameOf("pz"), ShallowProbeOutcome.ConnectionRefused);
        await h.ResumeAsync();
        Assert.Equal(MountState.Unreachable, h.State("pz"));
        var afterResume = h.ShallowCalls("pz");

        // 5+15+30+60+300+300+... : at least six ladder probes in 15 minutes.
        await h.AdvanceAsync(TimeSpan.FromMinutes(15));
        Assert.True(h.ShallowCalls("pz") >= afterResume + 6, $"the ladder stopped after resume: {h.ShallowCalls("pz") - afterResume} probes in 15 min");

        h.Probe.SetShallow(PreemptSpecHarness.HostnameOf("pz"), ShallowProbeOutcome.Success);
        await h.AdvanceAsync(TimeSpan.FromMinutes(6));
        Assert.Equal(MountState.Mounted, h.State("pz"));
    }
}
