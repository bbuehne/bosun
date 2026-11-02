using Bosun.Configuration;
using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Fakes;
using Bosun.Tests.Supervisor.Independent.SuspendBudget;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor;

/// <summary>
/// bs-hrg / ADR-022: an on-demand drive the user had mounted when the machine slept comes back at
/// wake, if (and only if) the host's first post-resume probe reaches <c>Ready</c>. Each test names
/// the numbered decision of ADR-022 it pins. Fakes and a fake clock only: no rclone, drive letter,
/// SystemEvents or wt.exe. Time moves only when a test advances it.
/// </summary>
public sealed class RemountAfterResumeTests
{
    private const string Key = "archive";
    private const string Drive = "Q:";

    private static string Hostname => SuspendBudgetHarness.HostnameOf(Key);

    private static BosunConfig Config(params HostConfig[] hosts) =>
        HostFixtures.Build(HostFixtures.Global(suspendUnmountTimeoutSeconds: 3), hosts);

    private static HostConfig OnDemand(int idleUnmountSeconds = 0) =>
        HostFixtures.OnDemand(Key, drive: Drive, idleUnmountSeconds: idleUnmountSeconds);

    /// <summary>An on-demand host the user has just mounted from the tray.</summary>
    private static async Task<SuspendBudgetHarness> UserMountedAsync(int idleUnmountSeconds = 0)
    {
        var h = new SuspendBudgetHarness(Config(OnDemand(idleUnmountSeconds)));
        await h.StartAsync();
        Assert.Equal(MountState.Ready, h.State(Key));
        await h.RunAsync(() => h.Supervisor.RequestMountAsync(Key));
        Assert.Equal(MountState.Mounted, h.State(Key));
        Assert.Equal(1, h.Rclone.MountCount(Drive));
        return h;
    }

    private static IEnumerable<MountState> StatesAfter(SuspendBudgetHarness h, int transitionsBefore) =>
        h.Transitions(Key).Skip(transitionsBefore).Select(t => t.To);

    // ------------------------------------------------------------------------------------------
    // Decisions 1 and 2: recorded at suspend, remounted via Probing -> Ready -> Mounting
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Protects:</b> ADR-022 decisions 1 and 2, and Invariant I1. The host the user mounted comes
    /// back after resume through <c>Disabled -> Probing -> Ready -> Mounting -> Mounted</c>, with a
    /// fresh shallow probe and then the deep probe BEFORE <c>mount/mount</c>, and every entry into
    /// <c>Mounting</c> is from <c>Ready</c>.
    /// <b>Catches:</b> no remount at all (the bug this fixes); a remount that skips the probes; a
    /// remount that reaches <c>Mounting</c> from anywhere but <c>Ready</c>.
    /// </summary>
    [Fact]
    public async Task A_user_mounted_on_demand_host_is_remounted_after_resume_through_a_fresh_probe_and_the_deep_probe()
    {
        var h = await UserMountedAsync();

        await h.SuspendAsync();
        Assert.Equal(MountState.Disabled, h.State(Key));
        Assert.Equal(1, h.Rclone.UnmountCount(Drive)); // exactly the one suspend unmount
        var mark = h.Log.Entries.Count;
        var transitionsBefore = h.Transitions(Key).Count;

        await h.ResumeAsync();

        Assert.Equal(MountState.Mounted, h.State(Key));
        Assert.Equal(2, h.Rclone.MountCount(Drive));
        Assert.Equal(
            [MountState.Probing, MountState.Ready, MountState.Mounting, MountState.Mounted],
            StatesAfter(h, transitionsBefore).ToArray());
        Assert.All(h.Transitions(Key).Where(t => t.To == MountState.Mounting), t => Assert.Equal(MountState.Ready, t.From));

        var calls = h.Log.Entries.Skip(mark).Where(e => e.Contains(Key) || e.StartsWith("mount|", StringComparison.Ordinal)).ToList();
        var shallow = calls.FindIndex(e => e.StartsWith($"shallow|{Hostname}|", StringComparison.Ordinal));
        var deep = calls.FindIndex(e => e.StartsWith($"deep|{Key}|", StringComparison.Ordinal));
        var mount = calls.FindIndex(e => e == $"mount|{Drive}");
        Assert.True(shallow >= 0 && shallow < deep && deep < mount, $"expected shallow < deep < mount, got:{Environment.NewLine}{string.Join(Environment.NewLine, calls)}");
        Assert.Contains("ADR-022", h.Transitions(Key).Last(t => t.To == MountState.Mounting).Trigger);
    }

