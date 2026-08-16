using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor.Independent;

/// <summary>
/// Invariant I2 / ADR-005 / docs/ARCHITECTURE.md §4 rule 3: <b>exactly</b>
/// <c>failures_before_unmount</c> <i>consecutive</i> failures unmount a host. Not fewer (a drive
/// that vanishes on one dropped packet is a different, equally user-hostile bug), not more, and
/// not a count shared between hosts.
/// </summary>
/// <remarks>
/// These assert on <c>mount/unmount</c> calls rather than on the snapshot's failure counter. The
/// counter is an implementation detail the state machine happens to expose; the unmount is the
/// behaviour the invariant is about, and a test written against the counter would still pass if
/// the counter were correct and the threshold comparison wrong.
/// </remarks>
public sealed class FailureAccountingTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public async Task Exactly_N_consecutive_failures_unmount_and_N_minus_one_does_not(int failuresBeforeUnmount)
    {
        var host = HostFixtures.OnDemand("archive", probeIntervalSeconds: 0, drive: "Q:");
        var global = HostFixtures.Global(failuresBeforeUnmount: failuresBeforeUnmount, mountedProbeIntervalSeconds: 60);
        var harness = new IndependentHarness(HostFixtures.Build(global, host));

        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));
        Assert.Equal(MountState.Mounted, harness.State("archive"));

        harness.Probe.DefaultShallow = ShallowProbeOutcome.Timeout;

        for (var failure = 1; failure < failuresBeforeUnmount; failure++)
        {
            await harness.AdvanceSecondsAsync(60);
            Assert.Equal(0, harness.Rclone.UnmountCount("Q:"));
            Assert.Equal(MountState.Mounted, harness.State("archive"));
        }

        await harness.AdvanceSecondsAsync(60);

        Assert.Equal(1, harness.Rclone.UnmountCount("Q:"));
        Assert.NotEqual(MountState.Mounted, harness.State("archive"));
    }

    /// <summary>
    /// "Consecutive" is the load-bearing word in ADR-005. Bug this catches: counting cumulative
    /// failures instead of consecutive ones, so a host on a lossy link is eventually unmounted for
    /// no reason -- drives disappearing under a user who is actively working on them.
    /// </summary>
    [Fact]
    public async Task Interleaved_successes_reset_the_count_so_a_lossy_link_never_unmounts()
    {
        var host = HostFixtures.OnDemand("archive", probeIntervalSeconds: 0, drive: "Q:");
        var global = HostFixtures.Global(failuresBeforeUnmount: 3, mountedProbeIntervalSeconds: 60);
        var harness = new IndependentHarness(HostFixtures.Build(global, host));
        var hostname = IndependentHarness.HostnameOf("archive");

        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));

        // Four rounds of "two failures then a success" -- eight failures in total, never three in
        // a row.
        for (var round = 0; round < 4; round++)
        {
            harness.Probe.SetShallow(hostname, ShallowProbeOutcome.Timeout, times: 2);
            await harness.AdvanceSecondsAsync(60);
            await harness.AdvanceSecondsAsync(60);
            await harness.AdvanceSecondsAsync(60); // scripted budget exhausted -> success again

            Assert.Equal(0, harness.Rclone.UnmountCount("Q:"));
            Assert.Equal(MountState.Mounted, harness.State("archive"));
        }

        // ... and three in a row still unmounts, so the reset did not disable the mechanism.
        harness.Probe.DefaultShallow = ShallowProbeOutcome.Timeout;
        await harness.AdvanceSecondsAsync(60);
        await harness.AdvanceSecondsAsync(60);
        await harness.AdvanceSecondsAsync(60);

        Assert.Equal(1, harness.Rclone.UnmountCount("Q:"));
    }

    /// <summary>
    /// Bug this catches: a failure counter held on the supervisor rather than per host, so probe
    /// results for one host push a completely different host over its threshold. With mixed
    /// reachability -- one LAN host gone, one VPN host fine -- that unmounts the healthy drive.
    /// </summary>
    [Fact]
    public async Task Failure_counts_do_not_leak_between_hosts()
    {
        var dying = HostFixtures.Persistent("dying", probeIntervalSeconds: 0, drive: "D:");
        var healthy = HostFixtures.Persistent("healthy", probeIntervalSeconds: 0, drive: "H:");
        var global = HostFixtures.Global(failuresBeforeUnmount: 3, mountedProbeIntervalSeconds: 60);
        var harness = new IndependentHarness(HostFixtures.Build(global, dying, healthy));

        await harness.StartAsync();
        Assert.Equal(MountState.Mounted, harness.State("dying"));
        Assert.Equal(MountState.Mounted, harness.State("healthy"));

        // Only "dying" fails, and it fails on every probe from here on.
        harness.Probe.SetShallow(IndependentHarness.HostnameOf("dying"), ShallowProbeOutcome.ConnectionRefused);

        await harness.AdvanceSecondsAsync(60);
        await harness.AdvanceSecondsAsync(60);
        Assert.Equal(0, harness.Rclone.UnmountCount("D:"));
        Assert.Equal(0, harness.Rclone.UnmountCount("H:"));

        await harness.AdvanceSecondsAsync(60);

        Assert.Equal(1, harness.Rclone.UnmountCount("D:"));
        Assert.Equal(0, harness.Rclone.UnmountCount("H:"));
        Assert.Equal(MountState.Mounted, harness.State("healthy"));
        Assert.NotEqual(MountState.Mounted, harness.State("dying"));
    }

    /// <summary>
    /// Bug this catches: a failure counter that survives the drain, so the freshly remounted host
    /// starts life one probe away from being unmounted again and flaps.
    /// </summary>
    [Fact]
    public async Task The_failure_count_starts_from_zero_again_after_a_remount()
    {
        var host = HostFixtures.Persistent("prod", probeIntervalSeconds: 0, drive: "P:");
        var global = HostFixtures.Global(failuresBeforeUnmount: 2, mountedProbeIntervalSeconds: 60);
        var harness = new IndependentHarness(HostFixtures.Build(global, host));
        var hostname = IndependentHarness.HostnameOf("prod");

        await harness.StartAsync();

        // Two failures -> drain; the shallow probe then recovers and the persistent tier remounts.
        harness.Probe.SetShallow(hostname, ShallowProbeOutcome.Timeout, times: 2);
        await harness.AdvanceSecondsAsync(60);
        await harness.AdvanceSecondsAsync(60);
        Assert.Equal(1, harness.Rclone.UnmountCount("P:"));
        Assert.Equal(MountState.Mounted, harness.State("prod"));
        Assert.Equal(0, harness.Snapshot("prod").ConsecutiveMountedFailures);

        // A single failure against the fresh mount must not be enough to unmount it again.
        harness.Probe.SetShallow(hostname, ShallowProbeOutcome.Timeout, times: 1);
        await harness.AdvanceSecondsAsync(60);

        Assert.Equal(1, harness.Rclone.UnmountCount("P:"));
        Assert.Equal(MountState.Mounted, harness.State("prod"));
    }
}
