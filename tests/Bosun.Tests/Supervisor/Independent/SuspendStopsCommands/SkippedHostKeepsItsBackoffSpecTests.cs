using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Independent.SuspendPreempt;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor.Independent.SuspendStopsCommands;

/// <summary>
/// "No backoff reset" for a skipped host, made order-free: every host the loop could visit first is
/// <c>Unreachable</c> with a non-zero backoff, of both tiers, so whichever host the command stops at is one
/// whose backoff a reset would visibly change.
/// </summary>
/// <remarks>
/// Spec (ADR-023 amendment, bs-5ga.1): "What a skipped host is left as. Exactly as it was: ... no backoff
/// reset ... Every call site checks before it resets a backoff or disposes a timer". ADR-014: an on-demand
/// <c>Unreachable</c> host is not on the recurring ladder, but resume and a network change re-probe it
/// ("no tier split").
/// </remarks>
public sealed class SkippedHostKeepsItsBackoffSpecTests
{
    private static readonly string[] Keys = ["u1", "u2", "ou1", "ou2"];

    /// <summary>
    /// <b>Protects:</b> the check sits BEFORE the per-host backoff reset in resume's loop and in a network
    /// change's loop.
    /// <b>Catches:</b> the reset (or the timer disposal next to it) moved above the check: the host the
    /// command stops at has its backoff ladder thrown away although it was never probed.
    /// </summary>
    [Theory]
    [InlineData("resume")]
    [InlineData("network change")]
    public async Task The_host_a_command_stops_at_keeps_its_backoff(string command)
    {
        var h = StopSpec.NewHarness(
            Keys,
            HostFixtures.Persistent("u1", drive: "U:"),
            HostFixtures.Persistent("u2", drive: "V:"),
            HostFixtures.OnDemand("ou1", drive: "Q:"),
            HostFixtures.OnDemand("ou2", drive: "R:"));
        await h.StartAsync();
        await h.AdvanceSecondsAsync(6);
        foreach (var key in Keys)
        {
            Assert.Equal(MountState.Unreachable, h.State(key));
            Assert.True(h.Snapshot(key).ConsecutiveIdleFailures >= 1,
                $"setup: {key} should have a non-zero backoff (got {h.Snapshot(key).ConsecutiveIdleFailures})");
        }

        if (command == "resume")
        {
            await h.SuspendAsync();
        }

        var before = Keys.ToDictionary(k => k, k => StopSpec.Fingerprint(h.Snapshot(k)));
        var shallow0 = StopSpec.Counts(h, Keys, StopSpec.ShallowCalls);

        var holds = HoldSet.Shallow(h, Keys);
        var run = command == "resume" ? h.Supervisor.ResumeAsync() : h.Supervisor.NetworkChangedAsync();
        var pump = h.StartPump();
        var inFlight = await holds.InFlightAtArrivalAsync(1, _ => null);

        var suspend = SuspendCaller.Call(h.Supervisor);
        holds.ReleaseNotArrived(_ => ShallowProbeOutcome.Success);
        holds[inFlight].Release(ShallowProbeOutcome.Timeout);
        await h.FinishAsync(pump, $"the {command} or the suspend never finished", run, suspend);

        foreach (var key in Keys.Where(k => k != inFlight))
        {
            Assert.True(StopSpec.ShallowCalls(h, key) == shallow0[key], $"the {command} probed {key} after a suspend became pending");
            Assert.Equal(before[key], StopSpec.Fingerprint(h.Snapshot(key)));
        }
    }
}
