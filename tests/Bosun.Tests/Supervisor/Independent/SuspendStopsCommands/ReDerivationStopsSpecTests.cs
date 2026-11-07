using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Independent.SuspendPreempt;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor.Independent.SuspendStopsCommands;

/// <summary>
/// The ADR-017 re-derivation that resume and a network change share (<c>core/version</c>,
/// <c>mount/listmounts</c>, then per <c>Mounted</c> host a forced shallow and a forced deep probe, then
/// the <c>Ready</c>/<c>Unreachable</c> loop), against a suspend that arrives while it runs.
/// Driven through <c>NetworkChangedAsync</c>, because a network change is the command that finds hosts
/// <c>Mounted</c> in the ordinary case (after a suspend, resume normally finds them <c>Disabled</c>).
/// </summary>
/// <remarks>
/// Spec (ADR-023 amendment, bs-5ga.1): "commands are still not preempted, but a command that works through
/// several hosts checks for a pending suspend before it starts the next unit of work and stops if one is
/// pending" -- before <c>core/version</c>, before <c>mount/listmounts</c>, before each <c>Mounted</c> host's
/// forced shallow probe, between that host's shallow and deep probes, and before each host of the loops.
/// "What a skipped host is left as. Exactly as it was ... nothing counts toward failures_before_unmount, and
/// nothing is booked Automatic". "Mounted: ... the suspend that is pending drains it (DrainCause.Suspend;
/// an on-demand host gets its ADR-022 record)".
/// </remarks>
public sealed class ReDerivationStopsSpecTests
{
    private static readonly string[] Mounted4 = ["p1", "p2", "p3", "od"];

    private static async Task<PreemptSpecHarness> FourMountedAsync()
    {
        var h = StopSpec.NewHarness(
            HostFixtures.Persistent("p1", drive: "P:"),
            HostFixtures.Persistent("p2", drive: "S:"),
            HostFixtures.Persistent("p3", drive: "T:"),
            HostFixtures.OnDemand("od", drive: "Q:"));
        await h.StartAsync();
        await h.RunAsync(() => h.Supervisor.RequestMountAsync("od"));
        foreach (var key in Mounted4)
        {
            Assert.Equal(MountState.Mounted, h.State(key));
        }

        return h;
    }

