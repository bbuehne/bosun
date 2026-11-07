using Bosun.Configuration;
using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Independent.RemountAfterResume;
using Bosun.Tests.Supervisor.Independent.SuspendPreempt;
using Bosun.Tests.Supervisor.Support;
using static Bosun.Tests.Supervisor.Independent.UnparkAndRepairEnd.UnparkSpec;

namespace Bosun.Tests.Supervisor.Independent.UnparkAndRepairEnd;

/// <summary>
/// bs-uzy: a mode change from on-demand to persistent is an explicit act that clears the user park.
/// Written from the spec, not the implementation. Invariants: I1 (every mount the un-park leads to goes
/// through a probe and <c>Ready -&gt; Mounting</c>), I8 (an un-park while a suspend is pending or the
/// system is suspended does not mount before resume), and ADR-015's "only an explicit action clears what
/// an explicit action set".
/// </summary>
/// <remarks>
/// <para>The spec quotes these tests rest on:</para>
/// <list type="bullet">
/// <item>ADR-015, bs-uzy amendment: "that one edit now clears the park, and the host then behaves as an
/// unparked persistent host: if <c>Ready</c> it mounts now, if <c>Unreachable</c> it mounts when it next
/// reaches <c>Ready</c>, and in any other state the ordinary persistent paths mount it later. Every one of
/// those mounts still goes through <c>TryBeginMountAsync</c> and its probe (Invariant I1)."</item>
/// <item>Same amendment: "What does not clear a park: any other edit (hostname, idle seconds, tab colour and
/// so on); a change to <c>on-demand</c> ... and a change from <c>none</c>, which was not in the
/// decision." and "<c>Mounted</c> and parked no longer coexist on any path found."</item>
/// <item>docs/ARCHITECTURE.md §4 rule 5: "No mount happens while the supervisor is suspended — or while a
/// suspend is pending".</item>
/// <item>ADR-022 decision 5: the remount record "is cleared by: the remount itself, a user unmount, the host
/// being removed from the config or its mode changed, or a restart".</item>
/// </list>
/// </remarks>
public sealed class ModeChangeUnparkSpecTests
{
    private static BosunConfig Persistent(params HostConfig[] others) =>
        Config([WithMode(OnDemandHost(), MountMode.Persistent), .. others]);

    private static BosunConfig OnDemand(params HostConfig[] others) => Config([OnDemandHost(), .. others]);

    // ---------------------------------------------------------------- the states the edit can find

    /// <summary>
    /// <b>Protects:</b> "if <c>Ready</c> it mounts now", I1 on that mount, and the tray-visible snapshot.
    /// <b>Catches:</b> option A (respect the park) or no change at all: the host stays parked and unmounted
    /// after the user switched it to persistent; a mount sent without the deep probe of rule 2; the snapshot
    /// still saying parked (or the drive) after the edit.
    /// </summary>
    [Fact]
    public async Task A_parked_Ready_host_edited_to_persistent_is_unparked_and_mounts_now_through_a_deep_probe()
    {
        var h = await ParkedOnDemandAsync();
        var from = Here(h.Log);
        var before = h.Transitions(Key).Count;

        await h.ReloadAsync(Persistent());

        var s = h.Snapshot(Key);
        Assert.False(s.UserParked, "the edit to persistent is the explicit act that clears the park");
        Assert.Equal(MountState.Mounted, s.State);
        Assert.True(s.AdministrativelyEnabled);
        Assert.Equal(Drive, s.Drive);
        Assert.Equal(0, s.ConsecutiveMountFailures);
        Assert.Equal(1, MountCallsAfter(h.Log, Drive, from));
        AssertEveryMountPrecededByPassingDeepProbe(h.Log, Key, Drive, from);
        Assert.Equal(
            [(MountState.Ready, MountState.Mounting), (MountState.Mounting, MountState.Mounted)],
            RemountSpec.Pairs(h.Transitions(Key).Skip(before)));
        Assert.NotNull(h.Rclone.FsAt(Drive));
        Assert.Empty(Errors(h.Logger));
    }

