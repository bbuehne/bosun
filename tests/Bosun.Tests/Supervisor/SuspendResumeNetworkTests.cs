using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor;

/// <summary>
/// docs/ARCHITECTURE.md §4 rule 5 ("On suspend, every host in Mounted or Mounting goes to
/// Draining, then Disabled. On resume, everything previously enabled goes to Probing") and the
/// Backoff section's reset triggers ("reset to zero by a network address change, a power resume,
/// or an explicit user 'retry now'").
/// </summary>
public sealed class SuspendResumeNetworkTests
{
    [Fact]
    public async Task Suspend_drains_a_Mounted_host_and_parks_it_in_Disabled_without_reprobing()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        await harness.StartAsync();
        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);
        var probeCallsBefore = harness.Probe.ShallowProbeCalls.Count;

        await harness.RunAsync(() => harness.Supervisor.SuspendAsync());

        Assert.Equal(MountState.Disabled, harness.Snapshot("prod").State);
        Assert.Contains("P:", harness.Rclone.UnmountCalls);
        // Parked, not re-armed -- must not auto re-enable while suspended.
        Assert.Equal(probeCallsBefore, harness.Probe.ShallowProbeCalls.Count);
    }

    [Fact]
    public async Task Suspend_does_not_touch_a_host_that_is_already_Ready_or_Unreachable()
    {
        var mounted = HostFixtures.Persistent("prod", drive: "P:");
        var idle = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), mounted, idle));
        await harness.StartAsync();
        Assert.Equal(MountState.Ready, harness.Snapshot("archive").State);

        await harness.RunAsync(() => harness.Supervisor.SuspendAsync());

        // Rule 5 names only Mounted/Mounting -- an already-idle host is untouched by suspend.
        Assert.Equal(MountState.Ready, harness.Snapshot("archive").State);
    }

    [Fact]
    public async Task Resume_re_enables_a_host_parked_in_Disabled_by_suspend()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.SuspendAsync());
        Assert.Equal(MountState.Disabled, harness.Snapshot("prod").State);

        await harness.RunAsync(() => harness.Supervisor.ResumeAsync());

        // Persistent tier -> re-probes and auto-mounts again once Ready.
        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);
    }

    [Fact]
    public async Task Resume_bypasses_backoff_for_an_Unreachable_persistent_host()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var global = HostFixtures.Global(backoffSeconds: [5, 15, 30, 60, 300]);
        var harness = new SupervisorHarness(HostFixtures.Build(global, host));
        harness.Probe.SetDefaultShallow(ShallowProbeOutcome.ConnectionRefused);
        await harness.StartAsync();
        Assert.Equal(MountState.Unreachable, harness.Snapshot("prod").State);
        Assert.Equal(1, harness.Snapshot("prod").ConsecutiveIdleFailures);

        harness.Probe.SetDefaultShallow(ShallowProbeOutcome.Success);

        // Without resume, the next probe would not fire until the 5s backoff rung elapses.
        // ResumeAsync must probe NOW, not wait for that timer. Persistent tier auto-mounts once
        // Ready (rule 6).
        await harness.RunAsync(() => harness.Supervisor.ResumeAsync());

        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);
        Assert.Equal(0, harness.Snapshot("prod").ConsecutiveIdleFailures);
    }

    /// <summary>
    /// ADR-014 splits the RECURRING ladder by tier -- it does NOT split resume. Resume force-probes
    /// every enabled host, on-demand included: it is one bounded probe per host, not the continuous
    /// traffic ADR-014's cost argument is about, and OPERATIONS.md T2 turns on exactly this. Bug
    /// this catches: over-applying the tier split to resume, which leaves an on-demand row showing
    /// a stale Unreachable after the machine has moved networks -- confidently wrong, which is worse
    /// than ADR-008's "unknown until acted on".
    /// </summary>
    [Fact]
    public async Task Resume_force_probes_an_Unreachable_on_demand_host_too()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        harness.Probe.SetDefaultShallow(ShallowProbeOutcome.ConnectionRefused);
        await harness.StartAsync();
        Assert.Equal(MountState.Unreachable, harness.Snapshot("archive").State);
        var probesBefore = harness.Probe.ShallowProbeCalls.Count;

        harness.Probe.SetDefaultShallow(ShallowProbeOutcome.Success);
        await harness.RunAsync(() => harness.Supervisor.ResumeAsync());

        // Probed immediately, with no clock movement, and recovered. On-demand means "do not poll
        // it in the background", not "never tell the user whether it is up".
        Assert.Equal(probesBefore + 1, harness.Probe.ShallowProbeCalls.Count);
        Assert.Equal(MountState.Ready, harness.Snapshot("archive").State);
    }

    [Fact]
    public async Task NetworkChanged_resets_backoff_and_force_probes_an_Unreachable_persistent_host()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        harness.Probe.SetDefaultShallow(ShallowProbeOutcome.ConnectionRefused);
        await harness.StartAsync();
        Assert.Equal(MountState.Unreachable, harness.Snapshot("prod").State);

        harness.Probe.SetDefaultShallow(ShallowProbeOutcome.Success);
        await harness.RunAsync(() => harness.Supervisor.NetworkChangedAsync());

        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);
    }

    /// <summary>Same reasoning as <see cref="Resume_force_probes_an_Unreachable_on_demand_host_too"/>,
    /// for the network-change trigger -- the event a dock or an undock actually raises.</summary>
    [Fact]
    public async Task NetworkChanged_force_probes_an_Unreachable_on_demand_host_too()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        harness.Probe.SetDefaultShallow(ShallowProbeOutcome.ConnectionRefused);
        await harness.StartAsync();
        Assert.Equal(MountState.Unreachable, harness.Snapshot("archive").State);
        var probesBefore = harness.Probe.ShallowProbeCalls.Count;

        harness.Probe.SetDefaultShallow(ShallowProbeOutcome.Success);
        await harness.RunAsync(() => harness.Supervisor.NetworkChangedAsync());

        Assert.Equal(probesBefore + 1, harness.Probe.ShallowProbeCalls.Count);
        Assert.Equal(MountState.Ready, harness.Snapshot("archive").State);
    }

    [Fact]
    public async Task NetworkChanged_force_probes_a_Mounted_host_immediately()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:", probeIntervalSeconds: 3600);
        var global = HostFixtures.Global(mountedProbeIntervalSeconds: 3600);
        var harness = new SupervisorHarness(HostFixtures.Build(global, host));
        await harness.StartAsync();
        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);
        var callsBefore = harness.Probe.ShallowProbeCalls.Count;

        await harness.RunAsync(() => harness.Supervisor.NetworkChangedAsync());

        Assert.True(harness.Probe.ShallowProbeCalls.Count > callsBefore);
    }

    [Fact]
    public async Task NetworkChanged_does_not_reawaken_a_Disabled_host()
    {
        var host = HostFixtures.None("jump");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        await harness.StartAsync();
        Assert.Equal(MountState.Disabled, harness.Snapshot("jump").State);

        await harness.RunAsync(() => harness.Supervisor.NetworkChangedAsync());

        Assert.Equal(MountState.Disabled, harness.Snapshot("jump").State);
        Assert.Empty(harness.Probe.ShallowProbeCalls);
    }

    [Fact]
    public async Task RequestRetryNowAsync_resets_backoff_and_force_probes_immediately()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        harness.Probe.SetDefaultShallow(ShallowProbeOutcome.ConnectionRefused);
        await harness.StartAsync();
        Assert.Equal(MountState.Unreachable, harness.Snapshot("archive").State);

        harness.Probe.SetDefaultShallow(ShallowProbeOutcome.Success);
        await harness.RunAsync(() => harness.Supervisor.RequestRetryNowAsync("archive"));

        Assert.Equal(MountState.Ready, harness.Snapshot("archive").State);
    }
}
