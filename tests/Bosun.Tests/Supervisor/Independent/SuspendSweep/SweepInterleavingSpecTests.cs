using Bosun.Configuration;
using Bosun.Probe;
using Bosun.Rclone;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Independent.SuspendPreempt;
using Bosun.Tests.Supervisor.Support;
using static Bosun.Tests.Supervisor.Independent.SuspendSweep.SweepSpec;

namespace Bosun.Tests.Supervisor.Independent.SuspendSweep;

/// <summary>
/// bs-5o4 against what else can be going on: every kind of host in one suspend, and the commands that can
/// arrive while the sweep's rc calls are still outstanding (a resume, a second suspend, a config reload, a
/// user's click), plus many calls completing at once on different threads. Written from ADR-023's amendment,
/// docs/ARCHITECTURE.md §4 rules 5, 9 and 9a and §5, not from the implementation.
/// </summary>
/// <remarks>
/// A command that arrives mid-sweep is called on the test thread while the pump is blocked on a held rc call:
/// production's shape (the channel's consumer waiting on I/O, the event arriving on another thread).
/// </remarks>
public sealed class SweepInterleavingSpecTests
{
    private static RcloneRcTimeoutException Timeout() => new("mount/unmount", TimeSpan.FromSeconds(30));

    /// <summary>
    /// <b>Protects:</b> rule 5 and the amendment's scope with every kind of host in one suspend:
    /// two persistent <c>Mounted</c> hosts and a user-mounted on-demand host (swept: one request each at
    /// arrival, one suspend drain each, the on-demand one gets the ADR-022 record); an on-demand host whose
    /// mount was in flight (D1: abandoned on <c>Mounting -> Draining</c>, no <c>mount/mount</c>, keeps the
    /// record); and a host already <c>Draining</c> from a user unmount (not swept: keeps its cause and stays
    /// parked, but its unmount is re-issued at once and it escalates at arrival + budget, bs-u4a item 4).
    /// <b>Catches:</b> the sweep picking up the already-Draining host (a second Draining entry with the Suspend
    /// cause, which un-parks it so resume remounts a drive the user unmounted); a lost or doubled request for
    /// one kind of host when kinds are mixed; the record lost for the swept or the D1 host.
    /// </summary>
    [Fact]
    public async Task One_suspend_over_every_kind_of_host_treats_each_by_its_own_rule()
    {
        var h = New(
            Persistent("alpha", "M:"),
            Persistent("bravo", "N:"),
            OnDemand("oscar", "Q:"),
            OnDemand("yankee", "Y:"),
            OnDemand("zulu", "Z:"));
        await h.StartAsync();
        await h.RunAsync(() => h.Supervisor.RequestMountAsync("oscar"));
        await h.RunAsync(() => h.Supervisor.RequestMountAsync("yankee"));
        h.Rclone.ConfirmUnmountAtCall("Y:", int.MaxValue);
        await h.RunAsync(() => h.Supervisor.RequestUnmountAsync("yankee"));
        Assert.Equal(MountState.Draining, h.State("yankee"));
        await h.AdvanceAsync(TimeSpan.FromSeconds(1), Step);

        var deep = h.HProbe.HoldNextDeep("zulu", OnCancel.Ignore);
        var click = h.Supervisor.RequestMountAsync("zulu");
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(deep, "zulu's deep probe entering Mounting");
        Assert.Equal(MountState.Mounting, h.State("zulu"));

        string[] swept = ["alpha", "bravo", "oscar"];
        var before = swept.ToDictionary(k => k, k => h.Transitions(k).Count);
        var arrival = h.Now;
        var suspend = SuspendCaller.Call(h.Supervisor);
        deep.Release(DeepProbeOutcome.Success);
        await h.FinishAsync(pump, "zulu's mount command or the suspend never finished", click, suspend);

        // The sweep: one request each, at arrival; one suspend drain each, then Disabled.
        Assert.Equal(1, UnmountsAt(h, "M:", arrival));
        Assert.Equal(1, UnmountsAt(h, "N:", arrival));
        Assert.Equal(1, UnmountsAt(h, "Q:", arrival));
        foreach (var key in swept)
        {
            AssertOneSuspendDrainThenDisabled(h, key, before[key]);
        }

        // D1: abandoned, never sent.
        Assert.Equal(0, h.MountCalls("Z:"));
        var zuluDrains = h.Transitions("zulu").Where(t => t.To == MountState.Draining).ToList();
        Assert.Single(zuluDrains);
        Assert.Equal((MountState.Mounting, DrainCause.Suspend), (zuluDrains[0].From, zuluDrains[0].DrainCause!.Value));

        // Already Draining: cause kept, re-issued at once, escalated on the suspend budget.
        Assert.Equal([DrainCause.UserUnmount], h.DrainCauses("yankee"));
        Assert.Equal(1, UnmountsAt(h, "Y:", arrival));
        await h.AdvanceAsync(TimeSpan.FromSeconds(Budget + 1), Step);
        var yankeeEscalations = h.EscalationTimes("yankee");
        Assert.Single(yankeeEscalations);
        Assert.InRange(yankeeEscalations[0], arrival.AddSeconds(Budget) - Step, arrival.AddSeconds(Budget) + Step);
        Assert.Equal([DrainCause.UserUnmount], h.DrainCauses("yankee"));

        // Let yankee's drive go and its drain finish, then wake up.
        h.Rclone.DropMountSilently("Y:");
        await h.AdvanceAsync(TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(100));
        await h.ResumeAsync();
        await h.AdvanceAsync(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(100));

        Assert.Equal(MountState.Mounted, h.State("alpha"));
        Assert.Equal(MountState.Mounted, h.State("bravo"));
        Assert.Equal(MountState.Mounted, h.State("oscar")); // ADR-022 record from the sweep
        Assert.Equal(2, h.MountCalls("Q:"));
        Assert.Equal(MountState.Mounted, h.State("zulu")); // ADR-022 record from D1
        Assert.Equal(1, h.MountCalls("Z:"));
        Assert.NotEqual(MountState.Mounted, h.State("yankee")); // still parked
        Assert.Equal(1, h.MountCalls("Y:"));
        AssertChainsAreConsistent(h, ["alpha", "bravo", "oscar", "yankee", "zulu"]);
        Assert.Empty(LoopFaults(h));
    }