    /// <summary>
    /// <b>Protects:</b> "if <c>Unreachable</c> it mounts when it next reaches <c>Ready</c>", which needs the
    /// host, now persistent, to be polled on the backoff ladder (ADR-014 decision 1); and I1 while it is down.
    /// <b>Catches:</b> the park cleared but the host left in the on-demand "Unreachable is not polled" state
    /// (ADR-014 decision 2), so it never reaches <c>Ready</c> and the drive never returns; or a mount attempted
    /// from <c>Unreachable</c>.
    /// </summary>
    [Fact]
    public async Task A_parked_Unreachable_host_edited_to_persistent_is_unparked_polled_and_mounts_when_it_comes_back()
    {
        var h = await ParkedOnDemandAsync();
        h.Probe.SetShallow(Hostname, ShallowProbeOutcome.Timeout);
        await h.RunAsync(() => h.Supervisor.RequestRetryNowAsync(Key));
        Assert.Equal(MountState.Unreachable, h.State(Key));
        Assert.True(h.Snapshot(Key).UserParked, "setup: retry now is not a remount and must not un-park");
        var from = Here(h.Log);

        await h.ReloadAsync(Persistent());
        Assert.False(h.Snapshot(Key).UserParked);
        var probesAtEdit = h.Probe.ShallowCount(Hostname);

        await h.AdvanceSecondsAsync(60);
        Assert.Equal(MountState.Unreachable, h.State(Key));
        Assert.Equal(0, MountCallsAfter(h.Log, Drive, from));
        Assert.True(
            h.Probe.ShallowCount(Hostname) > probesAtEdit,
            "a persistent Unreachable host is polled on the backoff ladder (ADR-014 decision 1); it was not probed for 60 s after the edit");

        h.Probe.SetShallow(Hostname, ShallowProbeOutcome.Success);
        await h.AdvanceSecondsAsync(400);

        Assert.Equal(MountState.Mounted, h.State(Key));
        Assert.False(h.Snapshot(Key).UserParked);
        AssertEveryMountPrecededByPassingDeepProbe(h.Log, Key, Drive, from);
        Assert.DoesNotContain(
            h.Transitions(Key),
            t => t.To == MountState.Mounting && t.From != MountState.Ready);
    }

    /// <summary>
    /// <b>Protects:</b> the edit arriving while the host is in <c>Probing</c> (the re-enable after the user's
    /// unmount, probe held open). The channel is serial, so the edit is applied once the probe has an outcome:
    /// <c>Ready</c> mounts now, <c>Unreachable</c> mounts when it comes back.
    /// <b>Catches:</b> the un-park keyed to a state snapshot taken before the probe (the edit "lost" because the
    /// host was neither Ready nor Unreachable when the reload was queued); a mount from <c>Unreachable</c>.
    /// </summary>
    [Theory]
    [InlineData(ShallowProbeOutcome.Success)]
    [InlineData(ShallowProbeOutcome.Timeout)]
    public async Task An_edit_to_persistent_queued_while_the_parked_host_is_Probing_unparks_it_and_mounts_it_once_Ready(
        ShallowProbeOutcome probeOutcome)
    {
        var h = new RemountHarness(OnDemand());
        await h.StartAsync();
        await h.MountAsync(Key);
        var from = Here(h.Log);

        h.Hold.HoldNextShallow(Hostname);
        var unmount = h.Supervisor.RequestUnmountAsync(Key);
        var pump = h.Supervisor.DrainAsync();
        Assert.True(h.Hold.IsHoldingShallow, "setup: the re-enable probe after the user's unmount was never reached");
        Assert.Equal(MountState.Probing, h.State(Key));

        var next = Persistent();
        h.Store.Publish(next);
        var reload = h.Supervisor.ConfigChangedAsync(next);
        if (probeOutcome != ShallowProbeOutcome.Success)
        {
            h.Probe.SetShallow(Hostname, probeOutcome);
        }

        h.Hold.ReleaseShallow(probeOutcome);
        await pump.WaitAsync(PreemptSpecHarness.Guard);
        await h.Supervisor.DrainAsync();
        await unmount.WaitAsync(PreemptSpecHarness.Guard);
        await reload.WaitAsync(PreemptSpecHarness.Guard);

        Assert.False(h.Snapshot(Key).UserParked);
        AssertNotMountedWhileParked(h.Snapshot(Key), "after the edit");
        if (probeOutcome == ShallowProbeOutcome.Success)
        {
            Assert.Equal(MountState.Mounted, h.State(Key));
        }
        else
        {
            Assert.Equal(MountState.Unreachable, h.State(Key));
            Assert.Null(h.Rclone.FsAt(Drive));
            h.Probe.SetShallow(Hostname, ShallowProbeOutcome.Success);
            await h.AdvanceSecondsAsync(400);
            Assert.Equal(MountState.Mounted, h.State(Key));
            Assert.False(h.Snapshot(Key).UserParked);
        }

        AssertEveryMountPrecededByPassingDeepProbe(h.Log, Key, Drive, from);
        Assert.Empty(Errors(h.Logger));
    }

