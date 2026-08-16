using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor;

/// <summary>
/// bs-z3y (open decision, no validation rule yet for <c>global.failures_before_unmount</c>): the
/// brief requires clamping any value below 1 up to 1 -- erring toward unmounting, never toward
/// "never unmounts" (Invariant I2). These tests exist specifically so a future change to
/// <c>ConfigValidator</c> that adds the missing rule does not silently remove this safety net
/// without someone noticing the coverage gap.
/// </summary>
public sealed class FailuresBeforeUnmountClampTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public async Task Non_positive_failures_before_unmount_is_clamped_to_one(int configuredValue)
    {
        var host = HostFixtures.OnDemand("archive", probeIntervalSeconds: 0, drive: "Q:");
        var global = HostFixtures.Global(failuresBeforeUnmount: configuredValue, mountedProbeIntervalSeconds: 60);
        var harness = new SupervisorHarness(HostFixtures.Build(global, host));
        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));
        Assert.Equal(MountState.Mounted, harness.Snapshot("archive").State);

        harness.Probe.SetDefaultShallow(ShallowProbeOutcome.OtherFailure);

        // Clamped to 1 -> the VERY FIRST mounted-probe failure must drain immediately, never
        // "never". The fakes default to success again once re-enabled, so within this one
        // Advance+Drain pass the host actually completes a full Mounted -> Draining -> Disabled ->
        // (still administratively enabled) -> Probing -> Unreachable cycle (the probe still fails,
        // since it is the SAME fake shallow-probe outcome the re-enable check also uses) --
        // asserting on the unmount call, rather than the transient Draining state, is what proves
        // the drain actually happened without over-specifying exactly where the state machine
        // settles afterwards. Exactly one unmount call also rules out the other unsafe direction
        // (a bad clamp that unmounts on every tick).
        await harness.AdvanceAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(1, harness.Rclone.UnmountCallCountFor("Q:"));
        Assert.NotEqual(MountState.Mounted, harness.Snapshot("archive").State);
    }

    [Fact]
    public async Task Positive_failures_before_unmount_is_used_as_configured_not_clamped()
    {
        var host = HostFixtures.OnDemand("archive", probeIntervalSeconds: 0, drive: "Q:");
        var global = HostFixtures.Global(failuresBeforeUnmount: 3, mountedProbeIntervalSeconds: 60);
        var harness = new SupervisorHarness(HostFixtures.Build(global, host));
        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));

        harness.Probe.SetDefaultShallow(ShallowProbeOutcome.OtherFailure);
        await harness.AdvanceAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(MountState.Mounted, harness.Snapshot("archive").State);
        await harness.AdvanceAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(MountState.Mounted, harness.Snapshot("archive").State);
        await harness.AdvanceAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(1, harness.Rclone.UnmountCallCountFor("Q:"));
        Assert.NotEqual(MountState.Mounted, harness.Snapshot("archive").State);
    }
}
