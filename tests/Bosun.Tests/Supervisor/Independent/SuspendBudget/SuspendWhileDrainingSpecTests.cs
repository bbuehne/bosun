using Bosun.Configuration;
using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor.Independent.SuspendBudget;

/// <summary>
/// bs-u4a item 4, written from docs/ARCHITECTURE.md §4 rule 5 rather than from the implementation:
/// a host that is <b>already <c>Draining</c></b> for an ordinary reason when suspend arrives keeps its
/// drain cause, but its forced-unmount escalation happens no later than suspend arrival plus
/// <c>global.suspend_unmount_timeout_seconds</c>. The deadline is only ever shortened. A host already
/// escalated is left alone. Hosts in <c>Mounted</c> or <c>Mounting</c> drain with the suspend cause.
/// </summary>
/// <remarks>
/// <para>
/// Why it matters (Invariant I8): the ordinary drain escalates after a fixed 10 s. The suspend handler
/// gets roughly two seconds before Windows sleeps (PBT_APMSUSPEND). A drain that was already running
/// when the machine suspended could therefore still be on its 10 s clock when the machine sleeps, and the
/// forced unmount would never be sent. The drive letter then survives the sleep.
/// </para>
/// <para>
/// Every unmount here is scripted never to confirm unless a test says otherwise, so the only way a
/// drain can end is the escalation the test is timing. Times are fake-clock seconds after
/// <c>t0</c>, the moment the ordinary drain began.
/// </para>
/// </remarks>
public sealed class SuspendWhileDrainingSpecTests
{
    private const int Budget = 3;

    // ------------------------------------------------------------------------------------------
    // The deadline: shortened to suspend arrival + budget
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Protects:</b> rule 5, "its forced-unmount escalation deadline becomes the suspend budget,
    /// counted from the moment suspend arrives", and "aims the next check at the new deadline".
    /// <b>Catches:</b> a suspend handler that only sweeps <c>Mounted</c>/<c>Mounting</c>, leaving a
    /// user-unmount drain on its 10 s clock -- the machine sleeps at t0+3 or so and the forced unmount
    /// is never sent. Also catches a deadline counted from the drain's start instead of from suspend
    /// arrival (that would escalate at t0+3, before the budget the spec grants).
    /// </summary>
    [Fact]
    public async Task A_user_unmount_drain_escalates_exactly_at_suspend_arrival_plus_budget()
    {
        var h = await DrainingAfterUserUnmountAsync();
        var t0 = h.Now;

        await h.AdvanceSecondsAsync(1);
        await h.SuspendAsync(); // deadline = t0 + 1 + 3 = t0 + 4

        await h.AdvanceAsync(TimeSpan.FromMilliseconds(2999));
        Assert.Empty(h.EscalationTimes("archive"));

        await h.AdvanceAsync(TimeSpan.FromMilliseconds(1));
        AssertEscalatedOnceAt(h, "archive", t0.AddSeconds(4));

        // ... and the ordinary 10 s deadline does not produce a second escalation.
        await h.AdvanceSecondsAsync(20);
        AssertEscalatedOnceAt(h, "archive", t0.AddSeconds(4));
    }

    /// <summary>
    /// <b>Protects:</b> rule 5, "The supervisor re-issues the unmount at once".
    /// <b>Catches:</b> a suspend handler that only re-arms the timer, so nothing at all is sent to
    /// rclone between suspend arrival and the deadline. If the machine sleeps a little early, that
    /// unmount was the last chance.
    /// </summary>
    [Fact]
    public async Task Suspend_re_issues_the_unmount_of_an_already_draining_host_at_once()
    {
        var h = await DrainingAfterUserUnmountAsync();

        await h.AdvanceSecondsAsync(1);
        var arrival = h.Now;
        await h.SuspendAsync();

        Assert.Contains(arrival, h.UnmountTimes("Q:"));
    }

    /// <summary>
    /// <b>Protects:</b> "a drain whose ordinary deadline is already sooner than suspend arrival plus
    /// the budget keeps it".
    /// <b>Catches:</b> a handler that overwrites the deadline with arrival + budget unconditionally.
    /// Here that would move escalation from t0+10 to t0+11.
    /// </summary>
    [Fact]
    public async Task A_drain_whose_ordinary_deadline_is_sooner_keeps_it()
    {
        var h = await DrainingAfterUserUnmountAsync();
        var t0 = h.Now;

        await h.AdvanceSecondsAsync(8);
        await h.SuspendAsync(); // arrival + budget = t0 + 11; ordinary = t0 + 10

        await h.AdvanceSecondsAsync(10);

        AssertEscalatedOnceAt(h, "archive", t0.AddSeconds(10));
    }

