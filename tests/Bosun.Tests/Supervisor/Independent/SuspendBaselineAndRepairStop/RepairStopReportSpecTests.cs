using Bosun.Configuration;
using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Independent.SuspendPreempt;
using Bosun.Tests.Supervisor.Independent.SuspendStopsCommands;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor.Independent.SuspendBaselineAndRepairStop;

/// <summary>
/// bs-5ga.2: "Unmount all &amp; re-probe" stopped partway by a pending suspend reports it. Written from the
/// note in ADR-023's bs-5ga.1 amendment ("a repair that stops reports it") and docs/ARCHITECTURE.md §4 rule 5,
/// not from the implementation. Invariants: I8 (the suspend still gets every drive off), I1 (nothing mounts
/// between the suspend and the resume), I2 (no failure is booked for a host the repair did not reach).
/// </summary>
/// <remarks>
/// <para>The spec quotes these tests rest on:</para>
/// <list type="bullet">
/// <item>"It now throws <c>RepairStoppedException</c> (reason "the computer is going to sleep") from either loop,
/// the drain step or the re-probe step".</item>
/// <item>"The two stay distinct: refused means nothing started (the system was suspended or a suspend was
/// already pending when the command was dequeued), stopped means partway."</item>
/// <item>"The outcome is otherwise unchanged: work already done stays done, hosts the repair did not reach are
/// left for the suspend (drained as <c>DrainCause.Suspend</c>, so an on-demand one gets its ADR-022 record and
/// returns at wake, rule 9a; a completed repair would have left it <c>Ready</c>), and no counter or backoff is
/// touched. A repair that completes before any suspend is pending still returns normally."</item>
/// </list>
/// <para>
/// The spec fixes the order of the repair's two steps (drains, then re-probes) but not the order of hosts
/// inside a step, so every host of a step is held and "the host in flight" is whichever arrives
/// (<see cref="HoldSet"/>). Fakes and a fake clock only.
/// </para>
/// </remarks>
public sealed class RepairStopReportSpecTests
{
    private const string SleepReason = "the computer is going to sleep";

    private static readonly Dictionary<string, string> MountedDrives = new()
    {
        ["od1"] = "Q:",
        ["od2"] = "R:",
        ["od3"] = "S:",
    };

    private static HostConfig OnDemand(string key, string drive) =>
        HostFixtures.OnDemand(key, probeIntervalSeconds: 0, drive: drive);

    /// <summary>Starts a harness and mounts every host in <paramref name="mount"/> on demand.</summary>
    private static async Task<PreemptSpecHarness> MountedAsync(
        IReadOnlyDictionary<string, string> mount, string[]? unreachable = null, params HostConfig[] others)
    {
        var hosts = mount.Select(kv => OnDemand(kv.Key, kv.Value)).Concat(others).ToArray();
        var h = StopSpec.NewHarness(unreachable ?? [], hosts);
        await h.StartAsync();
        foreach (var key in mount.Keys)
        {
            await h.RunAsync(() => h.Supervisor.RequestMountAsync(key));
            Assert.Equal(MountState.Mounted, h.State(key));
        }

        return h;
    }

    private static async Task<RepairStoppedException> AssertStoppedAsync(Task repair)
    {
        var stopped = await Assert.ThrowsAsync<RepairStoppedException>(() => repair);
        Assert.Equal(SleepReason, stopped.Reason);
        return stopped;
    }