    /// <summary>
    /// <b>Protects:</b> Invariant I8 / bs-bv0 with decision 2. Nothing mounts while suspended, however
    /// long the machine sleeps; the remount waits for resume.
    /// <b>Catches:</b> a remount performed in the suspend handler, or by a timer during the sleep.
    /// </summary>
    [Fact]
    public async Task Nothing_is_mounted_while_suspended_the_remount_waits_for_resume()
    {
        var h = await UserMountedAsync();
        await h.SuspendAsync();
        var probesAtSuspend = h.Probe.ShallowCount(Hostname);

        await h.AdvanceAsync(TimeSpan.FromHours(8));

        Assert.Equal(MountState.Disabled, h.State(Key));
        Assert.Equal(1, h.Rclone.MountCount(Drive));
        Assert.Equal(probesAtSuspend, h.Probe.ShallowCount(Hostname));
    }

    /// <summary>
    /// <b>Protects:</b> decision 2's "exactly as a user click would": a deep-probe failure on the
    /// remount takes the existing mount-failure route and the host is not mounted, and decision 3's
    /// spirit that nothing retries it later on its own.
    /// <b>Catches:</b> a remount that bypasses the deep probe (I1), or one that keeps retrying a
    /// failing mount on the persistent mount-retry ladder.
    /// </summary>
    [Fact]
    public async Task A_remount_whose_deep_probe_fails_is_not_mounted_and_is_not_retried()
    {
        var h = await UserMountedAsync();
        await h.SuspendAsync();
        h.Probe.SetDeep(Key, DeepProbeOutcome.Failed, times: 1);

        await h.ResumeAsync();
        await h.AdvanceAsync(TimeSpan.FromHours(2));

        Assert.Equal(MountState.Ready, h.State(Key));
        Assert.Equal(1, h.Rclone.MountCount(Drive)); // never reached mount/mount again
        Assert.Equal(1, h.Snapshot(Key).ConsecutiveMountFailures);
    }

    /// <summary>
    /// <b>Protects:</b> the same, for a <c>mount/mount</c> that throws.
    /// <b>Catches:</b> an on-demand host retried on the mount-retry ladder as if it were persistent.
    /// </summary>
    [Fact]
    public async Task A_remount_whose_mount_call_fails_is_not_retried_later()
    {
        var h = await UserMountedAsync();
        await h.SuspendAsync();
        h.Rclone.FailMount(Drive, new InvalidOperationException("scripted mount failure"), times: 1);

        await h.ResumeAsync();
        await h.AdvanceAsync(TimeSpan.FromHours(2));

        Assert.Equal(MountState.Ready, h.State(Key));
        Assert.Equal(2, h.Rclone.MountCount(Drive)); // the original mount, plus the one failed remount
    }

    /// <summary>
    /// <b>Protects:</b> decision 2 under the existing availability gate (bs-yvw.1), as amended on
    /// 2026-11-01. If mounting is unavailable at wake the remount is refused like a click would be,
    /// the record is KEPT, and when mounting becomes available the host is probed once more and
    /// mounted.
    /// <b>Catches:</b> a record spent by the refusal (an <c>rclone rcd</c> restart across a sleep
    /// loses the user's drive), or a remount that is not made once the gate reopens.
    /// </summary>
    [Fact]
    public async Task A_remount_refused_because_mounting_is_unavailable_is_made_when_it_returns()
    {
        var h = await UserMountedAsync();
        await h.SuspendAsync();
        await h.RunAsync(() => h.Supervisor.SetMountingAvailabilityAsync(
            MountingAvailability.Unavailable(MountingUnavailableCause.WinFspMissing, "WinFsp is not installed")));

        await h.ResumeAsync();
        Assert.Equal(MountState.Ready, h.State(Key));
        Assert.Equal(1, h.Rclone.MountCount(Drive)); // refused: no second mount/mount, no deep probe
        Assert.Equal(1, h.Probe.DeepCount(Key));

        await h.RunAsync(() => h.Supervisor.SetMountingAvailabilityAsync(MountingAvailability.Available));

        Assert.Equal(MountState.Mounted, h.State(Key));
        Assert.Equal(2, h.Rclone.MountCount(Drive));
        Assert.Equal(2, h.Probe.DeepCount(Key)); // the deep probe of the remount
        Assert.Contains("ADR-022", h.Transitions(Key).Last(t => t.To == MountState.Mounting).Trigger);
    }

