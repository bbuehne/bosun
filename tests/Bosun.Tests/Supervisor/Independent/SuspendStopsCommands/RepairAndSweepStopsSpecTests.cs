using Bosun.Configuration;
using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Independent.SuspendPreempt;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor.Independent.SuspendStopsCommands;

/// <summary>
/// The two other multi-host commands the amendment covers: "Unmount all &amp; re-probe" (its drains, then
/// its re-probe step) and the ADR-022 deferred-remount sweep.
/// </summary>
/// <remarks>
/// Spec (ADR-023 amendment, bs-5ga.1): the check runs before "each drain of 'Unmount all &amp; re-probe' and
/// each host of its re-probe step" and before each host "of the deferred-remount sweep". "'Unmount all &amp;
/// re-probe' starts with drains, so its in-flight unit is one drain." "Mounted: ... the suspend that is
/// pending drains it (DrainCause.Suspend; an on-demand host gets its ADR-022 record)." "A host holding an
/// ADR-022 remount record keeps it." Rule 9a: "a drain for any other cause (... a repair ...) never sets the
/// record"; the record is kept, not spent, when "the system is suspended again".
/// </remarks>
public sealed class RepairAndSweepStopsSpecTests
{
    private static readonly Dictionary<string, string> MountedDrives = new()
    {
        ["p1"] = "P:",
        ["p2"] = "S:",
        ["od1"] = "Q:",
        ["od2"] = "R:",
    };

    /// <summary>
    /// <b>Protects:</b> the check before each drain of the repair; the hand-off of the hosts it did not reach
    /// to the suspend (cause Suspend, so an on-demand one gets its ADR-022 record and comes back after resume);
    /// the check before the re-probe step (an idle host is not probed); the drain in flight is not cut short.
    /// <b>Catches:</b> a repair that drains every host before the suspend gets the channel (one unmount, up to
    /// 30 s, per remaining host); a skipped on-demand host drained as Repair instead of Suspend, so the user's
    /// drive silently does not come back after the sleep; the repair's drain in flight abandoned half-done.
    /// </summary>
    [Fact]
    public async Task Suspend_during_a_repairs_first_drain_leaves_the_other_mounts_to_the_suspend()
    {
        var h = StopSpec.NewHarness(
            HostFixtures.Persistent("p1", drive: "P:"),
            HostFixtures.Persistent("p2", drive: "S:"),
            HostFixtures.OnDemand("od1", drive: "Q:"),
            HostFixtures.OnDemand("od2", drive: "R:"),
            HostFixtures.OnDemand("idle", drive: "T:"));
        await h.StartAsync();
        await h.RunAsync(() => h.Supervisor.RequestMountAsync("od1"));
        await h.RunAsync(() => h.Supervisor.RequestMountAsync("od2"));
        foreach (var key in MountedDrives.Keys)
        {
            Assert.Equal(MountState.Mounted, h.State(key));
        }

        Assert.Equal(MountState.Ready, h.State("idle"));
        var idleShallow0 = StopSpec.ShallowCalls(h, "idle");
        var unmounts0 = MountedDrives.ToDictionary(kv => kv.Key, kv => h.HRclone.Count("unmount", kv.Value));
        var mounts0 = h.TotalMountCalls;
        var mounting0 = StopSpec.MountingEntries(h);

        var holds = HoldSet.Unmount(h, MountedDrives);
        var repair = h.Supervisor.RepairAllAsync();
        var pump = h.StartPump();
        var inFlight = await holds.InFlightAtArrivalAsync(1, _ => null);

        var suspend = SuspendCaller.Call(h.Supervisor);
        Assert.False(holds[inFlight].CancellationRequested, "the suspend cancelled the repair's mount/unmount in flight");
        holds.ReleaseNotArrived(_ => null);
        holds[inFlight].Release();
        await h.FinishAsync(pump, "the repair or the suspend never finished", repair, suspend);
        await StopSpec.AssertRepairReportedStopAsync(repair);

        Assert.Equal([DrainCause.Repair], h.DrainCauses(inFlight));
        foreach (var key in MountedDrives.Keys.Where(k => k != inFlight))
        {
            Assert.Equal([DrainCause.Suspend], h.DrainCauses(key));
            Assert.True(h.HRclone.Count("unmount", MountedDrives[key]) == unmounts0[key] + 1,
                $"{key} was unmounted more than once (by the repair after the suspend became pending, and by the suspend)");
        }

        foreach (var drive in MountedDrives.Values)
        {
            Assert.False(h.IsMountedInRclone(drive), $"{drive} is still mounted after the suspend ran");
        }

        Assert.True(StopSpec.ShallowCalls(h, "idle") == idleShallow0,
            "the repair's re-probe step probed an idle host after a suspend became pending");
        Assert.Equal(mounts0, h.TotalMountCalls);
        Assert.Equal(mounting0, StopSpec.MountingEntries(h));

        await h.ResumeAsync();
        Assert.Equal(MountState.Mounted, h.State("p1"));
        Assert.Equal(MountState.Mounted, h.State("p2"));
        foreach (var key in new[] { "od1", "od2" })
        {
            // Rule 9a: the suspend's drain records the user's on-demand mount; the repair's does not.
            Assert.Equal(key == inFlight ? MountState.Ready : MountState.Mounted, h.State(key));
        }

        Assert.Equal(MountState.Ready, h.State("idle"));
    }