    /// <summary>
    /// <b>Protects:</b> a resume that arrives while the sweep's <c>mount/unmount</c> calls are still
    /// outstanding runs after the sweep, not into it: the channel is FIFO (ADR-023's rejected "priority lane").
    /// Every host's log reads one suspend drain, <c>Draining -> Disabled</c>, then the resume's re-probe and
    /// remount -- and no drive was unmounted twice.
    /// <b>Catches:</b> a sweep that yields the channel between its request and verification phases, so the
    /// resume re-enables or remounts hosts whose drain the sweep then completes (a host Mounted with a drain
    /// still in progress, or Disabled after resume with nothing to bring it back).
    /// </summary>
    [Fact]
    public async Task A_resume_during_outstanding_sweep_requests_runs_after_the_sweep_and_remounts_everything()
    {
        var h = New(Persistent("alpha", "M:"), Persistent("bravo", "N:"));
        await h.StartAsync();
        var before = new[] { "alpha", "bravo" }.ToDictionary(k => k, k => h.Transitions(k).Count);
        var held = h.HRclone.HoldNextAt(OnCancel.Ignore, "unmount", "N:");

        var suspend = SuspendCaller.Call(h.Supervisor);
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(held, "bravo's unmount");
        var resume = h.Supervisor.ResumeAsync();
        held.Release();
        await h.FinishAsync(pump, "the sweep or the resume never finished", suspend, resume);
        await h.AdvanceAsync(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(100));

        foreach (var (key, drive) in new[] { ("alpha", "M:"), ("bravo", "N:") })
        {
            AssertOneSuspendDrainThenDisabled(h, key, before[key]);
            Assert.Equal(MountState.Mounted, h.State(key));
            Assert.Equal(2, h.MountCalls(drive));
            Assert.Equal(1, h.HRclone.Count("unmount", drive));
        }

        AssertChainsAreConsistent(h, ["alpha", "bravo"]);
    }

