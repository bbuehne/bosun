using Bosun.Probe;
using Bosun.Rclone;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor.Independent.RepairAll;

/// <summary>
/// Independent tests for <see cref="IMountSupervisor.RepairAllAsync"/> ("Unmount all &amp;
/// re-probe", bs-aoz), written from ADR-020's bs-aoz amendment, ADR-015, and
/// docs/ARCHITECTURE.md §4 -- not from the implementation or its own tests.
/// </summary>
/// <remarks>
/// <para>
/// The repair means "drop everything and start clean". It is not a user preference: it parks no
/// one (ADR-015), it un-parks no one, and every remount it leads to goes through
/// <c>Disabled -&gt; Probing -&gt; Ready -&gt; Mounting</c> after a probe that happened after the drain
/// (Invariant I1).
/// </para>
/// <para>
/// Every step of every test runs <see cref="RepairHarness.AssertInvariants"/>: Mounting only from
/// Ready, Mounted only to Draining, Draining only to Disabled and only after a listmounts that
/// confirmed the drive gone.
/// </para>
/// </remarks>
public sealed class RepairAllSpecTests
{
    // -- Spec 3: a persistent host goes the whole way round, and only after a fresh probe ----------

    /// <summary>
    /// Bug classes: a repair that remounts directly (skipping the probe), that leaves the host
    /// parked, or that takes a shortcut through the state machine (e.g. Draining -&gt; Ready).
    /// </summary>
    [Fact]
    public async Task A_mounted_persistent_host_drains_then_returns_through_Disabled_Probing_Ready_Mounting()
    {
        var harness = new RepairHarness(HostFixtures.Build(HostFixtures.Global(), HostFixtures.Persistent("prod", drive: "P:")));
        await harness.StartAsync();
        Assert.Equal(MountState.Mounted, harness.State("prod"));

        var mark = harness.History().Count;
        await harness.RepairAsync();
        Assert.True(await harness.AdvanceUntilAsync(() => harness.State("prod") == MountState.Mounted, maxSeconds: 60),
            $"the persistent host never came back after the repair.{Environment.NewLine}{harness.Dump()}");

        Assert.Equal(
            [
                (MountState.Mounted, MountState.Draining),
                (MountState.Draining, MountState.Disabled),
                (MountState.Disabled, MountState.Probing),
                (MountState.Probing, MountState.Ready),
                (MountState.Ready, MountState.Mounting),
                (MountState.Mounting, MountState.Mounted),
            ],
            harness.PathSince(mark, "prod"));
        Assert.False(harness.Snapshot("prod").UserParked, "a repair must never park a host (ADR-020 bs-aoz amendment)");
        Assert.Equal(2, harness.MountCalls("P:"));
    }

    /// <summary>
    /// Invariant I1 as an ordering claim: after the repair's unmount is confirmed by listmounts,
    /// there is a successful shallow probe, then a successful deep probe, and only then a
    /// <c>mount/mount</c>. Bug class: remounting on the strength of a probe result from before the
    /// drain (a stale "Ready").
    /// </summary>
    [Fact]
    public async Task The_remount_follows_a_shallow_then_deep_probe_that_happened_after_the_unmount_was_confirmed()
    {
        var harness = new RepairHarness(HostFixtures.Build(HostFixtures.Global(), HostFixtures.Persistent("prod", drive: "P:")));
        await harness.StartAsync();

        var since = harness.Log.LastSequence;
        await harness.RepairAsync();
        Assert.True(await harness.AdvanceUntilAsync(() => harness.State("prod") == MountState.Mounted, maxSeconds: 60),
            harness.Dump(since));

        var events = harness.Log.Since(since).ToList();
        var unmount = events.FindIndex(e => e.Kind == RepairEventLog.Unmount && e.Subject == "P:");
        Assert.True(unmount >= 0, $"the repair never called mount/unmount for P:.{Environment.NewLine}{harness.Dump(since)}");

        var confirm = events.FindIndex(unmount + 1, e =>
            e.Kind == RepairEventLog.ListMounts && e.Ok && !e.Listed!.Contains("P:", StringComparer.OrdinalIgnoreCase));
        Assert.True(confirm >= 0, $"no listmounts ever confirmed P: gone.{Environment.NewLine}{harness.Dump(since)}");

        var remount = events.FindIndex(confirm + 1, e => e.Kind == RepairEventLog.Mount && e.Subject == "P:");
        Assert.True(remount >= 0, $"P: was never remounted.{Environment.NewLine}{harness.Dump(since)}");
        Assert.DoesNotContain(events.Take(confirm), e => e.Kind == RepairEventLog.Mount && e.Subject == "P:");

        var window = events.Skip(confirm + 1).Take(remount - confirm - 1).ToList();
        var shallow = window.FindIndex(e => e.Kind == RepairEventLog.Shallow && e.Subject == "prod" && e.Ok);
        Assert.True(shallow >= 0, $"no successful shallow probe between the confirmed unmount and the remount.{Environment.NewLine}{harness.Dump(since)}");
        var deep = window.FindIndex(shallow + 1, e => e.Kind == RepairEventLog.Deep && e.Subject == "prod" && e.Ok);
        Assert.True(deep >= 0, $"no successful deep probe after that shallow probe and before the remount.{Environment.NewLine}{harness.Dump(since)}");
    }