    /// <summary>
    /// <b>Protects:</b> a parked host resting <c>Disabled</c> (its user-unmount drain completed while a suspend
    /// was pending, so the re-enable was refused) edited to persistent while the system is suspended: the park
    /// is cleared, nothing mounts while suspended (rule 5, I8), and resume mounts it by its tier through
    /// <c>Disabled -&gt; Probing -&gt; Ready -&gt; Mounting</c> (I1).
    /// <b>Catches:</b> the un-park only implemented for <c>Ready</c>/<c>Unreachable</c>, so the host comes back
    /// parked at wake; or the un-park path mounting while suspended.
    /// </summary>
    [Fact]
    public async Task A_parked_Disabled_host_edited_to_persistent_while_suspended_is_unparked_and_mounts_only_after_resume()
    {
        var h = new PreemptSpecHarness(OnDemand());
        await h.StartAsync();
        await h.RunAsync(() => h.Supervisor.RequestMountAsync(Key));
        Assert.Equal(MountState.Mounted, h.State(Key));

        var hold = h.HRclone.HoldNextAt(OnCancel.Ignore, "unmount", Drive);
        var unmount = h.Supervisor.RequestUnmountAsync(Key);
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(hold, "the user's unmount");
        var suspend = SuspendCaller.Call(h.Supervisor);
        hold.Release();
        await h.FinishAsync(pump, "the user's unmount or the suspend never finished", suspend, unmount);
        await unmount;
        await suspend;
        Assert.Equal(MountState.Disabled, h.State(Key));
        Assert.True(h.Snapshot(Key).UserParked, "setup: a user unmount parks the host (ADR-015)");
        Assert.False(h.IsMountedInRclone(Drive));

        var from = Here(h.Log);
        var next = Persistent();
        h.Store.Publish(next);
        await h.RunAsync(() => h.Supervisor.ConfigChangedAsync(next));

        Assert.False(h.Snapshot(Key).UserParked);
        Assert.DoesNotContain(h.State(Key), new[] { MountState.Mounting, MountState.Mounted });
        await h.AdvanceSecondsAsync(120);
        Assert.Equal(0, MountCallsAfter(h.Log, Drive, from));
        Assert.False(h.IsMountedInRclone(Drive));
        Assert.False(h.Snapshot(Key).UserParked);

        await h.ResumeAsync();

        Assert.Equal(MountState.Mounted, h.State(Key));
        Assert.False(h.Snapshot(Key).UserParked);
        AssertEveryMountPrecededByPassingDeepProbe(h.Log, Key, Drive, from);
        Assert.Empty(h.Errors);
    }

