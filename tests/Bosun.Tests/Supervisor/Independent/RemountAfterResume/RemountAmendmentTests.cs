using Bosun.Configuration;
using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;
using Xunit.Abstractions;
using static Bosun.Tests.Supervisor.Independent.RemountAfterResume.RemountSpec;

namespace Bosun.Tests.Supervisor.Independent.RemountAfterResume;

/// <summary>
/// The ADR-022 amendment (2026-11-01, bs-hrg). When the first post-resume probe reaches <c>Ready</c>
/// but a LOCAL, process-wide gate keeps the remount from going ahead (mounting unavailable,
/// <c>rclone rcd</c> failed ADR-017's <c>core/version</c> gate, or the system is suspended again), the
/// remount record is KEPT; when the gate clears the host is probed once more, mounted if that probe
/// reaches <c>Ready</c> (deep probe first, I1), and the record is dropped if it does not. What the
/// amendment leaves alone is pinned here too: a host-side failure of the first probe still drops the
/// record, and a remount that fails while mounting IS available is still not retried.
/// Fakes and a fake clock only (see <see cref="RemountHarness"/>).
/// </summary>
public sealed class RemountAmendmentTests(ITestOutputHelper output)
{
    private static readonly MountingAvailability RcdRestarting =
        MountingAvailability.Unavailable(MountingUnavailableCause.RcloneUnhealthy, "rcd restarting");

    /// <summary>User-mounted, suspended, mounting unavailable, resumed: the host rests in Ready and the
    /// record (if the amendment holds) is waiting. Asserts the setup, so a test failing later is
    /// failing on what it is about.</summary>
    private static async Task<(RemountHarness H, int ResumeMark)> WaitingForAvailabilityAsync()
    {
        var h = await UserMountedOnDemandAsync();
        await h.SuspendAsync();
        await h.RunAsync(() => h.Supervisor.SetMountingAvailabilityAsync(RcdRestarting));
        await h.ResumeAsync();
        var resumeMark = h.LastMarkIndex("MARK|resume");
        Assert.Equal(MountState.Ready, h.State(OnDemandKey));
        Assert.Equal(0, MountsAfter(h, OnDemandDrive, resumeMark));
        return (h, resumeMark);
    }

    private static Task MakeAvailableAsync(RemountHarness h) =>
        h.RunAsync(() => h.Supervisor.SetMountingAvailabilityAsync(MountingAvailability.Available));

    // ------------------------------------------------------------------------------------------
    // The amended rule
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Protects:</b> the amendment's core: while mounting is unavailable the record is kept, and the
    /// host is left alone (no probe traffic beyond its ordinary idle probing, no deep probe); when
    /// availability returns the host is re-probed ONCE, in place, and mounted with the deep probe
    /// first.
    /// <b>Catches:</b> the record spent by the first probe; a mount without a fresh shallow probe; a
    /// mount that skips the deep probe (I1); a second mount.
    /// </summary>
    [Fact]
    public async Task The_record_is_kept_while_mounting_is_unavailable_and_the_host_is_re_probed_and_mounted_when_it_returns()
    {
        var (h, resumeMark) = await WaitingForAvailabilityAsync();
        var deepBefore = h.Probe.DeepCount(OnDemandKey);

        // Inside the ten-minute wait limit (ADR-022's second amendment, bs-ane); RemountExpiryTests
        // covers what happens past it.
        await h.AdvanceAsync(TimeSpan.FromMinutes(9));
        Assert.Equal(MountState.Ready, h.State(OnDemandKey));
        Assert.Equal(deepBefore, h.Probe.DeepCount(OnDemandKey));
        var shallowBefore = h.Probe.ShallowCount(OnDemandHostname);
        var transitionsBefore = h.Transitions(OnDemandKey).Count;
        var availableMark = h.Log.Entries.Count - 1;

        await MakeAvailableAsync(h);

        output.WriteLine(h.TransitionDump());
        Assert.Equal(MountState.Mounted, h.State(OnDemandKey));
        Assert.Equal(1, MountsAfter(h, OnDemandDrive, resumeMark));
        Assert.Equal(shallowBefore + 1, h.Probe.ShallowCount(OnDemandHostname));
        Assert.Equal(deepBefore + 1, h.Probe.DeepCount(OnDemandKey));
        AssertEveryMountPrecededByPassingDeepProbe(h, OnDemandKey, OnDemandDrive, resumeMark);
        var after = h.LogAfter(availableMark).ToList();
        var shallowAt = after.FindIndex(e => e.StartsWith($"shallow|{OnDemandHostname}|", StringComparison.Ordinal));
        var deepAt = after.FindIndex(e => e.StartsWith($"deep|{OnDemandKey}|", StringComparison.Ordinal));
        var mountAt = after.FindIndex(e => e == $"mount|{OnDemandDrive}");
        Assert.True(shallowAt < deepAt && deepAt < mountAt, "expected re-probe, then deep probe, then mount");
        Assert.Equal(
            [(MountState.Ready, MountState.Mounting), (MountState.Mounting, MountState.Mounted)],
            Pairs(h.Transitions(OnDemandKey).Skip(transitionsBefore)));
    }

