using Bosun.Configuration;
using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;
using Xunit.Abstractions;
using static Bosun.Tests.Supervisor.Independent.RemountAfterResume.RemountSpec;

namespace Bosun.Tests.Supervisor.Independent.RemountAfterResume;

/// <summary>
/// ADR-022 Decision 5 / rule 9a "Cleared by", and the interleavings around resume: what clears the
/// record, what does not spend it, and what happens when a user click, a second suspend, an
/// unavailable mount backend or a silent <c>rclone rcd</c> lands in the window around the first
/// post-resume probe. Fakes and a fake clock only (see <see cref="RemountHarness"/>).
/// </summary>
public sealed class RemountIntentLifecycleTests(ITestOutputHelper output)
{
    // ------------------------------------------------------------------------------------------
    // Cleared by: the remount, a user unmount, a mode change, removal, a restart.
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Protects:</b> Decision 5, "cleared by ... the remount itself" and "a user unmount": the user
    /// unmounts right after the remount; the next sleep must not bring the drive back.
    /// <b>Catches:</b> a record that outlives the remount, re-spent by the next resume's in-place
    /// probe of the <c>Ready</c> host -- undoing an explicit unmount (ADR-015).
    /// </summary>
    [Fact]
    public async Task A_user_unmount_right_after_the_remount_is_not_undone_by_the_next_sleep()
    {
        var h = await UserMountedOnDemandAsync();
        await h.SuspendAsync();
        await h.ResumeAsync();
        Assert.Equal(MountState.Mounted, h.State(OnDemandKey));

        await h.UnmountAsync(OnDemandKey);
        Assert.Equal(MountState.Ready, h.State(OnDemandKey));

        await h.SuspendAsync();
        await h.ResumeAsync();
        await h.AdvanceAsync(TimeSpan.FromMinutes(5));

        Assert.Equal(MountState.Ready, h.State(OnDemandKey));
        Assert.Equal(2, h.Rclone.MountCount(OnDemandDrive));
    }

    /// <summary>
    /// <b>Protects:</b> Decision 5, "cleared by the remount itself", without any user action
    /// afterwards: a later failure drain's re-enable probe must not find a leftover record.
    /// <b>Catches:</b> a record cleared only by suspend/resume bookkeeping and not by the remount,
    /// which the probe-failure drain's re-enable would then spend -- remounting an on-demand host
    /// after a failure drain, against Decision 6 and rule 6.
    /// </summary>
    [Fact]
    public async Task The_remount_spends_the_intent_so_a_later_failure_drain_rests_in_Ready()
    {
        var h = await UserMountedOnDemandAsync();
        await h.SuspendAsync();
        await h.ResumeAsync();
        Assert.Equal(2, h.Rclone.MountCount(OnDemandDrive));

        h.Probe.SetShallow(OnDemandHostname, ShallowProbeOutcome.Timeout, times: 3);
        await h.AdvanceAsync(TimeSpan.FromMinutes(5));

        Assert.Contains(h.Transitions(OnDemandKey), t => t.From == MountState.Mounted && t.To == MountState.Draining);
        Assert.Equal(MountState.Ready, h.State(OnDemandKey));
        Assert.Equal(2, h.Rclone.MountCount(OnDemandDrive));
    }

    /// <summary>
    /// <b>Protects:</b> rule 9a "Cleared by ... a user unmount (including a click while the suspend
    /// drain is still running)".
    /// <b>Catches:</b> <c>RequestUnmountAsync</c> on a <c>Draining</c> host being a pure no-op that
    /// leaves the record in place, so the drive the user just asked to release comes back once the
    /// slow suspend drain confirms after resume.
    /// </summary>
    [Fact]
    public async Task A_user_unmount_click_during_the_suspend_drain_clears_the_intent()
    {
        var h = await UserMountedOnDemandAsync();
        h.Rclone.ConfirmUnmountAtCall(OnDemandDrive, 3);
        await h.SuspendAsync();
        Assert.Equal(MountState.Draining, h.State(OnDemandKey));

        await h.UnmountAsync(OnDemandKey);
        await h.ResumeAsync();
        await h.AdvanceAsync(TimeSpan.FromMinutes(2));
        Assert.Null(h.Rclone.FsAt(OnDemandDrive));
        await h.AdvanceAsync(TimeSpan.FromMinutes(10));

        Assert.NotEqual(MountState.Mounted, h.State(OnDemandKey));
        Assert.Equal(1, h.Rclone.MountCount(OnDemandDrive));
    }