    /// <summary>
    /// <b>Protects:</b> I8 at the moment of the edit: a reload dequeued while a suspend is pending (called, not
    /// yet run) clears the park but must not mount (ADR-023 decision 1: the gate is
    /// <c>suspended || SuspendPending</c>). Resume then mounts it.
    /// <b>Catches:</b> the un-park branch mounting through a path that checks only the <c>suspended</c> flag (or
    /// no gate), creating a drive in the seconds before sleep that survives the sleep.
    /// </summary>
    [Fact]
    public async Task An_edit_to_persistent_dequeued_while_a_suspend_is_pending_unparks_but_does_not_mount_until_resume()
    {
        var other = HostFixtures.OnDemand("other", drive: "R:");
        var h = await ParkedOnDemandAsync(0, other);

        h.Hold.HoldNextShallow(RemountHarness.HostnameOf("other"));
        var retry = h.Supervisor.RequestRetryNowAsync("other");
        var pump = h.Supervisor.DrainAsync();
        Assert.True(h.Hold.IsHoldingShallow, "setup: the other host's retry-now probe was never reached");

        var next = Persistent(other);
        h.Store.Publish(next);
        var reload = h.Supervisor.ConfigChangedAsync(next);
        h.Log.Record("MARK|suspend");
        var suspend = h.Supervisor.SuspendAsync();
        h.Hold.ReleaseShallow(ShallowProbeOutcome.Success);
        await pump.WaitAsync(PreemptSpecHarness.Guard);
        await h.Supervisor.DrainAsync();
        await retry.WaitAsync(PreemptSpecHarness.Guard);
        await reload.WaitAsync(PreemptSpecHarness.Guard);
        await suspend.WaitAsync(PreemptSpecHarness.Guard);

        var mark = h.LastMarkIndex("MARK|suspend");
        Assert.False(h.Snapshot(Key).UserParked);
        Assert.Equal(0, MountCallsAfter(h.Log, Drive, mark));
        Assert.Null(h.Rclone.FsAt(Drive));
        await h.AdvanceSecondsAsync(60);
        Assert.Equal(0, MountCallsAfter(h.Log, Drive, mark));

        await h.ResumeAsync();

        Assert.Equal(MountState.Mounted, h.State(Key));
        Assert.False(h.Snapshot(Key).UserParked);
        Assert.True(MountCallsAfter(h.Log, Drive, h.LastMarkIndex("MARK|resume")) >= 1);
        AssertEveryMountPrecededByPassingDeepProbe(h.Log, Key, Drive, mark);
        Assert.Empty(Errors(h.Logger));
    }

    /// <summary>
    /// <b>Protects:</b> the same for an edit while the system is already suspended (the parked host rests
    /// <c>Ready</c>, nothing was mounted): un-parked at once, no mount while suspended, mounted at resume.
    /// <b>Catches:</b> the un-park mounting while suspended; or the un-park deferred to resume and then lost.
    /// </summary>
    [Fact]
    public async Task An_edit_to_persistent_while_suspended_unparks_at_once_and_mounts_at_resume()
    {
        var h = await ParkedOnDemandAsync();
        await h.SuspendAsync();
        var mark = h.LastMarkIndex("MARK|suspend");

        await h.ReloadAsync(Persistent());

        Assert.False(h.Snapshot(Key).UserParked);
        await h.AdvanceSecondsAsync(120);
        Assert.Equal(0, MountCallsAfter(h.Log, Drive, mark));
        Assert.Null(h.Rclone.FsAt(Drive));

        await h.ResumeAsync();

        Assert.Equal(MountState.Mounted, h.State(Key));
        Assert.False(h.Snapshot(Key).UserParked);
        AssertEveryMountPrecededByPassingDeepProbe(h.Log, Key, Drive, mark);
    }

    // ---------------------------------------------------------------- the mount the un-park leads to fails

