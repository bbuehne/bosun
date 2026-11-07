using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Independent.SuspendPreempt;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor.Independent.SuspendStopsCommands;

/// <summary>
/// Resume's per-host loop (every previously enabled host re-probed: a <c>Disabled</c> host through
/// <c>Probing</c>, a <c>Ready</c> or <c>Unreachable</c> host in place) against a second suspend that
/// arrives while it runs, with mixed tiers and states.
/// </summary>
/// <remarks>
/// Spec (ADR-023 amendment, bs-5ga.1): the check runs "before ... each host of resume's loop". "What a
/// skipped host is left as. Exactly as it was: no state change, no failure counter touched ..., no backoff
/// reset, no remount record dropped, and no timer disposed." "Ready and Unreachable: ... the resume that
/// follows the suspend force-probes it (rule 5; no tier split)." "Disabled and administratively enabled:
/// re-enabled by that resume."
/// </remarks>
public sealed class ResumeLoopStopsSpecTests
{
    private static readonly string[] Keys = ["m", "u1", "u2", "r1", "r2"];

    /// <summary>
    /// <b>Protects:</b> the check before each host of resume's loop, at the first host and at a middle one
    /// (so hosts are both visited and skipped by the same resume), and "left exactly as it was" for the
    /// skipped ones -- in particular an <c>Unreachable</c> host's backoff, which a resume would otherwise
    /// reset.
    /// <b>Catches:</b> a resume loop with no check (each remaining host costs one more ~5 s probe after the
    /// suspend arrived, and an Unreachable host's backoff ladder is reset for nothing); a check placed after
    /// the backoff reset or the timer disposal (the snapshot of the skipped host changes); a skipped host the
    /// following resume does not pick up.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task A_suspend_during_resumes_loop_leaves_the_remaining_hosts_untouched_and_the_next_resume_takes_them(int inFlightAt)
    {
        var h = StopSpec.NewHarness(
            ["u1", "u2"],
            HostFixtures.Persistent("m", drive: "P:"),
            HostFixtures.Persistent("u1", drive: "U:"),
            HostFixtures.Persistent("u2", drive: "V:"),
            HostFixtures.OnDemand("r1", drive: "Q:"),
            HostFixtures.OnDemand("r2", drive: "R:"));
        await h.StartAsync();

        // Move the Unreachable hosts up their backoff ladder ([5, 15, ...] s), so a reset is visible.
        await h.AdvanceSecondsAsync(6);
        foreach (var key in new[] { "u1", "u2" })
        {
            Assert.Equal(MountState.Unreachable, h.State(key));
            Assert.True(h.Snapshot(key).ConsecutiveIdleFailures >= 2,
                $"setup: {key} should have at least two idle failures (got {h.Snapshot(key).ConsecutiveIdleFailures})");
        }

        await h.SuspendAsync();
        Assert.Equal(MountState.Disabled, h.State("m"));
        Assert.Equal(MountState.Ready, h.State("r1"));

        var before = Keys.ToDictionary(k => k, k => StopSpec.Fingerprint(h.Snapshot(k)));
        var shallow0 = StopSpec.Counts(h, Keys, StopSpec.ShallowCalls);

        // A resume visit "as the host really is": the Unreachable ones still fail.
        object? Natural(string key) => key is "u1" or "u2" ? ShallowProbeOutcome.Timeout : ShallowProbeOutcome.Success;

        var holds = HoldSet.Shallow(h, Keys);
        var resume1 = h.Supervisor.ResumeAsync();
        var pump = h.StartPump();
        var inFlight = await holds.InFlightAtArrivalAsync(inFlightAt, Natural);

        // Taken at the moment the suspend arrives: a host visited before it may legitimately have mounted.
        var mounts0 = h.TotalMountCalls;
        var mounting0 = StopSpec.MountingEntries(h);
        var suspend2 = SuspendCaller.Call(h.Supervisor);
        Assert.False(holds[inFlight].CancellationRequested, "the suspend cancelled resume's probe in flight");

        // If resume carried on, these would all pass, which would change every skipped host's snapshot.
        holds.ReleaseNotArrived(_ => ShallowProbeOutcome.Success);
        holds[inFlight].Release(Natural(inFlight));
        await h.FinishAsync(pump, "the resume or the second suspend never finished", resume1, suspend2);

        var skipped = Keys.Except(holds.Arrived).ToList();
        Assert.Equal(Keys.Length - inFlightAt, skipped.Count);
        foreach (var key in skipped)
        {
            Assert.True(StopSpec.ShallowCalls(h, key) == shallow0[key], $"resume probed {key} after a suspend became pending");
            Assert.Equal(before[key], StopSpec.Fingerprint(h.Snapshot(key)));
        }

        Assert.Equal(mounts0, h.TotalMountCalls);
        Assert.Equal(mounting0, StopSpec.MountingEntries(h));

        // The next resume visits everyone; the pre-released holds make every host reachable now.
        var shallowBeforeResume2 = StopSpec.Counts(h, Keys, StopSpec.ShallowCalls);
        await h.ResumeAsync();
        foreach (var key in Keys)
        {
            Assert.True(StopSpec.ShallowCalls(h, key) > shallowBeforeResume2[key], $"the following resume did not re-probe {key}");
        }

        Assert.Equal(MountState.Mounted, h.State("m"));
        Assert.Equal(MountState.Ready, h.State("r1"));
        Assert.Equal(MountState.Ready, h.State("r2"));
    }
}
