using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor.Independent;

/// <summary>
/// ADR-011 / docs/ARCHITECTURE.md §4 rule 3, pinned as a <b>cadence</b> rather than as an eventual
/// outcome.
/// </summary>
/// <remarks>
/// <para>
/// The existing suite proves a mounted host eventually drains. That is a weaker claim than the one
/// ADR-011 actually makes, and the gap is exploitable: replacing the effective-interval formula
/// with <c>return interval;</c> -- i.e. reverting to "interval 0 means never probe while mounted",
/// the precise hole ADR-011 exists to close -- leaves the eventual-outcome tests green, because a
/// zero-delay timer also fires once per clock advance and the host still drains after three
/// advances. Only the cadence is different, so only a cadence assertion can catch it.
/// </para>
/// <para>
/// Every test here therefore asserts an exact probe count on both sides of the boundary: advancing
/// to one second short of the effective interval must produce <b>zero</b> probes, and advancing to
/// exactly the interval must produce <b>exactly one</b>. A zero-delay timer fails the first half; a
/// timer armed at the wrong (unclamped) interval fails the second.
/// </para>
/// </remarks>
public sealed class MountedProbeCadenceTests
{
    /// <summary>
    /// Bug this catches: any change that makes <c>probe.interval_seconds = 0</c> mean "do not probe
    /// while mounted" (ADR-011's hole), or that arms the mounted timer at zero/immediate. Either
    /// one is an Invariant I2 violation -- the first leaves a drive letter up pointing at a dead
    /// host forever, the second hammers the host as fast as the loop turns.
    /// </summary>
    [Fact]
    public async Task Mounted_host_with_interval_zero_probes_exactly_once_per_mounted_probe_interval()
    {
        var host = HostFixtures.OnDemand("archive", probeIntervalSeconds: 0, drive: "Q:");
        var global = HostFixtures.Global(mountedProbeIntervalSeconds: 60);
        var harness = new IndependentHarness(HostFixtures.Build(global, host));

        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));
        Assert.Equal(MountState.Mounted, harness.State("archive"));

        var expected = harness.ShallowProbesFor("archive");

        for (var interval = 0; interval < 5; interval++)
        {
            await harness.AdvanceSecondsAsync(59);
            Assert.Equal(expected, harness.ShallowProbesFor("archive"));

            await harness.AdvanceSecondsAsync(1);
            expected++;
            Assert.Equal(expected, harness.ShallowProbesFor("archive"));
        }

        Assert.Equal(MountState.Mounted, harness.State("archive"));
    }

    /// <summary>
    /// Bug this catches: dropping ADR-011's upper clamp, so a host configured with a long idle
    /// cadence inherits an unbounded unmount latency the moment it is mounted (five minutes of
    /// probing here would produce five probes; an unclamped 3600s interval produces none).
    /// </summary>
    [Fact]
    public async Task Mounted_host_with_a_long_interval_probes_at_the_ceiling_not_at_its_own_interval()
    {
        var host = HostFixtures.OnDemand("archive", probeIntervalSeconds: 3600, drive: "Q:");
        var global = HostFixtures.Global(mountedProbeIntervalSeconds: 60);
        var harness = new IndependentHarness(HostFixtures.Build(global, host));

        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));
        var baseline = harness.ShallowProbesFor("archive");

        await harness.AdvanceSecondsAsync(59);
        Assert.Equal(baseline, harness.ShallowProbesFor("archive"));

        for (var i = 0; i < 5; i++)
        {
            await harness.AdvanceSecondsAsync(i == 0 ? 1 : 60);
        }

        Assert.Equal(baseline + 5, harness.ShallowProbesFor("archive"));
    }

    /// <summary>
    /// Bug this catches: clamping in the wrong direction (using the global ceiling unconditionally,
    /// or <c>Math.Max</c> instead of <c>Math.Min</c>). A host that asked to be watched every 15
    /// seconds would silently be watched every 60, quadrupling its worst-case unmount latency.
    /// </summary>
    [Fact]
    public async Task Mounted_host_with_a_short_interval_probes_at_its_own_interval_not_the_ceiling()
    {
        var host = HostFixtures.OnDemand("archive", probeIntervalSeconds: 15, drive: "Q:");
        var global = HostFixtures.Global(mountedProbeIntervalSeconds: 60);
        var harness = new IndependentHarness(HostFixtures.Build(global, host));

        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));
        var baseline = harness.ShallowProbesFor("archive");

        await harness.AdvanceSecondsAsync(14);
        Assert.Equal(baseline, harness.ShallowProbesFor("archive"));

        await harness.AdvanceSecondsAsync(1);
        Assert.Equal(baseline + 1, harness.ShallowProbesFor("archive"));

        // Four 15s ticks inside one minute -- not one 60s tick.
        await harness.AdvanceSecondsAsync(15);
        await harness.AdvanceSecondsAsync(15);
        await harness.AdvanceSecondsAsync(15);
        Assert.Equal(baseline + 4, harness.ShallowProbesFor("archive"));
    }

    /// <summary>
    /// Bug this catches: re-arming the mounted probe timer only on the success path, so the first
    /// failure silently stops the cadence. The host would sit at 1/3 failures forever and never
    /// reach <c>Draining</c> -- a wedged drive letter that looks healthy in the snapshot.
    /// </summary>
    [Fact]
    public async Task A_failed_mounted_probe_still_re_arms_the_next_probe_at_the_same_cadence()
    {
        var host = HostFixtures.OnDemand("archive", probeIntervalSeconds: 0, drive: "Q:");
        var global = HostFixtures.Global(failuresBeforeUnmount: 5, mountedProbeIntervalSeconds: 60);
        var harness = new IndependentHarness(HostFixtures.Build(global, host));

        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));
        var baseline = harness.ShallowProbesFor("archive");

        harness.Probe.DefaultShallow = ShallowProbeOutcome.Timeout;

        await harness.AdvanceSecondsAsync(60);
        Assert.Equal(baseline + 1, harness.ShallowProbesFor("archive"));

        await harness.AdvanceSecondsAsync(59);
        Assert.Equal(baseline + 1, harness.ShallowProbesFor("archive"));

        await harness.AdvanceSecondsAsync(1);
        Assert.Equal(baseline + 2, harness.ShallowProbesFor("archive"));
        Assert.Equal(MountState.Mounted, harness.State("archive"));
    }

    /// <summary>
    /// docs/OPERATIONS.md T3 as a deadline, not a tendency: "drive disappears within
    /// <c>interval x failures_before_unmount</c>". With the ADR-011 defaults that is exactly
    /// 60 x 3 = 180 seconds. Bug this catches: an off-by-one in the failure threshold, or a cadence
    /// drift that pushes the third failure past the documented worst case.
    /// </summary>
    [Fact]
    public async Task A_dead_mounted_host_is_unmounted_at_the_documented_deadline_and_not_before()
    {
        var host = HostFixtures.OnDemand("archive", probeIntervalSeconds: 0, drive: "Q:");
        var global = HostFixtures.Global(failuresBeforeUnmount: 3, mountedProbeIntervalSeconds: 60);
        var harness = new IndependentHarness(HostFixtures.Build(global, host));

        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));

        harness.Probe.DefaultShallow = ShallowProbeOutcome.OtherFailure;

        await harness.AdvanceSecondsAsync(60);
        await harness.AdvanceSecondsAsync(60);
        await harness.AdvanceSecondsAsync(59); // t = 179
        Assert.Equal(0, harness.Rclone.UnmountCount("Q:"));
        Assert.Equal(MountState.Mounted, harness.State("archive"));

        await harness.AdvanceSecondsAsync(1); // t = 180
        Assert.Equal(1, harness.Rclone.UnmountCount("Q:"));
        Assert.NotEqual(MountState.Mounted, harness.State("archive"));
    }
}
