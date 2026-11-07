using Bosun.Configuration;
using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Independent.SuspendPreempt;
using Bosun.Tests.Supervisor.Independent.SuspendStopsCommands;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor.Independent.SuspendBaselineAndRepairStop;

/// <summary>
/// bs-5ga.5 (maintainer decision A): a suspend that becomes pending during the repair's LAST step, when nothing is
/// left to skip, is reported as a stop all the same. Written from the notes in ADR-023's bs-5ga.1 amendment and
/// docs/ARCHITECTURE.md §4 rule 5.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rule these tests pin.</b> A repair that reaches its end with a suspend pending AND a host it drained still
/// waiting for its re-enable throws <see cref="RepairStoppedException"/> (same reason as one stopped partway). The
/// re-enable is what "then re-probe" means for a drained host, and ADR-023 decision 1 refuses it while a suspend is
/// pending: the host rests <c>Disabled</c> until resume. A repair that drained nothing, and had re-probed every
/// host before the suspend arrived, did all of its work and returns normally: a bare "a suspend is pending at the
/// end" would report a stop for it, which is false. Only what the caller is told changes: no state, counter, timer
/// or drain cause.
/// </para>
/// <para>
/// <see cref="PreemptSpecHarness.FinishAsync"/> only awaits completion, so every test asserts the repair's fault
/// (or its absence) explicitly. Fakes and a fake clock only: no real rclone, drive letter, host or wt.exe.
/// </para>
/// </remarks>
public sealed class RepairLastStepStopSpecTests
{
    private const string SleepReason = "the computer is going to sleep";

    private const string Why = "the repair or the suspend never finished";

    private static readonly Dictionary<string, string> LastDrainDrives = new()
    {
        ["p1"] = "P:",
        ["od1"] = "Q:",
    };

    private static async Task AssertStoppedAsync(Task repair)
    {
        var stopped = await Assert.ThrowsAsync<RepairStoppedException>(() => repair);
        Assert.Equal(SleepReason, stopped.Reason);
    }

    /// <summary>One persistent and one on-demand host, both mounted, and no other host at all: the repair
    /// has a drain step and no re-probe step.</summary>
    private static async Task<PreemptSpecHarness> MountedOnlyAsync()
    {
        var h = StopSpec.NewHarness(
            HostFixtures.Persistent("p1", drive: "P:"),
            HostFixtures.OnDemand("od1", drive: "Q:"));
        await h.StartAsync();
        await h.RunAsync(() => h.Supervisor.RequestMountAsync("od1"));
        Assert.Equal(MountState.Mounted, h.State("p1"));
        Assert.Equal(MountState.Mounted, h.State("od1"));
        return h;
    }

    /// <summary>
    /// <b>Protects:</b> the first case of bs-5ga.5: the suspend arrives during the LAST drain and there is no idle
    /// host, so nothing is skipped and no re-probe step exists. Both drained hosts end <c>Disabled</c> (decision 1
    /// withholds their re-enable), so the repair reports it stopped. Nothing else changes: both drains are
    /// <c>Repair</c> (not <c>Suspend</c>: the hosts were already off when the suspend ran), no drive is mounted, no
    /// host entered <c>Mounting</c>, no probe was spent on them. Resume then re-enables them: the persistent host
    /// remounts, the on-demand one rests <c>Ready</c> (a repair records no ADR-022 remount).
    /// <b>Catches:</b> the old behaviour (the repair returns normally and the click shows nothing), a stop check that
    /// only covers "before the next host", a check made only in the re-probe loop (which never runs here), and a
    /// stop implemented by changing state (a re-enable forced past the gate, or a drain cause rewritten).
    /// </summary>
    [Fact]
    public async Task A_suspend_during_the_last_drain_with_no_idle_host_reports_the_stop()
    {
        var h = await MountedOnlyAsync();
        var mountCalls = h.TotalMountCalls;
        var mounting = StopSpec.MountingEntries(h);
        var shallow = StopSpec.Counts(h, ["p1", "od1"], StopSpec.ShallowCalls);
        var unmounts = HoldSet.Unmount(h, LastDrainDrives);

        var repair = h.Supervisor.RepairAllAsync();
        var pump = h.StartPump();
        var last = await unmounts.InFlightAtArrivalAsync(2, _ => null);

        var suspend = SuspendCaller.Call(h.Supervisor);
        unmounts[last].Release();
        await h.FinishAsync(pump, Why, suspend, repair);

        await AssertStoppedAsync(repair);
        foreach (var key in LastDrainDrives.Keys)
        {
            Assert.Equal([DrainCause.Repair], h.DrainCauses(key));
            Assert.Equal(MountState.Disabled, h.State(key));
            Assert.False(h.IsMountedInRclone(LastDrainDrives[key]));
        }

        Assert.Equal(shallow, StopSpec.Counts(h, ["p1", "od1"], StopSpec.ShallowCalls));
        Assert.Equal(mountCalls, h.TotalMountCalls);
        Assert.Equal(mounting, StopSpec.MountingEntries(h));
        Assert.Empty(h.Errors);

        await h.ResumeAsync();

        Assert.Equal(MountState.Mounted, h.State("p1"));
        Assert.Equal(2, h.MountCalls("P:"));
        Assert.Equal(MountState.Ready, h.State("od1"));
        Assert.Equal(1, h.MountCalls("Q:"));
    }