    // ------------------------------------------------------------------------------------------
    // Decision 3: the first probe is the only chance
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Protects:</b> decision 3. When the first post-resume probe fails the host shows Unreachable,
    /// nothing is mounted, and nothing mounts it later: not the passage of time, not a retry-now, not a
    /// network change onto a network where the host answers. Only the user's click does.
    /// <b>Catches:</b> a record kept "until the host is next reachable" (the late surprise mount the
    /// ADR rejects).
    /// </summary>
    [Fact]
    public async Task When_the_first_probe_fails_the_host_is_unreachable_and_never_mounts_on_its_own_later()
    {
        var h = await UserMountedAsync();
        await h.SuspendAsync();
        h.Probe.SetShallow(Hostname, ShallowProbeOutcome.ConnectionRefused, times: 1);

        await h.ResumeAsync();

        Assert.Equal(MountState.Unreachable, h.State(Key));
        Assert.Equal(1, h.Rclone.MountCount(Drive));
        Assert.Equal(1, h.Probe.DeepCount(Key)); // no deep probe beyond the original mount's

        // Time passes; an on-demand Unreachable host is not polled (ADR-014), so it stays dark.
        var probes = h.Probe.ShallowCount(Hostname);
        await h.AdvanceAsync(TimeSpan.FromDays(2));
        Assert.Equal(probes, h.Probe.ShallowCount(Hostname));
        Assert.Equal(MountState.Unreachable, h.State(Key));

        // The host now answers (the machine is on another network). Neither route mounts it.
        await h.RunAsync(() => h.Supervisor.RequestRetryNowAsync(Key));
        Assert.Equal(MountState.Ready, h.State(Key));
        await h.RunAsync(() => h.Supervisor.NetworkChangedAsync());
        await h.AdvanceAsync(TimeSpan.FromHours(1));
        Assert.Equal(MountState.Ready, h.State(Key));
        Assert.Equal(1, h.Rclone.MountCount(Drive));

        // A later click mounts it as usual.
        await h.RunAsync(() => h.Supervisor.RequestMountAsync(Key));
        Assert.Equal(MountState.Mounted, h.State(Key));
        Assert.Equal(2, h.Rclone.MountCount(Drive));
    }

    /// <summary>
    /// <b>Protects:</b> decision 3 across a second sleep. A host whose record was dropped is not
    /// resurrected by a later suspend/resume while it is not mounted.
    /// <b>Catches:</b> a record that is dropped for display but kept for the next resume.
    /// </summary>
    [Fact]
    public async Task A_dropped_record_does_not_come_back_at_the_next_suspend_and_resume()
    {
        var h = await UserMountedAsync();
        await h.SuspendAsync();
        h.Probe.SetShallow(Hostname, ShallowProbeOutcome.ConnectionRefused, times: 1);
        await h.ResumeAsync();
        Assert.Equal(MountState.Unreachable, h.State(Key));

        await h.SuspendAsync();
        await h.ResumeAsync(); // the host answers now

        Assert.Equal(MountState.Ready, h.State(Key));
        Assert.Equal(1, h.Rclone.MountCount(Drive));
    }

    /// <summary>
    /// <b>Protects:</b> decision 3's "the record is dropped" in the one place it is observable only
    /// later. After a failed first probe the user clicks Mount, and the drive idle-unmounts; the
    /// re-enable that follows must not read a leftover record and mount it again.
    /// <b>Catches:</b> a record cleared only when the probe succeeds, which leaves it armed for the
    /// next <c>Disabled -> Probing</c> of the same host.
    /// </summary>
    [Fact]
    public async Task A_record_dropped_by_a_failed_first_probe_cannot_fire_at_a_later_re_enable()
    {
        var h = await UserMountedAsync(idleUnmountSeconds: 60);
        await h.SuspendAsync();
        h.Probe.SetShallow(Hostname, ShallowProbeOutcome.ConnectionRefused, times: 1);
        await h.ResumeAsync();
        Assert.Equal(MountState.Unreachable, h.State(Key));

        await h.RunAsync(() => h.Supervisor.RequestMountAsync(Key)); // the user's own click
        Assert.Equal(MountState.Mounted, h.State(Key));
        Assert.Equal(2, h.Rclone.MountCount(Drive));

        await h.AdvanceSecondsAsync(61); // idle unmount, then the ordinary re-enable

        Assert.Equal(MountState.Ready, h.State(Key));
        Assert.Equal(2, h.Rclone.MountCount(Drive));
    }

