using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor;

/// <summary>
/// bs-pwz / ADR-011: a host in <c>Mounted</c> is ALWAYS probed, at
/// <c>min(interval_seconds, mounted_probe_interval_seconds)</c> when <c>interval_seconds &gt; 0</c>,
/// else at <c>mounted_probe_interval_seconds</c>. Exercises every acceptance criterion listed on
/// bs-pwz directly, plus the ADR-008 regression check (idle on-demand hosts still generate no
/// probe traffic).
/// </summary>
public sealed class AdrElevenProbeSchedulingTests
{
    /// <summary>
    /// THE test this ADR exists for: an on-demand host with <c>interval_seconds = 0</c> -- the
    /// documented default, and what <c>hosts.example.toml</c> ships -- must still be probed while
    /// Mounted, at <c>mounted_probe_interval_seconds</c>, and must still reach <c>Draining</c>
    /// after <c>failures_before_unmount</c> consecutive failures. Regressing this reopens the
    /// exact wedged-Explorer hole ADR-011 was written to close.
    /// </summary>
    [Fact]
    public async Task Mounted_host_with_interval_zero_is_probed_at_mounted_probe_interval_and_reaches_Draining()
    {
        var host = HostFixtures.OnDemand("archive", probeIntervalSeconds: 0, drive: "Q:");
        var global = HostFixtures.Global(failuresBeforeUnmount: 3, mountedProbeIntervalSeconds: 60);
        var harness = new SupervisorHarness(HostFixtures.Build(global, host));

        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));
        Assert.Equal(MountState.Mounted, harness.Snapshot("archive").State);

        harness.Probe.SetDefaultShallow(ShallowProbeOutcome.OtherFailure);

        await harness.AdvanceAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(MountState.Mounted, harness.Snapshot("archive").State); // 1st failure, not yet at threshold
        Assert.Equal(1, harness.Snapshot("archive").ConsecutiveMountedFailures);

        await harness.AdvanceAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(MountState.Mounted, harness.Snapshot("archive").State); // 2nd failure

        await harness.AdvanceAsync(TimeSpan.FromSeconds(60));

        // 3rd failure -> drains. The fakes default back to success once re-enabled, so within
        // this same Advance+Drain pass the host actually completes Mounted -> Draining ->
        // Disabled -> Probing -> Unreachable (the re-enable probe fails too, since it is the same
        // fake outcome) -- asserting the unmount call is what proves the drain happened without
        // over-specifying where the state machine settles afterwards; see
        // FailuresBeforeUnmountClampTests for the same pattern.
        Assert.Equal(1, harness.Rclone.UnmountCallCountFor("Q:"));
        Assert.NotEqual(MountState.Mounted, harness.Snapshot("archive").State);
    }

    [Fact]
    public async Task Mounted_host_with_long_interval_is_clamped_to_mounted_probe_interval()
    {
        var host = HostFixtures.OnDemand("archive", probeIntervalSeconds: 3600, drive: "Q:");
        var global = HostFixtures.Global(mountedProbeIntervalSeconds: 60);
        var harness = new SupervisorHarness(HostFixtures.Build(global, host));

        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));

        var callsBeforeAdvance = harness.Probe.ShallowProbeCalls.Count;

        // Without clamping, nothing would probe until 3600s. With the 60s ceiling, it must.
        await harness.AdvanceAsync(TimeSpan.FromSeconds(60));

        Assert.True(
            harness.Probe.ShallowProbeCalls.Count > callsBeforeAdvance,
            "expected a mounted probe at the 60s ceiling, not the host's own 3600s interval");
    }

    [Fact]
    public async Task Mounted_host_with_short_interval_is_not_clamped_up()
    {
        var host = HostFixtures.OnDemand("archive", probeIntervalSeconds: 15, drive: "Q:");
        var global = HostFixtures.Global(mountedProbeIntervalSeconds: 60);
        var harness = new SupervisorHarness(HostFixtures.Build(global, host));

        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));

        var callsBeforeAdvance = harness.Probe.ShallowProbeCalls.Count;

        await harness.AdvanceAsync(TimeSpan.FromSeconds(15));

        Assert.True(
            harness.Probe.ShallowProbeCalls.Count > callsBeforeAdvance,
            "expected a mounted probe at the host's own 15s interval, not the 60s ceiling");
    }

    /// <summary>ADR-008 regression guard: the fix for the Mounted case must not accidentally start
    /// probing idle on-demand hosts too.</summary>
    [Fact]
    public async Task OnDemand_host_in_Ready_with_interval_zero_generates_no_probe_traffic()
    {
        var host = HostFixtures.OnDemand("archive", probeIntervalSeconds: 0, drive: "Q:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));

        await harness.StartAsync();
        Assert.Equal(MountState.Ready, harness.Snapshot("archive").State);

        var callsAfterStart = harness.Probe.ShallowProbeCalls.Count(h => h.StartsWith("archive", StringComparison.Ordinal));

        await harness.AdvanceAsync(TimeSpan.FromDays(1));

        var callsAfterAdvance = harness.Probe.ShallowProbeCalls.Count(h => h.StartsWith("archive", StringComparison.Ordinal));
        Assert.Equal(callsAfterStart, callsAfterAdvance);
        Assert.Equal(MountState.Ready, harness.Snapshot("archive").State);
    }

    [Fact]
    public async Task Mounted_probe_success_resets_the_consecutive_failure_counter()
    {
        var host = HostFixtures.OnDemand("archive", probeIntervalSeconds: 0, drive: "Q:");
        var global = HostFixtures.Global(failuresBeforeUnmount: 3, mountedProbeIntervalSeconds: 60);
        var harness = new SupervisorHarness(HostFixtures.Build(global, host));

        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));

        harness.Probe.SetDefaultShallow(ShallowProbeOutcome.Timeout);
        await harness.AdvanceAsync(TimeSpan.FromSeconds(60));
        await harness.AdvanceAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(2, harness.Snapshot("archive").ConsecutiveMountedFailures);

        harness.Probe.SetDefaultShallow(ShallowProbeOutcome.Success);
        await harness.AdvanceAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(0, harness.Snapshot("archive").ConsecutiveMountedFailures);
        Assert.Equal(MountState.Mounted, harness.Snapshot("archive").State);
    }
}
