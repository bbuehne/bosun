using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor;

/// <summary>
/// bs-2eg / ADR-016: a host in <c>Mounted</c> is deep-probed (<c>operations/list</c>) on its own,
/// slower cadence, in addition to the shallow (TCP) probe ADR-011 already established. Closes an
/// Invariant I2 hole: a shallow probe can keep succeeding while the SSH channel underneath the
/// mount is dead (sleep/resume, a network blip too brief to trip the shallow-failure threshold),
/// leaving a wedged drive letter that the shallow-only check can never detect.
/// </summary>
/// <remarks>
/// Deep-probe failures accumulate on their own counter, <c>ConsecutiveDeepProbeFailures</c> --
/// deliberately never <c>ConsecutiveMountedFailures</c> (the shallow probe's counter). An earlier
/// version of this feature shared the counter and broke
/// <c>Independent/FailureAccountingTests</c>: with the shipped defaults (60s shallow / 300s deep,
/// an exact 5:1 ratio), the two timers coincide on the same instant every 5th shallow tick, and a
/// coincidentally-successful deep probe could silently erase an about-to-unmount shallow-failure
/// streak depending on which same-instant channel continuation ran first. See ADR-016's "Rejected:
/// share the counter" for the full account. These tests use a fixed 2-failure threshold for the
/// deep probe (also ADR-016) -- deliberately fewer than the shallow ladder's default of 3, because
/// <c>operations/list</c> failing is stronger evidence than a TCP timeout.
/// </remarks>
public sealed class MountedDeepProbeTests
{
    /// <summary>
    /// THE test this ADR exists for: a shallow probe that never stops succeeding must not mask a
    /// dead mount. Two consecutive deep-probe failures (the fixed threshold) drain the host even
    /// though every shallow probe in between reports success.
    /// </summary>
    [Fact]
    public async Task Mounted_host_whose_shallow_probe_keeps_succeeding_but_deep_probe_fails_reaches_Draining()
    {
        var host = HostFixtures.OnDemand("nas", probeIntervalSeconds: 30, drive: "Q:");
        var global = HostFixtures.Global(mountedProbeIntervalSeconds: 30, mountedDeepProbeIntervalSeconds: 100);
        var harness = new SupervisorHarness(HostFixtures.Build(global, host));

        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("nas"));
        Assert.Equal(MountState.Mounted, harness.Snapshot("nas").State);

        // Shallow probes keep succeeding for the entire test -- the fake's default outcome is
        // Success and nothing below ever changes it. Only the deep probe is made to fail.
        harness.Probe.SetDefaultDeep(DeepProbeOutcome.Failed);

        await harness.AdvanceAsync(TimeSpan.FromSeconds(100));
        Assert.Equal(MountState.Mounted, harness.Snapshot("nas").State); // 1st deep failure, not yet at threshold (2)
        Assert.Equal(1, harness.Snapshot("nas").ConsecutiveDeepProbeFailures);
        Assert.Equal(0, harness.Snapshot("nas").ConsecutiveMountedFailures); // shallow counter untouched

        await harness.AdvanceAsync(TimeSpan.FromSeconds(100));