    /// <summary>
    /// <b>Protects:</b> the un-park is not conditional on the mount succeeding. A failed mount attempt (deep
    /// probe failing on entry, or <c>mount/mount</c> failing) drains as a mount failure, the host stays
    /// unparked, and it keeps retrying as a persistent host on the mount-retry ladder until it mounts.
    /// <b>Catches:</b> the park restored (or the failure drained as <c>UserUnmount</c>, which re-parks) when the
    /// mount after the edit fails, so a transient failure silently undoes the user's edit; a failed deep probe
    /// routed to <c>Mounted</c> (rule 2).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_unpark_survives_a_failed_mount_and_the_host_keeps_retrying_as_persistent(bool deepProbeFails)
    {
        var h = await ParkedOnDemandAsync();
        var before = h.Transitions(Key).Count;
        var from = Here(h.Log);
        if (deepProbeFails)
        {
            h.Probe.SetDeep(Key, DeepProbeOutcome.Failed, times: 1);
        }
        else
        {
            h.Rclone.FailMount(Drive, new InvalidOperationException("drive letter already in use"), times: 1);
        }

        await h.ReloadAsync(Persistent());

        Assert.False(h.Snapshot(Key).UserParked, "a failed mount must not re-park the host");
        Assert.NotEqual(MountState.Mounted, h.State(Key));
        Assert.Equal(1, h.Snapshot(Key).ConsecutiveMountFailures);
        Assert.Equal([DrainCause.MountFailure], DrainCausesAfter(h.Transitions(Key), before));
        Assert.Null(h.Rclone.FsAt(Drive));

        for (var i = 0; i < 60 && h.State(Key) != MountState.Mounted; i++)
        {
            await h.AdvanceSecondsAsync(5);
            AssertNotMountedWhileParked(h.Snapshot(Key), $"{(i + 1) * 5} s after the failed mount");
            Assert.False(h.Snapshot(Key).UserParked, $"re-parked {(i + 1) * 5} s after the failed mount");
        }

        Assert.Equal(MountState.Mounted, h.State(Key));
        Assert.NotNull(h.Rclone.FsAt(Drive));
        Assert.DoesNotContain(DrainCause.UserUnmount, DrainCausesAfter(h.Transitions(Key), before));
        AssertEveryMountPrecededByPassingDeepProbe(h.Log, Key, Drive, from);
    }

    // ---------------------------------------------------------------- combined and repeated edits

    /// <summary>
    /// <b>Protects:</b> the mode change still un-parks when the same save also changes the drive letter, the
    /// hostname and the idle seconds; the mount uses the NEW drive letter; and the persistent mount is not
    /// idle-unmounted (§4 rule 7: on-demand to persistent "disarms the clock").
    /// <b>Catches:</b> the un-park keyed to "the only change is mode" (a combined save classified as
    /// mount-affecting and the tier branch skipped, leaving the host parked); a mount at the old letter; the
    /// on-demand idle clock left armed on a persistent drive.
    /// </summary>
    [Fact]
    public async Task A_mode_change_combined_with_drive_hostname_and_idle_edits_unparks_and_mounts_at_the_new_letter()
    {
        var h = await ParkedOnDemandAsync();
        var from = Here(h.Log);
        var edited = OnDemandHost() with
        {
            Hostname = "od-renamed.example.internal",
            Mount = OnDemandHost().Mount with { Mode = MountMode.Persistent, Drive = "R:", IdleUnmountSeconds = 30 },
        };

        await h.ReloadAsync(Config(edited));

        var s = h.Snapshot(Key);
        Assert.False(s.UserParked);
        Assert.Equal(MountState.Mounted, s.State);
        Assert.Equal("R:", s.Drive);
        Assert.NotNull(h.Rclone.FsAt("R:"));
        Assert.Equal(0, MountCallsAfter(h.Log, Drive, from));
        Assert.Null(h.Rclone.FsAt(Drive));
        AssertEveryMountPrecededByPassingDeepProbe(h.Log, Key, "R:", from);

        await h.AdvanceSecondsAsync(150);
        Assert.Equal(MountState.Mounted, h.State(Key));
        Assert.NotNull(h.Rclone.FsAt("R:"));
    }