    /// <summary>
    /// Bug class: the repair remounting on the host's pre-repair reachability. The host answered
    /// before the repair and stops answering at the moment of it; it must stay unmounted.
    /// </summary>
    [Fact]
    public async Task A_shallow_probe_failure_after_the_repair_leaves_the_persistent_host_unmounted()
    {
        var harness = new RepairHarness(HostFixtures.Build(HostFixtures.Global(), HostFixtures.Persistent("prod", drive: "P:")));
        await harness.StartAsync();
        Assert.Equal(1, harness.MountCalls("P:"));

        harness.Probe.SetShallow(RepairHarness.HostnameOf("prod"), ShallowProbeOutcome.Timeout);
        await harness.RepairAsync();
        await harness.StepSecondsAsync(600, () => Assert.NotEqual(MountState.Mounted, harness.State("prod")));

        Assert.Equal(1, harness.MountCalls("P:"));
        Assert.False(harness.Rclone.IsMounted("P:"));
        Assert.Equal(MountState.Unreachable, harness.State("prod"));
    }

    /// <summary>
    /// Same as above for the deep probe: TCP answers, the SFTP channel does not. §4 rule 2 routes
    /// a deep failure on entering Mounting to Draining, never to <c>mount/mount</c>. Bug class: a
    /// repair-path remount that skips the deep probe.
    /// </summary>
    [Fact]
    public async Task A_deep_probe_failure_after_the_repair_leaves_the_persistent_host_unmounted()
    {
        var harness = new RepairHarness(HostFixtures.Build(HostFixtures.Global(), HostFixtures.Persistent("prod", drive: "P:")));
        await harness.StartAsync();

        harness.Probe.SetDeep("prod", DeepProbeOutcome.Timeout);
        await harness.RepairAsync();
        await harness.StepSecondsAsync(300, () => Assert.NotEqual(MountState.Mounted, harness.State("prod")));

        Assert.Equal(1, harness.MountCalls("P:"));
        Assert.False(harness.Rclone.IsMounted("P:"));
    }

    // -- Spec 2: a repair parks no one and un-parks no one --------------------------------------------

    /// <summary>
    /// Bug class: implementing the repair as "user unmount every host", which parks every host
    /// (ADR-015) and leaves every persistent drive down after a click meant to fix them.
    /// </summary>
    [Fact]
    public async Task A_repair_parks_neither_a_persistent_nor_an_on_demand_host()
    {
        var harness = new RepairHarness(HostFixtures.Build(
            HostFixtures.Global(),
            HostFixtures.Persistent("prod", drive: "P:"),
            HostFixtures.OnDemand("archive", drive: "Q:")));
        await harness.StartAsync();
        await harness.RunAsync("mount archive", () => harness.Supervisor.RequestMountAsync("archive"));
        Assert.Equal(MountState.Mounted, harness.State("archive"));

        await harness.RepairAsync();
        await harness.StepSecondsAsync(60);

        Assert.False(harness.Snapshot("prod").UserParked, "persistent host was parked by a repair");
        Assert.False(harness.Snapshot("archive").UserParked, "on-demand host was parked by a repair");
        Assert.Equal(MountState.Mounted, harness.State("prod"));
    }