    /// <summary>
    /// <b>Protects:</b> "The change only ever shortens a deadline", when the configured budget is
    /// longer than the ordinary 10 s timeout. The schema bounds the budget only from below.
    /// <b>Catches:</b> a handler that applies the budget measured from the drain start (or any
    /// "replace" rather than "min"). With a 15 s budget that pushes escalation from t0+10 to t0+15.5
    /// for a drain that was already running.
    /// </summary>
    [Fact]
    public async Task A_budget_longer_than_the_ordinary_timeout_never_delays_an_escalation()
    {
        var h = await DrainingAfterUserUnmountAsync(budget: 15);
        var t0 = h.Now;

        await h.AdvanceAsync(TimeSpan.FromMilliseconds(500));
        await h.SuspendAsync();

        await h.AdvanceSecondsAsync(30);

        AssertEscalatedOnceAt(h, "archive", t0.AddSeconds(10));
    }

    /// <summary>
    /// <b>Protects:</b> "a second suspend never moves a deadline later".
    /// <b>Catches:</b> recomputing the deadline from the latest suspend. Windows can deliver
    /// PBT_APMSUSPEND twice; if the second arrival at t0+2 reset the deadline to t0+5, a drain could
    /// keep losing ground to repeated notifications.
    /// </summary>
    [Fact]
    public async Task A_second_suspend_never_moves_the_deadline_later()
    {
        var h = await DrainingAfterUserUnmountAsync();
        var t0 = h.Now;

        await h.AdvanceSecondsAsync(1);
        await h.SuspendAsync(); // t0 + 4
        await h.AdvanceSecondsAsync(1);
        await h.SuspendAsync(); // would be t0 + 5

        await h.AdvanceSecondsAsync(10);

        AssertEscalatedOnceAt(h, "archive", t0.AddSeconds(4));
    }

    /// <summary>
    /// <b>Protects:</b> "never moves a deadline later", across a resume that arrives before the
    /// shortened deadline. The spec says nothing explicit about resume restoring the ordinary 10 s
    /// deadline (flagged as an ambiguity in the report). This test takes the reading that a deadline,
    /// once shortened, stays short. That is the safe direction: escalating at t0+4 rather than t0+10
    /// only means the forced unmount comes sooner.
    /// <b>Catches:</b> a resume that clears the suspend deadline, so a suspend/resume/suspend sequence
    /// (a suspend, resume, suspend bounce) puts the drain back on the 10 s clock and the next sleep misses it.
    /// </summary>
    [Fact]
    public async Task A_resume_before_the_shortened_deadline_does_not_push_escalation_back_to_ten_seconds()
    {
        var h = await DrainingAfterUserUnmountAsync();
        var t0 = h.Now;

        await h.AdvanceSecondsAsync(1);
        await h.SuspendAsync(); // t0 + 4
        await h.AdvanceSecondsAsync(1);
        await h.ResumeAsync(); // t0 + 2, before the shortened deadline

        await h.AdvanceSecondsAsync(15);

        var escalations = h.EscalationTimes("archive");
        Assert.True(escalations.Count == 1, Describe("exactly one escalation", escalations));
        Assert.True(escalations[0] <= t0.AddSeconds(4), Describe("escalation no later than t0+4", escalations));
    }

    /// <summary>
    /// <b>Protects:</b> "a second suspend never moves a deadline later", with a resume in between
    /// (a suspend, a resume, and a second suspend within a few seconds).
    /// <b>Catches:</b> a resume that forgets the earlier deadline and lets the second suspend set a
    /// later one (t0+5 instead of t0+4).
    /// </summary>
    [Fact]
    public async Task Suspend_resume_suspend_keeps_the_first_shortened_deadline()
    {
        var h = await DrainingAfterUserUnmountAsync();
        var t0 = h.Now;

        await h.AdvanceSecondsAsync(1);
        await h.SuspendAsync(); // t0 + 4
        await h.AdvanceAsync(TimeSpan.FromMilliseconds(500));
        await h.ResumeAsync();
        await h.AdvanceAsync(TimeSpan.FromMilliseconds(500));
        await h.SuspendAsync(); // would be t0 + 5

        await h.AdvanceSecondsAsync(15);

        AssertEscalatedOnceAt(h, "archive", t0.AddSeconds(4));
    }

