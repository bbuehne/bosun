using Bosun.Configuration;
using Bosun.Probe;
using Bosun.Repair;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Independent.SuspendPreempt;
using Bosun.Tests.Supervisor.Independent.SuspendStopsCommands;
using Bosun.Tests.Supervisor.Support;
using System.Text.RegularExpressions;

namespace Bosun.Tests.Supervisor.Independent.UnparkAndRepairEnd;

/// <summary>
/// bs-5ga.5: "Unmount all &amp; re-probe" whose suspend arrives in its LAST step reports that it stopped.
/// Written from ADR-023's bs-5ga.1 amendment (the bs-5ga.2 and bs-5ga.5 notes), docs/ARCHITECTURE.md §4
/// rule 5 and <see cref="IMountSupervisor.RepairAllAsync"/>'s contract, not from the implementation.
/// Invariants: I8 (the suspend still gets every drive off and nothing mounts before resume), I1 (the
/// resume's mounts go through a probe), I2 (an unconfirmed drain is never treated as gone).
/// </summary>
/// <remarks>
/// <para>The spec quotes these tests rest on:</para>
/// <list type="bullet">
/// <item>"The repair now throws <c>RepairStoppedException</c>, same reason and same log path, when it reaches
/// its end with a suspend pending <b>and</b> a host it drained still waiting for that re-enable
/// (<c>Disabled</c>, or <c>Draining</c> whose drain will complete under the suspend)."</item>
/// <item>"a repair that drained nothing and had already re-probed every host when the suspend arrived did all
/// of its work, and still returns normally."</item>
/// <item>"Only what the caller is told changes: no host state, counter, timer or the suspend's handling. A
/// repair that completes with no suspend pending returns normally, and one refused before it starts is
/// unchanged (<c>RepairRefusedException</c>)."</item>
/// <item>"a suspend that arrives after the command has returned and before one of them runs leaves that host
/// <c>Disabled</c> with the repair already reported complete. Resume re-enables it."</item>
/// </list>
/// <para>
/// <see cref="PreemptSpecHarness.FinishAsync"/> only waits for completion: every test awaits the repair
/// itself to assert how it ended. Fakes and a fake clock only.
/// </para>
/// </remarks>
public sealed class RepairEndOfRunStopSpecTests
{
    private const string SleepReason = "the computer is going to sleep";

    private static HostConfig OnDemand(string key, string drive) =>
        HostFixtures.OnDemand(key, probeIntervalSeconds: 0, drive: drive);

    private static HostConfig Persistent(string key, string drive) =>
        HostFixtures.Persistent(key, probeIntervalSeconds: 60, drive: drive);

    private static async Task<PreemptSpecHarness> StartedAsync(string[] mountOnDemand, params HostConfig[] hosts)
    {
        var h = StopSpec.NewHarness(hosts);
        await h.StartAsync();
        foreach (var key in mountOnDemand)
        {
            await h.RunAsync(() => h.Supervisor.RequestMountAsync(key));
        }

        foreach (var host in hosts.Where(x => x.Mount.Mode == MountMode.Persistent || mountOnDemand.Contains(x.Key)))
        {
            Assert.Equal(MountState.Mounted, h.State(host.Key));
        }

        return h;
    }

    private static async Task AssertStoppedAsync(Task repair)
    {
        var stopped = await Assert.ThrowsAsync<RepairStoppedException>(() => repair);
        Assert.Equal(SleepReason, stopped.Reason);
    }

    /// <summary>Runs a repair whose ONLY drain is held, calls the suspend while that drain's unmount is in
    /// flight, then lets everything finish. Returns the (finished) repair task.</summary>
    private static async Task<Task> RepairWithSuspendDuringTheOnlyDrainAsync(PreemptSpecHarness h, string drive)
    {
        var hold = h.HRclone.HoldNextAt(OnCancel.Ignore, "unmount", drive);
        var repair = h.Supervisor.RepairAllAsync();
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(hold, "the repair's only drain");

        var suspend = SuspendCaller.Call(h.Supervisor);
        hold.Release();
        await h.FinishAsync(pump, "the repair or the suspend never finished", suspend, repair);
        await suspend;
        return repair;
    }

    // ---------------------------------------------------------------- the last step is a drain