    /// <summary>
    /// ADR-020's bs-aoz amendment: "A host the user unmounted earlier ... stays parked (still
    /// probed)." Bug classes: a repair that clears every park (bringing back a drive the user took
    /// down on purpose), or one that skips parked hosts when it re-probes.
    /// </summary>
    [Fact]
    public async Task A_host_the_user_parked_before_the_repair_stays_parked_but_is_reprobed()
    {
        // A long idle interval so no scheduled probe can land in the window being measured.
        var harness = new RepairHarness(HostFixtures.Build(
            HostFixtures.Global(), HostFixtures.Persistent("prod", probeIntervalSeconds: 3600, drive: "P:")));
        await harness.StartAsync();
        await harness.RunAsync("user unmount", () => harness.Supervisor.RequestUnmountAsync("prod"));
        Assert.True(harness.Snapshot("prod").UserParked, "precondition: a user unmount parks the host (ADR-015)");
        Assert.NotEqual(MountState.Mounted, harness.State("prod"));

        await harness.AdvanceSecondsAsync(1);
        var probesBefore = harness.ShallowProbes("prod");

        await harness.RepairAsync();

        Assert.True(harness.ShallowProbes("prod") > probesBefore,
            $"a parked host was not re-probed by the repair.{Environment.NewLine}{harness.Dump()}");
        Assert.True(harness.Snapshot("prod").UserParked, "the repair un-parked a host the user parked");

        await harness.StepSecondsAsync(1800, () => Assert.NotEqual(MountState.Mounted, harness.State("prod")));
        Assert.Equal(1, harness.MountCalls("P:"));
        Assert.True(harness.Snapshot("prod").UserParked);
    }

    // -- Spec 4: an on-demand host is unmounted and rests in Ready -----------------------------------

    /// <summary>
    /// §4 rule 6 plus the amendment. Bug classes: an on-demand host remounted by the repair (it is
    /// on-demand: only the user mounts it), or left somewhere other than Ready, or left in a state
    /// a later explicit mount cannot use.
    /// </summary>
    [Fact]
    public async Task A_mounted_on_demand_host_is_unmounted_and_rests_in_Ready_until_requested()
    {
        var harness = new RepairHarness(HostFixtures.Build(HostFixtures.Global(), HostFixtures.OnDemand("archive", drive: "Q:")));
        await harness.StartAsync();
        await harness.RunAsync("mount archive", () => harness.Supervisor.RequestMountAsync("archive"));
        Assert.Equal(MountState.Mounted, harness.State("archive"));

        await harness.RepairAsync();
        await harness.StepSecondsAsync(60);

        Assert.Equal(MountState.Ready, harness.State("archive"));
        Assert.False(harness.Rclone.IsMounted("Q:"));

        await harness.AdvanceAsync(TimeSpan.FromHours(6));
        Assert.Equal(MountState.Ready, harness.State("archive"));
        Assert.Equal(1, harness.MountCalls("Q:"));

        await harness.RunAsync("mount archive again", () => harness.Supervisor.RequestMountAsync("archive"));
        Assert.Equal(MountState.Mounted, harness.State("archive"));
        Assert.Equal(2, harness.MountCalls("Q:"));
    }

    // -- Spec 5: every other enabled host is re-probed at once, backoff reset ------------------------