    /// <summary>
    /// <b>Protects:</b> the check before each <c>Mounted</c> host's forced shallow probe, the check between a
    /// host's shallow and deep probes, "not preempted" for the call in flight, and the hand-off to the
    /// suspend (I8) and the next resume (ADR-022 for the on-demand host).
    /// <b>Catches:</b> a loop that finishes its walk after the suspend became pending (the suspend then waits
    /// ~5 s per remaining probe against Windows' ~2 s); a fix that cancels the call in flight instead of
    /// stopping after it; a skipped host booked as a failure or drained as anything but Suspend; an on-demand
    /// host that loses its remount because a network change, not the suspend, took it down.
    /// </summary>
    [Fact]
    public async Task Suspend_during_the_first_forced_shallow_probe_stops_the_walk_and_the_suspend_drains_the_rest()
    {
        var h = await FourMountedAsync();
        var shallow0 = StopSpec.Counts(h, Mounted4, StopSpec.ShallowCalls);
        var deep0 = StopSpec.Counts(h, Mounted4, StopSpec.DeepCalls);
        var mounts0 = h.TotalMountCalls;
        var mounting0 = StopSpec.MountingEntries(h);

        var holds = HoldSet.Shallow(h, Mounted4);
        var nc = h.Supervisor.NetworkChangedAsync();
        var pump = h.StartPump();
        var inFlight = await holds.InFlightAtArrivalAsync(1, _ => null);

        var suspend = SuspendCaller.Call(h.Supervisor);

        // Read while the probe is still held (Hold.CancellationRequested, bs-s30).
        Assert.False(holds[inFlight].CancellationRequested,
            "the suspend cancelled the network change's forced probe; commands are not preempted, they stop between hosts");
        holds.ReleaseNotArrived(_ => ShallowProbeOutcome.Success);
        holds[inFlight].Release(ShallowProbeOutcome.Success);
        await h.FinishAsync(pump, "the network change or the suspend never finished", nc, suspend);

        foreach (var key in Mounted4.Where(k => k != inFlight))
        {
            Assert.True(StopSpec.ShallowCalls(h, key) == shallow0[key],
                $"the network change force-probed {key} (shallow) after a suspend became pending");
            Assert.True(StopSpec.DeepCalls(h, key) == deep0[key],
                $"the network change force-probed {key} (deep) after a suspend became pending");
        }

        Assert.Equal(shallow0[inFlight] + 1, StopSpec.ShallowCalls(h, inFlight));
        Assert.True(StopSpec.DeepCalls(h, inFlight) == deep0[inFlight],
            $"{inFlight}'s deep probe was started after its shallow probe although a suspend was pending by then");

        foreach (var key in Mounted4)
        {
            Assert.Equal([DrainCause.Suspend], h.DrainCauses(key));
            Assert.Equal(MountState.Disabled, h.State(key));
        }

        Assert.False(h.IsMountedInRclone("P:") || h.IsMountedInRclone("S:") || h.IsMountedInRclone("T:") || h.IsMountedInRclone("Q:"),
            "a drive is still mounted after the suspend ran");
        Assert.Equal(mounts0, h.TotalMountCalls);
        Assert.Equal(mounting0, StopSpec.MountingEntries(h));

        // The next resume picks every host up, the on-demand one through its ADR-022 record.
        await h.ResumeAsync();
        foreach (var key in Mounted4)
        {
            Assert.Equal(MountState.Mounted, h.State(key));
        }

        // And the skipped hosts' probing carries on as normal afterwards (nothing stranded).
        var shallowAfterResume = StopSpec.Counts(h, Mounted4, StopSpec.ShallowCalls);
        await h.AdvanceSecondsAsync(60);
        foreach (var key in Mounted4)
        {
            Assert.True(StopSpec.ShallowCalls(h, key) > shallowAfterResume[key],
                $"{key} was not shallow-probed on its mounted cadence after the resume");
        }
    }

    /// <summary>
    /// <b>Protects:</b> the check BETWEEN the last <c>Mounted</c> host's shallow and deep probes, and the check
    /// before each host of the <c>Ready</c>/<c>Unreachable</c> loop that follows the re-derivation.
    /// <b>Catches:</b> a check placed only at the top of each Mounted host's step (the deep probe of the host in
    /// flight still runs: one more ~5 s call); a check in the Mounted walk but none in the idle loop (an
    /// on-demand <c>Ready</c> host and an <c>Unreachable</c> host are still probed, and the Unreachable one's
    /// backoff is reset by a network change that should have stopped).
    /// </summary>
    [Fact]
    public async Task Suspend_during_the_last_mounted_hosts_shallow_probe_skips_its_deep_probe_and_the_idle_loop()
    {
        var mounted = new[] { "p1", "p2", "p3" };
        var h = StopSpec.NewHarness(
            ["u"],
            HostFixtures.Persistent("p1", drive: "P:"),
            HostFixtures.Persistent("p2", drive: "S:"),
            HostFixtures.Persistent("p3", drive: "T:"),
            HostFixtures.OnDemand("r", drive: "Q:"),
            HostFixtures.Persistent("u", drive: "U:"));
        await h.StartAsync();
        Assert.Equal(MountState.Ready, h.State("r"));
        Assert.Equal(MountState.Unreachable, h.State("u"));

        var all = new[] { "p1", "p2", "p3", "r", "u" };
        var shallow0 = StopSpec.Counts(h, all, StopSpec.ShallowCalls);
        var deep0 = StopSpec.Counts(h, all, StopSpec.DeepCalls);
        var r0 = StopSpec.Fingerprint(h.Snapshot("r"));
        var u0 = StopSpec.Fingerprint(h.Snapshot("u"));
        var mounting0 = StopSpec.MountingEntries(h);

        var holds = HoldSet.Shallow(h, mounted);
        var nc = h.Supervisor.NetworkChangedAsync();
        var pump = h.StartPump();
        var last = await holds.InFlightAtArrivalAsync(3, _ => ShallowProbeOutcome.Success);

        var suspend = SuspendCaller.Call(h.Supervisor);
        Assert.False(holds[last].CancellationRequested, "the suspend cancelled the network change's forced probe");
        holds[last].Release(ShallowProbeOutcome.Success);
        await h.FinishAsync(pump, "the network change or the suspend never finished", nc, suspend);

        foreach (var key in holds.Arrived.Take(2))
        {
            Assert.True(StopSpec.DeepCalls(h, key) == deep0[key] + 1,
                $"setup: {key}, visited before the suspend, should have had its forced deep probe");
        }

        Assert.True(StopSpec.DeepCalls(h, last) == deep0[last],
            $"{last}'s forced deep probe was started after its shallow probe although a suspend was pending by then");
        Assert.True(StopSpec.ShallowCalls(h, "r") == shallow0["r"],
            "the network change re-probed the Ready on-demand host after a suspend became pending");
        Assert.True(StopSpec.ShallowCalls(h, "u") == shallow0["u"],
            "the network change re-probed the Unreachable host after a suspend became pending");
        Assert.Equal(r0, StopSpec.Fingerprint(h.Snapshot("r")));
        Assert.Equal(u0, StopSpec.Fingerprint(h.Snapshot("u")));

        foreach (var key in mounted)
        {
            Assert.Equal([DrainCause.Suspend], h.DrainCauses(key));
        }

        Assert.Equal(mounting0, StopSpec.MountingEntries(h));
    }