    /// <summary>
    /// <b>Protects:</b> I2 across a resume: rule 5's "On resume, the supervisor does not assume suspend
    /// succeeded". bravo's sweep request fails (30 s timeout) and its drive is still listed when the resume
    /// runs. bravo must finish its drain (<c>Draining -> Disabled</c>, which needs the table to stop listing
    /// it) before it is mounted again.
    /// <b>Catches:</b> a resume that re-enables or remounts a host still Draining from the sweep -- a
    /// <c>mount/mount</c> onto a letter rclone still serves, or a host whose drain is silently abandoned.
    /// </summary>
    [Fact]
    public async Task A_host_whose_sweep_request_failed_finishes_its_drain_before_the_resume_remounts_it()
    {
        var h = New(Persistent("alpha", "M:"), Persistent("bravo", "N:"));
        await h.StartAsync();
        var before = h.Transitions("bravo").Count;
        h.Rclone.ConfirmUnmountAtCall("N:", 2); // the timed-out call has no effect; the next one does
        var held = h.HRclone.HoldNextAt(OnCancel.Ignore, "unmount", "N:");

        var suspend = SuspendCaller.Call(h.Supervisor);
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(held, "bravo's unmount");
        var resume = h.Supervisor.ResumeAsync();
        held.Abandon(Timeout());
        held.FinishInRcd(); // rcd carries on and finishes; the call had no effect on the table (call 1 of 2)
        await h.FinishAsync(pump, "the sweep or the resume never finished", suspend, resume);
        Assert.NotEqual(MountState.Mounted, h.State("bravo"));

        await h.AdvanceAsync(TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(100));

        AssertOneSuspendDrainThenDisabled(h, "bravo", before);
        Assert.Equal(MountState.Mounted, h.State("bravo"));
        Assert.Equal(2, h.MountCalls("N:"));
        AssertChainsAreConsistent(h, ["alpha", "bravo"]);
    }

    /// <summary>
    /// <b>Protects:</b> a second suspend that arrives during the sweep: no host is drained twice, no request is
    /// lost, and "a second suspend never moves a deadline later" (rule 5) -- alpha, which never confirms,
    /// escalates at the FIRST arrival + budget. A single resume afterwards still resumes.
    /// <b>Catches:</b> the second suspend re-sweeping hosts the first moved to Draining (a second
    /// <c>Mounted -> Draining</c> entry, or a fresh deadline from the second arrival); the pending-suspend
    /// counter left raised so the resume cannot mount anything.
    /// </summary>
    [Fact]
    public async Task A_second_suspend_during_the_sweep_drains_nothing_twice_and_keeps_the_first_deadline()
    {
        var h = New(Persistent("alpha", "M:"), Persistent("bravo", "N:"));
        await h.StartAsync();
        var before = new[] { "alpha", "bravo" }.ToDictionary(k => k, k => h.Transitions(k).Count);
        h.Rclone.ConfirmUnmountAtCall("M:", int.MaxValue);
        var held = h.HRclone.HoldNextAt(OnCancel.Ignore, "unmount", "N:");

        var arrival = h.Now;
        var first = SuspendCaller.Call(h.Supervisor);
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(held, "bravo's unmount");
        h.FireTimers(TimeSpan.FromSeconds(1));
        var second = SuspendCaller.Call(h.Supervisor);
        held.Release();
        await h.FinishAsync(pump, "a suspend never finished", first, second);
        await h.AdvanceAsync(TimeSpan.FromSeconds(Budget + 2), Step);

        Assert.Equal(1, UnmountsAt(h, "M:", arrival));
        Assert.Equal(1, UnmountsAt(h, "N:", arrival));
        AssertOneSuspendDrainThenDisabled(h, "bravo", before["bravo"]);
        var alphaDrains = h.Transitions("alpha").Skip(before["alpha"]).Where(t => t.To == MountState.Draining).ToList();
        Assert.Single(alphaDrains);
        Assert.Equal(DrainCause.Suspend, alphaDrains[0].DrainCause);

        var escalations = h.EscalationTimes("alpha");
        Assert.Single(escalations);
        Assert.InRange(escalations[0], arrival.AddSeconds(Budget) - Step, arrival.AddSeconds(Budget) + Step);

        await h.ResumeAsync();
        await h.AdvanceAsync(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(100));
        Assert.Equal(MountState.Mounted, h.State("bravo"));
    }

