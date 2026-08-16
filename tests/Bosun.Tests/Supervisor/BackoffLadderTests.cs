using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor;

/// <summary>
/// docs/ARCHITECTURE.md §4 "Backoff": <c>Unreachable -&gt; Probing</c> uses the configured
/// ladder, holding at the last rung, reset to zero by an explicit trigger.
/// </summary>
/// <remarks>
/// ADR-014 restricts the ladder to <c>persistent</c> hosts (an on-demand host in
/// <c>Unreachable</c> is not polled at all -- see
/// <c>Adr014UnreachableTierSplitTests.OnDemandPollingTests</c> for that half), so these mechanism
/// tests use a persistent-tier host. A persistent host that recovers auto-mounts (rule 6), which is
/// why the "recovers" test asserts <c>Mounted</c> rather than <c>Ready</c>.
/// </remarks>
public sealed class BackoffLadderTests
{
    [Fact]
    public async Task Idle_retry_follows_the_ladder_and_holds_at_the_last_rung()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var global = HostFixtures.Global(backoffSeconds: [5, 15, 30]);
        var harness = new SupervisorHarness(HostFixtures.Build(global, host));
        harness.Probe.SetDefaultShallow(ShallowProbeOutcome.ConnectionRefused);

        await harness.StartAsync();
        Assert.Equal(MountState.Unreachable, harness.Snapshot("prod").State);
        Assert.Equal(1, harness.Snapshot("prod").ConsecutiveIdleFailures);

        await harness.AdvanceAsync(TimeSpan.FromSeconds(5)); // rung 1 elapses -> 2nd attempt
        Assert.Equal(2, harness.Snapshot("prod").ConsecutiveIdleFailures);

        await harness.AdvanceAsync(TimeSpan.FromSeconds(15)); // rung 2 elapses -> 3rd attempt
        Assert.Equal(3, harness.Snapshot("prod").ConsecutiveIdleFailures);

        await harness.AdvanceAsync(TimeSpan.FromSeconds(30)); // rung 3 (last) elapses -> 4th attempt
        Assert.Equal(4, harness.Snapshot("prod").ConsecutiveIdleFailures);

        // Ladder exhausted -- holds at the last rung (30s), does not grow unbounded.
        await harness.AdvanceAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(5, harness.Snapshot("prod").ConsecutiveIdleFailures);
    }

    [Fact]
    public async Task A_successful_probe_resets_backoff_to_zero()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var global = HostFixtures.Global(backoffSeconds: [5, 15, 30]);
        var harness = new SupervisorHarness(HostFixtures.Build(global, host));
        harness.Probe.SetDefaultShallow(ShallowProbeOutcome.ConnectionRefused);
        await harness.StartAsync();
        await harness.AdvanceAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, harness.Snapshot("prod").ConsecutiveIdleFailures);

        harness.Probe.SetDefaultShallow(ShallowProbeOutcome.Success);
        await harness.AdvanceAsync(TimeSpan.FromSeconds(15));

        // Persistent tier: recovering from Unreachable auto-mounts (rule 6) rather than resting at
        // Ready -- the on-demand equivalent rests at Ready, see MountingLifecycleTests.
        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);
        Assert.Equal(0, harness.Snapshot("prod").ConsecutiveIdleFailures);
    }
}