    /// <summary>
    /// <b>Protects:</b> the check before <c>mount/listmounts</c>, with <c>core/version</c> the call in flight
    /// (and not cancelled).
    /// <b>Catches:</b> a re-derivation that checks only inside its per-host loops: after a suspend arrives during
    /// <c>core/version</c> it still sends <c>mount/listmounts</c> (10 s client timeout, and it queues behind
    /// rcd's mount lock, bs-6lu) before the suspend's own unmounts.
    /// </summary>
    [Fact]
    public async Task Suspend_during_core_version_sends_no_listmounts_and_no_probe()
    {
        var h = StopSpec.NewHarness(
            ["u"],
            HostFixtures.Persistent("p1", drive: "P:"),
            HostFixtures.OnDemand("r", drive: "Q:"),
            HostFixtures.Persistent("u", drive: "U:"));
        await h.StartAsync();
        Assert.Equal(MountState.Mounted, h.State("p1"));

        var all = new[] { "p1", "r", "u" };
        var shallow0 = StopSpec.Counts(h, all, StopSpec.ShallowCalls);
        var deep0 = StopSpec.Counts(h, all, StopSpec.DeepCalls);
        var callsBefore = h.HRclone.Calls.Count;

        var version = h.HRclone.HoldNext(OnCancel.LikeProduction, "version");
        var nc = h.Supervisor.NetworkChangedAsync();
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(version, "the network change's core/version");

        var suspend = SuspendCaller.Call(h.Supervisor);
        Assert.False(version.CancellationRequested, "the suspend cancelled the network change's core/version");
        version.Release();
        await h.FinishAsync(pump, "the network change or the suspend never finished", nc, suspend);

        var after = h.HRclone.Calls.Skip(callsBefore).Select(c => c.Operation).ToList();
        var firstUnmount = after.IndexOf("unmount");
        var firstListMounts = after.IndexOf("listmounts");
        Assert.True(firstUnmount >= 0, $"setup: the suspend never unmounted P: (calls: {string.Join(",", after)})");
        Assert.True(firstListMounts > firstUnmount,
            $"the network change sent mount/listmounts after a suspend became pending (calls: {string.Join(",", after)})");

        foreach (var key in all)
        {
            Assert.True(StopSpec.ShallowCalls(h, key) == shallow0[key], $"{key} was shallow-probed after a suspend became pending");
            Assert.True(StopSpec.DeepCalls(h, key) == deep0[key], $"{key} was deep-probed after a suspend became pending");
        }

        Assert.Equal([DrainCause.Suspend], h.DrainCauses("p1"));
    }