    private static void AssertNoFailureDrains(PreemptSpecHarness h, IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            var causes = h.DrainCauses(key);
            Assert.DoesNotContain(DrainCause.Automatic, causes);
            Assert.DoesNotContain(DrainCause.MountFailure, causes);
        }
    }

    /// <summary>
    /// <b>Protects:</b> the report, from the drain step, with the suspend arriving during the FIRST drain: one host
    /// drained by the repair (<c>Repair</c>), the others left for the suspend (<c>Suspend</c>).
    /// <b>Catches:</b> the old behaviour (bs-5ga.2): the loop breaks and the repair returns normally, so the click
    /// reports success; or the stop reported as a refusal ("nothing was unmounted", false here: one was).
    /// </summary>
    [Fact]
    public async Task A_suspend_during_the_first_drain_stops_the_repair_and_it_reports_the_stop()
    {
        var h = await MountedAsync(MountedDrives);
        var unmounts = HoldSet.Unmount(h, MountedDrives);

        var repair = h.Supervisor.RepairAllAsync();
        var pump = h.StartPump();
        var inFlight = await unmounts.InFlightAtArrivalAsync(1, _ => null);

        var suspend = SuspendCaller.Call(h.Supervisor);
        unmounts.ReleaseNotArrived(_ => null);
        unmounts[inFlight].Release();
        await h.FinishAsync(pump, "the repair or the suspend never finished", suspend, repair);

        await AssertStoppedAsync(repair);
        Assert.Equal([DrainCause.Repair], h.DrainCauses(inFlight));
        foreach (var other in MountedDrives.Keys.Where(k => k != inFlight))
        {
            Assert.Equal([DrainCause.Suspend], h.DrainCauses(other));
        }

        Assert.All(MountedDrives.Values, d => Assert.False(h.IsMountedInRclone(d), $"{d} is still mounted after the suspend (I8)"));
        AssertNoFailureDrains(h, MountedDrives.Keys);
        Assert.Empty(h.Errors);
    }

    /// <summary>
    /// <b>Protects:</b> the report when the suspend arrives during the LAST drain and the next unit is the
    /// re-probe step's first host: every drain ran, no idle host was re-probed, and the repair still reports it
    /// stopped (it did not finish: the re-probe step did not run).
    /// <b>Catches:</b> the throw added to the drain loop only, so a stop between the two steps (or at the top of
    /// the re-probe loop) returns normally; or the re-probe step run anyway while the machine goes to sleep.
    /// </summary>
    [Fact]
    public async Task A_suspend_during_the_last_drain_stops_before_the_reprobe_step_and_reports_the_stop()
    {
        var mounted = new Dictionary<string, string> { ["od1"] = "Q:", ["od2"] = "R:" };
        var h = await MountedAsync(mounted, null, OnDemand("idle1", "T:"), OnDemand("idle2", "U:"));
        Assert.Equal(MountState.Ready, h.State("idle1"));
        var idleProbesBefore = StopSpec.Counts(h, ["idle1", "idle2"], StopSpec.ShallowCalls);
        var unmounts = HoldSet.Unmount(h, mounted);

        var repair = h.Supervisor.RepairAllAsync();
        var pump = h.StartPump();
        var last = await unmounts.InFlightAtArrivalAsync(2, _ => null);

        var suspend = SuspendCaller.Call(h.Supervisor);
        unmounts[last].Release();
        await h.FinishAsync(pump, "the repair or the suspend never finished", suspend, repair);

        await AssertStoppedAsync(repair);
        Assert.Equal([DrainCause.Repair], h.DrainCauses("od1"));
        Assert.Equal([DrainCause.Repair], h.DrainCauses("od2"));
        Assert.Equal(idleProbesBefore, StopSpec.Counts(h, ["idle1", "idle2"], StopSpec.ShallowCalls));
        Assert.False(h.IsMountedInRclone("Q:"));
        Assert.False(h.IsMountedInRclone("R:"));
        Assert.Empty(h.Errors);
    }

    /// <summary>
    /// <b>Protects:</b> the report from the re-probe step, mid-loop: one idle host re-probed, the rest skipped, and
    /// the skipped ones "left exactly as they were" (no failure counted, no backoff reset).
    /// <b>Catches:</b> the throw added to the drain loop only (the re-probe loop's stop still a silent
    /// <c>break</c>); and the skip placed after a backoff reset, so a skipped <c>Unreachable</c> host loses its
    /// ladder position.
    /// </summary>
    [Fact]
    public async Task A_suspend_during_a_reprobe_stops_the_rest_of_the_step_and_reports_the_stop()
    {
        string[] idle = ["down1", "down2", "down3"];
        var h = await MountedAsync(
            new Dictionary<string, string> { ["od1"] = "Q:" },
            idle,
            HostFixtures.Persistent("down1", drive: "D:"),
            HostFixtures.Persistent("down2", drive: "E:"),
            HostFixtures.Persistent("down3", drive: "F:"));
        Assert.All(idle, k => Assert.Equal(MountState.Unreachable, h.State(k)));
        var probes = HoldSet.Shallow(h, idle);

        var repair = h.Supervisor.RepairAllAsync();
        var pump = h.StartPump();
        var inFlight = await probes.InFlightAtArrivalAsync(1, _ => ShallowProbeOutcome.Timeout);
        var skipped = idle.Where(k => k != inFlight).ToList();
        var skippedBefore = skipped.ToDictionary(k => k, k => StopSpec.Fingerprint(h.Snapshot(k)));
        var skippedProbesBefore = StopSpec.Counts(h, skipped, StopSpec.ShallowCalls);

        var suspend = SuspendCaller.Call(h.Supervisor);
        probes.ReleaseNotArrived(_ => ShallowProbeOutcome.Timeout);
        probes[inFlight].Release(ShallowProbeOutcome.Timeout);
        await h.FinishAsync(pump, "the repair or the suspend never finished", suspend, repair);

        await AssertStoppedAsync(repair);
        Assert.Equal([DrainCause.Repair], h.DrainCauses("od1"));
        Assert.Equal(skippedProbesBefore, StopSpec.Counts(h, skipped, StopSpec.ShallowCalls));
        foreach (var key in skipped)
        {
            Assert.Equal(skippedBefore[key], StopSpec.Fingerprint(h.Snapshot(key)));
        }

        Assert.Empty(h.Errors);
    }

    /// <summary>
    /// <b>Protects:</b> "refused means nothing started ... a suspend was already pending when the command was
    /// dequeued". The repair is queued, the suspend arrives before the channel reaches it: refused, not stopped,
    /// and nothing drained as <c>Repair</c>.
    /// <b>Catches:</b> the new stop check reached before the refusal check, so a repair that never started reports
    /// <c>RepairStoppedException</c> and the user is told some of it ran (and the dialog does not say "nothing was
    /// unmounted", which is the true statement here).
    /// </summary>
    [Fact]
    public async Task A_repair_dequeued_with_a_suspend_already_pending_is_refused_not_stopped()
    {
        var h = await MountedAsync(MountedDrives);

        var repair = h.Supervisor.RepairAllAsync();
        var suspend = SuspendCaller.Call(h.Supervisor);
        await h.FinishAsync(h.StartPump(), "the repair or the suspend never finished", suspend, repair);

        var refused = await Assert.ThrowsAsync<RepairRefusedException>(() => repair);
        Assert.False(string.IsNullOrWhiteSpace(refused.Reason));
        foreach (var key in MountedDrives.Keys)
        {
            Assert.Equal([DrainCause.Suspend], h.DrainCauses(key));
        }
    }

    /// <summary>
    /// <b>Protects:</b> "A repair that completes before any suspend is pending still returns normally", including
    /// after an earlier suspend and resume (the pending count is back to zero).
    /// <b>Catches:</b> the stop check reading a flag that a past suspend left set (or <c>MountingSuspended</c>
    /// rather than a pending suspend), so every repair after the first wake reports "stopped".
    /// </summary>
    [Fact]
    public async Task A_repair_with_no_suspend_pending_returns_normally_even_after_an_earlier_suspend_and_resume()
    {
        var h = await MountedAsync(MountedDrives);
        await h.SuspendAsync();
        await h.ResumeAsync();
        Assert.All(MountedDrives.Keys, k => Assert.Equal(MountState.Mounted, h.State(k)));

        var repair = h.Supervisor.RepairAllAsync();
        await h.FinishAsync(h.StartPump(), "the repair never finished", repair);

        await repair;
        foreach (var key in MountedDrives.Keys)
        {
            Assert.Equal([DrainCause.Suspend, DrainCause.Repair], h.DrainCauses(key));
            Assert.Equal(MountState.Ready, h.State(key));
        }
    }

    /// <summary>
    /// <b>Protects:</b> "The outcome is otherwise unchanged": at the wake, the on-demand hosts the suspend drained
    /// come back (ADR-022, rule 9a), and the one the repair drained does not (a repair leaves an on-demand host
    /// <c>Ready</c>). Nothing enters <c>Mounting</c> between the suspend and the resume (I1, I8).
    /// <b>Catches:</b> a "stopped" repair that also drains or re-records the hosts it did not reach as
    /// <c>Repair</c> (they would then stay down after the wake), or one that hands its own host an ADR-022
    /// record (it would come back although the user asked for it to be dropped).
    /// </summary>
    [Fact]
    public async Task After_a_stopped_repair_the_wake_brings_back_only_the_hosts_the_suspend_drained()
    {
        var h = await MountedAsync(MountedDrives);
        var unmounts = HoldSet.Unmount(h, MountedDrives);
        var repair = h.Supervisor.RepairAllAsync();
        var pump = h.StartPump();
        var inFlight = await unmounts.InFlightAtArrivalAsync(1, _ => null);
        var mountingBefore = StopSpec.MountingEntries(h);

        var suspend = SuspendCaller.Call(h.Supervisor);
        unmounts.ReleaseNotArrived(_ => null);
        unmounts[inFlight].Release();
        await h.FinishAsync(pump, "the repair or the suspend never finished", suspend, repair);
        await AssertStoppedAsync(repair);
        await h.AdvanceSecondsAsync(30);
        Assert.Equal(mountingBefore, StopSpec.MountingEntries(h));

        await h.ResumeAsync();

        Assert.Equal(MountState.Ready, h.State(inFlight));
        Assert.Equal(1, h.MountCalls(MountedDrives[inFlight]));
        foreach (var other in MountedDrives.Keys.Where(k => k != inFlight))
        {
            Assert.Equal(MountState.Mounted, h.State(other));
            Assert.Equal(2, h.MountCalls(MountedDrives[other]));
        }
    }

    /// <summary>
    /// <b>Protects:</b> "no counter or backoff is touched" for the hosts the stopped repair did not reach, and the
    /// drain the suspend gives them is the suspend's, not a failure.
    /// <b>Catches:</b> the stop implemented as a cancellation (an <see cref="OperationCanceledException"/> through
    /// the drain), which escapes as an Error in the channel's catch-all or books the unreached hosts' drains as
    /// <c>Automatic</c> with the "unexpectedly unmounted" toast.
    /// </summary>
    [Fact]
    public async Task The_hosts_a_stopped_repair_did_not_reach_keep_their_counters()
    {
        var h = await MountedAsync(MountedDrives);
        var before = MountedDrives.Keys.ToDictionary(k => k, k => h.Snapshot(k));
        var unmounts = HoldSet.Unmount(h, MountedDrives);
        var repair = h.Supervisor.RepairAllAsync();
        var pump = h.StartPump();
        var inFlight = await unmounts.InFlightAtArrivalAsync(1, _ => null);

        var suspend = SuspendCaller.Call(h.Supervisor);
        unmounts.ReleaseNotArrived(_ => null);
        unmounts[inFlight].Release();
        await h.FinishAsync(pump, "the repair or the suspend never finished", suspend, repair);
        await AssertStoppedAsync(repair);

        foreach (var key in MountedDrives.Keys.Where(k => k != inFlight))
        {
            var after = h.Snapshot(key);
            Assert.Equal(before[key].ConsecutiveMountedFailures, after.ConsecutiveMountedFailures);
            Assert.Equal(before[key].ConsecutiveDeepProbeFailures, after.ConsecutiveDeepProbeFailures);
            Assert.Equal(before[key].ConsecutiveIdleFailures, after.ConsecutiveIdleFailures);
            Assert.Equal(before[key].ConsecutiveMountFailures, after.ConsecutiveMountFailures);
            Assert.Equal(before[key].UserParked, after.UserParked);
        }

        AssertNoFailureDrains(h, MountedDrives.Keys);
        Assert.Empty(h.Errors);
    }
}