        // 2nd deep failure -> drains, exactly like the shallow path's own threshold behaviour.
        Assert.Equal(1, harness.Rclone.UnmountCallCountFor("Q:"));
        Assert.NotEqual(MountState.Mounted, harness.Snapshot("nas").State);
    }

    /// <summary>
    /// Cadence, not just eventual outcome (mirrors the reasoning in
    /// <c>Independent/MountedProbeCadenceTests</c> for the shallow probe): a deep probe armed at
    /// zero delay, or coupled to the shallow interval instead of its own, would still eventually
    /// produce the right call counts under a coarse "did it happen" assertion. Advancing to one
    /// second short of the deep interval must produce zero deep probes; advancing the rest of the
    /// way must produce exactly one -- and shallow probes must keep firing on their own, much
    /// shorter, interval throughout, proving the two cadences are genuinely independent.
    /// </summary>
    [Fact]
    public async Task Deep_probe_fires_on_its_own_cadence_not_the_shallow_ones()
    {
        var host = HostFixtures.OnDemand("nas", probeIntervalSeconds: 15, drive: "Q:");
        var global = HostFixtures.Global(mountedProbeIntervalSeconds: 15, mountedDeepProbeIntervalSeconds: 100);
        var harness = new SupervisorHarness(HostFixtures.Build(global, host));

        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("nas"));

        var deepCallsAtMount = harness.Probe.DeepProbeCalls.Count(k => k == "nas");
        var shallowCallsAtMount = harness.Probe.ShallowProbeCalls.Count(h => h.StartsWith("nas", StringComparison.Ordinal));

        await harness.AdvanceAsync(TimeSpan.FromSeconds(99));
        Assert.Equal(deepCallsAtMount, harness.Probe.DeepProbeCalls.Count(k => k == "nas"));

        // Six 15s shallow ticks fit inside those same 99 seconds -- proof the shallow cadence kept
        // running on its own schedule while the deep probe stayed silent.
        Assert.True(
            harness.Probe.ShallowProbeCalls.Count(h => h.StartsWith("nas", StringComparison.Ordinal)) > shallowCallsAtMount,
            "expected shallow probes to keep firing on their own 15s cadence while the deep probe waited");

        await harness.AdvanceAsync(TimeSpan.FromSeconds(1)); // t = 100: the deep interval elapses
        Assert.Equal(deepCallsAtMount + 1, harness.Probe.DeepProbeCalls.Count(k => k == "nas"));

        Assert.Equal(MountState.Mounted, harness.Snapshot("nas").State);
    }

    /// <summary>
    /// A deep-probe success resets its own counter, directly -- not merely "eventually zero again
    /// after a full drain/remount cycle" (that regression is already covered for the shallow path
    /// by <c>Independent/FailureAccountingTests.The_failure_count_starts_from_zero_again_after_a_remount</c>).
    /// One deep failure (below the 2-failure threshold, so no drain yet), then a recovery, then
    /// proof the mechanism still works: two more failures still drains.
    /// </summary>
    [Fact]
    public async Task A_recovering_deep_probe_resets_its_own_counter()
    {
        var host = HostFixtures.OnDemand("nas", probeIntervalSeconds: 30, drive: "Q:");
        var global = HostFixtures.Global(mountedProbeIntervalSeconds: 30, mountedDeepProbeIntervalSeconds: 50);
        var harness = new SupervisorHarness(HostFixtures.Build(global, host));

        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("nas"));

        harness.Probe.SetDefaultDeep(DeepProbeOutcome.Failed);
        await harness.AdvanceAsync(TimeSpan.FromSeconds(50));
        Assert.Equal(1, harness.Snapshot("nas").ConsecutiveDeepProbeFailures);
        Assert.Equal(MountState.Mounted, harness.Snapshot("nas").State);

        harness.Probe.SetDefaultDeep(DeepProbeOutcome.Success);
        await harness.AdvanceAsync(TimeSpan.FromSeconds(50));
        Assert.Equal(0, harness.Snapshot("nas").ConsecutiveDeepProbeFailures);
        Assert.Equal(MountState.Mounted, harness.Snapshot("nas").State);
        Assert.Equal(0, harness.Rclone.UnmountCallCountFor("Q:"));

        // The reset must not have disabled the mechanism: two more consecutive failures still
        // reaches the threshold and drains.
        harness.Probe.SetDefaultDeep(DeepProbeOutcome.Failed);
        await harness.AdvanceAsync(TimeSpan.FromSeconds(50));
        await harness.AdvanceAsync(TimeSpan.FromSeconds(50));

        Assert.Equal(1, harness.Rclone.UnmountCallCountFor("Q:"));
        Assert.NotEqual(MountState.Mounted, harness.Snapshot("nas").State);
    }

    /// <summary>ADR-008 regression guard, extended to the new probe kind: an on-demand host resting
    /// idle in <c>Ready</c> must generate zero probe traffic of EITHER kind. This feature must only
    /// ever add traffic to a host that is already <c>Mounted</c>.</summary>
    [Fact]
    public async Task OnDemand_host_idle_in_Ready_generates_zero_probes_of_either_kind()
    {
        var host = HostFixtures.OnDemand("nas", probeIntervalSeconds: 0, drive: "Q:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));

        await harness.StartAsync();
        Assert.Equal(MountState.Ready, harness.Snapshot("nas").State);

        var shallowBefore = harness.Probe.ShallowProbeCalls.Count(h => h.StartsWith("nas", StringComparison.Ordinal));
        var deepBefore = harness.Probe.DeepProbeCalls.Count(k => k == "nas");

        await harness.AdvanceAsync(TimeSpan.FromDays(1));

        Assert.Equal(shallowBefore, harness.Probe.ShallowProbeCalls.Count(h => h.StartsWith("nas", StringComparison.Ordinal)));
        Assert.Equal(deepBefore, harness.Probe.DeepProbeCalls.Count(k => k == "nas"));
        Assert.Equal(MountState.Ready, harness.Snapshot("nas").State);
    }

    /// <summary>Sanity check that the deep-probe timer is torn down, not merely ignored, once the
    /// host leaves <c>Mounted</c> -- otherwise a stale timer from a previous mount session could
    /// fire into a since-remounted host's state. Suspend is a convenient way to force a drain
    /// deterministically without waiting out a probe failure.</summary>
    [Fact]
    public async Task Deep_probe_timer_is_torn_down_when_the_host_leaves_Mounted()
    {
        var host = HostFixtures.OnDemand("nas", probeIntervalSeconds: 30, drive: "Q:");
        var global = HostFixtures.Global(mountedProbeIntervalSeconds: 30, mountedDeepProbeIntervalSeconds: 50);
        var harness = new SupervisorHarness(HostFixtures.Build(global, host));

        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("nas"));
        Assert.Equal(MountState.Mounted, harness.Snapshot("nas").State);

        await harness.RunAsync(() => harness.Supervisor.SuspendAsync());
        Assert.NotEqual(MountState.Mounted, harness.Snapshot("nas").State);

        var deepCallsAfterSuspend = harness.Probe.DeepProbeCalls.Count(k => k == "nas");

        // If the deep-probe timer were not disposed on drain, it would still fire here and either
        // throw (host no longer in the dictionary in a real crash scenario) or silently probe a
        // host that is no longer Mounted. Neither should happen.
        await harness.AdvanceAsync(TimeSpan.FromSeconds(200));

        Assert.Equal(deepCallsAfterSuspend, harness.Probe.DeepProbeCalls.Count(k => k == "nas"));
    }
}