    // ------------------------------------------------------------------------------------------
    // A host that already escalated is untouched
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Protects:</b> "A host already escalated is left alone (it is already on the forced path)."
    /// <b>Catches:</b> a handler that sweeps every <c>Draining</c> host regardless of escalation. It
    /// would re-issue an unmount at suspend arrival, or (worse) reset the drain so it escalates a
    /// second time.
    /// </summary>
    [Fact]
    public async Task An_already_escalated_drain_is_not_touched_by_suspend()
    {
        var h = await DrainingAfterUserUnmountAsync();
        var t0 = h.Now;

        await h.AdvanceSecondsAsync(10);
        AssertEscalatedOnceAt(h, "archive", t0.AddSeconds(10)); // precondition: escalated

        await h.AdvanceSecondsAsync(2);
        var arrival = h.Now;
        await h.SuspendAsync();

        Assert.DoesNotContain(arrival, h.UnmountTimes("Q:"));
        Assert.Equal(MountState.Draining, h.State("archive"));

        await h.AdvanceSecondsAsync(30);
        AssertEscalatedOnceAt(h, "archive", t0.AddSeconds(10));
    }

    // ------------------------------------------------------------------------------------------
    // The drain cause survives the suspend
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Protects:</b> "keeps its drain cause -- a user unmount still parks it" (rule 5), together
    /// with rule 9 (a user unmount parks a persistent host).
    /// <b>Catches:</b> a suspend handler that re-labels an in-flight drain as a suspend drain. That
    /// looks harmless until resume: a suspend drain is re-enabled, so the persistent host the user
    /// deliberately unmounted comes back on the next wake. The mount count is the witness.
    /// </summary>
    [Fact]
    public async Task A_persistent_host_the_user_unmounted_stays_parked_through_suspend_and_resume()
    {
        var h = new SuspendBudgetHarness(Config(HostFixtures.Persistent("prod", probeIntervalSeconds: 30, drive: "P:")));
        await h.StartAsync();
        Assert.Equal(MountState.Mounted, h.State("prod"));

        h.Rclone.ConfirmUnmountAtCall("P:", int.MaxValue);
        await h.RunAsync(() => h.Supervisor.RequestUnmountAsync("prod"));
        await h.AdvanceSecondsAsync(1);
        await h.SuspendAsync();
        await h.AdvanceSecondsAsync(3);

        // The forced unmount finally takes; the next drain check sees the drive gone.
        h.Rclone.DropMountSilently("P:");
        await h.AdvanceSecondsAsync(30);
        Assert.NotEqual(MountState.Draining, h.State("prod"));
        Assert.True(h.Snapshot("prod").UserParked, "the user's unmount must still park the host after suspend");

        await h.ResumeAsync();
        await h.AdvanceAsync(TimeSpan.FromMinutes(10));

        Assert.Equal(MountState.Ready, h.State("prod"));
        Assert.True(h.Snapshot("prod").UserParked);
        Assert.Equal(1, h.Rclone.MountCount("P:"));
    }

    /// <summary>
    /// <b>Protects:</b> rule 5's shortened deadline for a drain caused by removing the host from
    /// config (one of the "ordinary" drains).
    /// <b>Catches:</b> a handler that only recognises some ordinary causes (user unmount, probe
    /// failure) and leaves a config-removal drain on its 10 s clock.
    /// </summary>
    [Fact]
    public async Task A_config_removal_drain_escalates_by_suspend_arrival_plus_budget()
    {
        var h = await DrainingAfterConfigRemovalAsync();
        var t0 = h.Now;

        await h.AdvanceSecondsAsync(1);
        await h.SuspendAsync();
        await h.AdvanceSecondsAsync(15);

        AssertEscalatedOnceAt(h, "old", t0.AddSeconds(4));
    }