    /// <summary>
    /// Bug classes: a repair that ignores hosts that are not mounted, or that probes them but
    /// leaves the backoff ladder where it was (so the next retry is still minutes away).
    /// </summary>
    [Fact]
    public async Task An_unreachable_persistent_host_is_reprobed_at_once_and_its_backoff_starts_again_at_the_first_rung()
    {
        var harness = new RepairHarness(HostFixtures.Build(
            HostFixtures.Global(backoffSeconds: [5, 15, 30, 60, 300]),
            HostFixtures.Persistent("prod", drive: "P:")));
        harness.Probe.SetShallow(RepairHarness.HostnameOf("prod"), ShallowProbeOutcome.Timeout);
        await harness.StartAsync();
        Assert.Equal(MountState.Unreachable, harness.State("prod"));

        // Climb the ladder: after 5+15+30+60 the host is on the 300s rung.
        await harness.AdvanceSecondsAsync(5 + 15 + 30 + 60 + 1);
        var probesBefore = harness.ShallowProbes("prod");

        await harness.RepairAsync();
        Assert.Equal(probesBefore + 1, harness.ShallowProbes("prod"));

        // Still failing: the next retry is on the first rung (5s), not the 300s rung it was on.
        await harness.AdvanceSecondsAsync(5);
        Assert.Equal(probesBefore + 2, harness.ShallowProbes("prod"));
    }

    /// <summary>
    /// The amendment: "Every other enabled host is re-probed at once". An on-demand host in
    /// Unreachable is not polled (ADR-014), but the repair click is a user action, which is
    /// exactly what ADR-014 says wakes it. The second half guards ADR-008/ADR-014 in the other
    /// direction: the repair is one probe, not the start of a polling schedule.
    /// </summary>
    [Fact]
    public async Task An_unreachable_on_demand_host_is_reprobed_once_and_is_not_polled_afterwards()
    {
        var harness = new RepairHarness(HostFixtures.Build(HostFixtures.Global(), HostFixtures.OnDemand("archive", drive: "Q:")));
        harness.Probe.SetShallow(RepairHarness.HostnameOf("archive"), ShallowProbeOutcome.Timeout);
        await harness.StartAsync();
        await harness.AdvanceAsync(TimeSpan.FromHours(1));
        Assert.Equal(MountState.Unreachable, harness.State("archive"));
        var probesBefore = harness.ShallowProbes("archive");

        await harness.RepairAsync();
        Assert.Equal(probesBefore + 1, harness.ShallowProbes("archive"));

        await harness.AdvanceAsync(TimeSpan.FromHours(1));
        await harness.AdvanceAsync(TimeSpan.FromHours(1));
        Assert.Equal(probesBefore + 1, harness.ShallowProbes("archive"));
        Assert.Equal(0, harness.MountCalls("Q:"));
    }

    /// <summary>
    /// The amendment: "one waiting out a failed-mount pacing timer gets a fresh attempt from the
    /// first rung". Bug classes: the repair leaving a mount-failure host on its slow rung, or
    /// giving it a fresh attempt without a fresh probe (I1).
    /// </summary>
    [Fact]
    public async Task A_host_paced_after_failed_mounts_gets_a_fresh_probed_attempt_on_the_first_rung()
    {
        var harness = new RepairHarness(HostFixtures.Build(
            HostFixtures.Global(backoffSeconds: [5, 15, 30, 60, 300]),
            HostFixtures.Persistent("prod", drive: "P:")));
        harness.Rclone.FailMount("P:", () => new RcloneRcException("mount/mount", 500, "simulated: drive letter in use"), times: 5);
        await harness.StartAsync();

        // Let three attempts fail; the ladder is then well past its first rung.
        Assert.True(await harness.AdvanceUntilAsync(() => harness.MountCalls("P:") >= 3, maxSeconds: 120), harness.Dump());
        await harness.AdvanceSecondsAsync(1);
        var attemptsBefore = harness.MountCalls("P:");
        var since = harness.Log.LastSequence;

        await harness.RepairAsync();
        Assert.True(await harness.AdvanceUntilAsync(() => harness.MountCalls("P:") > attemptsBefore, maxSeconds: 5),
            $"no fresh mount attempt within the first rung (5s) after the repair.{Environment.NewLine}{harness.Dump(since)}");

        var events = harness.Log.Since(since).ToList();
        var attempt = events.FindIndex(e => e.Kind == RepairEventLog.Mount && e.Subject == "P:");
        var before = events.Take(attempt).ToList();
        Assert.Contains(before, e => e.Kind == RepairEventLog.Shallow && e.Subject == "prod" && e.Ok);
        Assert.Contains(before, e => e.Kind == RepairEventLog.Deep && e.Subject == "prod" && e.Ok);

        // That attempt failed too (budget 5); its retry also sits on the first rung.
        var afterFirst = harness.MountCalls("P:");
        Assert.True(await harness.AdvanceUntilAsync(() => harness.MountCalls("P:") > afterFirst, maxSeconds: 5),
            $"after the repair, the next failure was not paced from the first rung.{Environment.NewLine}{harness.Dump(since)}");
    }