    /// <summary>
    /// <b>Protects:</b> the second case: the suspend arrives during the LAST re-probe, after a host was drained. The
    /// re-probe in flight finishes (it is not cancelled and not skipped), but the drained host's re-enable is
    /// withheld, so the repair reports it stopped. The drained host is probed by nothing until resume.
    /// <b>Catches:</b> a check that only guards "before the next host" (nothing follows the last host), and a check
    /// that looks only at the idle hosts and forgets the drained one.
    /// </summary>
    [Fact]
    public async Task A_suspend_during_the_last_reprobe_after_a_drain_reports_the_stop()
    {
        var h = StopSpec.NewHarness(
            ["down1"],
            HostFixtures.OnDemand("od1", drive: "Q:"),
            HostFixtures.Persistent("down1", drive: "D:"));
        await h.StartAsync();
        await h.RunAsync(() => h.Supervisor.RequestMountAsync("od1"));
        Assert.Equal(MountState.Mounted, h.State("od1"));
        Assert.Equal(MountState.Unreachable, h.State("down1"));
        var od1Probes = StopSpec.ShallowCalls(h, "od1");
        var probes = HoldSet.Shallow(h, ["down1"]);

        var repair = h.Supervisor.RepairAllAsync();
        var pump = h.StartPump();
        var inFlight = await probes.InFlightAtArrivalAsync(1, _ => null);
        Assert.Equal("down1", inFlight);
        Assert.Equal(MountState.Disabled, h.State("od1")); // drained, its re-enable queued behind the repair

        var suspend = SuspendCaller.Call(h.Supervisor);
        probes[inFlight].Release(ShallowProbeOutcome.Timeout);
        await h.FinishAsync(pump, Why, suspend, repair);

        await AssertStoppedAsync(repair);
        Assert.Equal([DrainCause.Repair], h.DrainCauses("od1"));
        Assert.Equal(MountState.Disabled, h.State("od1"));
        Assert.Equal(od1Probes, StopSpec.ShallowCalls(h, "od1")); // the re-enable was refused, no probe spent
        Assert.Empty(h.Errors);

        await h.ResumeAsync();

        Assert.Equal(MountState.Ready, h.State("od1"));
        Assert.Equal(od1Probes + 1, StopSpec.ShallowCalls(h, "od1"));
        Assert.Equal(1, h.MountCalls("Q:"));
    }

