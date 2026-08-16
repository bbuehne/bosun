using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor;

/// <summary>
/// docs/ARCHITECTURE.md §4 "Backoff": <c>Unreachable -&gt; Probing</c> uses the configured
/// ladder, holding at the last rung, reset to zero by an explicit trigger.
/// </summary>
public sealed class BackoffLadderTests
{
    [Fact]
    public async Task Idle_retry_follows_the_ladder_and_holds_at_the_last_rung()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var global = HostFixtures.Global(backoffSeconds: [5, 15, 30]);
        var harness = new SupervisorHarness(HostFixtures.Build(global, host));
        harness.Probe.SetDefaultShallow(ShallowProbeOutcome.ConnectionRefused);

        await harness.StartAsync();
        Assert.Equal(MountState.Unreachable, harness.Snapshot("archive").State);
        Assert.Equal(1, harness.Snapshot("archive").ConsecutiveIdleFailures);

        await harness.AdvanceAsync(TimeSpan.FromSeconds(5)); // rung 1 elapses -> 2nd attempt
        Assert.Equal(2, harness.Snapshot("archive").ConsecutiveIdleFailures);

        await harness.AdvanceAsync(TimeSpan.FromSeconds(15)); // rung 2 elapses -> 3rd attempt
        Assert.Equal(3, harness.Snapshot("archive").ConsecutiveIdleFailures);

        await harness.AdvanceAsync(TimeSpan.FromSeconds(30)); // rung 3 (last) elapses -> 4th attempt
        Assert.Equal(4, harness.Snapshot("archive").ConsecutiveIdleFailures);

        // Ladder exhausted -- holds at the last rung (30s), does not grow unbounded.
        await harness.AdvanceAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(5, harness.Snapshot("archive").ConsecutiveIdleFailures);
    }

    [Fact]
    public async Task A_successful_probe_resets_backoff_to_zero()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var global = HostFixtures.Global(backoffSeconds: [5, 15, 30]);
        var harness = new SupervisorHarness(HostFixtures.Build(global, host));
        harness.Probe.SetDefaultShallow(ShallowProbeOutcome.ConnectionRefused);
        await harness.StartAsync();
        await harness.AdvanceAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, harness.Snapshot("archive").ConsecutiveIdleFailures);

        harness.Probe.SetDefaultShallow(ShallowProbeOutcome.Success);
        await harness.AdvanceAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(MountState.Ready, harness.Snapshot("archive").State);
        Assert.Equal(0, harness.Snapshot("archive").ConsecutiveIdleFailures);
    }
}