    /// <summary>
    /// <b>Protects:</b> the core bs-5ga.5 case: a repair with exactly one host, mounted, and the suspend arriving
    /// during its drain. Nothing is left to skip, yet the host ends <c>Disabled</c> with no re-probe, so the
    /// repair reports it stopped. The host then rests <c>Disabled</c> until resume and, being on-demand and
    /// drained by the repair (not the suspend), gets no ADR-022 record and is not remounted at wake.
    /// <b>Catches:</b> the old behaviour (returns normally: the click says nothing for a repair whose re-probe
    /// half did not run); a suspend-drain cause or record invented for a host the repair already drained.
    /// </summary>
    [Fact]
    public async Task A_suspend_during_the_drain_of_a_one_host_repair_reports_the_repair_stopped()
    {
        var h = await StartedAsync(["od1"], OnDemand("od1", "Q:"));

        var repair = await RepairWithSuspendDuringTheOnlyDrainAsync(h, "Q:");

        await AssertStoppedAsync(repair);
        Assert.Equal([DrainCause.Repair], h.DrainCauses("od1"));
        Assert.Equal(MountState.Disabled, h.State("od1"));
        Assert.False(h.IsMountedInRclone("Q:"), "the drive survived the suspend (I8)");
        Assert.Equal(0, h.Snapshot("od1").ConsecutiveMountedFailures);
        Assert.Empty(h.Errors);

        var mountsBeforeResume = h.MountCalls("Q:");
        await h.ResumeAsync();
        await h.AdvanceSecondsAsync(30);
        Assert.Equal(MountState.Ready, h.State("od1"));
        Assert.Equal(mountsBeforeResume, h.MountCalls("Q:"));
    }

    /// <summary>
    /// <b>Protects:</b> the same for a persistent host, plus I8 and I1 around it: nothing mounts while the
    /// suspend is pending or after it ran (the queued re-enable refuses), and resume mounts it after a
    /// passing deep probe.
    /// <b>Catches:</b> the report keyed to on-demand hosts only; the queued re-enable running anyway and
    /// remounting a persistent drive in the seconds before sleep.
    /// </summary>
    [Fact]
    public async Task A_suspend_during_the_drain_of_a_one_host_repair_of_a_persistent_host_reports_stopped_and_mounts_only_after_resume()
    {
        var h = await StartedAsync([], Persistent("p1", "P:"));
        var mountingBefore = StopSpec.MountingEntries(h);
        var mountsBefore = h.MountCalls("P:");

        var repair = await RepairWithSuspendDuringTheOnlyDrainAsync(h, "P:");

        await AssertStoppedAsync(repair);
        Assert.Equal(MountState.Disabled, h.State("p1"));
        await h.AdvanceSecondsAsync(60);
        Assert.Equal(mountingBefore, StopSpec.MountingEntries(h));
        Assert.Equal(mountsBefore, h.MountCalls("P:"));
        Assert.False(h.IsMountedInRclone("P:"));

        var from = UnparkSpec.Here(h.Log);
        await h.ResumeAsync();
        Assert.Equal(MountState.Mounted, h.State("p1"));
        UnparkSpec.AssertEveryMountPrecededByPassingDeepProbe(h.Log, "p1", "P:", from);
        Assert.Empty(h.Errors);
    }

    /// <summary>
    /// <b>Protects:</b> "<c>Draining</c> whose drain will complete under the suspend": the last drain's unmount
    /// does not confirm, so the host is still <c>Draining</c> when the repair ends with a suspend pending. The
    /// repair reports it stopped; the suspend then finishes that drain (rule 5: an already-Draining host keeps
    /// its cause and is re-issued under the budget), so the drive is gone before sleep and the host has ONE
    /// drain entry, cause <c>Repair</c>.
    /// <b>Catches:</b> a condition that looks only for <c>Disabled</c> drained hosts (returns normally here);
    /// the state machine believing an unconfirmed drive is gone (I2).
    /// </summary>
    [Fact]
    public async Task A_suspend_while_the_last_drain_is_still_unconfirmed_reports_stopped_and_the_suspend_finishes_the_drain()
    {
        var h = await StartedAsync(["od1"], OnDemand("od1", "Q:"));
        h.Rclone.ConfirmUnmountAtCall("Q:", 2);

        var repair = await RepairWithSuspendDuringTheOnlyDrainAsync(h, "Q:");

        await AssertStoppedAsync(repair);
        await h.AdvanceSecondsAsync(10);
        Assert.False(h.IsMountedInRclone("Q:"), "the unconfirmed drive was never taken off before the sleep (I8)");
        Assert.Equal(MountState.Disabled, h.State("od1"));
        Assert.Equal([DrainCause.Repair], h.DrainCauses("od1"));
        Assert.Equal(1, h.Transitions("od1").Count(t => t.To == MountState.Draining));
    }