    // -- Spec 1: Disabled only on confirmation; unconfirmed means Draining and no remount ------------

    /// <summary>
    /// Bug class: a repair drain that trusts <c>mount/unmount</c>'s return, or that remounts a
    /// letter whose unmount was never confirmed. The call returns normally every time; the drive
    /// never goes.
    /// </summary>
    [Fact]
    public async Task A_repair_drain_whose_unmount_never_takes_effect_stays_Draining_and_is_never_remounted()
    {
        var harness = new RepairHarness(HostFixtures.Build(HostFixtures.Global(), HostFixtures.Persistent("prod", drive: "P:")));
        await harness.StartAsync();
        harness.Rclone.IgnoreUnmounts("P:", int.MaxValue);
        var mark = harness.History().Count;

        await harness.RepairAsync();
        await harness.StepSecondsAsync(600, () => Assert.Equal(MountState.Draining, harness.State("prod")));

        Assert.Equal(1, harness.MountCalls("P:"));
        Assert.True(harness.Rclone.IsMounted("P:"));
        Assert.DoesNotContain(harness.HistorySince(mark), t => t.HostKey == "prod" && t.To == MountState.Disabled);
    }

    /// <summary>
    /// The same requirement for a drive that does go, but late: the unmount takes effect only on
    /// the third call. The host must reach Disabled only after a listmounts that no longer lists
    /// it, and must then come back. Bug class: giving up and declaring the drive gone on a timer.
    /// </summary>
    [Fact]
    public async Task A_late_confirming_unmount_reaches_Disabled_only_after_listmounts_confirms_and_then_remounts()
    {
        var harness = new RepairHarness(HostFixtures.Build(HostFixtures.Global(), HostFixtures.Persistent("prod", drive: "P:")));
        await harness.StartAsync();
        harness.Rclone.IgnoreUnmounts("P:", 2);

        await harness.RepairAsync();
        Assert.True(await harness.AdvanceUntilAsync(() => harness.State("prod") == MountState.Mounted, maxSeconds: 120), harness.Dump());

        // AssertInvariants (run every step) already proved the Disabled was confirmed; this pins
        // that the drain really did have to retry.
        Assert.True(harness.Log.Events.Count(e => e.Kind == RepairEventLog.Unmount && e.Subject == "P:") >= 3);
        Assert.Equal(2, harness.MountCalls("P:"));
    }

    // -- Spec 8: no host re-enabled until every drain has completed ----------------------------------

    /// <summary>
    /// The amendment: "All mounted hosts drain before any host is re-enabled". Every unmount
    /// confirms at once here, so both readings of that sentence (see the skipped test below)
    /// agree: no host may leave Disabled until the last of the three has reached it. Bug class:
    /// per-host re-enable, which remounts the first host while the repair has not yet even
    /// unmounted the others.
    /// </summary>
    [Fact]
    public async Task With_several_mounted_hosts_none_is_reenabled_until_every_drain_has_completed()
    {
        var harness = new RepairHarness(HostFixtures.Build(
            HostFixtures.Global(),
            HostFixtures.Persistent("alpha", drive: "P:"),
            HostFixtures.Persistent("bravo", drive: "R:"),
            HostFixtures.OnDemand("charlie", drive: "Q:")));
        await harness.StartAsync();
        await harness.RunAsync("mount charlie", () => harness.Supervisor.RequestMountAsync("charlie"));
        var mark = harness.History().Count;

        await harness.RepairAsync();
        Assert.True(
            await harness.AdvanceUntilAsync(
                () => harness.State("alpha") == MountState.Mounted && harness.State("bravo") == MountState.Mounted,
                maxSeconds: 60),
            harness.Dump());

        var since = harness.HistorySince(mark).ToList();
        var lastDrainDone = since.FindLastIndex(t => t.From == MountState.Draining && t.To == MountState.Disabled);
        var firstReEnable = since.FindIndex(t => t.From == MountState.Disabled);
        Assert.True(lastDrainDone >= 0 && firstReEnable >= 0, harness.Dump());
        Assert.True(
            firstReEnable > lastDrainDone,
            $"a host was re-enabled (transition {mark + firstReEnable}) before every drain had completed " +
            $"(last Draining -> Disabled at {mark + lastDrainDone}).{Environment.NewLine}{harness.Dump()}");
        Assert.Equal(3, since.Count(t => t.From == MountState.Draining && t.To == MountState.Disabled));
        Assert.Equal(MountState.Ready, harness.State("charlie"));
    }