    /// <summary>
    /// <b>Protects:</b> the check before each host of the repair's re-probe step, after its drains are done,
    /// including "no backoff reset" for an <c>Unreachable</c> host it skips.
    /// <b>Catches:</b> a check in the repair's drain loop but not in its re-probe loop (each idle host is still
    /// probed after the suspend arrived).
    /// </summary>
    [Fact]
    public async Task Suspend_during_a_repairs_re_probe_step_skips_the_remaining_idle_hosts()
    {
        var idle = new[] { "r1", "r2", "u" };
        var h = StopSpec.NewHarness(
            ["u"],
            HostFixtures.Persistent("p1", drive: "P:"),
            HostFixtures.OnDemand("r1", drive: "Q:"),
            HostFixtures.OnDemand("r2", drive: "R:"),
            HostFixtures.Persistent("u", drive: "U:"));
        await h.StartAsync();
        await h.AdvanceSecondsAsync(6);
        Assert.Equal(MountState.Mounted, h.State("p1"));
        Assert.Equal(MountState.Unreachable, h.State("u"));
        Assert.True(h.Snapshot("u").ConsecutiveIdleFailures >= 2, "setup: u should have climbed its backoff ladder");

        var before = idle.ToDictionary(k => k, k => StopSpec.Fingerprint(h.Snapshot(k)));
        var shallow0 = StopSpec.Counts(h, idle, StopSpec.ShallowCalls);
        var mounting0 = StopSpec.MountingEntries(h);

        var holds = HoldSet.Shallow(h, idle);
        var repair = h.Supervisor.RepairAllAsync();
        var pump = h.StartPump();
        var inFlight = await holds.InFlightAtArrivalAsync(1, _ => null);

        var suspend = SuspendCaller.Call(h.Supervisor);
        Assert.False(holds[inFlight].CancellationRequested, "the suspend cancelled the repair's probe in flight");
        holds.ReleaseNotArrived(_ => ShallowProbeOutcome.Success);
        holds[inFlight].Release(inFlight == "u" ? ShallowProbeOutcome.Timeout : ShallowProbeOutcome.Success);
        await h.FinishAsync(pump, "the repair or the suspend never finished", repair, suspend);
        await StopSpec.AssertRepairReportedStopAsync(repair);

        foreach (var key in idle.Where(k => k != inFlight))
        {
            Assert.True(StopSpec.ShallowCalls(h, key) == shallow0[key], $"the repair re-probed {key} after a suspend became pending");
            Assert.Equal(before[key], StopSpec.Fingerprint(h.Snapshot(key)));
        }

        Assert.Equal([DrainCause.Repair], h.DrainCauses("p1"));
        Assert.Equal(mounting0, StopSpec.MountingEntries(h));
        Assert.False(h.IsMountedInRclone("P:"));

        await h.ResumeAsync();
        Assert.Equal(MountState.Mounted, h.State("p1"));
    }