    /// <summary>
    /// <b>Protects:</b> "keeps its drain cause ... a removed host is still evicted" -- the task brief's
    /// reading of rule 5's cause preservation, applied to <c>ConfigRemoved</c>.
    /// <b>Catches:</b> re-labelling the drain as a suspend drain. The host would then finish
    /// <c>Disabled</c> and stay in the snapshot, and resume would re-enable a host that no longer
    /// exists in config, probe it, and remount it.
    /// </summary>
    [Fact]
    public async Task A_host_removed_from_config_is_still_evicted_when_its_drain_finishes_during_suspend()
    {
        var h = await DrainingAfterConfigRemovalAsync();

        await h.AdvanceSecondsAsync(1);
        await h.SuspendAsync();
        await h.AdvanceSecondsAsync(3);
        h.Rclone.DropMountSilently("O:");
        await h.AdvanceSecondsAsync(30);

        Assert.False(h.IsSupervised("old"), "a host removed from config must be evicted once its drain completes");

        var probesAtEviction = h.Probe.ShallowCount(SuspendBudgetHarness.HostnameOf("old"));
        await h.ResumeAsync();
        await h.AdvanceAsync(TimeSpan.FromMinutes(10));

        Assert.False(h.IsSupervised("old"));
        Assert.Equal(1, h.Rclone.MountCount("O:"));
        Assert.Equal(probesAtEviction, h.Probe.ShallowCount(SuspendBudgetHarness.HostnameOf("old")));
    }

    /// <summary>
    /// <b>Protects:</b> rule 5's shortened deadline for a drain caused by the mounted-probe failure
    /// threshold (rule 3).
    /// <b>Catches:</b> the same omission as above for the most common ordinary drain: the host went
    /// unreachable, the drain started, and the machine suspended a second later.
    /// </summary>
    [Fact]
    public async Task A_probe_failure_drain_escalates_by_suspend_arrival_plus_budget()
    {
        var h = await DrainingAfterProbeFailuresAsync();
        var t0 = h.Now;

        await h.AdvanceSecondsAsync(1);
        await h.SuspendAsync();
        await h.AdvanceSecondsAsync(15);

        AssertEscalatedOnceAt(h, "prod", t0.AddSeconds(4));
    }

    /// <summary>
    /// <b>Protects:</b> cause preservation in the other direction: a probe-failure drain is not a
    /// parking cause, so the host comes back on resume once a fresh probe passes. It also must not
    /// come back <i>while</i> suspended.
    /// <b>Catches:</b> a handler that "preserves" the cause by marking the host parked, or a drain
    /// completion during suspend that re-enables the host straight away (a fresh drive letter in the
    /// seconds before sleep).
    /// </summary>
    [Fact]
    public async Task A_probe_failure_drain_finished_during_suspend_stays_down_until_resume_then_remounts()
    {
        var h = await DrainingAfterProbeFailuresAsync();

        await h.AdvanceSecondsAsync(1);
        await h.SuspendAsync();
        await h.AdvanceSecondsAsync(3);
        h.Rclone.DropMountSilently("P:");
        h.Probe.SetShallow(SuspendBudgetHarness.HostnameOf("prod"), ShallowProbeOutcome.Success);
        await h.AdvanceAsync(TimeSpan.FromHours(1));

        Assert.NotEqual(MountState.Mounted, h.State("prod"));
        Assert.Equal(1, h.Rclone.MountCount("P:"));

        await h.ResumeAsync();

        Assert.Equal(MountState.Mounted, h.State("prod"));
        Assert.Equal(2, h.Rclone.MountCount("P:"));
        Assert.False(h.Snapshot("prod").UserParked);
    }

    // ------------------------------------------------------------------------------------------
    // Hosts in Mounted or Mounting at suspend
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Protects:</b> item 1 (the default budget is 3 s) end to end, plus rule 5 for a
    /// <c>Mounted</c> host: its suspend drain escalates on the budget.
    /// <b>Catches:</b> the supervisor using a hard-coded or stale value (the old 8 s default) instead
    /// of <c>global.suspend_unmount_timeout_seconds</c>, or a config default that did not change.
    /// Built from <c>new GlobalConfig()</c> so nothing in the test sets the number.
    /// </summary>
    [Fact]
    public async Task With_the_default_config_a_mounted_host_escalates_three_seconds_after_suspend()
    {
        var h = new SuspendBudgetHarness(
            HostFixtures.Build(new GlobalConfig(), HostFixtures.Persistent("prod", probeIntervalSeconds: 0, drive: "P:")));
        await h.StartAsync();
        h.Rclone.ConfirmUnmountAtCall("P:", int.MaxValue);
        var arrival = h.Now;

        await h.SuspendAsync();
        await h.AdvanceSecondsAsync(15);

        AssertEscalatedOnceAt(h, "prod", arrival.AddSeconds(3));
    }