    /// <summary>
    /// <b>Protects:</b> "A repair that completes with no suspend pending returns normally" even when its drain
    /// is unconfirmed at the end (the drain keeps retrying on its own timer and does not hold the command).
    /// <b>Catches:</b> the end check written without the "suspend pending" half, so every repair whose
    /// unmount is slow to confirm reports "stopped: the computer is going to sleep" on a machine that is not
    /// going to sleep.
    /// </summary>
    [Fact]
    public async Task An_unconfirmed_drain_at_the_end_with_no_suspend_returns_normally()
    {
        var h = await StartedAsync(["od1"], OnDemand("od1", "Q:"));
        h.Rclone.ConfirmUnmountAtCall("Q:", 2);

        var repair = h.Supervisor.RepairAllAsync();
        await h.Supervisor.DrainAsync();
        await repair;

        Assert.Equal(MountState.Draining, h.State("od1"));
        await h.AdvanceSecondsAsync(30);
        Assert.False(h.IsMountedInRclone("Q:"));
        Assert.Equal(MountState.Ready, h.State("od1"));
        Assert.Empty(h.Errors);
    }

    // ---------------------------------------------------------------- the last step is a re-probe

    /// <summary>
    /// <b>Protects:</b> the suspend arriving during the LAST re-probe of a repair that drained a host: the probe
    /// in flight finishes (not preempted), nothing is skipped, but the drained host is left <c>Disabled</c>, so
    /// the repair reports it stopped.
    /// <b>Catches:</b> the end check placed only after the drain step; the in-flight probe abandoned or repeated.
    /// </summary>
    [Fact]
    public async Task A_suspend_during_the_last_reprobe_after_a_drain_reports_the_repair_stopped()
    {
        var h = await StartedAsync(["od1"], OnDemand("od1", "Q:"), OnDemand("idle1", "T:"));
        Assert.Equal(MountState.Ready, h.State("idle1"));
        var idleProbesBefore = StopSpec.ShallowCalls(h, "idle1");
        var hold = h.HProbe.HoldNextShallow(StopSpec.Hostname("idle1"));

        var repair = h.Supervisor.RepairAllAsync();
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(hold, "the repair's re-probe of the idle host");
        var suspend = SuspendCaller.Call(h.Supervisor);
        hold.Release(ShallowProbeOutcome.Success);
        await h.FinishAsync(pump, "the repair or the suspend never finished", suspend, repair);

        await AssertStoppedAsync(repair);
        Assert.Equal([DrainCause.Repair], h.DrainCauses("od1"));
        Assert.Equal(MountState.Disabled, h.State("od1"));
        Assert.Equal(MountState.Ready, h.State("idle1"));
        Assert.Equal(idleProbesBefore + 1, StopSpec.ShallowCalls(h, "idle1"));
        Assert.False(h.IsMountedInRclone("Q:"));
        Assert.Empty(h.Errors);
    }

    /// <summary>
    /// <b>Protects:</b> the one case where "a suspend is pending at the end" and "the repair did not finish"
    /// differ: nothing was drained and the suspend arrived during the last re-probe, so every host was
    /// re-probed. The repair did all its work and returns normally.
    /// <b>Catches:</b> the end check written as a bare "suspend pending" (reports a complete repair as stopped).
    /// </summary>
    [Fact]
    public async Task A_suspend_during_the_last_reprobe_of_a_repair_that_drained_nothing_returns_normally()
    {
        var h = await StartedAsync([], OnDemand("idle1", "T:"), OnDemand("idle2", "U:"));
        var probes = HoldSet.Shallow(h, ["idle1", "idle2"]);

        var repair = h.Supervisor.RepairAllAsync();
        var pump = h.StartPump();
        var last = await probes.InFlightAtArrivalAsync(2, _ => ShallowProbeOutcome.Success);
        var suspend = SuspendCaller.Call(h.Supervisor);
        probes[last].Release(ShallowProbeOutcome.Success);
        await h.FinishAsync(pump, "the repair or the suspend never finished", suspend, repair);

        await repair;
        Assert.Equal(MountState.Ready, h.State("idle1"));
        Assert.Equal(MountState.Ready, h.State("idle2"));
        Assert.Empty(h.Errors);
    }