    // ------------------------------------------------------------------------------------------
    // Decision 4: the idle timer starts fresh
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Protects:</b> decision 4. The idle-unmount timer of the remounted drive counts from the
    /// remount, not from the pre-sleep mount or from the time already idle.
    /// <b>Catches:</b> a remount that carries the old deadline (it would expire 100 s after wake, here)
    /// or never arms the timer (the drive would never idle-unmount).
    /// </summary>
    [Fact]
    public async Task The_idle_unmount_timer_starts_fresh_on_the_remount()
    {
        var h = await UserMountedAsync(idleUnmountSeconds: 600);
        await h.AdvanceSecondsAsync(500); // 100 s from expiring when the lid closes
        await h.SuspendAsync();
        await h.AdvanceAsync(TimeSpan.FromHours(1));
        await h.ResumeAsync();
        Assert.Equal(MountState.Mounted, h.State(Key));
        var remountedAt = h.Now;

        await h.AdvanceSecondsAsync(599);
        Assert.Equal(MountState.Mounted, h.State(Key)); // a carried-over timer would have fired at +100

        await h.AdvanceSecondsAsync(2);
        Assert.NotEqual(MountState.Mounted, h.State(Key));
        var idleDrainAt = h.Transitions(Key).Last(t => t.To == MountState.Draining).TimestampUtc;
        Assert.Equal(remountedAt.AddSeconds(600), idleDrainAt);
    }

    // ------------------------------------------------------------------------------------------
    // Decision 5: what clears the record
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Protects:</b> decision 5, "the remount itself". After the remount the record is spent: when
    /// the drive later idle-unmounts, neither a retry-now, a network change, nor time brings it back.
    /// <b>Catches:</b> a record that survives its own use.
    /// </summary>
    [Fact]
    public async Task The_remount_clears_the_record()
    {
        var h = await UserMountedAsync(idleUnmountSeconds: 600);
        await h.SuspendAsync();
        await h.ResumeAsync();
        Assert.Equal(MountState.Mounted, h.State(Key));
        Assert.Equal(2, h.Rclone.MountCount(Drive));

        await h.AdvanceSecondsAsync(601); // idle unmount
        Assert.Equal(MountState.Ready, h.State(Key));

        await h.RunAsync(() => h.Supervisor.RequestRetryNowAsync(Key));
        await h.RunAsync(() => h.Supervisor.NetworkChangedAsync());
        await h.AdvanceAsync(TimeSpan.FromHours(1));

        Assert.Equal(MountState.Ready, h.State(Key));
        Assert.Equal(2, h.Rclone.MountCount(Drive));
    }

    /// <summary>
    /// <b>Protects:</b> decision 5, "a user unmount", in the hard case: the user clicks Unmount while
    /// the suspend drain is already running (the drain has not confirmed, so the request finds the host
    /// <c>Draining</c> and does nothing else). The click still clears the record.
    /// <b>Catches:</b> an unmount request that returns early for a non-Mounted host before clearing, so
    /// the drive the user took down comes back at wake.
    /// </summary>
    [Fact]
    public async Task A_user_unmount_during_the_suspend_drain_clears_the_record()
    {
        var h = await UserMountedAsync();
        h.Rclone.ConfirmUnmountAtCall(Drive, 3); // the first two unmount attempts do not take effect
        await h.SuspendAsync();
        Assert.Equal(MountState.Draining, h.State(Key));

        await h.RunAsync(() => h.Supervisor.RequestUnmountAsync(Key));

        await h.AdvanceSecondsAsync(20); // the drain confirms
        Assert.Equal(MountState.Disabled, h.State(Key));
        await h.ResumeAsync();

        Assert.Equal(MountState.Ready, h.State(Key));
        Assert.Equal(1, h.Rclone.MountCount(Drive));
    }