    /// <summary>
    /// <b>Protects:</b> a host removed from the config while the sweep's requests are outstanding. The store's
    /// <c>Current</c> changes on another thread at once; the reload command runs after the sweep. The sweep
    /// finishes with the config it started with, the removed host is no longer supervised, and nothing mounts
    /// it on resume.
    /// <b>Catches:</b> a sweep that re-reads the host's config from the store between its phases and faults
    /// (or skips verification) when the host has vanished -- an Error in the loop and a sweep that never
    /// completes the other hosts.
    /// </summary>
    [Fact]
    public async Task A_host_removed_from_config_mid_sweep_is_drained_once_and_then_evicted()
    {
        var h = New(Persistent("alpha", "M:"), Persistent("bravo", "N:"));
        await h.StartAsync();
        var held = h.HRclone.HoldNextAt(OnCancel.Ignore, "unmount", "M:");

        var suspend = SuspendCaller.Call(h.Supervisor);
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(held, "alpha's unmount");
        var next = WithoutBravo();
        h.Store.Publish(next);
        var reload = h.Supervisor.ConfigChangedAsync(next);
        held.Release();
        await h.FinishAsync(pump, "the sweep or the reload never finished", suspend, reload);

        Assert.Empty(LoopFaults(h));
        Assert.Equal(MountState.Disabled, h.State("alpha"));
        Assert.False(h.IsMountedInRclone("N:"));
        Assert.Equal(1, h.HRclone.Count("unmount", "N:"));
        Assert.DoesNotContain(h.Supervisor.GetSnapshot(), s => s.HostKey == "bravo");

        await h.ResumeAsync();
        await h.AdvanceAsync(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(100));
        Assert.Equal(MountState.Mounted, h.State("alpha"));
        Assert.Equal(1, h.MountCalls("N:"));
    }

    /// <summary>
    /// <b>Protects:</b> I2 for the removed host whose sweep request FAILED: its drive is still listed when the
    /// reload removes it from the config. The drive must still be unmounted -- removal from the config is not
    /// evidence the drive is gone (bs-7ck: a removed host "drains exactly like Automatic", then is evicted).
    /// <b>Catches:</b> a removed, still-Draining host evicted (or its escalation skipped because its config is
    /// gone) while rclone still serves its drive: a drive letter nobody supervises any more, through the sleep.
    /// </summary>
    [Fact]
    public async Task A_removed_host_whose_sweep_request_failed_still_gets_its_drive_unmounted()
    {
        var h = New(Persistent("alpha", "M:"), Persistent("bravo", "N:"));
        await h.StartAsync();
        h.Rclone.ConfirmUnmountAtCall("N:", 2); // the timed-out call has no effect; the next one does
        var held = h.HRclone.HoldNextAt(OnCancel.Ignore, "unmount", "N:");

        var suspend = SuspendCaller.Call(h.Supervisor);
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(held, "bravo's unmount");
        var next = WithoutBravo();
        h.Store.Publish(next);
        var reload = h.Supervisor.ConfigChangedAsync(next);
        held.Abandon(Timeout());
        held.FinishInRcd(); // rcd carries on and finishes; the call had no effect on the table (call 1 of 2)
        await h.FinishAsync(pump, "the sweep or the reload never finished", suspend, reload);
        Assert.True(h.IsMountedInRclone("N:"), "setup: bravo's drive is still served after its failed request");

        await h.AdvanceAsync(TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(100));

        Assert.False(h.IsMountedInRclone("N:"), "bravo's drive was left mounted after bravo was removed mid-sweep");
        Assert.Empty(LoopFaults(h));
    }