    /// <summary>
    /// <b>Protects:</b> "What does not clear a park: any other edit (hostname, idle seconds, tab colour and so
    /// on)" -- for a parked on-demand host and for a parked persistent host (whose idle probes keep arriving at
    /// <c>Ready</c>, where an un-parked persistent host would auto-mount).
    /// <b>Catches:</b> the un-park triggered by "any reload of this host" or by "the host is persistent after the
    /// reload" rather than by the on-demand-to-persistent change; the pre-bs-7ck behaviour (any reload un-parks).
    /// </summary>
    [Theory]
    [InlineData(MountMode.OnDemand, "hostname")]
    [InlineData(MountMode.OnDemand, "idle")]
    [InlineData(MountMode.OnDemand, "tab")]
    [InlineData(MountMode.OnDemand, "drive")]
    [InlineData(MountMode.Persistent, "hostname")]
    [InlineData(MountMode.Persistent, "idle")]
    [InlineData(MountMode.Persistent, "tab")]
    [InlineData(MountMode.Persistent, "drive")]
    public async Task Any_other_edit_leaves_a_parked_host_parked_and_unmounted(MountMode tier, string edit)
    {
        var host = WithMode(OnDemandHost(probeIntervalSeconds: 60), tier);
        var h = new RemountHarness(Config(host));
        await h.StartAsync();
        if (tier == MountMode.OnDemand)
        {
            await h.MountAsync(Key);
        }

        Assert.Equal(MountState.Mounted, h.State(Key));
        await h.UnmountAsync(Key);
        Assert.True(h.Snapshot(Key).UserParked, "setup: a user unmount parks the host");
        var from = Here(h.Log);

        var edited = edit switch
        {
            "hostname" => host with { Hostname = "od-renamed.example.internal" },
            "idle" => host with { Mount = host.Mount with { IdleUnmountSeconds = 45 } },
            "tab" => host with { Session = host.Session with { TabColor = "#123456" } },
            "drive" => host with { Mount = host.Mount with { Drive = "R:" } },
            _ => throw new ArgumentOutOfRangeException(nameof(edit)),
        };
        await h.ReloadAsync(Config(edited));
        Assert.True(h.Snapshot(Key).UserParked, $"a '{edit}' edit un-parked the host");

        await h.AdvanceSecondsAsync(300);

        Assert.True(h.Snapshot(Key).UserParked, $"a '{edit}' edit un-parked the host");
        Assert.Equal(0, h.Log.Entries.Skip(from + 1).Count(e => e.StartsWith("mount|", StringComparison.Ordinal)));
        Assert.Null(h.Rclone.FsAt(Drive));
        Assert.Null(h.Rclone.FsAt("R:"));
    }

    /// <summary>
    /// <b>Protects:</b> "a change to <c>on-demand</c>" keeps the park, and the later change back to persistent
    /// is the one that clears it (two reloads, each applied before the next).
    /// <b>Catches:</b> the un-park on ANY tier change (persistent to on-demand included), which leaves the
    /// host un-parked on-demand and makes the second edit look like a no-op; or the second edit ignored because
    /// the host "was persistent before" in some stale copy of the config.
    /// </summary>
    [Fact]
    public async Task Persistent_to_on_demand_keeps_the_park_and_the_edit_back_to_persistent_clears_it()
    {
        var persistent = WithMode(OnDemandHost(), MountMode.Persistent);
        var h = new RemountHarness(Config(persistent));
        await h.StartAsync();
        Assert.Equal(MountState.Mounted, h.State(Key));
        await h.UnmountAsync(Key);
        Assert.True(h.Snapshot(Key).UserParked, "setup: a user unmount parks a persistent host too");
        var from = Here(h.Log);

        await h.ReloadAsync(Config(OnDemandHost()));
        Assert.True(h.Snapshot(Key).UserParked, "a change to on-demand does not clear a park");
        Assert.Null(h.Rclone.FsAt(Drive));

        await h.ReloadAsync(Config(persistent));
        Assert.False(h.Snapshot(Key).UserParked);
        Assert.Equal(MountState.Mounted, h.State(Key));
        Assert.Equal(1, MountCallsAfter(h.Log, Drive, from));
        AssertEveryMountPrecededByPassingDeepProbe(h.Log, Key, Drive, from);
    }

    /// <summary>
    /// <b>Protects:</b> two saves in quick succession (both queued before the channel runs either): to
    /// persistent and straight back to on-demand. The first un-parks and mounts (the host is <c>Ready</c>);
    /// the second keeps the live mount (persistent to on-demand "keeps any live mount") and, having nothing to
    /// clear, leaves the host un-parked. Never <c>Mounted</c> while parked.
    /// <b>Catches:</b> the second reload re-reading a stale park (or restoring a saved one), leaving a host that
    /// is <c>Mounted</c> and parked -- the state behind bs-uzy's wake remount.
    /// </summary>
    [Fact]
    public async Task Two_quick_saves_to_persistent_and_back_leave_the_host_mounted_and_unparked()
    {
        var h = await ParkedOnDemandAsync();
        var from = Here(h.Log);

        var toPersistent = Persistent();
        var back = OnDemand();
        h.Store.Publish(toPersistent);
        var first = h.Supervisor.ConfigChangedAsync(toPersistent);
        h.Store.Publish(back);
        var second = h.Supervisor.ConfigChangedAsync(back);
        await h.Supervisor.DrainAsync();
        await first;
        await second;

        var s = h.Snapshot(Key);
        AssertNotMountedWhileParked(s, "after both saves");
        Assert.False(s.UserParked);
        Assert.Equal(MountState.Mounted, s.State);
        AssertEveryMountPrecededByPassingDeepProbe(h.Log, Key, Drive, from);

        // The host is on-demand again with no idle timeout: nothing takes the drive down.
        await h.AdvanceSecondsAsync(120);
        Assert.Equal(MountState.Mounted, h.State(Key));
        Assert.False(h.Snapshot(Key).UserParked);
    }