    /// <summary>
    /// <b>Protects:</b> "a host IT drained": a host that is <c>Disabled</c> for another reason (a failed mount
    /// waiting on its mount-retry timer) does not make a repair that drained nothing report a stop.
    /// <b>Catches:</b> the condition written as "any host Disabled or Draining" rather than "a host this repair
    /// drained".
    /// </summary>
    [Fact]
    public async Task A_host_disabled_for_another_reason_does_not_make_a_complete_repair_report_a_stop()
    {
        var h = StopSpec.NewHarness(Persistent("bad", "P:"), OnDemand("idle1", "T:"));
        h.Rclone.FailMount("P:", new InvalidOperationException("drive letter already in use"), times: 1);
        await h.StartAsync();
        Assert.Equal(MountState.Disabled, h.State("bad"));
        Assert.Equal(MountState.Ready, h.State("idle1"));
        var hold = h.HProbe.HoldNextShallow(StopSpec.Hostname("idle1"));

        var repair = h.Supervisor.RepairAllAsync();
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(hold, "the repair's re-probe of the idle host");
        var suspend = SuspendCaller.Call(h.Supervisor);
        hold.Release(ShallowProbeOutcome.Success);
        await h.FinishAsync(pump, "the repair or the suspend never finished", suspend, repair);

        await repair;
        Assert.DoesNotContain(DrainCause.Repair, h.DrainCauses("bad"));
        Assert.False(h.IsMountedInRclone("P:"));
    }

    /// <summary>
    /// <b>Protects:</b> the contrast case (bs-5ga.2, kept here so the boundary is pinned from both sides): with
    /// nothing drained, a suspend during the FIRST of two re-probes skips the second, so the repair did not
    /// finish and reports it stopped.
    /// <b>Catches:</b> the bs-5ga.5 condition misapplied to the mid-loop stop ("nothing drained, so return
    /// normally"), hiding a skipped re-probe.
    /// </summary>
    [Fact]
    public async Task A_suspend_during_the_first_of_two_reprobes_of_a_repair_that_drained_nothing_still_reports_stopped()
    {
        var h = await StartedAsync([], OnDemand("idle1", "T:"), OnDemand("idle2", "U:"));
        var probes = HoldSet.Shallow(h, ["idle1", "idle2"]);

        var repair = h.Supervisor.RepairAllAsync();
        var pump = h.StartPump();
        var first = await probes.InFlightAtArrivalAsync(1, _ => ShallowProbeOutcome.Success);
        var suspend = SuspendCaller.Call(h.Supervisor);
        probes.ReleaseNotArrived(_ => ShallowProbeOutcome.Success);
        probes[first].Release(ShallowProbeOutcome.Success);
        await h.FinishAsync(pump, "the repair or the suspend never finished", suspend, repair);

        await AssertStoppedAsync(repair);
    }

    // ---------------------------------------------------------------- no suspend, or one that comes too late

    /// <summary>
    /// <b>Protects:</b> the baseline: no suspend at all, the repair returns normally, each drained host is
    /// re-enabled (a persistent one remounts after a fresh deep probe, I1; an on-demand one rests <c>Ready</c>).
    /// <b>Catches:</b> the new end check firing without a suspend (e.g. on "a drained host is still Disabled at
    /// the end", which is always true here because the re-enables are queued behind the command).
    /// </summary>
    [Fact]
    public async Task A_repair_with_no_suspend_returns_normally_and_re_enables_what_it_drained()
    {
        var h = await StartedAsync(["od1"], OnDemand("od1", "Q:"), Persistent("p1", "P:"));
        var from = UnparkSpec.Here(h.Log);

        var repair = h.Supervisor.RepairAllAsync();
        await h.Supervisor.DrainAsync();
        await repair;

        Assert.Equal([DrainCause.Repair], h.DrainCauses("od1"));
        Assert.Equal([DrainCause.Repair], h.DrainCauses("p1"));
        Assert.Equal(MountState.Ready, h.State("od1"));
        Assert.Equal(MountState.Mounted, h.State("p1"));
        Assert.False(h.IsMountedInRclone("Q:"));
        UnparkSpec.AssertEveryMountPrecededByPassingDeepProbe(h.Log, "p1", "P:", from);
        Assert.Empty(h.Errors);
    }

