using Bosun.Configuration;
using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Independent.SuspendBudget;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor;

/// <summary>
/// bs-bv0 (Invariant I8): while the system is suspended, no automatic path may call
/// <c>mount/mount</c>; and what the gate holds back is picked up again at resume, through a fresh
/// probe (ADR-017), without a new state-machine transition.
/// </summary>
/// <remarks>
/// The two acceptance tests for the two original routes (a mount-retry timer, and an Unreachable
/// persistent host's periodic probe) live in
/// <c>Independent/SuspendBudget/SuspendWhileDrainingSpecTests</c>. These cover the rest of the gate and
/// the resume half. Fakes and a fake clock only: no rclone, drive letter, or SystemEvents.
/// </remarks>
public sealed class SuspendGateTests
{
    private static BosunConfig Config(params HostConfig[] hosts) =>
        HostFixtures.Build(HostFixtures.Global(suspendUnmountTimeoutSeconds: 3), hosts);

    private static string Hostname(string key) => SuspendBudgetHarness.HostnameOf(key);

    // ------------------------------------------------------------------------------------------
    // Held back, then mounted at resume
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Protects:</b> the resume half of bs-bv0. A persistent host that became Ready while
    /// suspended was held back; resume must mount it once a fresh probe passes, not leave it in
    /// Ready (an in-place probe on a Ready host does not re-run the persistent auto-mount).
    /// <b>Catches:</b> a gate with no resume handling: the host sits in Ready forever, un-mounted.
    /// Also catches a resume that mounts without probing (I1, ADR-017): the shallow probe count must
    /// rise across resume.
    /// </summary>
    [Fact]
    public async Task A_persistent_host_held_back_while_suspended_mounts_after_resume_once_a_probe_passes()
    {
        var h = new SuspendBudgetHarness(Config(HostFixtures.Persistent("prod", probeIntervalSeconds: 30, drive: "P:")));
        h.Probe.SetShallow(Hostname("prod"), ShallowProbeOutcome.ConnectionRefused);
        await h.StartAsync();
        Assert.Equal(MountState.Unreachable, h.State("prod"));

        await h.SuspendAsync();
        h.Probe.SetShallow(Hostname("prod"), ShallowProbeOutcome.Success);
        await h.AdvanceAsync(TimeSpan.FromHours(1));

        // The periodic probe succeeded while suspended: the host is Ready, and held back.
        Assert.Equal(MountState.Ready, h.State("prod"));
        Assert.Equal(0, h.Rclone.TotalMountCalls);
        var probesBeforeResume = h.Probe.ShallowCount(Hostname("prod"));

        await h.ResumeAsync();

        Assert.True(
            h.Probe.ShallowCount(Hostname("prod")) > probesBeforeResume,
            "resume must probe a held-back Ready host afresh before mounting it (ADR-017)");
        Assert.Equal(MountState.Mounted, h.State("prod"));
        Assert.Equal(1, h.Rclone.MountCount("P:"));
    }

    /// <summary>
    /// <b>Protects:</b> I1 across resume. A host held back in Ready is not trusted at resume: if the
    /// fresh probe fails, it goes Unreachable and is not mounted.
    /// <b>Catches:</b> a resume that mounts a held-back Ready host on the strength of the stale Ready
    /// it earned before the machine slept.
    /// </summary>
    [Fact]
    public async Task A_held_back_host_whose_resume_probe_fails_is_not_mounted()
    {
        var h = new SuspendBudgetHarness(Config(HostFixtures.Persistent("prod", probeIntervalSeconds: 30, drive: "P:")));
        h.Probe.SetShallow(Hostname("prod"), ShallowProbeOutcome.ConnectionRefused);
        await h.StartAsync();
        await h.SuspendAsync();
        h.Probe.SetShallow(Hostname("prod"), ShallowProbeOutcome.Success);
        await h.AdvanceAsync(TimeSpan.FromHours(1));
        Assert.Equal(MountState.Ready, h.State("prod"));

        h.Probe.SetShallow(Hostname("prod"), ShallowProbeOutcome.ConnectionRefused);
        await h.ResumeAsync();

        Assert.Equal(MountState.Unreachable, h.State("prod"));
        Assert.Equal(0, h.Rclone.TotalMountCalls);
    }

    /// <summary>
    /// <b>Protects:</b> the mount-retry half of the gate. A retry timer that fires during suspend
    /// must not probe or re-enable the host (no traffic, no mount in the seconds before sleep); the
    /// host stays Disabled and resume re-enables it.
    /// <b>Catches:</b> a retry timer that still probes while suspended (probe count rises), or one
    /// that mounts.
    /// </summary>
    [Fact]
    public async Task A_mount_retry_timer_firing_while_suspended_leaves_the_host_disabled_and_silent()
    {
        var h = new SuspendBudgetHarness(Config(HostFixtures.Persistent("prod", probeIntervalSeconds: 0, drive: "P:")));
        h.Probe.SetDeep("prod", DeepProbeOutcome.Failed, times: 1);
        await h.StartAsync();
        Assert.Equal(MountState.Disabled, h.State("prod"));
        Assert.Equal(0, h.Rclone.TotalMountCalls);

        await h.SuspendAsync();
        var probesAtSuspend = h.Probe.ShallowCount(Hostname("prod"));
        await h.AdvanceAsync(TimeSpan.FromDays(1));

        Assert.Equal(MountState.Disabled, h.State("prod"));
        Assert.Equal(probesAtSuspend, h.Probe.ShallowCount(Hostname("prod")));
        Assert.Equal(0, h.Rclone.TotalMountCalls);

        await h.ResumeAsync();
        Assert.Equal(MountState.Mounted, h.State("prod"));
        Assert.Equal(1, h.Rclone.MountCount("P:"));
    }