    /// <summary>
    /// <b>Protects:</b> decision 5 and ADR-015 together. A drive the user unmounted before the sleep
    /// is not mounted, and stays parked.
    /// <b>Catches:</b> a record that is set from "was mounted at some point" rather than "is mounted
    /// at suspend"; or a remount that un-parks a parked host.
    /// </summary>
    [Fact]
    public async Task A_host_the_user_unmounted_before_suspend_is_not_remounted_and_stays_parked()
    {
        var h = await UserMountedAsync();
        await h.RunAsync(() => h.Supervisor.RequestUnmountAsync(Key));
        await h.AdvanceSecondsAsync(1);
        Assert.Equal(MountState.Ready, h.State(Key));
        Assert.True(h.Snapshot(Key).UserParked);

        await h.SuspendAsync();
        await h.AdvanceAsync(TimeSpan.FromHours(1));
        await h.ResumeAsync();

        Assert.Equal(MountState.Ready, h.State(Key));
        Assert.True(h.Snapshot(Key).UserParked);
        Assert.Equal(1, h.Rclone.MountCount(Drive));
    }

    /// <summary>
    /// <b>Protects:</b> decision 5, "removed from the config". The host is taken out of the config
    /// while its suspend drain is still running (so it is evicted when the drain confirms), then added
    /// back under the same key before resume. The new host is a new host: no remount.
    /// <b>Catches:</b> a record that is kept by key rather than by the host it belongs to.
    /// </summary>
    [Fact]
    public async Task A_host_removed_from_config_loses_the_record()
    {
        var h = await UserMountedAsync();
        h.Rclone.ConfirmUnmountAtCall(Drive, 3);
        await h.SuspendAsync();
        Assert.Equal(MountState.Draining, h.State(Key));

        await h.ReloadAsync(Config());
        await h.AdvanceSecondsAsync(20); // drain confirms, host is evicted
        Assert.False(h.IsSupervised(Key));

        await h.ReloadAsync(Config(OnDemand()));
        await h.ResumeAsync();

        Assert.Equal(MountState.Ready, h.State(Key));
        Assert.Equal(1, h.Rclone.MountCount(Drive));
    }

    /// <summary>
    /// <b>Protects:</b> decision 5, "its mode changed". The host goes on-demand -> persistent -> back to
    /// on-demand during the sleep; the user's click belonged to the first arrangement, so no remount.
    /// <b>Catches:</b> a record that survives a round trip through another tier.
    /// </summary>
    [Fact]
    public async Task A_mode_change_during_the_sleep_clears_the_record()
    {
        var h = await UserMountedAsync();
        await h.SuspendAsync();

        await h.ReloadAsync(Config(HostFixtures.Persistent(Key, probeIntervalSeconds: 0, drive: Drive)));
        await h.ReloadAsync(Config(OnDemand()));
        await h.ResumeAsync();

        Assert.Equal(MountState.Ready, h.State(Key));
        Assert.Equal(1, h.Rclone.MountCount(Drive));
    }

    /// <summary>
    /// <b>Protects:</b> decision 5, "its mode changed", through <c>none</c>, which takes a different
    /// path in the supervisor (the host is disabled and later enabled again).
    /// <b>Catches:</b> a clear placed on only some of the mode-change branches.
    /// </summary>
    [Fact]
    public async Task A_round_trip_through_mode_none_during_the_sleep_clears_the_record()
    {
        var h = await UserMountedAsync();
        await h.SuspendAsync();

        await h.ReloadAsync(Config(HostFixtures.None(Key)));
        await h.ReloadAsync(Config(OnDemand()));
        await h.ResumeAsync();

        Assert.Equal(MountState.Ready, h.State(Key));
        Assert.Equal(1, h.Rclone.MountCount(Drive));
    }

    /// <summary>
    /// <b>Protects:</b> decision 5, "a restart of Bosun", and that nothing is persisted. A second
    /// supervisor started over the same collaborators, as a restarted process would be, never learns
    /// of the first one's record.
    /// <b>Catches:</b> a record held anywhere other than the supervisor instance.
    /// </summary>
    [Fact]
    public async Task A_restarted_supervisor_has_no_record()
    {
        var h = await UserMountedAsync();
        await h.SuspendAsync();

        var restarted = new MountSupervisor(
            h.Store, h.TimedRclone, h.Gate, new FakeDriveTypeReader(), h.Time, h.Logger);
        var start = restarted.StartAsync();
        await restarted.DrainAsync();
        await start;
        var resume = restarted.ResumeAsync();
        await restarted.DrainAsync();
        await resume;
        await h.AdvanceAsync(TimeSpan.FromHours(1));

        Assert.Equal(MountState.Ready, restarted.GetSnapshot().Single(s => s.HostKey == Key).State);
        Assert.Equal(1, h.Rclone.MountCount(Drive));
    }