    /// <summary>
    /// A drain that does not confirm on its first attempt: one drive takes three unmount calls
    /// (~10 s, through the forced-unmount escalation) to go. The hosts whose unmounts confirmed at
    /// once must come back without waiting for it.
    /// </summary>
    /// <remarks>
    /// <b>Decided (ADR-020 bs-aoz amendment, corrected 2026-10-01):</b> the barrier is the end of the
    /// repair's first drain pass, NOT the completion of every drain. The earlier ADR wording, read
    /// literally, would let one drive whose unmount never confirms keep every other host down for
    /// good. So a slow drain must NOT hold back hosts whose unmounts already confirmed, and the slow
    /// host must still come back on its own -- through Disabled and a fresh probe -- once rclone
    /// confirms it.
    /// </remarks>
    [Fact]
    public async Task A_slow_drain_does_not_hold_back_hosts_whose_unmounts_already_confirmed()
    {
        var harness = new RepairHarness(HostFixtures.Build(
            HostFixtures.Global(),
            HostFixtures.Persistent("alpha", drive: "P:"),
            HostFixtures.Persistent("bravo", drive: "R:"),
            HostFixtures.OnDemand("charlie", drive: "Q:")));
        await harness.StartAsync();
        await harness.RunAsync("mount charlie", () => harness.Supervisor.RequestMountAsync("charlie"));
        harness.Rclone.IgnoreUnmounts("R:", 2);
        var mark = harness.History().Count;

        await harness.RepairAsync();
        Assert.True(
            await harness.AdvanceUntilAsync(
                () => harness.State("alpha") == MountState.Mounted && harness.State("bravo") == MountState.Mounted,
                maxSeconds: 120),
            harness.Dump());

        var since = harness.HistorySince(mark).ToList();
        var alphaBackUp = since.FindIndex(t => t.HostKey == "alpha" && t.To == MountState.Mounted);
        var bravoConfirmed = since.FindIndex(t => t.HostKey == "bravo" && t.From == MountState.Draining && t.To == MountState.Disabled);
        var bravoReEnabled = since.FindIndex(t => t.HostKey == "bravo" && t.From == MountState.Disabled);
        Assert.True(alphaBackUp >= 0 && bravoConfirmed >= 0 && bravoReEnabled >= 0, harness.Dump());
        Assert.True(
            alphaBackUp < bravoConfirmed,
            $"alpha was held back until bravo's slow drain confirmed.{Environment.NewLine}{harness.Dump()}");
        Assert.True(bravoReEnabled > bravoConfirmed, harness.Dump());
        Assert.Equal(3, since.Count(t => t.From == MountState.Draining && t.To == MountState.Disabled));
        Assert.Equal(MountState.Ready, harness.State("charlie"));
    }

    // -- Spec 6: refused while suspended --------------------------------------------------------------

