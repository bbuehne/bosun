using Bosun.Configuration;
using Bosun.Rclone;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Independent.SuspendPreempt;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor;

/// <summary>
/// bs-5o4 (the maintainer's decision of 2026-11-02: on suspend, "unmount all, then verify"): the suspend
/// sweep sends EVERY host's <c>mount/unmount</c> before it verifies any, so a stuck rcd cannot hold host
/// N's request behind the whole drain step of hosts 1..N-1 (about 80 s each, docs/ARCHITECTURE.md §4
/// rule 5). Invariant I8: a drive left mounted when the machine sleeps is the failure this prevents.
/// </summary>
/// <remarks>
/// <para>
/// A "stuck rcd" is a <see cref="Hold"/> on <c>mount/unmount</c>: the call is captured and does not
/// complete until the test releases it or fails it (<see cref="Hold.Fail"/>, which is what
/// <c>RcloneClient</c> does when the 30 s call timeout fires). The pump is started WITHOUT awaiting it, so
/// it stops at the first call that hangs; what had been reached by then is what the supervisor sent
/// without waiting. With the previous serial drain the pump stopped at the first host and only one
/// <c>unmount</c> had been issued.
/// </para>
/// <para>
/// Nothing here reaches a real rclone, drive letter or WinFsp; time moves only when a test moves it.
/// </para>
/// </remarks>
public sealed class SuspendUnmountAllFirstTests
{
    private const int Budget = 3;
    private static readonly string[] Drives = ["A:", "B:", "C:"];
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(10);

    private static RcloneRcTimeoutException UnmountTimedOut() => new("mount/unmount", RcloneClient.UnmountTimeout);

    private static PreemptSpecHarness NewHarness(params HostConfig[] hosts) =>
        new(HostFixtures.Build(
            HostFixtures.Global(failuresBeforeUnmount: 3, suspendUnmountTimeoutSeconds: Budget), hosts));

    private static async Task<PreemptSpecHarness> ThreePersistentMountedAsync()
    {
        var h = NewHarness(
            HostFixtures.Persistent("a1", probeIntervalSeconds: 60, drive: "A:"),
            HostFixtures.Persistent("b1", probeIntervalSeconds: 60, drive: "B:"),
            HostFixtures.Persistent("c1", probeIntervalSeconds: 60, drive: "C:"));
        await h.StartAsync();
        foreach (var key in new[] { "a1", "b1", "c1" })
        {
            Assert.Equal(MountState.Mounted, h.State(key));
        }

        return h;
    }

    private static string HostOf(string drive) => drive switch { "A:" => "a1", "B:" => "b1", "C:" => "c1", _ => throw new ArgumentOutOfRangeException(nameof(drive)) };

    /// <summary>Calls SuspendAsync on this thread and starts the pump without awaiting it.</summary>
    private static (Task Suspend, Task Pump, DateTimeOffset Arrival) BeginSuspend(PreemptSpecHarness h)
    {
        var arrival = h.Now;
        var suspend = SuspendCaller.Call(h.Supervisor);
        return (suspend, h.StartPump(), arrival);
    }

    /// <summary>
    /// <b>Protects:</b> the decision itself. With the rcd not answering, all three <c>mount/unmount</c>
    /// requests are on the wire at the moment of arrival, none waiting for another, and nothing is
    /// verified until all of them are out.
    /// <b>Catches:</b> a serial sweep (host N's request issued only after host N-1's whole drain step): the
    /// second and third arrivals never happen while the first is held, and the guarded wait fails.
    /// </summary>
    [Fact]
    public async Task With_a_stuck_rcd_all_three_unmount_requests_are_issued_at_arrival_not_serially()
    {
        var h = await ThreePersistentMountedAsync();
        var holds = Drives.Select(_ => h.HRclone.HoldNext(OnCancel.Ignore, "unmount")).ToList();
        var listMountsBefore = h.HRclone.Count("listmounts");

        var (suspend, pump, arrival) = BeginSuspend(h);
        foreach (var hold in holds)
        {
            await PreemptSpecHarness.ArrivedAsync(hold, "an unmount request that a serial sweep would not have sent yet");
        }

        // All three are in flight together and the pump is still blocked on them.
        Assert.False(pump.IsCompleted);
        Assert.Equal(Drives.Order(), holds.Select(x => x.Target!).Order());
        Assert.All(Drives, drive => Assert.Equal([arrival], h.HRclone.Times("unmount", drive)));
        Assert.Equal(listMountsBefore, h.HRclone.Count("listmounts")); // verification waits for all requests
        Assert.All(["a1", "b1", "c1"], key => Assert.Equal(MountState.Draining, h.State(key)));

        holds.ForEach(x => x.Release());
        await h.FinishAsync(pump, "the sweep never finished after every unmount returned", suspend);

        Assert.Empty(h.Errors);
    }