    /// <summary>
    /// <b>Protects:</b> the amendment's "if not, drop the record": mounting returns but the host no
    /// longer answers. It becomes <c>Unreachable</c>, nothing mounts, and nothing mounts it later
    /// either (a retry-now onto a working network, another availability flap, time).
    /// <b>Catches:</b> a record kept past its resolving probe, which would be the late surprise mount
    /// ADR-022 decision 3 rejects.
    /// </summary>
    [Fact]
    public async Task When_availability_returns_and_the_host_is_now_unreachable_the_record_is_dropped()
    {
        var (h, resumeMark) = await WaitingForAvailabilityAsync();
        h.Probe.SetShallow(OnDemandHostname, ShallowProbeOutcome.ConnectionRefused);

        await MakeAvailableAsync(h);

        Assert.Equal(MountState.Unreachable, h.State(OnDemandKey));
        Assert.Equal(0, MountsAfter(h, OnDemandDrive, resumeMark));

        h.Probe.SetShallow(OnDemandHostname, ShallowProbeOutcome.Success);
        await h.RunAsync(() => h.Supervisor.RequestRetryNowAsync(OnDemandKey));
        Assert.Equal(MountState.Ready, h.State(OnDemandKey));
        await h.RunAsync(() => h.Supervisor.SetMountingAvailabilityAsync(RcdRestarting));
        await MakeAvailableAsync(h);
        await h.RunAsync(() => h.Supervisor.NetworkChangedAsync());
        await h.AdvanceAsync(TimeSpan.FromHours(2));

        Assert.Equal(MountState.Ready, h.State(OnDemandKey));
        Assert.Equal(0, MountsAfter(h, OnDemandDrive, resumeMark));
    }

    /// <summary>
    /// <b>Protects:</b> "a failed first probe for a host-side reason (unreachable) drops the record",
    /// unchanged by the amendment, even when mounting is also unavailable at that moment: the host
    /// did not answer, so there is nothing to wait for.
    /// <b>Catches:</b> an implementation that keeps the record whenever a gate is closed, without
    /// first asking whether the host answered.
    /// </summary>
    [Fact]
    public async Task A_failed_first_probe_drops_the_record_even_while_mounting_is_unavailable()
    {
        var h = await UserMountedOnDemandAsync();
        await h.SuspendAsync();
        await h.RunAsync(() => h.Supervisor.SetMountingAvailabilityAsync(RcdRestarting));
        h.Probe.SetShallow(OnDemandHostname, ShallowProbeOutcome.ConnectionRefused, times: 1);
        await h.ResumeAsync();
        var resumeMark = h.LastMarkIndex("MARK|resume");
        Assert.Equal(MountState.Unreachable, h.State(OnDemandKey));

        await MakeAvailableAsync(h);
        await h.RunAsync(() => h.Supervisor.RequestRetryNowAsync(OnDemandKey));
        await MakeAvailableAsync(h);
        await h.AdvanceAsync(TimeSpan.FromHours(1));

        Assert.Equal(MountState.Ready, h.State(OnDemandKey));
        Assert.Equal(0, MountsAfter(h, OnDemandDrive, resumeMark));
    }

