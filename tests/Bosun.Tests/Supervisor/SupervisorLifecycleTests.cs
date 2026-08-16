using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor;

public sealed class SupervisorLifecycleTests
{
    [Fact]
    public async Task StartAsync_is_idempotent()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));

        await harness.StartAsync();
        Assert.Single(harness.Rclone.MountCalls);

        await harness.StartAsync();

        // A second Start must not re-build the host set or re-run adopt-or-clear / re-enable.
        Assert.Single(harness.Rclone.MountCalls);
    }

    [Fact]
    public async Task Mode_none_host_stays_Disabled_forever_and_is_never_probed()
    {
        var host = HostFixtures.None("jump");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));

        await harness.StartAsync();
        await harness.AdvanceAsync(TimeSpan.FromDays(1));

        Assert.Equal(MountState.Disabled, harness.Snapshot("jump").State);
        Assert.False(harness.Snapshot("jump").AdministrativelyEnabled);
        Assert.Empty(harness.Probe.ShallowProbeCalls);
    }

    [Fact]
    public async Task StopAsync_disposes_timers_so_no_further_work_happens()
    {
        var host = HostFixtures.OnDemand("archive", probeIntervalSeconds: 30, drive: "Q:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        harness.Probe.SetDefaultShallow(ShallowProbeOutcome.ConnectionRefused);
        await harness.StartAsync();
        var callsBefore = harness.Probe.ShallowProbeCalls.Count;

        await harness.RunAsync(() => harness.Supervisor.StopAsync());
        await harness.AdvanceAsync(TimeSpan.FromDays(1));

        Assert.Equal(callsBefore, harness.Probe.ShallowProbeCalls.Count);
    }

    [Fact]
    public async Task GetSnapshot_reflects_every_configured_host()
    {
        var a = HostFixtures.Persistent("prod", drive: "P:");
        var b = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), a, b));

        await harness.StartAsync();

        var snapshot = harness.Supervisor.GetSnapshot();
        Assert.Equal(2, snapshot.Count);
        Assert.Contains(snapshot, s => s.HostKey == "prod" && s.State == MountState.Mounted && s.Drive == "P:");
        Assert.Contains(snapshot, s => s.HostKey == "archive" && s.State == MountState.Ready && s.Drive == "Q:");
    }

    [Fact]
    public async Task Commands_for_an_unknown_host_key_fail_rather_than_silently_no_op()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        await harness.StartAsync();

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => harness.RunAsync(() => harness.Supervisor.RequestMountAsync("does-not-exist")));
    }
}