    /// <summary>
    /// <b>Protects:</b> both halves of rule 5 in one suspend: a <c>Mounted</c> host and an
    /// already-<c>Draining</c> host both escalate by arrival + budget.
    /// <b>Catches:</b> a handler that takes its "already draining" list <i>after</i> the sweep, or
    /// returns early after one category, so one of the two hosts misses the budget.
    /// </summary>
    [Fact]
    public async Task One_suspend_puts_mounted_and_already_draining_hosts_on_the_same_deadline()
    {
        var h = new SuspendBudgetHarness(Config(
            HostFixtures.OnDemand("archive", drive: "Q:"),
            HostFixtures.Persistent("prod", probeIntervalSeconds: 0, drive: "P:")));
        await h.StartAsync();
        await h.RunAsync(() => h.Supervisor.RequestMountAsync("archive"));
        h.Rclone.ConfirmUnmountAtCall("Q:", int.MaxValue);
        h.Rclone.ConfirmUnmountAtCall("P:", int.MaxValue);
        await h.RunAsync(() => h.Supervisor.RequestUnmountAsync("archive"));

        await h.AdvanceSecondsAsync(1);
        var arrival = h.Now;
        await h.SuspendAsync();
        await h.AdvanceSecondsAsync(15);

        AssertEscalatedOnceAt(h, "archive", arrival.AddSeconds(Budget));
        AssertEscalatedOnceAt(h, "prod", arrival.AddSeconds(Budget));
    }

    /// <summary>
    /// <b>Protects:</b> rule 5, "every host in Mounted <b>or Mounting</b> goes to Draining". A suspend
    /// that arrives while a mount is in flight (the deep probe of rule 2 is still running) must not
    /// leave the drive mounted once the mount completes.
    /// <b>Catches:</b> a sweep that looks only at <c>Mounted</c> and runs before the in-flight mount
    /// finishes, or a mount completion that ignores a suspend which arrived during it. Either leaves a
    /// fresh drive letter in place across the sleep.
    /// </summary>
    [Fact]
    public async Task A_suspend_that_arrives_while_a_host_is_Mounting_leaves_no_drive_behind()
    {
        var h = new SuspendBudgetHarness(Config(HostFixtures.OnDemand("archive", drive: "Q:")));
        await h.StartAsync();

        await SuspendWhileMountingAsync(h, "archive", () => h.Supervisor.RequestMountAsync("archive"), DeepProbeOutcome.Success);

        Assert.Null(h.Rclone.FsAt("Q:"));
        Assert.NotEqual(MountState.Mounted, h.State("archive"));

        await h.AdvanceAsync(TimeSpan.FromDays(1));
        Assert.Null(h.Rclone.FsAt("Q:"));
        Assert.NotEqual(MountState.Mounted, h.State("archive"));
    }

    /// <summary>
    /// <b>Protects:</b> the budget applies to a host caught in <c>Mounting</c>: escalation no later
    /// than suspend arrival + budget.
    /// <b>Catches:</b> a mount that completes after the suspend was queued and is then drained with
    /// an ordinary cause (10 s deadline), or a drain timed from some later point than suspend arrival.
    /// </summary>
    [Fact]
    public async Task A_host_caught_Mounting_by_suspend_escalates_by_arrival_plus_budget()
    {
        var h = new SuspendBudgetHarness(Config(HostFixtures.OnDemand("archive", drive: "Q:")));
        await h.StartAsync();
        h.Rclone.ConfirmUnmountAtCall("Q:", int.MaxValue);
        var arrival = h.Now;

        await SuspendWhileMountingAsync(h, "archive", () => h.Supervisor.RequestMountAsync("archive"), DeepProbeOutcome.Success);
        await h.AdvanceSecondsAsync(15);

        var escalations = h.EscalationTimes("archive");
        Assert.True(escalations.Count == 1, Describe("exactly one escalation", escalations));
        Assert.True(escalations[0] <= arrival.AddSeconds(Budget), Describe($"escalation no later than arrival+{Budget}s", escalations));
    }