    // ------------------------------------------------------------------------------------------
    // Decision 6: suspend only
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Protects:</b> decision 6. A drive drained because its host failed a deep probe after a
    /// network change comes back to <c>Ready</c> only (rule 6), and a later sleep does not make it a
    /// remount candidate because it was not mounted when the machine suspended.
    /// <b>Catches:</b> a record set by every drain rather than by the suspend sweep.
    /// </summary>
    [Fact]
    public async Task A_drain_for_a_failed_deep_probe_after_a_network_change_does_not_remount()
    {
        var h = await UserMountedAsync();
        h.Probe.SetDeep(Key, DeepProbeOutcome.Failed, times: 1);

        await h.RunAsync(() => h.Supervisor.NetworkChangedAsync());
        await h.AdvanceAsync(TimeSpan.FromMinutes(5));

        Assert.Equal(MountState.Ready, h.State(Key));
        Assert.Equal(1, h.Rclone.MountCount(Drive));

        await h.SuspendAsync();
        await h.ResumeAsync();
        Assert.Equal(MountState.Ready, h.State(Key));
        Assert.Equal(1, h.Rclone.MountCount(Drive));
    }

    /// <summary>
    /// <b>Protects:</b> decision 6 for the probe-failure threshold and the idle timeout: neither drain
    /// is a suspend, so neither brings the drive back, at wake or otherwise.
    /// <b>Catches:</b> a record keyed to "the host was on-demand and had been mounted".
    /// </summary>
    [Fact]
    public async Task Probe_failure_and_idle_timeout_drains_do_not_remount_even_across_a_later_sleep()
    {
        // Idle timeout.
        var idle = await UserMountedAsync(idleUnmountSeconds: 60);
        await idle.AdvanceSecondsAsync(61);
        Assert.Equal(MountState.Ready, idle.State(Key));
        await idle.SuspendAsync();
        await idle.ResumeAsync();
        Assert.Equal(MountState.Ready, idle.State(Key));
        Assert.Equal(1, idle.Rclone.MountCount(Drive));

        // Probe-failure threshold (3 consecutive failures at the 60 s mounted cadence).
        var failing = await UserMountedAsync();
        failing.Probe.SetShallow(Hostname, ShallowProbeOutcome.ConnectionRefused, times: 4);
        await failing.AdvanceAsync(TimeSpan.FromMinutes(4));
        Assert.NotEqual(MountState.Mounted, failing.State(Key));
        await failing.RunAsync(() => failing.Supervisor.RequestRetryNowAsync(Key));
        await failing.SuspendAsync();
        await failing.ResumeAsync();
        Assert.NotEqual(MountState.Mounted, failing.State(Key));
        Assert.Equal(1, failing.Rclone.MountCount(Drive));
    }

    /// <summary>
    /// <b>Protects:</b> decision 6 and rule 6/9 for the other tier: a persistent host is remounted by
    /// its own tier rule, not by this record, and the on-demand sweep does not touch it.
    /// <b>Catches:</b> a record set for persistent hosts (it would then remount a host a second way).
    /// </summary>
    [Fact]
    public async Task A_persistent_host_is_remounted_by_its_own_tier_rule_not_by_the_record()
    {
        var h = new SuspendBudgetHarness(Config(HostFixtures.Persistent("prod", probeIntervalSeconds: 0, drive: "P:")));
        await h.StartAsync();
        Assert.Equal(MountState.Mounted, h.State("prod"));

        await h.SuspendAsync();
        await h.ResumeAsync();

        Assert.Equal(MountState.Mounted, h.State("prod"));
        Assert.Equal(2, h.Rclone.MountCount("P:"));
        Assert.Contains("persistent tier", h.Transitions("prod").Last(t => t.To == MountState.Mounting).Trigger);
    }

