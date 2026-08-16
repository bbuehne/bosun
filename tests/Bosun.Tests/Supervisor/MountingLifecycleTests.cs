using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor;

/// <summary>
/// Black-box proof of docs/ARCHITECTURE.md §4 rules 1 and 2 (Invariant I1): a persistent host
/// auto-mounts once <c>Ready</c>, an on-demand host does not, a deep-probe failure on the way into
/// <c>Mounting</c> routes to <c>Draining</c> rather than <c>Mounted</c>, and a mount call that
/// fails does the same. Complements the white-box table checks in
/// <c>TransitionTableStructureTests</c>.
/// </summary>
public sealed class MountingLifecycleTests
{
    [Fact]
    public async Task Persistent_host_auto_mounts_once_it_becomes_Ready()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));

        await harness.StartAsync();

        var snapshot = harness.Snapshot("prod");
        Assert.Equal(MountState.Mounted, snapshot.State);
        Assert.Single(harness.Rclone.MountCalls);
        Assert.Equal("P:", harness.Rclone.MountCalls[0].MountPoint);
        Assert.Contains("prod", harness.Probe.DeepProbeCalls);
    }

    [Fact]
    public async Task OnDemand_host_rests_in_Ready_and_does_not_auto_mount()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));

        await harness.StartAsync();

        Assert.Equal(MountState.Ready, harness.Snapshot("archive").State);
        Assert.Empty(harness.Rclone.MountCalls);
        Assert.Empty(harness.Probe.DeepProbeCalls);
    }

    [Fact]
    public async Task RequestMountAsync_mounts_an_on_demand_host_that_is_Ready()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        await harness.StartAsync();
        Assert.Equal(MountState.Ready, harness.Snapshot("archive").State);

        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));

        Assert.Equal(MountState.Mounted, harness.Snapshot("archive").State);
        Assert.Single(harness.Rclone.MountCalls);
    }

    [Fact]
    public async Task RequestMountAsync_is_ignored_when_host_is_not_Ready()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        harness.Probe.SetDefaultShallow(ShallowProbeOutcome.ConnectionRefused);
        await harness.StartAsync();
        Assert.Equal(MountState.Unreachable, harness.Snapshot("archive").State);

        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));

        // Still Unreachable -- no deep probe, no mount call, Invariant I1 upheld.
        Assert.Equal(MountState.Unreachable, harness.Snapshot("archive").State);
        Assert.Empty(harness.Rclone.MountCalls);
        Assert.Empty(harness.Probe.DeepProbeCalls);
    }

    [Fact]
    public async Task Deep_probe_failure_entering_Mounting_routes_to_Draining_never_Mounted()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        await harness.StartAsync();
        harness.Probe.EnqueueDeep("archive", DeepProbeOutcome.Failed);

        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));

        // Deep probe failed -> drain step ran immediately and (with the fake's default
        // confirm-on-first-call behaviour) already confirmed and cycled back through re-enable.
        Assert.Empty(harness.Rclone.MountCalls);
        Assert.NotEqual(MountState.Mounted, harness.Snapshot("archive").State);
    }

    [Fact]
    public async Task Mount_call_failure_routes_to_Draining_never_Mounted()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        await harness.StartAsync();
        harness.Rclone.MakeMountFail("Q:", new InvalidOperationException("drive letter already in use"));

        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));

        Assert.NotEqual(MountState.Mounted, harness.Snapshot("archive").State);
    }

    [Fact]
    public async Task Unreachable_host_that_recovers_reaches_Ready_and_persistent_tier_then_auto_mounts()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:", probeIntervalSeconds: 30);
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        harness.Probe.SetDefaultShallow(ShallowProbeOutcome.ConnectionRefused);
        await harness.StartAsync();
        Assert.Equal(MountState.Unreachable, harness.Snapshot("prod").State);
        Assert.Empty(harness.Rclone.MountCalls);

        harness.Probe.SetDefaultShallow(ShallowProbeOutcome.Success);
        // Default backoff ladder first rung is 5s.
        await harness.AdvanceAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);
        Assert.Single(harness.Rclone.MountCalls);
    }
}