    /// <summary>
    /// <b>Protects:</b> the check before each host of the deferred-remount sweep (reached here through
    /// <c>SetMountingAvailabilityAsync</c>), I1/I8 for the host in flight (its probe passes, but nothing mounts
    /// with a suspend pending), and "a host holding an ADR-022 remount record keeps it" -- for the skipped
    /// hosts and the one in flight alike -- so the resume after the second sleep remounts them all.
    /// <b>Catches:</b> a sweep that probes (and would mount) every waiting host after the suspend arrived; a
    /// sweep that drops or spends the records of hosts it stopped before, so the user's drives do not come
    /// back after the sleep.
    /// </summary>
    [Fact]
    public async Task Suspend_during_the_deferred_remount_sweep_keeps_every_record_and_the_next_resume_remounts_them()
    {
        var keys = new[] { "od1", "od2", "od3" };
        var drives = new Dictionary<string, string> { ["od1"] = "Q:", ["od2"] = "R:", ["od3"] = "S:" };
        var h = StopSpec.NewHarness(
            HostFixtures.OnDemand("od1", drive: "Q:"),
            HostFixtures.OnDemand("od2", drive: "R:"),
            HostFixtures.OnDemand("od3", drive: "S:"));
        await h.StartAsync();
        foreach (var key in keys)
        {
            await h.RunAsync(() => h.Supervisor.RequestMountAsync(key));
            Assert.Equal(MountState.Mounted, h.State(key));
        }

        // Sleep with the drives up; wake with mounting unavailable, so every record waits (rule 9a).
        await h.SuspendAsync();
        await h.RunAsync(() => h.Supervisor.SetMountingAvailabilityAsync(
            MountingAvailability.Unavailable(MountingUnavailableCause.WinFspMissing, "WinFsp is not installed")));
        await h.ResumeAsync();
        foreach (var key in keys)
        {
            Assert.Equal(MountState.Ready, h.State(key));
            Assert.Equal(1, h.MountCalls(drives[key]));
        }

        var shallow0 = StopSpec.Counts(h, keys, StopSpec.ShallowCalls);
        var mounting0 = StopSpec.MountingEntries(h);

        var holds = HoldSet.Shallow(h, keys);
        var available = h.Supervisor.SetMountingAvailabilityAsync(MountingAvailability.Available);
        var pump = h.StartPump();
        var inFlight = await holds.InFlightAtArrivalAsync(1, _ => null);

        var suspend = SuspendCaller.Call(h.Supervisor);
        Assert.False(holds[inFlight].CancellationRequested, "the suspend cancelled the sweep's probe in flight");
        holds.ReleaseNotArrived(_ => ShallowProbeOutcome.Success);
        holds[inFlight].Release(ShallowProbeOutcome.Success);
        await h.FinishAsync(pump, "the sweep or the suspend never finished", available, suspend);

        foreach (var key in keys.Where(k => k != inFlight))
        {
            Assert.True(StopSpec.ShallowCalls(h, key) == shallow0[key], $"the sweep probed {key} after a suspend became pending");
        }

        foreach (var key in keys)
        {
            Assert.Equal(MountState.Ready, h.State(key));
            Assert.Equal(1, h.MountCalls(drives[key]));
        }

        Assert.Equal(mounting0, StopSpec.MountingEntries(h));

        await h.ResumeAsync();
        foreach (var key in keys)
        {
            Assert.True(h.State(key) == MountState.Mounted,
                $"{key}'s ADR-022 remount record did not survive the interrupted sweep (state {h.State(key)})");
            Assert.Equal(2, h.MountCalls(drives[key]));
        }
    }
}