    // ------------------------------------------------------------------------------------------
    // Interleavings
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Protects:</b> a resume that arrives before the suspend drain has confirmed. The host is still
    /// <c>Draining</c> at resume; once the drain confirms, the ordinary re-enable runs and its probe is
    /// the first post-resume probe, so the remount happens then, once.
    /// <b>Catches:</b> a record consumed (or lost) by the resume handler's pass over a host it cannot
    /// yet enable, so the host is left unmounted; or a remount before the unmount is confirmed.
    /// </summary>
    [Fact]
    public async Task Resume_before_the_suspend_drain_finished_remounts_once_the_drain_confirms()
    {
        var h = await UserMountedAsync();
        h.Rclone.ConfirmUnmountAtCall(Drive, 3);
        await h.SuspendAsync();
        Assert.Equal(MountState.Draining, h.State(Key));

        await h.ResumeAsync();
        Assert.Equal(MountState.Draining, h.State(Key));
        Assert.Equal(1, h.Rclone.MountCount(Drive)); // not remounted over the unconfirmed unmount

        await h.AdvanceSecondsAsync(20);

        Assert.Equal(MountState.Mounted, h.State(Key));
        Assert.Equal(2, h.Rclone.MountCount(Drive));
    }

    /// <summary>
    /// <b>Protects:</b> a second suspend before the first remount completed (resume while draining,
    /// then the machine suspends again before the drain confirms). The record neither doubles nor is lost:
    /// after the next resume the host is remounted exactly once.
    /// <b>Catches:</b> a second suspend that clears the record, or a remount while still suspended.
    /// </summary>
    [Fact]
    public async Task A_second_suspend_before_the_first_remount_completed_still_remounts_once_at_the_next_resume()
    {
        var h = await UserMountedAsync();
        h.Rclone.ConfirmUnmountAtCall(Drive, 4);
        await h.SuspendAsync();
        await h.ResumeAsync();
        await h.AdvanceSecondsAsync(1);
        await h.SuspendAsync();
        await h.AdvanceSecondsAsync(20); // drain confirms while suspended: no re-enable, no mount
        Assert.Equal(MountState.Disabled, h.State(Key));
        Assert.Equal(1, h.Rclone.MountCount(Drive));

        await h.ResumeAsync();

        Assert.Equal(MountState.Mounted, h.State(Key));
        Assert.Equal(2, h.Rclone.MountCount(Drive));
    }

    /// <summary>
    /// <b>Protects:</b> repeated sleeps. After a successful remount the host is mounted again, so the
    /// next suspend records it again and the next resume remounts it again.
    /// <b>Catches:</b> a record that works once per process.
    /// </summary>
    [Fact]
    public async Task Every_sleep_with_the_drive_up_remounts_it()
    {
        var h = await UserMountedAsync();

        for (var night = 1; night <= 3; night++)
        {
            await h.SuspendAsync();
            await h.ResumeAsync();
            Assert.Equal(MountState.Mounted, h.State(Key));
            Assert.Equal(1 + night, h.Rclone.MountCount(Drive));
        }
    }

    /// <summary>
    /// <b>Protects:</b> a suspend that arrives while the user's mount is in flight (the host is
    /// <c>Mounting</c> with its deep probe outstanding). The mount completes, the suspend drains the
    /// now-mounted drive, and it is recorded and remounted at wake.
    /// <b>Catches:</b> a suspend sweep that records only hosts already <c>Mounted</c> at the moment it
    /// is queued.
    /// </summary>
    [Fact]
    public async Task A_suspend_that_arrives_while_the_user_mount_is_in_flight_is_remounted_at_wake()
    {
        var h = new SuspendBudgetHarness(Config(OnDemand()));
        await h.StartAsync();

        await SuspendWhileMountingAsync(h, DeepProbeOutcome.Success);
        Assert.Equal(MountState.Disabled, h.State(Key));
        Assert.Equal(1, h.Rclone.MountCount(Drive));
        Assert.Equal(1, h.Rclone.UnmountCount(Drive));

        await h.ResumeAsync();

        Assert.Equal(MountState.Mounted, h.State(Key));
        Assert.Equal(2, h.Rclone.MountCount(Drive));
    }

    /// <summary>
    /// <b>Protects:</b> the same interleaving when the user's mount fails: the drive was never up, so
    /// there is nothing to bring back.
    /// <b>Catches:</b> a record set from "the user asked to mount" rather than from "was mounted".
    /// </summary>
    [Fact]
    public async Task A_suspend_that_arrives_while_the_user_mount_fails_does_not_remount_at_wake()
    {
        var h = new SuspendBudgetHarness(Config(OnDemand()));
        await h.StartAsync();

        await SuspendWhileMountingAsync(h, DeepProbeOutcome.Failed);
        await h.ResumeAsync();
        await h.AdvanceAsync(TimeSpan.FromHours(1));

        Assert.Equal(MountState.Ready, h.State(Key));
        Assert.Equal(0, h.Rclone.MountCount(Drive));
    }