    /// <summary>
    /// <b>Protects:</b> rule 4 under the new shape: an unmount call's own result is never proof. One
    /// <c>mount/listmounts</c> verifies all three hosts, and each is taken to <c>Disabled</c> because the
    /// table no longer lists it.
    /// <b>Catches:</b> a sweep that completes a drain on the unmount call returning (no verification); one
    /// that verifies per host (three listmounts calls); one that never completes the drains.
    /// </summary>
    [Fact]
    public async Task One_listmounts_verifies_all_three_and_each_drains_to_Disabled_with_a_single_unmount()
    {
        var h = await ThreePersistentMountedAsync();
        var listMountsBefore = h.HRclone.Count("listmounts");

        await h.SuspendAsync();

        Assert.Equal(listMountsBefore + 1, h.HRclone.Count("listmounts"));
        foreach (var drive in Drives)
        {
            Assert.Equal(1, h.HRclone.Count("unmount", drive));
            Assert.False(h.IsMountedInRclone(drive));
            Assert.Equal(MountState.Disabled, h.State(HostOf(drive)));
            Assert.Equal(
                [MountState.Draining, MountState.Disabled],
                h.Transitions(HostOf(drive)).Skip(h.Transitions(HostOf(drive)).Count - 2).Select(t => t.To));
            Assert.Equal(DrainCause.Suspend, h.DrainCauses(HostOf(drive)).Last());
        }

        Assert.Empty(h.Errors);
        Assert.DoesNotContain(h.Logger.Entries, e => e.Message.Contains("escalat", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// <b>Protects:</b> the point of the change at its sharpest. Only ONE host's unmount hangs; the other
    /// two requests must still be sent, take effect, and be unmounted, with the first still pending.
    /// <b>Catches:</b> any ordering that lets one slow call gate the others.
    /// </summary>
    [Fact]
    public async Task One_hung_unmount_does_not_hold_back_the_other_two_which_are_unmounted_while_it_hangs()
    {
        var h = await ThreePersistentMountedAsync();
        var stuck = h.HRclone.HoldNext(OnCancel.Ignore, "unmount");

        var (suspend, pump, _) = BeginSuspend(h);
        await PreemptSpecHarness.ArrivedAsync(stuck, "the hung unmount");

        // The other two requests went out and completed although the first has not returned. Their
        // drives are gone from the rcd; the hung host's is not.
        await WaitUntilAsync(() => h.HRclone.Count("unmount") == 3, "the other two unmount requests being sent");
        var stuckDrive = stuck.Target!;
        foreach (var drive in Drives.Where(d => d != stuckDrive))
        {
            Assert.False(h.IsMountedInRclone(drive), $"{drive} should already be unmounted while {stuckDrive} hangs");
        }

        Assert.True(h.IsMountedInRclone(stuckDrive));
        Assert.True(stuck.IsPending);

        stuck.Release();
        await h.FinishAsync(pump, "the sweep never finished after the hung unmount returned", suspend);

        Assert.All(["a1", "b1", "c1"], key => Assert.Equal(MountState.Disabled, h.State(key)));
        Assert.All(Drives, drive => Assert.False(h.IsMountedInRclone(drive)));
    }

    /// <summary>
    /// <b>Protects:</b> the forced-unmount escalation and the arrival-counted budget (bs-7d7) under the
    /// worst case: every request times out after the 30 s call timeout. After that the whole budget has
    /// long gone, so every still-listed host escalates, once, and the forced requests are ALSO sent
    /// together; the drains then complete.
    /// <b>Catches:</b> no escalation after a timed-out request; an escalation counted from when the
    /// request phase ended rather than from arrival (it would not fire at all at +30 s for a 3 s budget
    /// if elapsed were taken from the wrong instant); a forced phase that is serial again.
    /// </summary>
    [Fact]
    public async Task With_every_unmount_timing_out_all_three_escalate_once_and_the_forced_requests_are_also_sent_together()
    {
        var h = await ThreePersistentMountedAsync();
        var first = Drives.Select(_ => h.HRclone.HoldNext(OnCancel.Ignore, "unmount")).ToList();
        var forced = Drives.Select(_ => h.HRclone.HoldNext(OnCancel.Ignore, "unmount")).ToList();

        var (suspend, pump, arrival) = BeginSuspend(h);
        foreach (var hold in first)
        {
            await PreemptSpecHarness.ArrivedAsync(hold, "a first-round unmount request");
        }

        h.FireTimers(RcloneClient.UnmountTimeout);
        first.ForEach(x => x.Fail(UnmountTimedOut()));

        foreach (var hold in forced)
        {
            await PreemptSpecHarness.ArrivedAsync(hold, "a forced unmount request that a serial escalation would not have sent yet");
        }

        Assert.Equal(6, h.HRclone.Count("unmount"));
        Assert.All(Drives, drive => Assert.Equal(2, h.HRclone.Count("unmount", drive)));
        Assert.All(Drives, drive => Assert.True(h.IsMountedInRclone(drive)));

        forced.ForEach(x => x.Release());
        await h.FinishAsync(pump, "the sweep never finished after the forced requests returned", suspend);

        foreach (var key in new[] { "a1", "b1", "c1" })
        {
            Assert.Equal(MountState.Disabled, h.State(key));
            var escalations = h.EscalationTimes(key);
            Assert.Single(escalations);
            Assert.True(escalations[0] >= arrival.AddSeconds(Budget), $"{key} escalated before arrival + budget");
        }

        Assert.All(Drives, drive => Assert.False(h.IsMountedInRclone(drive)));
        Assert.Empty(h.Errors);
    }

    /// <summary>
    /// <b>Protects:</b> "escalation at arrival plus budget, per host" when the rcd ANSWERS but the first
    /// unmount does not take effect. The first listmounts still lists all three, the budget has not run
    /// out, and each host's own timer escalates it at arrival + budget, not before and not on a coarser grid.
    /// <b>Catches:</b> escalating at once (budget ignored); dropping the per-host retry timers so nothing
    /// ever escalates; timers aimed at the wrong instant.
    /// </summary>
    [Fact]
    public async Task Hosts_the_first_verification_still_lists_escalate_at_arrival_plus_budget_each()
    {
        var h = await ThreePersistentMountedAsync();
        foreach (var drive in Drives)
        {
            // Neither the sweep's request (call 1) nor the per-host retry's own unmount (call 2) takes
            // effect; only the forced one (call 3) does. A retry step re-issues the unmount first.
            h.Rclone.ConfirmUnmountAtCall(drive, 3);
        }

        var arrival = h.Now;
        await h.SuspendAsync();

        Assert.All(Drives, drive => Assert.Equal(1, h.HRclone.Count("unmount", drive)));
        Assert.All(["a1", "b1", "c1"], key => Assert.Equal(MountState.Draining, h.State(key)));
        Assert.Empty(h.EscalationTimes("a1"));

        await h.AdvanceAsync(TimeSpan.FromSeconds(10), Step);

        foreach (var key in new[] { "a1", "b1", "c1" })
        {
            var escalations = h.EscalationTimes(key);
            Assert.Single(escalations);
            Assert.InRange(
                escalations[0], arrival.AddSeconds(Budget) - Step, arrival.AddSeconds(Budget) + Step);
            Assert.Equal(MountState.Disabled, h.State(key));
        }

        Assert.All(Drives, drive => Assert.Equal(3, h.HRclone.Count("unmount", drive)));
    }

    /// <summary>
    /// <b>Protects:</b> isolation between hosts. One host's unmount request failing outright (an error
    /// from rcd) neither stops the sweep nor the other two hosts' unmounts and drains; the failed host is
    /// still verified against listmounts (rule 4), stays <c>Draining</c>, and finishes through its own
    /// retry once the budget is up.
    /// <b>Catches:</b> an exception from one request escaping the sweep (the other hosts left
    /// <c>Draining</c>, no timers); a failed request treated as "gone".
    /// </summary>
    [Fact]
    public async Task A_failed_unmount_for_one_host_stops_neither_the_others_nor_its_own_eventual_drain()
    {
        var h = await ThreePersistentMountedAsync();
        var failing = h.HRclone.HoldNext(OnCancel.Ignore, "unmount");
        failing.Fail(new RcloneRcException("mount/unmount", 500, "rcd could not unmount"));

        await h.SuspendAsync();

        var failedDrive = failing.Target!;
        var failedKey = HostOf(failedDrive);
        Assert.Equal(3, h.HRclone.Count("unmount"));
        foreach (var drive in Drives.Where(d => d != failedDrive))
        {
            Assert.Equal(MountState.Disabled, h.State(HostOf(drive)));
            Assert.False(h.IsMountedInRclone(drive));
        }

        // Not believed gone because the call failed, and not believed gone because the others went.
        Assert.Equal(MountState.Draining, h.State(failedKey));
        Assert.True(h.IsMountedInRclone(failedDrive));

        await h.AdvanceAsync(TimeSpan.FromSeconds(10), Step);

        Assert.Equal(MountState.Disabled, h.State(failedKey));
        Assert.False(h.IsMountedInRclone(failedDrive));
        Assert.Empty(h.Errors);
    }

    /// <summary>
    /// <b>Protects:</b> ADR-022 decision 1 (a record that an on-demand drive was up) and its resume, with
    /// a sweep that has a stuck sibling. The record is set before the drain, not by it, so it must not
    /// depend on that host's own request having been issued early or late.
    /// <b>Catches:</b> the record set inside the (now phased) drain and lost; a sweep failure that
    /// skips the on-demand host.
    /// </summary>
    [Fact]
    public async Task An_on_demand_host_in_a_sweep_with_a_stuck_sibling_is_still_recorded_and_remounted_on_resume()
    {
        var h = NewHarness(
            HostFixtures.Persistent("a1", probeIntervalSeconds: 60, drive: "A:"),
            HostFixtures.OnDemand("od", drive: "Q:"),
            HostFixtures.Persistent("c1", probeIntervalSeconds: 60, drive: "C:"));
        await h.StartAsync();
        await h.RunAsync(() => h.Supervisor.RequestMountAsync("od"));
        Assert.Equal(MountState.Mounted, h.State("od"));

        var stuck = h.HRclone.HoldNext(OnCancel.Ignore, "unmount");
        var (suspend, pump, _) = BeginSuspend(h);
        await PreemptSpecHarness.ArrivedAsync(stuck, "the stuck unmount");
        await WaitUntilAsync(() => h.HRclone.Count("unmount") == 3, "the other two unmount requests being sent");

        h.FireTimers(RcloneClient.UnmountTimeout);
        stuck.Fail(UnmountTimedOut());
        await h.FinishAsync(pump, "the sweep never finished after the stuck unmount timed out", suspend);
        await h.AdvanceAsync(TimeSpan.FromSeconds(10), Step);
        Assert.All(["a1", "od", "c1"], key => Assert.Equal(MountState.Disabled, h.State(key)));

        await h.ResumeAsync();
        await h.AdvanceAsync(TimeSpan.FromSeconds(5), Step);

        Assert.True(h.IsMountedInRclone("Q:"), "the on-demand drive that was up before the sleep is remounted on resume");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        // Real-time guard only: the calls being waited for are issued by the supervisor without any
        // clock movement; this just yields until the pump's continuation has run.
        var deadline = DateTime.UtcNow + PreemptSpecHarness.Guard;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"setup: never reached: {what}");
            }

            await Task.Yield();
        }
    }
}