    /// <summary>
    /// <b>Protects:</b> rule 9a, "Cleared by ... a user unmount (including a click while the suspend drain is
    /// still running ...)", when the click lands while the sweep's request for that host is outstanding.
    /// <b>Catches:</b> the click handled against stale state and lost (the record survives and resume remounts
    /// a drive the user just unmounted), or the click starting a second drain of the swept host.
    /// </summary>
    [Fact]
    public async Task A_user_unmount_click_during_the_sweep_clears_the_remount_record()
    {
        var h = New(Persistent("alpha", "M:"), OnDemand("oscar", "Q:"));
        await h.StartAsync();
        await h.RunAsync(() => h.Supervisor.RequestMountAsync("oscar"));
        var held = h.HRclone.HoldNextAt(OnCancel.Ignore, "unmount", "Q:");

        var suspend = SuspendCaller.Call(h.Supervisor);
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(held, "oscar's unmount");
        var click = h.Supervisor.RequestUnmountAsync("oscar");
        held.Release();
        await h.FinishAsync(pump, "the sweep or the click never finished", suspend, click);

        await h.ResumeAsync();
        await h.AdvanceAsync(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(100));

        Assert.NotEqual(MountState.Mounted, h.State("oscar"));
        Assert.Equal(1, h.MountCalls("Q:"));
        Assert.Equal(MountState.Mounted, h.State("alpha"));
    }

    /// <summary>
    /// <b>Protects:</b> §5 and the amendment's "Only the rc calls run concurrently; every state change and log
    /// line stays on the channel": eight requests that complete at the same moment on eight pool threads leave
    /// the supervisor's state exactly as if they had completed one by one. Repeated to give a race a chance.
    /// <b>Catches:</b> per-host continuations that write host state, the transition history or a shared
    /// collection off the channel: lost or duplicated transitions, a broken From/To chain, an exception in the
    /// loop, a host left Draining whose drive is gone, or a corrupted state that stops the resume remounting.
    /// </summary>
    [Fact]
    public async Task Requests_completing_together_on_many_threads_leave_consistent_state()
    {
        string[] drives = ["E:", "F:", "G:", "H:", "I:", "J:", "K:", "L:"];
        var keys = drives.Select((_, i) => $"host{i}").ToArray();

        for (var iteration = 0; iteration < 20; iteration++)
        {
            var h = New(keys.Zip(drives, (k, d) => Persistent(k, d)).ToArray());
            await h.StartAsync();
            var before = keys.ToDictionary(k => k, k => h.Transitions(k).Count);
            var holds = drives.ToDictionary(d => d, d => h.HRclone.HoldNextAt(OnCancel.Ignore, "unmount", d));
            var mark = Mark(h);

            var suspend = SuspendCaller.Call(h.Supervisor);
            var pump = h.StartPump();
            await AllArrivedAsync(holds, $"iteration {iteration}");

            // Dedicated threads, not pool tasks: eight pool tasks blocking on a barrier can wait on thread
            // injection, and the point is that the releases really do coincide.
            using (var start = new Barrier(holds.Count))
            {
                var threads = holds.Values.Select(hold => new Thread(() =>
                {
                    start.SignalAndWait();
                    hold.Release();
                })).ToList();
                threads.ForEach(t => t.Start());
                threads.ForEach(t => t.Join());
            }

            await h.FinishAsync(pump, $"iteration {iteration}: the sweep never finished", suspend);

            Assert.Empty(h.Errors);
            Assert.Equal(1, ListMountsSince(h, mark));
            foreach (var (key, drive) in keys.Zip(drives))
            {
                Assert.Equal(MountState.Disabled, h.State(key));
                Assert.Equal(1, h.HRclone.Count("unmount", drive));
                AssertOneSuspendDrainThenDisabled(h, key, before[key]);
            }

            AssertChainsAreConsistent(h, keys);

            await h.ResumeAsync();
            await h.AdvanceAsync(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(100));
            Assert.All(keys, k => Assert.Equal(MountState.Mounted, h.State(k)));
        }
    }

    private static BosunConfig WithoutBravo() =>
        HostFixtures.Build(
            HostFixtures.Global(failuresBeforeUnmount: 3, suspendUnmountTimeoutSeconds: Budget),
            Persistent("alpha", "M:"));
}
