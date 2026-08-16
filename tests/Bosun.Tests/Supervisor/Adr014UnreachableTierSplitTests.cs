using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor;

/// <summary>
/// ADR-014 (both halves, landed together -- see the delivery report on why splitting them is a
/// deadlock): the backoff ladder polls <c>Unreachable</c> hosts by tier, and
/// <see cref="IMountSupervisor.RequestMountAsync"/> against an <c>Unreachable</c> host is never a
/// silent no-op. docs/ARCHITECTURE.md §4 rule 8 and the Backoff section.
/// </summary>
public sealed class Adr014UnreachableTierSplitTests
{
    /// <summary>
    /// Decision 2: "An on-demand host in Unreachable is not polled at all." Acceptance criterion:
    /// zero probes over a long simulated period -- an exact count, not a state observation, because
    /// a host being probed successfully every tick also never changes state.
    /// </summary>
    [Fact]
    public async Task An_on_demand_host_stuck_Unreachable_generates_zero_probes_over_a_long_period()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        harness.Probe.SetDefaultShallow(ShallowProbeOutcome.ConnectionRefused);

        await harness.StartAsync();
        Assert.Equal(MountState.Unreachable, harness.Snapshot("archive").State);
        var probesAfterStartup = harness.Probe.ShallowProbeCalls.Count;

        await harness.AdvanceAsync(TimeSpan.FromDays(365));