    /// <summary>
    /// <b>Protects:</b> both local gates must clear: mounting is back but <c>rclone rcd</c> is still
    /// silent on <c>core/version</c>, so nothing is probed for the decision or mounted; when rcd
    /// answers (the reconciliation tick) the host is mounted once.
    /// <b>Catches:</b> a sweep that opens on the availability gate alone and mounts against a dead rcd
    /// (the failure the amendment exists to prevent).
    /// </summary>
    [Fact]
    public async Task Both_gates_must_clear_before_the_host_is_probed_and_mounted()
    {
        var h = await UserMountedOnDemandAsync();
        await h.SuspendAsync();
        await h.RunAsync(() => h.Supervisor.SetMountingAvailabilityAsync(RcdRestarting));
        h.Rc.VersionFails = true;
        await h.ResumeAsync();
        var resumeMark = h.LastMarkIndex("MARK|resume");

        await MakeAvailableAsync(h);
        await h.AdvanceAsync(TimeSpan.FromMinutes(2));
        Assert.Equal(MountState.Ready, h.State(OnDemandKey));
        Assert.Equal(0, MountsAfter(h, OnDemandDrive, resumeMark));

        h.Rc.VersionFails = false;
        await h.AdvanceAsync(TimeSpan.FromSeconds(31));

        Assert.Equal(MountState.Mounted, h.State(OnDemandKey));
        Assert.Equal(1, MountsAfter(h, OnDemandDrive, resumeMark));
        AssertEveryMountPrecededByPassingDeepProbe(h, OnDemandKey, OnDemandDrive, resumeMark);
    }

    /// <summary>
    /// <b>Protects:</b> the amendment for ADR-017's gate when rcd answers again but the host does not:
    /// the record is dropped, nothing mounts.
    /// <b>Catches:</b> a deferred remount that skips the "probe once more" and mounts on stale
    /// reachability.
    /// </summary>
    [Fact]
    public async Task When_rcd_answers_again_but_the_host_is_unreachable_the_record_is_dropped()
    {
        var h = await UserMountedOnDemandAsync();
        await h.SuspendAsync();
        h.Rc.VersionFails = true;
        await h.ResumeAsync();
        var resumeMark = h.LastMarkIndex("MARK|resume");
        Assert.Equal(MountState.Ready, h.State(OnDemandKey));

        h.Rc.VersionFails = false;
        h.Probe.SetShallow(OnDemandHostname, ShallowProbeOutcome.Timeout);
        await h.AdvanceAsync(TimeSpan.FromSeconds(31));

        Assert.Equal(MountState.Unreachable, h.State(OnDemandKey));
        Assert.Equal(0, MountsAfter(h, OnDemandDrive, resumeMark));

        h.Probe.SetShallow(OnDemandHostname, ShallowProbeOutcome.Success);
        await h.RunAsync(() => h.Supervisor.RequestRetryNowAsync(OnDemandKey));
        await h.AdvanceAsync(TimeSpan.FromHours(1));
        Assert.Equal(MountState.Ready, h.State(OnDemandKey));
        Assert.Equal(0, MountsAfter(h, OnDemandDrive, resumeMark));
    }