    /// <summary>
    /// <b>Protects:</b> Invariant I8 for a mount that fails while the suspend is queued: the failed
    /// attempt's paced retry must not mount the host while the system is suspended.
    /// <b>Catches:</b> a mount-retry timer armed by the failed attempt (just before suspend was
    /// processed) firing during the suspend, probing, reaching <c>Ready</c>, and auto-mounting a
    /// persistent host. That recreates a drive letter in the seconds before sleep, or on wake before
    /// resume has re-derived anything.
    /// </summary>
    [Fact]
    public async Task A_mount_that_fails_while_suspend_is_queued_does_not_retry_into_a_mount_while_suspended()
    {
        var h = new SuspendBudgetHarness(Config(HostFixtures.Persistent("prod", probeIntervalSeconds: 0, drive: "P:")));

        await SuspendWhileMountingAsync(h, "prod", () => h.Supervisor.StartAsync(), DeepProbeOutcome.Failed);

        await h.AdvanceAsync(TimeSpan.FromDays(1));
        Assert.True(h.Rclone.TotalMountCalls == 0, MountsWhileSuspended(h, "prod"));
        Assert.NotEqual(MountState.Mounted, h.State("prod"));

        // Not vacuous: the host is mountable, and resume mounts it.
        await h.ResumeAsync();
        Assert.Equal(MountState.Mounted, h.State("prod"));
        Assert.Equal(1, h.Rclone.MountCount("P:"));
    }

    /// <summary>
    /// <b>Protects:</b> Invariant I8 ("On power suspend, unmount everything before the machine
    /// sleeps") and §3's "enter Suspended", for a persistent host that was <c>Unreachable</c> when the
    /// suspend arrived. Rule 5 does not name this state, so this is the strongest reading rather than
    /// a quoted rule; see the report.
    /// <b>Catches:</b> the idle backoff timer of an unreachable persistent host firing during suspend,
    /// succeeding, and auto-mounting -- a mount created after the suspend handler has run.
    /// </summary>
    [Fact]
    public async Task An_unreachable_persistent_host_does_not_mount_while_suspended()
    {
        var h = new SuspendBudgetHarness(Config(HostFixtures.Persistent("prod", probeIntervalSeconds: 30, drive: "P:")));
        h.Probe.SetShallow(SuspendBudgetHarness.HostnameOf("prod"), ShallowProbeOutcome.ConnectionRefused);
        await h.StartAsync();
        Assert.Equal(MountState.Unreachable, h.State("prod"));

        await h.SuspendAsync();
        h.Probe.SetShallow(SuspendBudgetHarness.HostnameOf("prod"), ShallowProbeOutcome.Success);
        await h.AdvanceAsync(TimeSpan.FromHours(1));

        Assert.True(h.Rclone.TotalMountCalls == 0, MountsWhileSuspended(h, "prod"));

        await h.ResumeAsync();
        Assert.Equal(MountState.Mounted, h.State("prod"));
    }

    // ------------------------------------------------------------------------------------------
    // Setup
    // ------------------------------------------------------------------------------------------

    private static BosunConfig Config(params HostConfig[] hosts) =>
        HostFixtures.Build(HostFixtures.Global(suspendUnmountTimeoutSeconds: Budget), hosts);

    /// <summary>An on-demand host, mounted, then unmounted by the user; the unmount never confirms,
    /// so the host sits in <c>Draining</c> from the returned harness's <c>Now</c>.</summary>
    private static async Task<SuspendBudgetHarness> DrainingAfterUserUnmountAsync(int budget = Budget)
    {
        var h = new SuspendBudgetHarness(HostFixtures.Build(
            HostFixtures.Global(suspendUnmountTimeoutSeconds: budget),
            HostFixtures.OnDemand("archive", drive: "Q:")));
        await h.StartAsync();
        await h.RunAsync(() => h.Supervisor.RequestMountAsync("archive"));
        Assert.Equal(MountState.Mounted, h.State("archive"));

        h.Rclone.ConfirmUnmountAtCall("Q:", int.MaxValue);
        await h.RunAsync(() => h.Supervisor.RequestUnmountAsync("archive"));
        Assert.Equal(MountState.Draining, h.State("archive"));
        Assert.Empty(h.EscalationTimes("archive"));
        return h;
    }