    /// <summary>
    /// <b>Protects:</b> Decision 5, "its mode changed": the host goes to <c>mode = "none"</c> while
    /// the machine is suspended and comes back as on-demand after resume; its fresh
    /// <c>Disabled -&gt; Probing</c> must not spend a record from before the change.
    /// <b>Catches:</b> a record keyed by host key that survives a mode change.
    /// </summary>
    [Fact]
    public async Task A_mode_change_clears_the_intent()
    {
        var h = await UserMountedOnDemandAsync();
        await h.SuspendAsync();
        await h.ReloadAsync(HostFixtures.Build(HostFixtures.Global(), HostFixtures.None(OnDemandKey)));
        await h.ResumeAsync();
        Assert.Equal(1, h.Rclone.MountCount(OnDemandDrive));

        await h.ReloadAsync(OneOnDemand());
        await h.AdvanceAsync(TimeSpan.FromMinutes(5));

        Assert.Equal(MountState.Ready, h.State(OnDemandKey));
        Assert.Equal(1, h.Rclone.MountCount(OnDemandDrive));
    }

    /// <summary>
    /// <b>Protects:</b> Decision 5, "the host being removed from the config": removed while
    /// suspended, re-added (same key) after resume; its startup-style first probe must not mount.
    /// <b>Catches:</b> a record held outside the per-host runtime (e.g. a set of host keys) that
    /// survives eviction and is spent by the re-added host.
    /// </summary>
    [Fact]
    public async Task Removing_the_host_from_config_clears_the_intent()
    {
        var h = await UserMountedOnDemandAsync();
        await h.SuspendAsync();
        await h.ReloadAsync(HostFixtures.Build(HostFixtures.Global()));
        await h.ResumeAsync();

        await h.ReloadAsync(OneOnDemand());
        await h.AdvanceAsync(TimeSpan.FromMinutes(5));

        Assert.Equal(MountState.Ready, h.State(OnDemandKey));
        Assert.Equal(1, h.Rclone.MountCount(OnDemandDrive));
    }

    /// <summary>
    /// <b>Protects:</b> Decision 5, "a restart of Bosun. It is never persisted."
    /// <b>Catches:</b> a record stored statically or on disk, which a fresh supervisor instance
    /// (a restart) would then act on.
    /// </summary>
    [Fact]
    public async Task The_intent_does_not_survive_a_restart()
    {
        var before = await UserMountedOnDemandAsync();
        await before.SuspendAsync();

        var after = new RemountHarness(OneOnDemand());
        await after.StartAsync();
        await after.SuspendAsync();
        await after.ResumeAsync();
        await after.AdvanceAsync(TimeSpan.FromMinutes(5));

        Assert.Equal(MountState.Ready, after.State(OnDemandKey));
        Assert.Equal(0, after.Rclone.MountCount(OnDemandDrive));
    }

    /// <summary>
    /// <b>Protects:</b> rule 9a "a network change neither spends nor sets it", and bs-bv0's
    /// "the mount-retry timer ... does not probe in those seconds": events that arrive while the
    /// machine is suspended (a network change, retry-now, the mounting backend flapping, time
    /// passing) leave the record for the resume.
    /// <b>Catches:</b> a record spent by a probe that ran while suspended (and so was refused by the
    /// bs-bv0 gate), which would silently lose the user's mount.
    /// </summary>
    [Fact]
    public async Task Events_while_suspended_do_not_spend_the_intent()
    {
        var h = await UserMountedOnDemandAsync();
        await h.SuspendAsync();

        await h.RunAsync(() => h.Supervisor.NetworkChangedAsync());
        await h.RunAsync(() => h.Supervisor.RequestRetryNowAsync(OnDemandKey));
        await h.RunAsync(() => h.Supervisor.SetMountingAvailabilityAsync(
            MountingAvailability.Unavailable(MountingUnavailableCause.RcloneUnhealthy, "rcd restarting")));
        await h.RunAsync(() => h.Supervisor.SetMountingAvailabilityAsync(MountingAvailability.Available));
        await h.AdvanceAsync(TimeSpan.FromMinutes(30));
        Assert.Null(h.Rclone.FsAt(OnDemandDrive));

        await h.ResumeAsync();

        Assert.Equal(MountState.Mounted, h.State(OnDemandKey));
        Assert.Equal(2, h.Rclone.MountCount(OnDemandDrive));
    }

