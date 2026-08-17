using Bosun.Probe;
using Bosun.Rclone;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor;

/// <summary>
/// bs-mk4 / bs-brv / ADR-017: <c>ResumeAsync</c> and <c>NetworkChangedAsync</c> converge on a
/// shared "re-derive" path that (1) gates on <c>rclone rcd</c> answering <c>core/version</c>,
/// (2) reconciles against <c>mount/listmounts</c>, then (3) force-probes -- shallow AND deep --
/// every host still <c>Mounted</c>, with a forced deep-probe failure conclusive at a single
/// failure rather than ADR-016's ordinary threshold of 2.
/// </summary>
public sealed class ReDeriveAfterTransitionTests
{
    /// <summary>
    /// ADR-017's settled sub-decision, verbatim: "If core/version does not answer, the
    /// reconciliation does not run at all... That is the same posture as I1 -- no action on
    /// unverified state". Without this gate, a forced deep-probe failure could not be told apart
    /// from "rcd has not finished coming back yet", and would unmount a healthy drive on every
    /// resume where rcd is merely slow to restart.
    /// </summary>
    [Fact]
    public async Task Resume_with_rcd_down_reconciles_and_drains_nothing()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        await harness.StartAsync();
        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);

        var listMountsBefore = harness.Rclone.ListMountsCallCount;
        var shallowBefore = harness.Probe.ShallowProbeCalls.Count;
        var deepBefore = harness.Probe.DeepProbeCalls.Count;

        harness.Rclone.MakeGetVersionThrowOnce(new RcloneRcException("core/version", null, "rcd not answering"));
        await harness.RunAsync(() => harness.Supervisor.ResumeAsync());

        // Gate tripped: nothing reconciled, nothing (re)probed, nothing drained.
        Assert.Equal(listMountsBefore, harness.Rclone.ListMountsCallCount);
        Assert.Equal(shallowBefore, harness.Probe.ShallowProbeCalls.Count);
        Assert.Equal(deepBefore, harness.Probe.DeepProbeCalls.Count);
        Assert.Empty(harness.Rclone.UnmountCalls);
        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);
    }

    /// <summary>Same gate, reached via <c>NetworkChangedAsync</c> instead of <c>ResumeAsync</c> --
    /// both call the identical shared path (ADR-017), so the gate must hold for either trigger.</summary>
    [Fact]
    public async Task NetworkChanged_with_rcd_down_reconciles_and_drains_nothing()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        await harness.StartAsync();
        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);

        var listMountsBefore = harness.Rclone.ListMountsCallCount;
        var deepBefore = harness.Probe.DeepProbeCalls.Count;

        harness.Rclone.MakeGetVersionThrowOnce(new RcloneRcException("core/version", null, "rcd not answering"));
        await harness.RunAsync(() => harness.Supervisor.NetworkChangedAsync());

        Assert.Equal(listMountsBefore, harness.Rclone.ListMountsCallCount);
        Assert.Equal(deepBefore, harness.Probe.DeepProbeCalls.Count);
        Assert.Empty(harness.Rclone.UnmountCalls);
        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);
    }

    /// <summary>
    /// The gate is scoped to the shared re-derive step only. A Ready/Unreachable host's own
    /// backoff-reset-and-force-probe (the existing per-state switch, which never touches
    /// <c>rclone rcd</c>) must still run even while rcd is down -- only the Mounted-host
    /// reconcile/force-probe/drain path is gated.
    /// </summary>
    [Fact]
    public async Task Resume_with_rcd_down_still_force_probes_an_Unreachable_host()
    {
        var mounted = HostFixtures.Persistent("prod", drive: "P:");
        var unreachable = HostFixtures.Persistent("stranded", drive: "S:", probeIntervalSeconds: 60);
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), mounted, unreachable));
        harness.Probe.EnqueueShallow("stranded.example.internal", ShallowProbeOutcome.ConnectionRefused);
        await harness.StartAsync();
        Assert.Equal(MountState.Unreachable, harness.Snapshot("stranded").State);

        harness.Rclone.MakeGetVersionThrowOnce(new RcloneRcException("core/version", null, "rcd not answering"));
        await harness.RunAsync(() => harness.Supervisor.ResumeAsync());

        // Unaffected by the rcd gate: probed immediately and recovered (fake defaults to success).
        Assert.Equal(MountState.Mounted, harness.Snapshot("stranded").State);
        // Untouched by the gate: still Mounted, never reconciled or probed.
        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);
    }

    /// <summary>
    /// ADR-017's headline behaviour: <c>NetworkChangedAsync</c> used to force only the shallow
    /// probe on a Mounted host (bs-mk4); it must now force the deep probe too, which is the only
    /// check that can see a dead SSH channel under a host that still answers TCP (ADR-016).
    /// </summary>
    [Fact]
    public async Task NetworkChanged_force_probes_a_Mounted_host_with_both_shallow_and_deep()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:", probeIntervalSeconds: 3600);
        var global = HostFixtures.Global(mountedProbeIntervalSeconds: 3600, mountedDeepProbeIntervalSeconds: 3600);
        var harness = new SupervisorHarness(HostFixtures.Build(global, host));
        await harness.StartAsync();
        var deepBefore = harness.Probe.DeepProbeCalls.Count(k => k == "prod");

        await harness.RunAsync(() => harness.Supervisor.NetworkChangedAsync());

        Assert.Equal(deepBefore + 1, harness.Probe.DeepProbeCalls.Count(k => k == "prod"));
        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);
    }

    /// <summary>
    /// The settled sub-decision from ADR-017: a forced deep-probe failure immediately after a
    /// transition drains on a SINGLE failure, not ADR-016's ordinary threshold of 2 -- because the
    /// rcd-health gate already ruled out "rcd is still coming back", leaving only "the channel is
    /// actually dead".
    /// </summary>
    [Fact]
    public async Task Resume_drains_a_Mounted_host_on_a_single_forced_deep_probe_failure()
    {
        // On-demand, not persistent: rule 6 means a drained on-demand host rests in Ready rather
        // than auto-remounting the instant the drain completes, which would otherwise mask the
        // very unmount this test is proving (DrainCause.Automatic re-enables immediately, and a
        // persistent host with healthy probes would simply mount itself straight back up again).
        var host = HostFixtures.OnDemand("nas", drive: "Q:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("nas"));
        Assert.Equal(MountState.Mounted, harness.Snapshot("nas").State);

        harness.Probe.EnqueueDeep("nas", DeepProbeOutcome.Failed);
        await harness.RunAsync(() => harness.Supervisor.ResumeAsync());

        // One failure -- not ADR-016's periodic threshold of 2 -- was conclusive: unmounted, and
        // (on-demand) rested in Ready rather than remounting itself.
        Assert.Contains("Q:", harness.Rclone.UnmountCalls);
        Assert.Equal(MountState.Ready, harness.Snapshot("nas").State);
        Assert.Equal(1, harness.Rclone.MountCalls.Count(m => m.MountPoint == "Q:")); // no auto-remount
    }

    /// <summary>
    /// The mirror image: a forced deep probe that succeeds must reset the deep-probe counter and
    /// re-arm the periodic timer, exactly like an ordinary periodic success (ADR-016) -- a
    /// transition must not, by itself, cost a healthy mount anything.
    /// </summary>
    [Fact]
    public async Task Resume_leaves_a_Mounted_host_alone_when_both_forced_probes_succeed()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        await harness.StartAsync();

        await harness.RunAsync(() => harness.Supervisor.ResumeAsync());

        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);
        Assert.Equal(0, harness.Snapshot("prod").ConsecutiveDeepProbeFailures);
        Assert.Empty(harness.Rclone.UnmountCalls);
    }

    /// <summary>
    /// A forced shallow-probe failure that reaches the ordinary accumulating threshold drains the
    /// host, exactly as the periodic shallow probe would -- and once drained, it must not also be
    /// deep-probed (nothing left in <c>Mounted</c> to deep-probe).
    /// </summary>
    [Fact]
    public async Task Resume_drain_via_shallow_failure_skips_the_deep_probe_entirely()
    {
        // On-demand, for the same reason as Resume_drains_a_Mounted_host_on_a_single_forced_deep_probe_failure
        // above -- a persistent host would auto-remount the instant the drain completes and pick up
        // its own fresh (unscripted, successful) deep probe as part of that remount, which would
        // make the "deep probe count unchanged" assertion pass for the wrong reason.
        var host = HostFixtures.OnDemand("nas", drive: "Q:");
        var global = HostFixtures.Global(failuresBeforeUnmount: 1);
        var harness = new SupervisorHarness(HostFixtures.Build(global, host));
        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("nas"));

        harness.Probe.EnqueueShallow("nas.example.internal", ShallowProbeOutcome.ConnectionRefused);
        var deepBefore = harness.Probe.DeepProbeCalls.Count(k => k == "nas");

        await harness.RunAsync(() => harness.Supervisor.ResumeAsync());

        Assert.Equal(deepBefore, harness.Probe.DeepProbeCalls.Count(k => k == "nas"));
        Assert.Equal(MountState.Ready, harness.Snapshot("nas").State);
    }

    /// <summary>
    /// docs/ARCHITECTURE.md §4 rule 5 / ADR-017: resume reconciles against <c>mount/listmounts</c>
    /// before interpreting any probe result. A host believed Mounted whose drive rclone no longer
    /// reports is drained by reconciliation itself -- and, being no longer Mounted afterwards, is
    /// never force-probed at all.
    /// </summary>
    [Fact]
    public async Task Resume_reconciliation_drains_a_host_whose_drive_rclone_no_longer_reports_before_any_force_probe()
    {
        // On-demand, for the same reason as the two tests above -- an auto-remounting persistent
        // host would immediately issue its own fresh deep probe as part of the remount, which would
        // make "deep probe count unchanged" pass without actually proving the ordering this test is
        // about (reconcile first, so a host reconciled away is never force-probed at all).
        var host = HostFixtures.OnDemand("nas", drive: "Q:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("nas"));
        Assert.Equal(MountState.Mounted, harness.Snapshot("nas").State);

        // rclone lost the mount out from under Bosun while it was asleep.
        harness.Rclone.SimulateMountLost("Q:");
        var deepBefore = harness.Probe.DeepProbeCalls.Count(k => k == "nas");

        await harness.RunAsync(() => harness.Supervisor.ResumeAsync());

        Assert.Equal(deepBefore, harness.Probe.DeepProbeCalls.Count(k => k == "nas"));
        Assert.Equal(MountState.Ready, harness.Snapshot("nas").State);
    }
}