    // ------------------------------------------------------------------------------------------
    // What the amendment leaves alone
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Protects:</b> "A remount whose own deep probe or mount/mount fails while mounting IS available
    /// is not retried", on the deferred path too. The waited-for remount is a single attempt: a failing
    /// <c>mount/mount</c>, or a failing deep probe, leaves the host in Ready and nothing retries it.
    /// <b>Catches:</b> a deferred remount that keeps its record through its own attempt, and so retries
    /// on every later availability flap or tick.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_deferred_remount_that_fails_while_mounting_is_available_is_not_retried(bool deepProbeFails)
    {
        var (h, resumeMark) = await WaitingForAvailabilityAsync();
        if (deepProbeFails)
        {
            h.Probe.SetDeep(OnDemandKey, DeepProbeOutcome.Failed, times: 1);
        }
        else
        {
            h.Rclone.FailMount(OnDemandDrive, new InvalidOperationException("scripted mount failure"), times: 1);
        }

        await MakeAvailableAsync(h);
        await h.AdvanceAsync(TimeSpan.FromHours(1));
        await h.RunAsync(() => h.Supervisor.SetMountingAvailabilityAsync(RcdRestarting));
        await MakeAvailableAsync(h);
        await h.AdvanceAsync(TimeSpan.FromHours(1));

        Assert.Equal(MountState.Ready, h.State(OnDemandKey));
        Assert.Equal(deepProbeFails ? 0 : 1, MountsAfter(h, OnDemandDrive, resumeMark));
        Assert.Null(h.Rclone.FsAt(OnDemandDrive));
    }