    // ------------------------------------------------------------------------------------------
    // Other routes into Mounting are gated too
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Protects:</b> that the gate is at the single entry to Mounting, not on one caller: a user
    /// mount request for a host that is already Ready, and a network change that brings a persistent
    /// host from Unreachable to Ready, both reach the mount path while suspended.
    /// <b>Catches:</b> a gate placed only in the idle-probe auto-mount.
    /// </summary>
    [Fact]
    public async Task No_other_route_mounts_while_suspended()
    {
        var h = new SuspendBudgetHarness(Config(
            HostFixtures.OnDemand("archive", drive: "Q:"),
            HostFixtures.Persistent("prod", probeIntervalSeconds: 0, drive: "P:")));
        h.Probe.SetShallow(Hostname("prod"), ShallowProbeOutcome.ConnectionRefused);
        await h.StartAsync();
        Assert.Equal(MountState.Ready, h.State("archive"));
        Assert.Equal(MountState.Unreachable, h.State("prod"));

        await h.SuspendAsync();
        h.Probe.SetShallow(Hostname("prod"), ShallowProbeOutcome.Success);

        // Route 1: an explicit mount request against a Ready host.
        await h.RunAsync(() => h.Supervisor.RequestMountAsync("archive"));
        // Route 2: a network change re-probes the Unreachable persistent host, which goes Ready.
        await h.RunAsync(() => h.Supervisor.NetworkChangedAsync());
        // Route 3: "retry now".
        await h.RunAsync(() => h.Supervisor.RequestRetryNowAsync("prod"));

        Assert.Equal(0, h.Rclone.TotalMountCalls);
        Assert.Equal(MountState.Ready, h.State("archive"));
        Assert.Equal(MountState.Ready, h.State("prod"));

        await h.ResumeAsync();

        // The persistent host mounts; the on-demand one was never mounted by the user, so there is nothing to bring back (ADR-022).
        Assert.Equal(MountState.Mounted, h.State("prod"));
        Assert.Equal(MountState.Ready, h.State("archive"));
        Assert.Equal(0, h.Rclone.MountCount("Q:"));
    }

    // ------------------------------------------------------------------------------------------
    // What resume must NOT mount
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Protects:</b> rule 9 across the new resume path. A parked persistent host that went
    /// Unreachable and came back Ready while suspended is not mounted at resume.
    /// <b>Catches:</b> the resume handling mounting every Ready persistent host without the parked
    /// check, undoing the user's explicit unmount on the next wake.
    /// </summary>
    [Fact]
    public async Task A_parked_host_is_not_auto_mounted_after_resume()
    {
        var h = new SuspendBudgetHarness(Config(HostFixtures.Persistent("prod", probeIntervalSeconds: 30, drive: "P:")));
        await h.StartAsync();
        Assert.Equal(MountState.Mounted, h.State("prod"));
        await h.RunAsync(() => h.Supervisor.RequestUnmountAsync("prod"));
        await h.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(MountState.Ready, h.State("prod"));
        Assert.True(h.Snapshot("prod").UserParked);
        Assert.Equal(1, h.Rclone.MountCount("P:"));

        // Down and back up again across the suspend, so it arrives at Ready while suspended too.
        h.Probe.SetShallow(Hostname("prod"), ShallowProbeOutcome.ConnectionRefused);
        await h.AdvanceAsync(TimeSpan.FromSeconds(31));
        Assert.Equal(MountState.Unreachable, h.State("prod"));
        await h.SuspendAsync();
        h.Probe.SetShallow(Hostname("prod"), ShallowProbeOutcome.Success);
        await h.AdvanceAsync(TimeSpan.FromHours(1));
        Assert.Equal(MountState.Ready, h.State("prod"));

        await h.ResumeAsync();
        await h.AdvanceAsync(TimeSpan.FromMinutes(10));

        Assert.Equal(MountState.Ready, h.State("prod"));
        Assert.True(h.Snapshot("prod").UserParked);
        Assert.Equal(1, h.Rclone.MountCount("P:"));
    }

    /// <summary>
    /// <b>Protects:</b> rule 6 across resume. An on-demand host rests in Ready and is not
    /// auto-mounted at resume when the user had not mounted it before the sleep (one the user had
    /// mounted is remounted, ADR-022; see RemountAfterResumeTests).
    /// <b>Catches:</b> resume handling that mounts any Ready host rather than only persistent ones.
    /// </summary>
    [Fact]
    public async Task An_on_demand_host_is_not_auto_mounted_after_resume()
    {
        var h = new SuspendBudgetHarness(Config(HostFixtures.OnDemand("archive", probeIntervalSeconds: 30, drive: "Q:")));
        await h.StartAsync();
        Assert.Equal(MountState.Ready, h.State("archive"));

        await h.SuspendAsync();
        await h.AdvanceAsync(TimeSpan.FromHours(1));
        await h.ResumeAsync();
        await h.AdvanceAsync(TimeSpan.FromMinutes(10));

        Assert.Equal(MountState.Ready, h.State("archive"));
        Assert.Equal(0, h.Rclone.TotalMountCalls);
    }
}