    /// <summary>
    /// <b>Protects:</b> the check before the first <c>Mounted</c> host's forced shallow probe, with
    /// <c>mount/listmounts</c> the call in flight.
    /// <b>Catches:</b> a check before <c>mount/listmounts</c> but none after it: every Mounted host is still
    /// force-probed (shallow and deep) and every idle host re-probed once the listing returns.
    /// </summary>
    [Fact]
    public async Task Suspend_during_listmounts_starts_no_probe()
    {
        var h = StopSpec.NewHarness(
            ["u"],
            HostFixtures.Persistent("p1", drive: "P:"),
            HostFixtures.Persistent("p2", drive: "S:"),
            HostFixtures.OnDemand("r", drive: "Q:"),
            HostFixtures.Persistent("u", drive: "U:"));
        await h.StartAsync();

        var all = new[] { "p1", "p2", "r", "u" };
        var shallow0 = StopSpec.Counts(h, all, StopSpec.ShallowCalls);
        var deep0 = StopSpec.Counts(h, all, StopSpec.DeepCalls);

        var listMounts = h.HRclone.HoldNext(OnCancel.LikeProduction, "listmounts");
        var nc = h.Supervisor.NetworkChangedAsync();
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(listMounts, "the network change's mount/listmounts");

        var suspend = SuspendCaller.Call(h.Supervisor);
        Assert.False(listMounts.CancellationRequested, "the suspend cancelled the network change's mount/listmounts");
        listMounts.Release();
        await h.FinishAsync(pump, "the network change or the suspend never finished", nc, suspend);

        foreach (var key in all)
        {
            Assert.True(StopSpec.ShallowCalls(h, key) == shallow0[key], $"{key} was shallow-probed after a suspend became pending");
            Assert.True(StopSpec.DeepCalls(h, key) == deep0[key], $"{key} was deep-probed after a suspend became pending");
        }

        Assert.Equal([DrainCause.Suspend], h.DrainCauses("p1"));
        Assert.Equal([DrainCause.Suspend], h.DrainCauses("p2"));
        Assert.False(h.IsMountedInRclone("P:") || h.IsMountedInRclone("S:"));
    }

    /// <summary>
    /// <b>Protects:</b> "nothing in flight is cancelled" together with the stop: the forced deep probe in
    /// flight FAILS, and ADR-017's single post-transition deep failure still drains that host (its drain is
    /// part of its step, decision 4), while every host after it is skipped and left to the suspend.
    /// <b>Catches:</b> a fix that discards the in-flight result once a suspend is pending (decision 3's
    /// "preempted, not failed" wrongly extended to commands; the amendment says it is not), leaving a dead
    /// mount to be booked as a Suspend drain; and the walk carrying on after the failure.
    /// </summary>
    [Fact]
    public async Task An_in_flight_forced_deep_probe_that_fails_still_drains_its_host_and_the_rest_are_left_to_the_suspend()
    {
        var h = await FourMountedAsync();
        var shallow0 = StopSpec.Counts(h, Mounted4, StopSpec.ShallowCalls);
        var deep0 = StopSpec.Counts(h, Mounted4, StopSpec.DeepCalls);
        var mounting0 = StopSpec.MountingEntries(h);

        var holds = HoldSet.Deep(h, Mounted4);
        var nc = h.Supervisor.NetworkChangedAsync();
        var pump = h.StartPump();
        var inFlight = await holds.InFlightAtArrivalAsync(1, _ => null);

        var suspend = SuspendCaller.Call(h.Supervisor);
        Assert.False(holds[inFlight].CancellationRequested, "the suspend cancelled the network change's forced deep probe");
        holds.ReleaseNotArrived(_ => DeepProbeOutcome.Success);
        holds[inFlight].Release(DeepProbeOutcome.Failed);
        await h.FinishAsync(pump, "the network change or the suspend never finished", nc, suspend);

        Assert.Equal([DrainCause.Automatic], h.DrainCauses(inFlight));
        foreach (var key in Mounted4.Where(k => k != inFlight))
        {
            Assert.True(StopSpec.ShallowCalls(h, key) == shallow0[key], $"{key} was shallow-probed after a suspend became pending");
            Assert.True(StopSpec.DeepCalls(h, key) == deep0[key], $"{key} was deep-probed after a suspend became pending");
            Assert.Equal([DrainCause.Suspend], h.DrainCauses(key));
        }

        foreach (var drive in new[] { "P:", "S:", "T:", "Q:" })
        {
            Assert.False(h.IsMountedInRclone(drive), $"{drive} is still mounted after the suspend ran");
        }

        Assert.Equal(mounting0, StopSpec.MountingEntries(h));

        await h.ResumeAsync();
        foreach (var key in new[] { "p1", "p2", "p3" })
        {
            Assert.Equal(MountState.Mounted, h.State(key));
        }
    }