    // ---------------------------------------------------------------- ADR-022 interplay

    /// <summary>
    /// <b>Protects:</b> bs-uzy's own reproduction, with the decided behaviour: user unmount (parked), edit to
    /// persistent (un-parked, mounted), edit back to on-demand (mount kept), suspend (ADR-022 record), resume
    /// (remounted by the record). The wake remount is now correct because the host is no longer parked; at no
    /// sampled point is it <c>Mounted</c> while parked.
    /// <b>Catches:</b> the pre-fix state: the edit mounting a host it left parked, so the host is Mounted and
    /// parked through the suspend; and a record-driven remount without a deep probe (I1).
    /// </summary>
    [Fact]
    public async Task The_bs_uzy_sequence_never_has_the_host_mounted_while_parked()
    {
        var h = await ParkedOnDemandAsync();
        var from = Here(h.Log);

        await h.ReloadAsync(Persistent());
        AssertNotMountedWhileParked(h.Snapshot(Key), "after the edit to persistent");
        Assert.Equal(MountState.Mounted, h.State(Key));

        await h.ReloadAsync(OnDemand());
        AssertNotMountedWhileParked(h.Snapshot(Key), "after the edit back to on-demand");
        Assert.Equal(MountState.Mounted, h.State(Key));
        Assert.False(h.Snapshot(Key).UserParked);

        await h.SuspendAsync();
        AssertNotMountedWhileParked(h.Snapshot(Key), "after the suspend");
        Assert.Null(h.Rclone.FsAt(Drive));

        await h.ResumeAsync();
        AssertNotMountedWhileParked(h.Snapshot(Key), "after the resume");
        Assert.Equal(MountState.Mounted, h.State(Key));
        Assert.False(h.Snapshot(Key).UserParked);
        AssertEveryMountPrecededByPassingDeepProbe(h.Log, Key, Drive, from);
    }

    /// <summary>
    /// <b>Protects:</b> "a user unmount after the un-park parks again": the edit is a one-time act, not a
    /// standing "never park this host". After the user unmounts the now-persistent host it stays down through
    /// its idle probes (which arrive at <c>Ready</c>) and through a suspend and resume (rule 5: a parked host
    /// is not mounted by resume; rule 9a: no record for a host that was not mounted at suspend).
    /// <b>Catches:</b> the edit implemented as a sticky "un-parkable" flag, or the persistent auto-mount
    /// ignoring a park set after the edit.
    /// </summary>
    [Fact]
    public async Task A_user_unmount_after_the_unpark_parks_the_host_again()
    {
        var h = await ParkedOnDemandAsync();
        await h.ReloadAsync(Persistent());
        Assert.Equal(MountState.Mounted, h.State(Key));

        await h.UnmountAsync(Key);
        Assert.True(h.Snapshot(Key).UserParked);
        var from = Here(h.Log);

        await h.AdvanceSecondsAsync(300);
        await h.SuspendAsync();
        await h.ResumeAsync();
        await h.AdvanceSecondsAsync(120);

        Assert.True(h.Snapshot(Key).UserParked);
        Assert.Equal(0, MountCallsAfter(h.Log, Drive, from));
        Assert.Null(h.Rclone.FsAt(Drive));
    }