    /// <summary>
    /// <b>Protects:</b> Decision 5's list is exhaustive: a mount-affecting config change (here
    /// <c>remote_path</c>) is not on it, so a change saved while suspended keeps the record, and the
    /// remount uses the NEW config.
    /// <b>Catches:</b> a config reload that clears (or spends) the record as a side effect, and a
    /// remount that mounts with the stale pre-suspend config.
    /// </summary>
    [Fact]
    public async Task A_mount_affecting_config_change_while_suspended_keeps_the_intent_and_the_remount_uses_the_new_config()
    {
        var h = await UserMountedOnDemandAsync();
        await h.SuspendAsync();
        var changed = HostFixtures.OnDemand(OnDemandKey, drive: OnDemandDrive);
        changed = changed with { Mount = changed.Mount with { RemotePath = "/srv/elsewhere" } };
        await h.ReloadAsync(HostFixtures.Build(HostFixtures.Global(), changed));
        Assert.Null(h.Rclone.FsAt(OnDemandDrive));

        await h.ResumeAsync();

        Assert.Equal(MountState.Mounted, h.State(OnDemandKey));
        Assert.Contains("/srv/elsewhere", h.Rclone.MountRequests[^1].Fs, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------
    // Interleavings around the first post-resume probe.
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Protects:</b> I1 and "exactly as a user click would": a user's Mount click that arrives
    /// together with the resume (queued before the first probe has run) produces one mount, deep
    /// probe first, and the click is not refused.
    /// <b>Catches:</b> a double mount (remount plus click both calling <c>mount/mount</c>), a click
    /// that throws "refused" while the drive is coming up, or a click path that mounts a
    /// <c>Disabled</c> host without the probe.
    /// </summary>
    [Fact]
    public async Task A_user_mount_click_queued_with_the_resume_produces_one_mount()
    {
        var h = await UserMountedOnDemandAsync();
        await h.SuspendAsync();

        h.Log.Record("MARK|resume");
        var resumeMark = h.LastMarkIndex("MARK|resume");
        var resume = h.Supervisor.ResumeAsync();
        var click = h.Supervisor.RequestMountAsync(OnDemandKey);
        await h.Supervisor.DrainAsync();
        await resume;
        await click;
        await h.AdvanceAsync(TimeSpan.FromMinutes(1));

        Assert.Equal(MountState.Mounted, h.State(OnDemandKey));
        Assert.Equal(1, MountsAfter(h, OnDemandDrive, resumeMark));
        AssertEveryMountPrecededByPassingDeepProbe(h, OnDemandKey, OnDemandDrive, resumeMark);
    }

    /// <summary>
    /// <b>Protects:</b> I1 and the single-remount property when the user clicks Mount while the
    /// suspend drain is still running after resume (the late-drain window of rule 9a). A refusal of
    /// that click is tolerated (the host is <c>Draining</c>); a second mount is not.
    /// <b>Catches:</b> the click and the record both acting on the drain's completion, mounting
    /// twice, or the click clearing the record without itself mounting (drive lost).
    /// </summary>
    [Fact]
    public async Task A_user_mount_click_while_the_suspend_drain_is_still_running_produces_one_mount()
    {
        var h = await UserMountedOnDemandAsync();
        h.Rclone.ConfirmUnmountAtCall(OnDemandDrive, 2);
        await h.SuspendAsync();
        await h.ResumeAsync();
        var resumeMark = h.LastMarkIndex("MARK|resume");
        Assert.Equal(MountState.Draining, h.State(OnDemandKey));

        try
        {
            await h.MountAsync(OnDemandKey);
        }
        catch (MountRequestRefusedException ex)
        {
            output.WriteLine($"click while Draining was refused: {ex.Message}");
        }

        await h.AdvanceAsync(TimeSpan.FromMinutes(1));

        Assert.Equal(MountState.Mounted, h.State(OnDemandKey));
        Assert.Equal(1, MountsAfter(h, OnDemandDrive, resumeMark));
        AssertEveryMountPrecededByPassingDeepProbe(h, OnDemandKey, OnDemandDrive, resumeMark);
    }

    /// <summary>
    /// <b>Protects:</b> I8 / bs-bv0 across suspend -&gt; resume -&gt; suspend with the second suspend
    /// arriving while the first post-resume (shallow) probe is still in flight: whatever the remount
    /// did, nothing is mounted once the second suspend has run, and after the final resume the drive
    /// state agrees with rclone's mount table.
    /// <b>Catches:</b> a remount that lands after the second suspend handler ran (a drive that
    /// survives the sleep), or a supervisor that believes <c>Mounted</c> when nothing is mounted.
    /// </summary>
    [Fact]
    public async Task A_second_suspend_during_the_first_post_resume_probe_leaves_nothing_mounted_over_the_sleep()
    {
        var h = await UserMountedOnDemandAsync();
        await h.SuspendAsync();

        h.Hold.HoldNextShallow(OnDemandHostname);
        var resume = h.Supervisor.ResumeAsync();
        var pump = h.Supervisor.DrainAsync();
        Assert.True(h.Hold.IsHoldingShallow, "setup: the first post-resume probe should be in flight");
        Assert.Equal(MountState.Probing, h.State(OnDemandKey));

        var suspend2 = h.Supervisor.SuspendAsync();
        h.Hold.ReleaseShallow(ShallowProbeOutcome.Success);
        await pump;
        await h.Supervisor.DrainAsync();
        await resume;
        await suspend2;
        await h.AdvanceSecondsAsync(30);

        output.WriteLine($"after second suspend: {h.State(OnDemandKey)}; fs at Q: {h.Rclone.FsAt(OnDemandDrive) ?? "none"}");
        output.WriteLine(h.TransitionDump());
        Assert.Null(h.Rclone.FsAt(OnDemandDrive));
        Assert.True(
            h.State(OnDemandKey) is MountState.Disabled or MountState.Ready,
            $"after the second suspend the host is {h.State(OnDemandKey)}");

        await h.ResumeAsync();
        await h.AdvanceAsync(TimeSpan.FromMinutes(1));

        output.WriteLine($"after final resume: {h.State(OnDemandKey)}; mounts {h.Rclone.MountCount(OnDemandDrive)}");
        Assert.Equal(h.State(OnDemandKey) == MountState.Mounted, h.Rclone.FsAt(OnDemandDrive) is not null);
        AssertEveryMountPrecededByPassingDeepProbe(h, OnDemandKey, OnDemandDrive, 0);
    }

    /// <summary>
    /// <b>Protects:</b> Decision 1, "Mounted <i>or Mounting</i>", on the remount itself: a second
    /// suspend arrives while the remount's deep probe is in flight. The host was mounting at that
    /// suspend, so it is recorded again and comes back after the next resume.
    /// <b>Catches:</b> the remount clearing the record in a way that also stops the next suspend from
    /// re-recording it, and a mount that survives the second suspend.
    /// </summary>
    [Fact]
    public async Task A_second_suspend_during_the_remount_records_the_host_again()
    {
        var h = await UserMountedOnDemandAsync();
        await h.SuspendAsync();

        h.Hold.HoldNextDeep(OnDemandKey);
        var resume = h.Supervisor.ResumeAsync();
        var pump = h.Supervisor.DrainAsync();
        Assert.True(h.Hold.IsHoldingDeep, "setup: the remount's deep probe should be in flight");
        Assert.Equal(MountState.Mounting, h.State(OnDemandKey));

        var suspend2 = h.Supervisor.SuspendAsync();
        h.Hold.ReleaseDeep(DeepProbeOutcome.Success);
        await pump;
        await h.Supervisor.DrainAsync();
        await resume;
        await suspend2;
        await h.AdvanceSecondsAsync(30);
        Assert.Null(h.Rclone.FsAt(OnDemandDrive));
        Assert.Equal(MountState.Disabled, h.State(OnDemandKey));

        await h.ResumeAsync();

        Assert.Equal(MountState.Mounted, h.State(OnDemandKey));
        Assert.NotNull(h.Rclone.FsAt(OnDemandDrive));
    }

    /// <summary>
    /// <b>Protects:</b> Decision 6 and rule 9a ("a failed mount ... never sets the record"): the
    /// remount's deep probe FAILS while a second suspend is queued; the host is draining for a
    /// mount failure when that suspend runs, so it is not recorded and stays down after resume.
    /// <b>Catches:</b> a record set for a host that was <c>Draining</c> (for a mount failure) at
    /// suspend.
    /// </summary>
    [Fact]
    public async Task A_remount_that_fails_while_a_second_suspend_is_queued_is_not_recorded_again()
    {
        var h = await UserMountedOnDemandAsync();
        await h.SuspendAsync();

        h.Hold.HoldNextDeep(OnDemandKey);
        var resume = h.Supervisor.ResumeAsync();
        var pump = h.Supervisor.DrainAsync();
        Assert.True(h.Hold.IsHoldingDeep, "setup: the remount's deep probe should be in flight");

        var suspend2 = h.Supervisor.SuspendAsync();
        h.Hold.ReleaseDeep(DeepProbeOutcome.Failed);
        await pump;
        await h.Supervisor.DrainAsync();
        await resume;
        await suspend2;
        await h.AdvanceSecondsAsync(30);

        await h.ResumeAsync();
        await h.AdvanceAsync(TimeSpan.FromMinutes(10));

        Assert.NotEqual(MountState.Mounted, h.State(OnDemandKey));
        Assert.Equal(1, h.Rclone.MountCount(OnDemandDrive));
    }

    // ------------------------------------------------------------------------------------------
    // Gates: bs-bv0 (suspended), mounting availability, ADR-017's core/version.
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Protects:</b> I8 / bs-bv0: "No mount happens while the supervisor is suspended", for every
    /// route in -- user clicks on Ready hosts (on-demand and parked persistent), retry-now, a network
    /// change, mounting becoming available, a config reload, the passage of time -- with ADR-022's
    /// record present on another host.
    /// <b>Catches:</b> the new remount path (or any old one) reaching <c>mount/mount</c> during the
    /// seconds before sleep; that drive survives the sleep.
    /// </summary>
    [Fact]
    public async Task Nothing_mounts_while_suspended_by_any_route()
    {
        var config = HostFixtures.Build(
            HostFixtures.Global(),
            HostFixtures.OnDemand("od", drive: "Q:"),
            HostFixtures.OnDemand("odIdle", drive: "S:"),
            HostFixtures.Persistent("p", probeIntervalSeconds: 30, drive: "P:"),
            HostFixtures.Persistent("pParked", probeIntervalSeconds: 30, drive: "O:"));
        var h = new RemountHarness(config);
        await h.StartAsync();
        await h.MountAsync("od");
        await h.UnmountAsync("pParked");
        Assert.Equal(MountState.Ready, h.State("odIdle"));
        Assert.Equal(MountState.Ready, h.State("pParked"));

        await h.SuspendAsync();
        var suspendMark = h.LastMarkIndex("MARK|suspend");

        foreach (var key in new[] { "od", "odIdle", "p", "pParked" })
        {
            try
            {
                await h.MountAsync(key);
            }
            catch (MountRequestRefusedException)
            {
                // Refusing is the documented response while suspended; the property is "no mount".
            }

            await h.RunAsync(() => h.Supervisor.RequestRetryNowAsync(key));
        }

        await h.RunAsync(() => h.Supervisor.NetworkChangedAsync());
        await h.RunAsync(() => h.Supervisor.SetMountingAvailabilityAsync(
            MountingAvailability.Unavailable(MountingUnavailableCause.RcloneUnhealthy, "rcd restarting")));
        await h.RunAsync(() => h.Supervisor.SetMountingAvailabilityAsync(MountingAvailability.Available));
        await h.ReloadAsync(config);
        await h.AdvanceAsync(TimeSpan.FromHours(1));

        var mountsWhileSuspended = h.LogAfter(suspendMark).Where(e => e.StartsWith("mount|", StringComparison.Ordinal)).ToList();
        Assert.True(mountsWhileSuspended.Count == 0, $"mounted while suspended: {string.Join(", ", mountsWhileSuspended)}");
        Assert.All(new[] { "Q:", "S:", "P:", "O:" }, d => Assert.Null(h.Rclone.FsAt(d)));
    }

    /// <summary>
    /// <b>Protects:</b> the ADR-022 amendment (2026-11-01, bs-hrg) for the availability gate: with
    /// mounting unavailable at resume the first probe reaches <c>Ready</c>, the remount is NOT
    /// attempted (no <c>mount/mount</c>, no deep probe) and the record is KEPT; when mounting becomes
    /// available again the host is probed once more and, being <c>Ready</c>, mounted, deep probe first.
    /// <b>Catches:</b> the old reading (the record spent by the first probe, so an <c>rclone rcd</c>
    /// restart across a sleep loses the user's drive), and a remount that skips the deep probe.
    /// </summary>
    [Fact]
    public async Task With_mounting_unavailable_at_resume_the_record_is_kept_and_the_host_is_mounted_when_it_returns()
    {
        var h = await UserMountedOnDemandAsync();
        await h.SuspendAsync();
        await h.RunAsync(() => h.Supervisor.SetMountingAvailabilityAsync(
            MountingAvailability.Unavailable(MountingUnavailableCause.RcloneUnhealthy, "rcd restarting")));

        await h.ResumeAsync();
        var resumeMark = h.LastMarkIndex("MARK|resume");
        Assert.Equal(0, MountsAfter(h, OnDemandDrive, resumeMark));
        Assert.Equal(MountState.Ready, h.State(OnDemandKey));
        var probesWhileWaiting = h.Probe.ShallowCount(OnDemandHostname);

        await h.RunAsync(() => h.Supervisor.SetMountingAvailabilityAsync(MountingAvailability.Available));

        Assert.Equal(MountState.Mounted, h.State(OnDemandKey));
        Assert.Equal(1, MountsAfter(h, OnDemandDrive, resumeMark));
        Assert.Equal(probesWhileWaiting + 1, h.Probe.ShallowCount(OnDemandHostname));
        AssertEveryMountPrecededByPassingDeepProbe(h, OnDemandKey, OnDemandDrive, resumeMark);
    }

    /// <summary>
    /// <b>Protects:</b> the amendment for ADR-017's gate, and I1: <c>rclone rcd</c> does not answer
    /// <c>core/version</c> at resume. The remount is not attempted against it (no deep probe, no
    /// <c>mount/mount</c>) for as long as rcd stays silent, and once rcd answers the host is probed
    /// again and mounted exactly once, deep probe first. The recovery here is the supervisor's own
    /// reconciliation tick: nothing else tells it that an rcd which merely stalled has recovered.
    /// <b>Catches:</b> a remount that goes ahead against a dead rcd (the old reading), a record
    /// spent while waiting, and a record that waits forever because nothing re-checks rcd.
    /// </summary>
    [Fact]
    public async Task With_rcd_not_answering_core_version_at_resume_the_remount_waits_for_rcd_and_follows_I1()
    {
        var h = await UserMountedOnDemandAsync();
        await h.SuspendAsync();
        h.Rc.VersionFails = true;

        await h.ResumeAsync();
        var resumeMark = h.LastMarkIndex("MARK|resume");
        Assert.Equal(MountState.Ready, h.State(OnDemandKey));
        Assert.Equal(0, MountsAfter(h, OnDemandDrive, resumeMark));
        Assert.DoesNotContain(h.LogAfter(resumeMark), e => e.StartsWith($"deep|{OnDemandKey}|", StringComparison.Ordinal));

        await h.AdvanceAsync(TimeSpan.FromMinutes(2));
        Assert.Equal(MountState.Ready, h.State(OnDemandKey));
        Assert.Equal(0, MountsAfter(h, OnDemandDrive, resumeMark));

        h.Rc.VersionFails = false;
        await h.AdvanceAsync(TimeSpan.FromSeconds(31));

        output.WriteLine(h.TransitionDump());
        Assert.Equal(MountState.Mounted, h.State(OnDemandKey));
        Assert.Equal(1, MountsAfter(h, OnDemandDrive, resumeMark));
        AssertEveryMountPrecededByPassingDeepProbe(h, OnDemandKey, OnDemandDrive, resumeMark);
        Assert.Equal(h.State(OnDemandKey) == MountState.Mounted, h.Rclone.FsAt(OnDemandDrive) is not null);
    }

    /// <summary>
    /// <b>Protects:</b> the amendment's "rcd answers again" signal from the process owner: the
    /// supervisor is told <c>rclone rcd</c> restarted and healthy (<c>OnRcloneRestartedAsync</c>,
    /// which the startup orchestrator calls on <c>Healthy</c>). The record held back by the failed
    /// gate is resolved at once, not at the next tick; and while rcd still does not answer
    /// <c>core/version</c> that call changes nothing.
    /// <b>Catches:</b> trusting the report without confirming it with <c>core/version</c>, and a
    /// restart notification that leaves the record waiting for the 30 s tick.
    /// </summary>
    [Fact]
    public async Task With_rcd_down_at_resume_the_remount_is_made_when_rcd_is_reported_healthy_and_answers()
    {
        var h = await UserMountedOnDemandAsync();
        await h.SuspendAsync();
        h.Rc.VersionFails = true;

        await h.ResumeAsync();
        var resumeMark = h.LastMarkIndex("MARK|resume");
        Assert.Equal(0, MountsAfter(h, OnDemandDrive, resumeMark));

        await h.RunAsync(() => h.Supervisor.OnRcloneRestartedAsync());
        Assert.Equal(MountState.Ready, h.State(OnDemandKey));
        Assert.Equal(0, MountsAfter(h, OnDemandDrive, resumeMark));

        h.Rc.VersionFails = false;
        await h.RunAsync(() => h.Supervisor.OnRcloneRestartedAsync());

        Assert.Equal(MountState.Mounted, h.State(OnDemandKey));
        Assert.Equal(1, MountsAfter(h, OnDemandDrive, resumeMark));
        AssertEveryMountPrecededByPassingDeepProbe(h, OnDemandKey, OnDemandDrive, resumeMark);
    }

    // ------------------------------------------------------------------------------------------
    // Rule 10: Probing is one-shot along every ADR-022 path.
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Protects:</b> rule 10 together with rule 9a's "No new transition": across a resume that
    /// remounts one host, fails another's first probe, and late-confirms a third's drain, every entry
    /// into <c>Probing</c> comes from <c>Disabled</c>, every exit goes to <c>Ready</c> or
    /// <c>Unreachable</c>, each host enters <c>Probing</c> exactly once after the resume, and the
    /// remounted drive's recurring probes log nothing.
    /// <b>Catches:</b> the remount implemented as a re-probe through <c>Probing</c> from <c>Ready</c>
    /// or <c>Unreachable</c>, or a second <c>Probing</c> per host introduced by the record.
    /// </summary>
    [Fact]
    public async Task Probing_stays_one_shot_along_every_remount_path()
    {
        var h = new RemountHarness(HostFixtures.Build(
            HostFixtures.Global(),
            HostFixtures.OnDemand("up", drive: "Q:"),
            HostFixtures.OnDemand("down", drive: "R:"),
            HostFixtures.OnDemand("slow", drive: "S:")));
        await h.StartAsync();
        await h.MountAsync("up");
        await h.MountAsync("down");
        await h.MountAsync("slow");
        h.Rclone.ConfirmUnmountAtCall("S:", 2);

        await h.SuspendAsync();
        h.Probe.SetShallow(RemountHarness.HostnameOf("down"), ShallowProbeOutcome.ConnectionRefused);
        var before = new[] { "up", "down", "slow" }.ToDictionary(k => k, k => h.Transitions(k).Count);
        await h.ResumeAsync();
        await h.AdvanceAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(MountState.Mounted, h.State("up"));
        Assert.Equal(MountState.Unreachable, h.State("down"));
        Assert.Equal(MountState.Mounted, h.State("slow"));

        var settled = new[] { "up", "slow" }.ToDictionary(k => k, k => h.Transitions(k).Count);
        await h.AdvanceAsync(TimeSpan.FromHours(1));

        var history = h.Supervisor.GetTransitionHistory();
        var dump = h.TransitionDump();
        Assert.All(history.Where(t => t.To == MountState.Probing), t =>
            Assert.True(t.From == MountState.Disabled, $"{t.HostKey} entered Probing from {t.From}{Environment.NewLine}{dump}"));
        Assert.All(history.Where(t => t.From == MountState.Probing), t =>
            Assert.True(t.To is MountState.Ready or MountState.Unreachable, $"{t.HostKey} left Probing for {t.To}{Environment.NewLine}{dump}"));
        foreach (var key in new[] { "up", "down", "slow" })
        {
            Assert.Equal(1, h.Transitions(key).Skip(before[key]).Count(t => t.To == MountState.Probing));
        }

        Assert.Equal(settled["up"], h.Transitions("up").Count);
        Assert.Equal(settled["slow"], h.Transitions("slow").Count);
    }
}