    /// <summary>
    /// <b>Protects:</b> "a skipped host ... no failure counter touched (so nothing counts toward
    /// failures_before_unmount, and nothing is booked Automatic)", against hosts each one failure short of
    /// the threshold, with the call in flight failing for real.
    /// <b>Catches:</b> a walk that carries on (each further forced probe fails and drains its host as
    /// <c>Automatic</c>, with an "unexpectedly unmounted" toast, seconds before sleep); and, the other way, a
    /// fix that throws away the in-flight host's genuine third failure.
    /// </summary>
    [Fact]
    public async Task Hosts_one_failure_short_are_skipped_not_failed_and_the_in_flight_failure_still_counts()
    {
        var keys = new[] { "p1", "p2", "p3" };
        var h = StopSpec.NewHarness(
            HostFixtures.Persistent("p1", drive: "P:"),
            HostFixtures.Persistent("p2", drive: "S:"),
            HostFixtures.Persistent("p3", drive: "T:"));
        await h.StartAsync();
        foreach (var key in keys)
        {
            h.Probe.SetShallow(StopSpec.Hostname(key), ShallowProbeOutcome.Timeout, times: 2);
        }

        await h.AdvanceSecondsAsync(120);
        foreach (var key in keys)
        {
            Assert.True(h.Snapshot(key).ConsecutiveMountedFailures == 2,
                $"setup: {key} should be one failure short of failures_before_unmount = 3 (got {h.Snapshot(key).ConsecutiveMountedFailures})");
            Assert.Equal(MountState.Mounted, h.State(key));
        }

        var shallow0 = StopSpec.Counts(h, keys, StopSpec.ShallowCalls);
        var holds = HoldSet.Shallow(h, keys);
        var nc = h.Supervisor.NetworkChangedAsync();
        var pump = h.StartPump();
        var inFlight = await holds.InFlightAtArrivalAsync(1, _ => null);

        var suspend = SuspendCaller.Call(h.Supervisor);
        Assert.False(holds[inFlight].CancellationRequested, "the suspend cancelled the network change's forced probe");

        // A walk that carries on would see these hosts fail too.
        holds.ReleaseNotArrived(_ => ShallowProbeOutcome.Timeout);
        holds[inFlight].Release(ShallowProbeOutcome.Timeout);
        await h.FinishAsync(pump, "the network change or the suspend never finished", nc, suspend);

        Assert.Equal([DrainCause.Automatic], h.DrainCauses(inFlight));
        foreach (var key in keys.Where(k => k != inFlight))
        {
            Assert.True(StopSpec.ShallowCalls(h, key) == shallow0[key], $"{key} was force-probed after a suspend became pending");
            Assert.Equal([DrainCause.Suspend], h.DrainCauses(key));
        }
    }
}