    // ------------------------------------------------------------------------------------------
    // Clearing the record while it waits
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Protects:</b> ADR-022 decision 5 during the wait: the user unmounts the host (the click lands
    /// on a Ready host, where the unmount itself is a no-op) and the record is cleared, so the drive
    /// does not return when mounting does.
    /// <b>Catches:</b> a record cleared only when the host is Mounted or Draining, which undoes an
    /// explicit unmount (ADR-015).
    /// </summary>
    [Fact]
    public async Task A_user_unmount_while_the_record_waits_clears_it()
    {
        var (h, resumeMark) = await WaitingForAvailabilityAsync();

        await h.UnmountAsync(OnDemandKey);
        await MakeAvailableAsync(h);
        await h.AdvanceAsync(TimeSpan.FromHours(1));

        Assert.Equal(MountState.Ready, h.State(OnDemandKey));
        Assert.Equal(0, MountsAfter(h, OnDemandDrive, resumeMark));
    }

    /// <summary>
    /// <b>Protects:</b> decision 5's "mode changed" during the wait: the host is set to
    /// <c>mode = "none"</c> and back; when mounting returns the drive does not come back.
    /// <b>Catches:</b> a record that survives a round trip through another tier.
    /// </summary>
    [Fact]
    public async Task A_mode_change_while_the_record_waits_clears_it()
    {
        var (h, resumeMark) = await WaitingForAvailabilityAsync();

        await h.ReloadAsync(HostFixtures.Build(HostFixtures.Global(), HostFixtures.None(OnDemandKey)));
        await h.ReloadAsync(OneOnDemand());
        await MakeAvailableAsync(h);
        await h.AdvanceAsync(TimeSpan.FromHours(1));

        Assert.Equal(MountState.Ready, h.State(OnDemandKey));
        Assert.Equal(0, MountsAfter(h, OnDemandDrive, resumeMark));
    }

    /// <summary>
    /// <b>Protects:</b> a user's own click while the record waits (rcd gate failed, availability still
    /// reads available, so the click is accepted): the host mounts once, and the record is spent by that
    /// mount, so it does not mount a second time when rcd later answers, nor after the user unmounts.
    /// <b>Catches:</b> a record that outlives a mount made by other means.
    /// </summary>
    [Fact]
    public async Task A_user_mount_click_while_the_record_waits_supersedes_it()
    {
        var h = await UserMountedOnDemandAsync();
        await h.SuspendAsync();
        h.Rc.VersionFails = true;
        await h.ResumeAsync();
        var resumeMark = h.LastMarkIndex("MARK|resume");
        Assert.Equal(MountState.Ready, h.State(OnDemandKey));

        await h.MountAsync(OnDemandKey);
        Assert.Equal(MountState.Mounted, h.State(OnDemandKey));
        await h.UnmountAsync(OnDemandKey);
        Assert.Equal(MountState.Ready, h.State(OnDemandKey));

        h.Rc.VersionFails = false;
        await h.AdvanceAsync(TimeSpan.FromMinutes(5));

        Assert.Equal(MountState.Ready, h.State(OnDemandKey));
        Assert.Equal(1, MountsAfter(h, OnDemandDrive, resumeMark));
    }

    /// <summary>
    /// <b>Protects:</b> "network change neither spends nor sets" the record, extended to the wait: a
    /// network change while mounting is unavailable force-probes the host but neither mounts it nor
    /// drops the record; the record is still honoured when mounting returns.
    /// <b>Catches:</b> a network change that resolves (spends or drops) a record whose gate is closed.
    /// </summary>
    [Fact]
    public async Task A_network_change_while_the_record_waits_neither_mounts_nor_loses_it()
    {
        var (h, resumeMark) = await WaitingForAvailabilityAsync();

        await h.RunAsync(() => h.Supervisor.NetworkChangedAsync());
        Assert.Equal(MountState.Ready, h.State(OnDemandKey));
        Assert.Equal(0, MountsAfter(h, OnDemandDrive, resumeMark));

        await MakeAvailableAsync(h);

        Assert.Equal(MountState.Mounted, h.State(OnDemandKey));
        Assert.Equal(1, MountsAfter(h, OnDemandDrive, resumeMark));
    }

    // ------------------------------------------------------------------------------------------
    // bs-bv0: nothing mounts while suspended
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Protects:</b> I8 / bs-bv0 with a waiting record: the machine is suspended again while the
    /// record waits, and mounting becomes available during the sleep. Nothing mounts until resume
    /// (the deferred sweep does not run, and does not spend the record); resume then re-probes the
    /// Ready host and mounts it, deep probe first.
    /// <b>Catches:</b> a deferred remount that fires while suspended (a drive that survives the sleep),
    /// and one that spends the record in the refusal so resume has nothing to do.
    /// </summary>
    [Fact]
    public async Task Nothing_mounts_while_suspended_again_and_resume_then_mounts_the_waiting_host()
    {
        var (h, firstResumeMark) = await WaitingForAvailabilityAsync();

        await h.SuspendAsync();
        var suspendMark = h.LastMarkIndex("MARK|suspend");
        await MakeAvailableAsync(h);
        await h.RunAsync(() => h.Supervisor.OnRcloneRestartedAsync());
        await h.RunAsync(() => h.Supervisor.NetworkChangedAsync());
        await h.AdvanceAsync(TimeSpan.FromHours(2));

        Assert.Equal(0, MountsAfter(h, OnDemandDrive, suspendMark));
        Assert.Null(h.Rclone.FsAt(OnDemandDrive));

        await h.ResumeAsync();
        var resumeMark = h.LastMarkIndex("MARK|resume");

        Assert.Equal(MountState.Mounted, h.State(OnDemandKey));
        Assert.Equal(1, MountsAfter(h, OnDemandDrive, resumeMark));
        Assert.Equal(1, MountsAfter(h, OnDemandDrive, firstResumeMark));
        AssertEveryMountPrecededByPassingDeepProbe(h, OnDemandKey, OnDemandDrive, firstResumeMark);
    }

    /// <summary>
    /// <b>Protects:</b> a second sleep while the record waits does not lose it: mounting is still
    /// unavailable at the second resume, the host stays Ready and unmounted, and the record is honoured
    /// when mounting finally returns.
    /// <b>Catches:</b> a resume that resolves a kept record against a closed gate.
    /// </summary>
    [Fact]
    public async Task A_second_sleep_with_mounting_still_unavailable_keeps_the_record()
    {
        var (h, resumeMark) = await WaitingForAvailabilityAsync();

        await h.SuspendAsync();
        await h.ResumeAsync();
        Assert.Equal(MountState.Ready, h.State(OnDemandKey));
        Assert.Equal(0, MountsAfter(h, OnDemandDrive, resumeMark));

        await MakeAvailableAsync(h);

        Assert.Equal(MountState.Mounted, h.State(OnDemandKey));
        Assert.Equal(1, MountsAfter(h, OnDemandDrive, resumeMark));
    }

    // ------------------------------------------------------------------------------------------
    // Rule 10: Probing stays one-shot
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Protects:</b> rule 10 and "no new transition". Re-probing the waiting host is in place: a
    /// <c>Ready</c> host is not routed through <c>Probing</c>, a host that the re-probe finds
    /// unreachable goes <c>Ready -> Unreachable</c>, and a <c>Disabled</c> host (suspend drain not yet
    /// confirmed at resume) takes <c>Disabled -> Probing</c> exactly once.
    /// <b>Catches:</b> the deferred re-probe implemented as an enable (<c>Probing</c> from
    /// <c>Ready</c> or <c>Unreachable</c>), and a second <c>Probing</c> for the late-draining host.
    /// </summary>
    [Fact]
    public async Task Probing_stays_one_shot_through_the_deferred_remount()
    {
        var config = HostFixtures.Build(
            HostFixtures.Global(),
            HostFixtures.OnDemand("up", drive: "Q:"),
            HostFixtures.OnDemand("gone", drive: "R:"),
            HostFixtures.OnDemand("late", drive: "S:"));
        var h = new RemountHarness(config);
        await h.StartAsync();
        await h.MountAsync("up");
        await h.MountAsync("gone");
        await h.MountAsync("late");
        h.Rclone.ConfirmUnmountAtCall("S:", 2);
        var preSuspend = new[] { "up", "gone", "late" }.ToDictionary(k => k, k => h.Transitions(k).Count);

        await h.SuspendAsync();
        await h.RunAsync(() => h.Supervisor.SetMountingAvailabilityAsync(RcdRestarting));
        await h.ResumeAsync();
        Assert.Equal(MountState.Ready, h.State("up"));
        Assert.Equal(MountState.Ready, h.State("gone"));
        Assert.Equal(MountState.Draining, h.State("late"));
        await h.AdvanceAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(MountState.Ready, h.State("late"));
        var before = new[] { "up", "gone", "late" }.ToDictionary(k => k, k => h.Transitions(k).Count);

        h.Probe.SetShallow(RemountHarness.HostnameOf("gone"), ShallowProbeOutcome.ConnectionRefused);
        await MakeAvailableAsync(h);
        await h.AdvanceAsync(TimeSpan.FromMinutes(1));

        output.WriteLine(h.TransitionDump());
        Assert.Equal(MountState.Mounted, h.State("up"));
        Assert.Equal(MountState.Unreachable, h.State("gone"));
        Assert.Equal(MountState.Mounted, h.State("late"));
        Assert.Equal(
            [(MountState.Ready, MountState.Mounting), (MountState.Mounting, MountState.Mounted)],
            Pairs(h.Transitions("up").Skip(before["up"])));
        Assert.Equal([(MountState.Ready, MountState.Unreachable)], Pairs(h.Transitions("gone").Skip(before["gone"])));
        Assert.Equal(
            [(MountState.Ready, MountState.Mounting), (MountState.Mounting, MountState.Mounted)],
            Pairs(h.Transitions("late").Skip(before["late"])));

        var history = h.Supervisor.GetTransitionHistory();
        Assert.All(history.Where(t => t.To == MountState.Probing), t =>
            Assert.True(t.From == MountState.Disabled, $"{t.HostKey} entered Probing from {t.From}{Environment.NewLine}{h.TransitionDump()}"));
        foreach (var key in new[] { "up", "gone", "late" })
        {
            Assert.Equal(1, h.Transitions(key).Skip(preSuspend[key]).Count(t => t.To == MountState.Probing));
        }
    }
}