    /// <summary>
    /// Invariant I8 in reverse: no probe and no mount in the moments around a suspend. Bug class:
    /// a repair click (tray menu, during the lid-close) that re-enables hosts while suspended.
    /// </summary>
    [Fact]
    public async Task A_repair_while_suspended_has_no_effect()
    {
        var harness = new RepairHarness(HostFixtures.Build(
            HostFixtures.Global(),
            HostFixtures.Persistent("prod", drive: "P:"),
            HostFixtures.OnDemand("archive", drive: "Q:")));
        await harness.StartAsync();
        await harness.RunAsync("suspend", () => harness.Supervisor.SuspendAsync());
        Assert.True(await harness.AdvanceUntilAsync(() => harness.State("prod") == MountState.Disabled, maxSeconds: 30), harness.Dump());
        var statesBefore = harness.Supervisor.GetSnapshot().ToDictionary(h => h.HostKey, h => h.State);
        var since = harness.Log.LastSequence;

        var task = harness.Supervisor.RepairAllAsync();
        await harness.Supervisor.DrainAsync();
        Assert.True(task.IsCompleted, "a refused repair must still complete");
        var refusal = await Record.ExceptionAsync(() => task);
        Assert.True(refusal is null or MountRequestRefusedException,
            $"a refused repair must either return or throw the refusal type, not {refusal?.GetType().Name}: {refusal?.Message}");

        await harness.AdvanceAsync(TimeSpan.FromMinutes(30));

        Assert.DoesNotContain(harness.Log.Since(since), e => e.Kind is RepairEventLog.Shallow or RepairEventLog.Deep or RepairEventLog.Mount);
        Assert.Equal(statesBefore, harness.Supervisor.GetSnapshot().ToDictionary(h => h.HostKey, h => h.State));

        // Not vacuous: resume does bring the persistent host back.
        await harness.RunAsync("resume", () => harness.Supervisor.ResumeAsync());
        Assert.True(await harness.AdvanceUntilAsync(() => harness.State("prod") == MountState.Mounted, maxSeconds: 30), harness.Dump());
    }

    // -- Spec 9: a failing rc call during a repair is an ordinary drain failure ----------------------

    public static TheoryData<string, string> RcFailures => new()
    {
        { "mount/unmount", "RcloneRcException" },
        { "mount/unmount", "RcloneRcTimeoutException" },
        { "mount/listmounts", "RcloneRcException" },
        { "mount/listmounts", "RcloneRcTimeoutException" },
        { "mount/mount", "RcloneRcException" },
        { "mount/mount", "RcloneRcTimeoutException" },
    };

    /// <summary>
    /// Bug classes: an rc failure inside the repair escaping to the caller or killing the loop
    /// (bs-x57's class of bug, on a new code path), or a failed unmount/listmounts being read as
    /// "drive gone". Each failure is bounded; once rc behaves the host must end Mounted again,
    /// which proves the loop kept running. RunAsync/AdvanceAsync fail the test if anything
    /// escapes the pump.
    /// </summary>
    [Theory]
    [MemberData(nameof(RcFailures))]
    public async Task A_failing_rc_call_during_the_repair_is_handled_and_the_host_recovers(string operation, string exceptionType)
    {
        var harness = new RepairHarness(HostFixtures.Build(HostFixtures.Global(), HostFixtures.Persistent("prod", drive: "P:")));
        await harness.StartAsync();

        Func<Exception> fault = exceptionType == "RcloneRcTimeoutException"
            ? () => new RcloneRcTimeoutException(operation, TimeSpan.FromSeconds(30))
            : () => new RcloneRcException(operation, 500, $"simulated {operation} failure");
        switch (operation)
        {
            case "mount/unmount":
                harness.Rclone.FailUnmount("P:", fault, times: 2);
                break;
            case "mount/listmounts":
                harness.Rclone.FailListMounts(fault, times: 2);
                break;
            default:
                harness.Rclone.FailMount("P:", fault, times: 2);
                break;
        }

        await harness.RepairAsync(); // fails the test if the command throws or the pump dies

        if (operation != "mount/mount")
        {
            Assert.Equal(MountState.Draining, harness.State("prod"));
            Assert.True(harness.Rclone.IsMounted("P:") || operation == "mount/listmounts");
        }

        Assert.True(await harness.AdvanceUntilAsync(() => harness.State("prod") == MountState.Mounted, maxSeconds: 180),
            $"the host never recovered after a bounded {operation} failure.{Environment.NewLine}{harness.Dump()}");
        Assert.True(harness.Rclone.IsMounted("P:"));
        Assert.False(harness.Snapshot("prod").UserParked);
    }
}