    /// <summary>
    /// <b>Protects:</b> the window the spec leaves uncovered by construction: the repair has returned and one
    /// queued re-enable is running (its probe held) when the suspend arrives. The repair stays reported as
    /// complete; nothing throws or logs an error; nothing mounts before resume (I8: the host whose enable probe
    /// was in flight reaches <c>Ready</c> but not <c>Mounting</c>, the other re-enable refuses and its host
    /// rests <c>Disabled</c>); resume mounts both through a deep probe (I1).
    /// <b>Catches:</b> the late suspend crashing the supervisor loop or faulting the already-returned repair;
    /// a re-enable that mounts while the suspend is pending.
    /// </summary>
    [Fact]
    public async Task A_suspend_after_the_repair_returned_but_before_its_re_enables_ran_breaks_nothing()
    {
        var h = await StartedAsync([], Persistent("p1", "P:"), Persistent("p2", "R:"));
        var reEnables = HoldSet.Shallow(h, ["p1", "p2"]);

        var repair = h.Supervisor.RepairAllAsync();
        var pump = h.StartPump();
        var inFlight = await reEnables.InFlightAtArrivalAsync(1, _ => ShallowProbeOutcome.Success);
        await PreemptSpecHarness.WaitForAsync(
            () => repair.IsCompleted, "setup: the repair had not returned when its first queued re-enable ran");
        Assert.True(repair.IsCompletedSuccessfully, "the repair reported something other than completion before any suspend");
        var other = inFlight == "p1" ? "p2" : "p1";

        var mountingBefore = StopSpec.MountingEntries(h);
        var suspend = SuspendCaller.Call(h.Supervisor);
        reEnables.ReleaseNotArrived(_ => ShallowProbeOutcome.Success);
        reEnables[inFlight].Release(ShallowProbeOutcome.Success);
        await h.FinishAsync(pump, "the re-enable or the suspend never finished", suspend);
        await suspend;

        await repair;
        Assert.Empty(h.Errors);
        Assert.Equal(mountingBefore, StopSpec.MountingEntries(h));
        Assert.False(h.IsMountedInRclone("P:"));
        Assert.False(h.IsMountedInRclone("R:"));
        Assert.Equal(MountState.Ready, h.State(inFlight));
        Assert.Equal(MountState.Disabled, h.State(other));

        var from = UnparkSpec.Here(h.Log);
        await h.ResumeAsync();
        Assert.Equal(MountState.Mounted, h.State("p1"));
        Assert.Equal(MountState.Mounted, h.State("p2"));
        UnparkSpec.AssertEveryMountPrecededByPassingDeepProbe(h.Log, "p1", "P:", from);
        UnparkSpec.AssertEveryMountPrecededByPassingDeepProbe(h.Log, "p2", "R:", from);
    }

    // ---------------------------------------------------------------- what the user is told

    /// <summary>
    /// <b>Protects:</b> the click's dialog for the end case: the same "stopped" report as a repair stopped
    /// partway ("the click shows 'stopped: the computer is going to sleep'"), never "nothing was unmounted"
    /// (the drive WAS unmounted) and never wording that addresses or blames the user; the click itself never
    /// throws and releases its in-flight flag.
    /// <b>Catches:</b> the end-of-run stop surfacing through the generic failure path ("could not ..." plus an
    /// exception message) or through the refusal wording.
    /// </summary>
    [Fact]
    public async Task The_click_shows_stopped_with_the_reason_for_an_end_of_run_stop()
    {
        var h = await StartedAsync(["od1"], OnDemand("od1", "Q:"));
        var prompt = new RecordingPrompt();
        var commands = new RepairCommands(h.Supervisor, () => null, () => null, prompt, h.Time);
        var hold = h.HRclone.HoldNextAt(OnCancel.Ignore, "unmount", "Q:");

        var click = commands.UnmountAllAsync();
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(hold, "the repair's only drain");
        var suspend = SuspendCaller.Call(h.Supervisor);
        hold.Release();
        await h.FinishAsync(pump, "the click or the suspend never finished", suspend, click);

        await click;
        Assert.False(commands.IsUnmountingAll);
        var (title, message) = Assert.Single(prompt.Shown);
        Assert.Equal("Unmount all & re-probe", title);
        Assert.Contains("stopped", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(SleepReason, message, StringComparison.Ordinal);
        Assert.DoesNotContain("nothing was unmounted", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("could not", message, StringComparison.OrdinalIgnoreCase);
        Assert.False(
            Regex.IsMatch(message, @"\byou(r|rs|rself)?\b", RegexOptions.IgnoreCase),
            $"the dialog addresses the user, which reads as blame for a stop the sleep caused: {message}");
        Assert.False(h.IsMountedInRclone("Q:"));
    }

    private sealed class RecordingPrompt : IRepairPrompt
    {
        public List<(string Title, string Message)> Shown { get; } = [];

        public Task<RepairConfirmation> ConfirmAsync(string title, string message) =>
            Task.FromResult(RepairConfirmation.Confirmed);

        public void ShowError(string title, string message)
        {
            lock (Shown)
            {
                Shown.Add((title, message));
            }
        }
    }
}