    /// <summary>
    /// <b>Protects:</b> ADR-022 decision 5, "its mode changed" clears the record, across the un-park edit: an
    /// on-demand host the user mounted is suspend-drained (record set); while suspended it is edited to
    /// persistent and back to on-demand; at resume it is an on-demand host with no record, so it rests
    /// <c>Ready</c> and unmounted.
    /// <b>Catches:</b> a record that survives a mode change (cleared only on some directions, or not at all),
    /// mounting an on-demand drive at wake that the record no longer covers.
    /// </summary>
    [Fact]
    public async Task A_mode_change_round_trip_while_suspended_clears_the_remount_record()
    {
        var h = new RemountHarness(OnDemand());
        await h.StartAsync();
        await h.MountAsync(Key);
        await h.SuspendAsync();
        Assert.Null(h.Rclone.FsAt(Drive));
        var mark = h.LastMarkIndex("MARK|suspend");

        await h.ReloadAsync(Persistent());
        await h.ReloadAsync(OnDemand());
        await h.ResumeAsync();
        await h.AdvanceSecondsAsync(60);

        Assert.Equal(MountState.Ready, h.State(Key));
        Assert.False(h.Snapshot(Key).UserParked);
        Assert.Equal(0, MountCallsAfter(h.Log, Drive, mark));
        Assert.Null(h.Rclone.FsAt(Drive));
    }

    // ---------------------------------------------------------------- what the decision does not cover

    /// <summary>
    /// <b>Protects:</b> "What does not clear a park: ... a change from <c>none</c>, which was not in the
    /// decision." A parked host edited to <c>none</c> and then to <c>persistent</c> stays parked and unmounted.
    /// (bs-uzy.1 item 2 asks the maintainer whether to change this; until then this is the spec.)
    /// <b>Catches:</b> the un-park keyed to "the new mode is persistent" rather than to the on-demand-to-
    /// persistent change.
    /// </summary>
    [Fact]
    public async Task A_parked_host_edited_to_none_and_then_to_persistent_stays_parked()
    {
        var h = await ParkedOnDemandAsync();
        var from = Here(h.Log);

        await h.ReloadAsync(Config(WithMode(OnDemandHost(), MountMode.None)));
        await h.ReloadAsync(Persistent());
        await h.AdvanceSecondsAsync(300);

        Assert.True(h.Snapshot(Key).UserParked, "a change from none does not clear a park (ADR-015, bs-uzy amendment)");
        Assert.Equal(0, MountCallsAfter(h.Log, Drive, from));
        Assert.Null(h.Rclone.FsAt(Drive));
    }

    /// <summary>
    /// <b>Protects:</b> the amendment's "in any other state the ordinary persistent paths mount it later",
    /// for the state it does not spell out: the user's own unmount still <c>Draining</c> (unconfirmed) when the
    /// edit to persistent lands. Read literally, the edit clears the park and the drain's completion must not
    /// set it again.
    /// <b>Catches:</b> the park set at drain completion (cause <c>UserUnmount</c>) after the edit already
    /// cleared it, leaving a persistent host parked although the user's last act was "use this drive again".
    /// </summary>
    [Fact(Skip = "bs-uzy.1 independent: ADR-015's bs-uzy amendment says that after the edit to persistent the host 'behaves as an unparked persistent host ... in any other state the ordinary persistent paths mount it later'. Observed: an edit landing while the user's own unmount is still Draining leaves the host parked once the drain completes (UserParked=true, never remounted). Already filed as bs-uzy.1 item 1 (needs-decision); kept skipped until the maintainer decides.")]
    public async Task An_edit_to_persistent_while_the_users_unmount_is_still_draining_leaves_the_host_unparked()
    {
        var h = new RemountHarness(OnDemand());
        await h.StartAsync();
        await h.MountAsync(Key);
        h.Rclone.ConfirmUnmountAtCall(Drive, 2);
        await h.UnmountAsync(Key);
        Assert.Equal(MountState.Draining, h.State(Key));
        var from = Here(h.Log);

        await h.ReloadAsync(Persistent());
        await h.AdvanceSecondsAsync(120);

        Assert.False(h.Snapshot(Key).UserParked);
        Assert.Equal(MountState.Mounted, h.State(Key));
        AssertEveryMountPrecededByPassingDeepProbe(h.Log, Key, Drive, from);
    }
}