    /// <summary>
    /// <b>Protects:</b> a network change while the host is dark between suspend and resume. A
    /// <c>Disabled</c> host is not touched by it, so the record survives and the resume still remounts.
    /// <b>Catches:</b> a network-change handler that consumes the record or mounts the host while
    /// suspended.
    /// </summary>
    [Fact]
    public async Task A_network_change_during_the_sleep_neither_mounts_nor_loses_the_record()
    {
        var h = await UserMountedAsync();
        await h.SuspendAsync();

        await h.RunAsync(() => h.Supervisor.NetworkChangedAsync());
        Assert.Equal(MountState.Disabled, h.State(Key));
        Assert.Equal(1, h.Rclone.MountCount(Drive));

        await h.ResumeAsync();

        Assert.Equal(MountState.Mounted, h.State(Key));
        Assert.Equal(2, h.Rclone.MountCount(Drive));
    }

    /// <summary>
    /// <b>Protects:</b> a network change after resume but before the first probe completes. Resume with
    /// the unmount still unconfirmed leaves the host <c>Draining</c>; a network change then does not
    /// mount it or spend the record, and the first probe after the drain confirms still remounts.
    /// <b>Catches:</b> a network-change handler that treats the draining host as probe-able, or that
    /// remounts it over an unconfirmed unmount.
    /// </summary>
    [Fact]
    public async Task A_network_change_after_resume_but_before_the_first_probe_does_not_spend_the_record()
    {
        var h = await UserMountedAsync();
        h.Rclone.ConfirmUnmountAtCall(Drive, 3);
        await h.SuspendAsync();
        await h.ResumeAsync();
        Assert.Equal(MountState.Draining, h.State(Key));

        await h.RunAsync(() => h.Supervisor.NetworkChangedAsync());
        Assert.Equal(MountState.Draining, h.State(Key));
        Assert.Equal(1, h.Rclone.MountCount(Drive));

        await h.AdvanceSecondsAsync(20);

        Assert.Equal(MountState.Mounted, h.State(Key));
        Assert.Equal(2, h.Rclone.MountCount(Drive));
    }

    /// <summary>
    /// <b>Protects:</b> the same window, when the network change moved the machine somewhere the host
    /// cannot be reached: the first probe (made after the change) fails, the record is dropped, and the
    /// host shows Unreachable.
    /// <b>Catches:</b> a remount decided on a probe result from before the network change.
    /// </summary>
    [Fact]
    public async Task A_network_change_before_the_first_probe_that_leaves_the_host_unreachable_drops_the_record()
    {
        var h = await UserMountedAsync();
        h.Rclone.ConfirmUnmountAtCall(Drive, 3);
        await h.SuspendAsync();
        await h.ResumeAsync();
        await h.RunAsync(() => h.Supervisor.NetworkChangedAsync());
        h.Probe.SetShallow(Hostname, ShallowProbeOutcome.ConnectionRefused, times: 1);

        await h.AdvanceSecondsAsync(20);

        Assert.Equal(MountState.Unreachable, h.State(Key));
        Assert.Equal(1, h.Rclone.MountCount(Drive));
    }

    // ------------------------------------------------------------------------------------------

    /// <summary>Queues a user mount whose deep probe is held open, queues a suspend behind it, then
    /// releases the probe with <paramref name="deepOutcome"/> and runs everything to completion.</summary>
    private static async Task SuspendWhileMountingAsync(SuspendBudgetHarness h, DeepProbeOutcome deepOutcome)
    {
        h.Gate.HoldNextDeep(Key);
        var mountTask = h.Supervisor.RequestMountAsync(Key);
        var pump = h.Supervisor.DrainAsync();

        Assert.True(h.Gate.IsHolding, "setup: the deep probe for the mount should be in flight");
        Assert.Equal(MountState.Mounting, h.State(Key));

        var suspendTask = h.Supervisor.SuspendAsync();
        h.Gate.Release(deepOutcome);

        await pump;
        await h.Supervisor.DrainAsync();
        await mountTask;
        await suspendTask;
    }
}