    /// <summary>Two persistent hosts mounted; "old" is then removed from config. Its unmount never
    /// confirms, so it sits in <c>Draining</c> from the returned harness's <c>Now</c>.</summary>
    private static async Task<SuspendBudgetHarness> DrainingAfterConfigRemovalAsync()
    {
        var old = HostFixtures.Persistent("old", probeIntervalSeconds: 0, drive: "O:");
        var keep = HostFixtures.Persistent("keep", probeIntervalSeconds: 0, drive: "K:");
        var h = new SuspendBudgetHarness(Config(old, keep));
        await h.StartAsync();
        Assert.Equal(MountState.Mounted, h.State("old"));

        h.Rclone.ConfirmUnmountAtCall("O:", int.MaxValue);
        await h.ReloadAsync(Config(keep));
        Assert.Equal(MountState.Draining, h.State("old"));
        Assert.Empty(h.EscalationTimes("old"));
        return h;
    }

    /// <summary>A persistent host, mounted, whose shallow probe then fails until the rule 3
    /// threshold drains it. The unmount never confirms. The returned harness's <c>Now</c> is the
    /// moment the drain began.</summary>
    private static async Task<SuspendBudgetHarness> DrainingAfterProbeFailuresAsync()
    {
        var h = new SuspendBudgetHarness(Config(HostFixtures.Persistent("prod", probeIntervalSeconds: 0, drive: "P:")));
        await h.StartAsync();
        Assert.Equal(MountState.Mounted, h.State("prod"));

        h.Rclone.ConfirmUnmountAtCall("P:", int.MaxValue);
        h.Probe.SetShallow(SuspendBudgetHarness.HostnameOf("prod"), ShallowProbeOutcome.ConnectionRefused);
        for (var i = 0; i < 60 && h.State("prod") != MountState.Draining; i++)
        {
            await h.AdvanceSecondsAsync(10);
        }

        Assert.Equal(MountState.Draining, h.State("prod"));
        var drainStart = h.Transitions("prod").Last(t => t.To == MountState.Draining).TimestampUtc;
        Assert.Equal(drainStart, h.Now); // the 10 s steps land on the 60 s probe ticks exactly
        Assert.Empty(h.EscalationTimes("prod"));
        return h;
    }

    /// <summary>
    /// Starts <paramref name="startMount"/> with <paramref name="hostKey"/>'s deep probe held open,
    /// checks the host really is in <c>Mounting</c>, queues a suspend, then lets the deep probe finish
    /// with <paramref name="deepOutcome"/> and runs everything to completion. The fake clock does not
    /// move, so suspend "arrives" at the harness's current time.
    /// </summary>
    private static async Task SuspendWhileMountingAsync(
        SuspendBudgetHarness h, string hostKey, Func<Task> startMount, DeepProbeOutcome deepOutcome)
    {
        h.Gate.HoldNextDeep(hostKey);
        var mountTask = startMount();
        var pump = h.Supervisor.DrainAsync();

        Assert.True(h.Gate.IsHolding, "setup: the deep probe for the mount should be in flight");
        Assert.Equal(MountState.Mounting, h.State(hostKey));

        var suspendTask = h.Supervisor.SuspendAsync();
        h.Gate.Release(deepOutcome);

        await pump;
        await h.Supervisor.DrainAsync();
        await mountTask;
        await suspendTask;
    }

    private static void AssertEscalatedOnceAt(SuspendBudgetHarness h, string hostKey, DateTimeOffset expected)
    {
        var escalations = h.EscalationTimes(hostKey);
        Assert.True(
            escalations.Count == 1 && escalations[0] == expected,
            Describe($"exactly one escalation for {hostKey}, at {Seconds(expected)}s", escalations));
    }

    /// <summary>Failure text for a mount made during suspend: when it happened and the transitions
    /// that led to it.</summary>
    private static string MountsWhileSuspended(SuspendBudgetHarness h, string hostKey) =>
        $"Expected no mount/mount while suspended; mounts at [" +
        string.Join(", ", h.TimedRclone.Calls.Where(c => c.Operation == "mount").Select(c => $"{Seconds(c.At)}s {c.MountPoint}")) +
        "]; transitions:" + Environment.NewLine +
        string.Join(Environment.NewLine, h.Transitions(hostKey).Select(t => $"  {Seconds(t.TimestampUtc)}s {t.From} -> {t.To} ({t.Trigger})"));

    private static string Describe(string expectation, IReadOnlyList<DateTimeOffset> escalations) =>
        $"Expected {expectation}; escalations recorded at [{string.Join(", ", escalations.Select(e => $"{Seconds(e)}s"))}].";

    private static string Seconds(DateTimeOffset t) =>
        (t - DateTimeOffset.UnixEpoch).TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
