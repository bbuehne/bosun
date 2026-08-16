using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor.Independent;

/// <summary>
/// ADR-008 as amended by ADR-011: "on-demand hosts generate no traffic until the user mounts one,
/// then probe at the mounted cadence until it is unmounted."
/// </summary>
/// <remarks>
/// This is the regression guard in the opposite direction from
/// <see cref="MountedProbeCadenceTests"/>. The fix for "a mounted host is always probed" is one
/// careless line away from "every host is always probed", which ADR-008 rejects on the grounds
/// that it makes the UI slow and the server's auth log noisy. Both tests have to hold at once, and
/// only an exact call count can express "no traffic" -- an unchanged state proves nothing, because
/// a host being probed successfully every second also never changes state.
/// </remarks>
public sealed class OnDemandPollingTests
{
    /// <summary>
    /// Bug this catches: arming an idle probe timer for a host configured with
    /// <c>interval_seconds = 0</c> -- the value <c>hosts.example.toml</c> ships as the recommended
    /// on-demand default, so the regression would hit the documented configuration first.
    /// </summary>
    [Fact]
    public async Task An_idle_on_demand_host_with_interval_zero_is_probed_once_at_startup_and_never_again()
    {
        var host = HostFixtures.OnDemand("archive", probeIntervalSeconds: 0, drive: "Q:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), host));

        await harness.StartAsync();
        Assert.Equal(MountState.Ready, harness.State("archive"));
        Assert.Equal(1, harness.ShallowProbesFor("archive"));

        await harness.AdvanceAsync(TimeSpan.FromDays(1));

        Assert.Equal(1, harness.ShallowProbesFor("archive"));
        Assert.Equal(1, harness.Probe.TotalShallowCalls);
        Assert.Equal(0, harness.Probe.TotalDeepCalls);
        Assert.Equal(0, harness.Rclone.TotalMountCalls);
        Assert.Equal(MountState.Ready, harness.State("archive"));
    }

    /// <summary>
    /// The mounted cadence must be given back when the mount is. Bug this catches: a mounted probe
    /// timer that survives the drain, so an on-demand host the user unmounted keeps polling
    /// forever -- ADR-008's "no traffic while idle" quietly lost after the first mount/unmount
    /// cycle, and never visible as a state change.
    /// </summary>
    [Fact]
    public async Task An_on_demand_host_stops_generating_probe_traffic_again_once_it_is_unmounted()
    {
        var host = HostFixtures.OnDemand("archive", probeIntervalSeconds: 0, drive: "Q:");
        var global = HostFixtures.Global(mountedProbeIntervalSeconds: 60);
        var harness = new IndependentHarness(HostFixtures.Build(global, host));

        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));

        // While mounted it IS polled (ADR-011) -- three cycles' worth.
        var mountedBaseline = harness.ShallowProbesFor("archive");
        await harness.AdvanceSecondsAsync(60);
        await harness.AdvanceSecondsAsync(60);
        await harness.AdvanceSecondsAsync(60);
        Assert.Equal(mountedBaseline + 3, harness.ShallowProbesFor("archive"));

        await harness.RunAsync(() => harness.Supervisor.RequestUnmountAsync("archive"));
        Assert.Equal(MountState.Ready, harness.State("archive"));

        var afterUnmount = harness.ShallowProbesFor("archive");
        await harness.AdvanceAsync(TimeSpan.FromDays(1));

        Assert.Equal(afterUnmount, harness.ShallowProbesFor("archive"));
    }

    /// <summary>
    /// Bug this catches: an idle probe timer armed for a host whose only reason to exist is a
    /// terminal profile (<c>mount.mode = "none"</c>, ADR-008's jump-host case). Such a host has no
    /// drive letter and no mount lifecycle; probing it is pure noise in someone's auth log.
    /// </summary>
    [Fact]
    public async Task A_mode_none_host_generates_no_probe_traffic_and_no_rclone_calls_at_all()
    {
        var jump = HostFixtures.None("jump");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), jump));

        await harness.StartAsync();
        await harness.AdvanceAsync(TimeSpan.FromDays(1));

        Assert.Equal(0, harness.Probe.TotalShallowCalls);
        Assert.Equal(0, harness.Probe.TotalDeepCalls);
        Assert.Equal(0, harness.Rclone.TotalMountCalls);
        Assert.Equal(0, harness.Rclone.UnmountCount("P:"));
        Assert.Equal(MountState.Disabled, harness.State("jump"));
    }
}