        Assert.Equal(probesAfterStartup, harness.Probe.ShallowProbeCalls.Count);
        Assert.Equal(MountState.Unreachable, harness.Snapshot("archive").State);
    }

    /// <summary>Decision 1: persistent hosts keep laddering while Unreachable, unchanged from
    /// pre-ADR-014 behaviour -- that is the mechanism by which a drive returns on its own
    /// (docs/OPERATIONS.md T2).</summary>
    [Fact]
    public async Task A_persistent_host_stuck_Unreachable_keeps_laddering()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var global = HostFixtures.Global(backoffSeconds: [5, 15, 30]);
        var harness = new SupervisorHarness(HostFixtures.Build(global, host));
        harness.Probe.SetDefaultShallow(ShallowProbeOutcome.ConnectionRefused);

        await harness.StartAsync();
        Assert.Equal(MountState.Unreachable, harness.Snapshot("prod").State);
        var probesAfterStartup = harness.Probe.ShallowProbeCalls.Count;

        await harness.AdvanceAsync(TimeSpan.FromSeconds(5));
        await harness.AdvanceAsync(TimeSpan.FromSeconds(15));
        await harness.AdvanceAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(probesAfterStartup + 3, harness.Probe.ShallowProbeCalls.Count);
    }

    /// <summary>
    /// The deadlock guard. Tier-split polling alone (decision 1) strands an on-demand host in
    /// Unreachable forever -- nothing would ever probe it again, and rule 1 forbids Mounting from
    /// anywhere but Ready, so without rule 8 supplying a trigger the host could NEVER leave
    /// Unreachable and the tray's Mount item would be a permanent, silent no-op. This proves rule 8
    /// closes that hole: an explicit mount click is still enough to bring the host all the way back
    /// to Mounted.
    /// </summary>
    [Fact]
    public async Task Mount_click_is_still_enough_to_bring_a_permanently_dark_on_demand_host_out_of_Unreachable_the_deadlock_guard()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        harness.Probe.SetDefaultShallow(ShallowProbeOutcome.ConnectionRefused);

        await harness.StartAsync();
        Assert.Equal(MountState.Unreachable, harness.Snapshot("archive").State);

        // Time passes -- with decision 1 alone, this host would never be probed again by anything
        // internal to the supervisor. If rule 8 did not exist (or were broken), nothing below would
        // ever move it off Unreachable.
        await harness.AdvanceAsync(TimeSpan.FromDays(30));
        Assert.Equal(MountState.Unreachable, harness.Snapshot("archive").State);

        // The server comes back, but the supervisor has no way to know that on its own -- only the
        // user's click can supply the trigger now.
        harness.Probe.SetDefaultShallow(ShallowProbeOutcome.Success);
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));

        Assert.Equal(MountState.Mounted, harness.Snapshot("archive").State);
        Assert.Single(harness.Rclone.MountCalls);
        Assert.Contains("archive", harness.Probe.DeepProbeCalls);
    }

    /// <summary>
    /// Rule 8, spelled out mechanically for an on-demand host: the click resets the ladder, issues
    /// an immediate shallow probe, and -- only once that passes -- proceeds through the ordinary
    /// Ready -&gt; Mounting authorisation path (its own deep probe first). Invariant I1 holds at
    /// every step: nothing here reaches Mounting except via Ready.
    /// </summary>
    [Fact]
    public async Task Mount_click_on_Unreachable_resets_the_ladder_probes_immediately_and_deep_probes_before_mounting()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var global = HostFixtures.Global(backoffSeconds: [5, 15, 30, 60, 300]);
        var harness = new SupervisorHarness(HostFixtures.Build(global, host));
        harness.Probe.SetDefaultShallow(ShallowProbeOutcome.ConnectionRefused);

        await harness.StartAsync();
        // ConsecutiveIdleFailures is 1 from the single startup probe -- decision 1 means an
        // on-demand host in Unreachable is never polled again on its own, so this is the only
        // failure it will ever accumulate without an explicit user action.
        Assert.Equal(1, harness.Snapshot("archive").ConsecutiveIdleFailures);

        var probesBefore = harness.Probe.ShallowProbeCalls.Count;
        var deepProbesBefore = harness.Probe.DeepProbeCalls.Count;

        harness.Probe.SetDefaultShallow(ShallowProbeOutcome.Success);
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));

        Assert.Equal(probesBefore + 1, harness.Probe.ShallowProbeCalls.Count); // exactly one immediate probe
        Assert.Equal(0, harness.Snapshot("archive").ConsecutiveIdleFailures); // ladder reset
        Assert.Equal(deepProbesBefore + 1, harness.Probe.DeepProbeCalls.Count); // deep probe before mount
        Assert.Equal(MountState.Mounted, harness.Snapshot("archive").State);
        Assert.Single(harness.Rclone.MountCalls);
    }

    /// <summary>Item 3: the probe rule 8 itself triggers can fail too, and that must surface
    /// causally rather than quietly -- the same standard already established for the process-wide
    /// mounting-unavailable gate (<see cref="MountingUnavailableException"/>).</summary>
    [Fact]
    public async Task Mount_click_on_Unreachable_throws_causally_when_the_immediate_probe_fails()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        harness.Probe.SetDefaultShallow(ShallowProbeOutcome.ConnectionRefused);
        await harness.StartAsync();

        var ex = await Assert.ThrowsAsync<MountRequestRefusedException>(
            () => harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive")));

        Assert.Equal("archive", ex.HostKey);
        Assert.NotEmpty(ex.Reason);
        Assert.Equal(MountState.Unreachable, harness.Snapshot("archive").State);
        Assert.Empty(harness.Rclone.MountCalls);
        Assert.Empty(harness.Probe.DeepProbeCalls);
    }

    /// <summary>Item 4: while suspended, a mount request on an Unreachable host refuses outright --
    /// no probe at all -- rather than bringing reachability information (or a mount) back up around
    /// a suspend, which is Invariant I8 in reverse.</summary>
    [Fact]
    public async Task Mount_click_on_Unreachable_while_suspended_refuses_without_probing()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        harness.Probe.SetDefaultShallow(ShallowProbeOutcome.ConnectionRefused);
        await harness.StartAsync();
        Assert.Equal(MountState.Unreachable, harness.Snapshot("archive").State);

        await harness.RunAsync(() => harness.Supervisor.SuspendAsync());
        var probesBefore = harness.Probe.ShallowProbeCalls.Count;

        var ex = await Assert.ThrowsAsync<MountRequestRefusedException>(
            () => harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive")));

        Assert.Equal("archive", ex.HostKey);
        Assert.Equal(probesBefore, harness.Probe.ShallowProbeCalls.Count); // no probe was issued
        Assert.Equal(MountState.Unreachable, harness.Snapshot("archive").State);
        Assert.Empty(harness.Rclone.MountCalls);
    }

    /// <summary>A persistent host's own automatic auto-mount-on-Ready path still runs when rule 8's
    /// probe succeeds -- the request does not need to separately re-trigger a mount that already
    /// happened.</summary>
    [Fact]
    public async Task Mount_click_on_an_Unreachable_persistent_host_reaches_Mounted_via_its_own_auto_mount()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        harness.Probe.SetDefaultShallow(ShallowProbeOutcome.ConnectionRefused);
        await harness.StartAsync();
        Assert.Equal(MountState.Unreachable, harness.Snapshot("prod").State);

        harness.Probe.SetDefaultShallow(ShallowProbeOutcome.Success);
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("prod"));

        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);
        Assert.Single(harness.Rclone.MountCalls);
    }
}