    /// <summary>
    /// <b>Protects:</b> a drained host whose drain is not yet confirmed still counts. The unmount is not confirmed by
    /// <c>mount/listmounts</c> on the repair's attempt, so the host is <c>Draining</c> at the end of the repair; its
    /// drain completes later, under the pending suspend, and the same gate withholds its re-enable. The repair is not
    /// finished for it either.
    /// <b>Catches:</b> a condition written as "a drained host is <c>Disabled</c>", which would call this repair
    /// complete while its re-probe will not happen.
    /// </summary>
    [Fact]
    public async Task A_suspend_pending_while_a_drained_host_is_still_draining_reports_the_stop()
    {
        var h = StopSpec.NewHarness(
            ["down1"],
            HostFixtures.OnDemand("od1", drive: "Q:"),
            HostFixtures.Persistent("down1", drive: "D:"));
        await h.StartAsync();
        await h.RunAsync(() => h.Supervisor.RequestMountAsync("od1"));
        h.Rclone.ConfirmUnmountAtCall("Q:", 3); // not confirmed by the repair's own attempt
        var probes = HoldSet.Shallow(h, ["down1"]);

        var repair = h.Supervisor.RepairAllAsync();
        var pump = h.StartPump();
        var inFlight = await probes.InFlightAtArrivalAsync(1, _ => null);
        Assert.Equal(MountState.Draining, h.State("od1"));

        var suspend = SuspendCaller.Call(h.Supervisor);
        probes[inFlight].Release(ShallowProbeOutcome.Timeout);
        await h.FinishAsync(pump, Why, suspend, repair);

        await AssertStoppedAsync(repair);
        Assert.Empty(h.Errors);
    }

    /// <summary>
    /// <b>Protects:</b> the other half of the condition, so the rule is not "a suspend is pending at the end": a
    /// repair that drained nothing and whose last re-probe was already in flight when the suspend arrived did all of
    /// its work (every host probed) and returns normally. Reporting a stop here would tell the user something
    /// false. The in-flight probe finished and was counted.
    /// <b>Catches:</b> the bare "<c>SuspendPending</c> at the end" test (it faults this repair).
    /// </summary>
    [Fact]
    public async Task A_suspend_during_the_last_reprobe_of_a_repair_that_drained_nothing_returns_normally()
    {
        var h = StopSpec.NewHarness(["down1"], HostFixtures.Persistent("down1", drive: "D:"));
        await h.StartAsync();
        var before = StopSpec.ShallowCalls(h, "down1");
        var probes = HoldSet.Shallow(h, ["down1"]);

        var repair = h.Supervisor.RepairAllAsync();
        var pump = h.StartPump();
        var inFlight = await probes.InFlightAtArrivalAsync(1, _ => null);

        var suspend = SuspendCaller.Call(h.Supervisor);
        probes[inFlight].Release(ShallowProbeOutcome.Timeout);
        await h.FinishAsync(pump, Why, suspend, repair);

        await repair; // no exception: nothing was skipped and nothing waits for a re-enable
        Assert.Equal(before + 1, StopSpec.ShallowCalls(h, "down1"));
        Assert.Empty(h.Errors);
    }

    /// <summary>
    /// <b>Protects:</b> "A repair that completes with no suspend pending still returns normally", in the same
    /// shape as the stopped one (the last drain, no idle host): both hosts are re-enabled and the persistent one
    /// remounts after its probe.
    /// <b>Catches:</b> a final check that fires whenever a host was drained, whether or not a suspend is pending.
    /// </summary>
    [Fact]
    public async Task A_repair_whose_last_step_is_a_drain_returns_normally_when_no_suspend_is_pending()
    {
        var h = await MountedOnlyAsync();

        var repair = h.Supervisor.RepairAllAsync();
        await h.FinishAsync(h.StartPump(), Why, repair);

        await repair;
        Assert.Equal(MountState.Mounted, h.State("p1"));
        Assert.Equal(2, h.MountCalls("P:"));
        Assert.Equal(MountState.Ready, h.State("od1"));
        Assert.Equal([DrainCause.Repair], h.DrainCauses("od1"));
    }

    /// <summary>
    /// <b>Protects:</b> a suspend that arrives AFTER the repair has finished is not the repair's: the repair already
    /// reported success and keeps it (the check runs at the repair's end, once).
    /// <b>Catches:</b> a stop decided lazily (for example when the task is awaited), or a check that also runs on
    /// the continuations the repair queued.
    /// </summary>
    [Fact]
    public async Task A_suspend_after_the_repair_has_finished_does_not_change_its_report()
    {
        var h = await MountedOnlyAsync();
        var repair = h.Supervisor.RepairAllAsync();
        await h.FinishAsync(h.StartPump(), Why, repair);

        var suspend = h.SuspendAsync();
        await suspend;

        await repair;
        Assert.Empty(h.Errors);
    }
}
