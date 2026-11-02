using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;
using static Bosun.Tests.Supervisor.Independent.RemountAfterResume.RemountSpec;

namespace Bosun.Tests.Supervisor.Independent.RemountAfterResume;

/// <summary>
/// ADR-022 (bs-hrg) / docs/ARCHITECTURE.md §4 rule 9a, written from the decision text: an on-demand
/// drive the user mounted comes back after a sleep -- but only through <c>Ready -&gt; Mounting</c>
/// with the deep probe first (I1), only if the FIRST post-resume probe reaches <c>Ready</c>, and
/// never later on its own.
/// </summary>
/// <remarks>
/// Every collaborator is an in-memory double and time is a fake clock (see <see cref="RemountHarness"/>).
/// No test here can reach a real drive letter, <c>rclone rcd</c>, WinFsp or the fragment path.
/// </remarks>
public sealed class RemountAfterResumeSpecTests
{
    /// <summary>
    /// <b>Protects:</b> ADR-022 Decisions 1-2 and rule 9a's "No new transition": the remount path is
    /// exactly <c>Disabled -&gt; Probing -&gt; Ready -&gt; Mounting -&gt; Mounted</c>, and on the call log
    /// the post-resume order is shallow probe, then passing deep probe, then <c>mount/mount</c>.
    /// <b>Catches:</b> a remount that is not done at all (the pre-ADR-022 behaviour), one that jumps
    /// <c>Disabled -&gt; Mounting</c> or <c>Probing -&gt; Mounting</c> (I1), or one that calls
    /// <c>mount/mount</c> without the deep probe of rule 2.
    /// </summary>
    [Fact]
    public async Task A_user_mounted_on_demand_drive_comes_back_after_resume_through_Ready_with_the_deep_probe_first()
    {
        var h = await UserMountedOnDemandAsync();

        await h.SuspendAsync();
        Assert.Equal(MountState.Disabled, h.State(OnDemandKey));
        Assert.Null(h.Rclone.FsAt(OnDemandDrive));
        var before = h.Transitions(OnDemandKey).Count;

        await h.ResumeAsync();
        var resumeMark = h.LastMarkIndex("MARK|resume");

        Assert.Equal(MountState.Mounted, h.State(OnDemandKey));
        Assert.NotNull(h.Rclone.FsAt(OnDemandDrive));
        Assert.Equal(
            [
                (MountState.Disabled, MountState.Probing),
                (MountState.Probing, MountState.Ready),
                (MountState.Ready, MountState.Mounting),
                (MountState.Mounting, MountState.Mounted),
            ],
            Pairs(h.Transitions(OnDemandKey).Skip(before)));

        var hostCalls = h.LogAfter(resumeMark)
            .Where(e => e.StartsWith($"shallow|{OnDemandHostname}|", StringComparison.OrdinalIgnoreCase)
                        || e.StartsWith($"deep|{OnDemandKey}|", StringComparison.Ordinal)
                        || e.StartsWith($"mount|{OnDemandDrive}", StringComparison.Ordinal))
            .Take(3)
            .ToArray();
        Assert.Equal(
            [$"shallow|{OnDemandHostname}|Success", $"deep|{OnDemandKey}|Success", $"mount|{OnDemandDrive}"],
            hostCalls);
        Assert.Equal(1, MountsAfter(h, OnDemandDrive, resumeMark));
    }

    /// <summary>
    /// <b>Protects:</b> ADR-022 Decision 3: a first post-resume probe that fails drops the record;
    /// the host shows <c>Unreachable</c>, is not polled (ADR-014), and is never mounted later on its
    /// own -- not when the server comes back, not after an hour, not on a network change ("a network
    /// change neither spends nor sets it"). Rule 8's click still mounts it.
    /// <b>Catches:</b> a record kept until the host is next reachable (the rejected alternative), or
    /// a record spent by a later network-change probe -- the "mount that appears hours later on
    /// another network" the ADR exists to prevent.
    /// </summary>
    [Fact]
    public async Task A_failed_first_probe_drops_the_intent_and_nothing_mounts_the_host_later_on_its_own()
    {
        var h = await UserMountedOnDemandAsync();
        await h.SuspendAsync();
        h.Probe.SetShallow(OnDemandHostname, ShallowProbeOutcome.ConnectionRefused);

        await h.ResumeAsync();

        Assert.Equal(MountState.Unreachable, h.State(OnDemandKey));
        Assert.Equal(1, h.Rclone.MountCount(OnDemandDrive));

        // The server comes back. Nothing polls an on-demand Unreachable host (ADR-014), and the
        // intent was spent by the failed probe, so the drive must stay down.
        h.Probe.SetShallow(OnDemandHostname, ShallowProbeOutcome.Success);
        var probesBefore = h.Probe.ShallowCount(OnDemandHostname);
        await h.AdvanceAsync(TimeSpan.FromHours(2));
        Assert.Equal(probesBefore, h.Probe.ShallowCount(OnDemandHostname));
        Assert.Equal(1, h.Rclone.MountCount(OnDemandDrive));

        // A network change (the machine moved networks) must not resurrect the intent either.
        await h.RunAsync(() => h.Supervisor.NetworkChangedAsync());
        await h.AdvanceAsync(TimeSpan.FromMinutes(10));
        Assert.Equal(1, h.Rclone.MountCount(OnDemandDrive));
        Assert.NotEqual(MountState.Mounted, h.State(OnDemandKey));

        // Rule 8: the user's click still works.
        await h.MountAsync(OnDemandKey);
        Assert.Equal(MountState.Mounted, h.State(OnDemandKey));
        Assert.Equal(2, h.Rclone.MountCount(OnDemandDrive));
    }

    /// <summary>
    /// <b>Protects:</b> ADR-022 Decision 1's scope -- only on-demand hosts that were <c>Mounted</c>
    /// at suspend are recorded -- with several on-demand hosts and mixed outcomes in one resume.
    /// <b>Catches:</b> a record shared across hosts (one host's intent mounting another), on-demand
    /// hosts that were merely <c>Ready</c> or user-parked being mounted by resume (rule 6/9), or one
    /// host's failed probe dropping another host's record.
    /// </summary>
    [Fact]
    public async Task Several_on_demand_hosts_each_get_their_own_outcome_from_one_resume()
    {
        var h = new RemountHarness(HostFixtures.Build(
            HostFixtures.Global(),
            HostFixtures.OnDemand("up", drive: "Q:"),
            HostFixtures.OnDemand("down", drive: "R:"),
            HostFixtures.OnDemand("idle", drive: "S:"),
            HostFixtures.OnDemand("parked", drive: "T:")));
        await h.StartAsync();
        await h.MountAsync("up");
        await h.MountAsync("down");
        await h.MountAsync("parked");
        await h.UnmountAsync("parked");
        Assert.Equal(MountState.Mounted, h.State("up"));
        Assert.Equal(MountState.Mounted, h.State("down"));
        Assert.Equal(MountState.Ready, h.State("idle"));
        Assert.Equal(MountState.Ready, h.State("parked"));

        await h.SuspendAsync();
        h.Probe.SetShallow(RemountHarness.HostnameOf("down"), ShallowProbeOutcome.Timeout);
        await h.ResumeAsync();

        Assert.Equal(MountState.Mounted, h.State("up"));
        Assert.Equal(MountState.Unreachable, h.State("down"));
        Assert.Equal(MountState.Ready, h.State("idle"));
        Assert.Equal(MountState.Ready, h.State("parked"));
        Assert.Equal(2, h.Rclone.MountCount("Q:"));
        Assert.Equal(1, h.Rclone.MountCount("R:"));
        Assert.Equal(0, h.Rclone.MountCount("S:"));
        Assert.Equal(1, h.Rclone.MountCount("T:"));
        AssertEveryMountPrecededByPassingDeepProbe(h, "up", "Q:", h.LastMarkIndex("MARK|resume"));
    }

    /// <summary>
    /// <b>Protects:</b> ADR-022 alongside rule 6: a persistent and an on-demand host suspended
    /// together both come back, and when both hosts' first probes fail, the persistent host returns
    /// on its own ladder once its server is back (T2) while the on-demand host stays down and unpolled.
    /// <b>Catches:</b> the on-demand remount hijacking or replacing the persistent path, or the
    /// on-demand host being put on the persistent backoff ladder after its failed first probe.
    /// </summary>
    [Fact]
    public async Task A_persistent_and_an_on_demand_host_suspended_together_each_follow_their_own_rule()
    {
        var h = new RemountHarness(HostFixtures.Build(
            HostFixtures.Global(),
            HostFixtures.Persistent("pUp", probeIntervalSeconds: 60, drive: "P:"),
            HostFixtures.Persistent("pDown", probeIntervalSeconds: 60, drive: "O:"),
            HostFixtures.OnDemand("odUp", drive: "Q:"),
            HostFixtures.OnDemand("odDown", drive: "R:")));
        await h.StartAsync();
        await h.MountAsync("odUp");
        await h.MountAsync("odDown");
        Assert.All(new[] { "pUp", "pDown", "odUp", "odDown" }, k => Assert.Equal(MountState.Mounted, h.State(k)));

        await h.SuspendAsync();
        h.Probe.SetShallow(RemountHarness.HostnameOf("pDown"), ShallowProbeOutcome.ConnectionRefused);
        h.Probe.SetShallow(RemountHarness.HostnameOf("odDown"), ShallowProbeOutcome.ConnectionRefused);
        await h.ResumeAsync();

        Assert.Equal(MountState.Mounted, h.State("pUp"));
        Assert.Equal(MountState.Mounted, h.State("odUp"));
        Assert.Equal(MountState.Unreachable, h.State("pDown"));
        Assert.Equal(MountState.Unreachable, h.State("odDown"));

        h.Probe.SetShallow(RemountHarness.HostnameOf("pDown"), ShallowProbeOutcome.Success);
        h.Probe.SetShallow(RemountHarness.HostnameOf("odDown"), ShallowProbeOutcome.Success);
        var odDownProbes = h.Probe.ShallowCount(RemountHarness.HostnameOf("odDown"));
        await h.AdvanceAsync(TimeSpan.FromMinutes(15));

        Assert.Equal(MountState.Mounted, h.State("pDown"));
        Assert.Equal(MountState.Unreachable, h.State("odDown"));
        Assert.Equal(odDownProbes, h.Probe.ShallowCount(RemountHarness.HostnameOf("odDown")));
        Assert.Equal(1, h.Rclone.MountCount("R:"));
        Assert.Equal(2, h.Rclone.MountCount("Q:"));
        Assert.Equal(2, h.Rclone.MountCount("P:"));
        Assert.Equal(2, h.Rclone.MountCount("O:"));
    }

    /// <summary>
    /// <b>Protects:</b> ADR-022 Decision 4: the remount is a new mount; <c>idle_unmount_seconds</c>
    /// counts from it, not from the idle time used before the sleep.
    /// <b>Catches:</b> carrying the pre-sleep idle clock (the rejected alternative: a nearly expired
    /// drive unmounts soon after wake, which reads as a fault), and a remount that never arms the
    /// idle timer at all (the drive then never idles out).
    /// </summary>
    [Fact]
    public async Task The_idle_unmount_timer_starts_fresh_on_the_remount()
    {
        var h = await UserMountedOnDemandAsync(idleUnmountSeconds: 600);
        await h.AdvanceSecondsAsync(500);
        Assert.Equal(MountState.Mounted, h.State(OnDemandKey));

        await h.SuspendAsync();
        await h.ResumeAsync();
        Assert.Equal(MountState.Mounted, h.State(OnDemandKey));
        var unmountsAfterRemount = h.Rclone.UnmountCount(OnDemandDrive);

        // 590 s after the remount: a carried-over clock (500 s already used) would have expired at 100 s.
        await h.AdvanceSecondsAsync(590);
        Assert.Equal(MountState.Mounted, h.State(OnDemandKey));
        Assert.Equal(unmountsAfterRemount, h.Rclone.UnmountCount(OnDemandDrive));

        // ...and it does expire once a full idle period has passed since the remount.
        await h.AdvanceSecondsAsync(20);
        Assert.NotEqual(MountState.Mounted, h.State(OnDemandKey));
        Assert.True(h.Rclone.UnmountCount(OnDemandDrive) > unmountsAfterRemount, "the idle timeout should have unmounted the drive");
        Assert.Null(h.Rclone.FsAt(OnDemandDrive));
    }

    /// <summary>
    /// <b>Protects:</b> ADR-022 Decision 6: a drain for a host failure (shallow-probe threshold) does
    /// not set the record, so a later suspend/resume does not bring that drive back.
    /// <b>Catches:</b> recording on every drain of a mounted on-demand host (rather than on suspend
    /// only), which would remount the drive straight after its own failure drain or after the next
    /// resume.
    /// </summary>
    [Fact]
    public async Task A_drain_for_probe_failures_does_not_set_the_intent()
    {
        var h = await UserMountedOnDemandAsync();
        h.Probe.SetShallow(OnDemandHostname, ShallowProbeOutcome.Timeout, times: 3);

        await h.AdvanceAsync(TimeSpan.FromMinutes(5));
        Assert.Contains(h.Transitions(OnDemandKey), t => t.To == MountState.Draining && t.DrainCause == DrainCause.Automatic);
        Assert.Equal(MountState.Ready, h.State(OnDemandKey));
        Assert.Equal(1, h.Rclone.MountCount(OnDemandDrive));

        await h.SuspendAsync();
        await h.ResumeAsync();
        await h.AdvanceAsync(TimeSpan.FromMinutes(5));

        Assert.Equal(MountState.Ready, h.State(OnDemandKey));
        Assert.Equal(1, h.Rclone.MountCount(OnDemandDrive));
    }

    /// <summary>
    /// <b>Protects:</b> ADR-022 Decision 6, the deep-probe-after-network-change case named in the
    /// decision ("deep probe after a network change"): that drain does not come back on its own.
    /// <b>Catches:</b> treating a transition-triggered drain (network change) as if it were a
    /// suspend drain because both run through the ADR-017 re-derive path.
    /// </summary>
    [Fact]
    public async Task A_drain_from_a_deep_probe_after_a_network_change_does_not_set_the_intent()
    {
        var h = await UserMountedOnDemandAsync();
        h.Probe.SetDeep(OnDemandKey, DeepProbeOutcome.Failed, times: 1);

        await h.RunAsync(() => h.Supervisor.NetworkChangedAsync());
        await h.AdvanceAsync(TimeSpan.FromMinutes(1));

        Assert.Contains(h.Transitions(OnDemandKey), t => t.From == MountState.Mounted && t.To == MountState.Draining);
        Assert.NotEqual(MountState.Mounted, h.State(OnDemandKey));
        Assert.Equal(1, h.Rclone.MountCount(OnDemandDrive));

        await h.SuspendAsync();
        await h.ResumeAsync();
        await h.AdvanceAsync(TimeSpan.FromMinutes(5));

        Assert.NotEqual(MountState.Mounted, h.State(OnDemandKey));
        Assert.Equal(1, h.Rclone.MountCount(OnDemandDrive));
    }

    /// <summary>
    /// <b>Protects:</b> rule 9a "A host already <c>Draining</c> ... when suspend arrives is not
    /// noted" -- here an idle-timeout drain whose unmount has not confirmed when the lid closes.
    /// <b>Catches:</b> recording every host the suspend touches, including one already on its way
    /// down for its own reason; the drive the idle timer was removing would reappear after wake.
    /// </summary>
    [Fact]
    public async Task An_on_demand_host_already_draining_for_idle_timeout_at_suspend_is_not_remounted()
    {
        var h = await UserMountedOnDemandAsync(idleUnmountSeconds: 300);
        h.Rclone.ConfirmUnmountAtCall(OnDemandDrive, 2);

        await h.AdvanceSecondsAsync(302);
        Assert.Equal(MountState.Draining, h.State(OnDemandKey));
        Assert.NotNull(h.Rclone.FsAt(OnDemandDrive));

        await h.SuspendAsync();
        await h.AdvanceSecondsAsync(30);
        Assert.Null(h.Rclone.FsAt(OnDemandDrive));
        await h.ResumeAsync();
        await h.AdvanceAsync(TimeSpan.FromMinutes(5));

        Assert.Equal(MountState.Ready, h.State(OnDemandKey));
        Assert.Equal(1, h.Rclone.MountCount(OnDemandDrive));
    }

    /// <summary>
    /// <b>Protects:</b> rule 9a's "The record is cleared when the probe starts, so a refused or
    /// failed remount is not retried either", for a remount whose entry deep probe fails.
    /// <b>Catches:</b> a record cleared only by a SUCCESSFUL remount: the mount-failure drain's paced
    /// re-enable would then spend it again, and again, on the mount-retry ladder -- a retry loop for
    /// an on-demand host the user is not asking for.
    /// </summary>
    [Fact]
    public async Task A_remount_whose_deep_probe_fails_is_not_retried()
    {
        var h = await UserMountedOnDemandAsync();
        await h.SuspendAsync();
        h.Probe.SetDeep(OnDemandKey, DeepProbeOutcome.Failed);
        var deepBefore = h.Probe.DeepCount(OnDemandKey);

        await h.ResumeAsync();
        await h.AdvanceAsync(TimeSpan.FromHours(1));

        Assert.Equal(deepBefore + 1, h.Probe.DeepCount(OnDemandKey));
        Assert.Equal(1, h.Rclone.MountCount(OnDemandDrive));
        Assert.Contains(h.Transitions(OnDemandKey), t => t.From == MountState.Mounting && t.To == MountState.Draining);
        Assert.Equal(MountState.Ready, h.State(OnDemandKey));
    }

    /// <summary>
    /// <b>Protects:</b> rule 9a's second spending path: "if the unmount had not yet confirmed at
    /// resume, the ordinary re-enable that follows the drain". The suspend drain is still running
    /// at resume; once it confirms, the first probe reaches Ready and the drive comes back, deep
    /// probe first.
    /// <b>Catches:</b> a record spent only inside <c>ResumeAsync</c>'s own <c>Disabled</c> sweep, so a
    /// slow unmount (the case the suspend budget exists for) silently loses the user's mount.
    /// </summary>
    [Fact]
    public async Task A_suspend_drain_that_confirms_after_resume_still_remounts_through_its_first_probe()
    {
        var h = await UserMountedOnDemandAsync();
        h.Rclone.ConfirmUnmountAtCall(OnDemandDrive, 2);

        await h.SuspendAsync();
        Assert.Equal(MountState.Draining, h.State(OnDemandKey));
        await h.ResumeAsync();
        Assert.NotEqual(MountState.Mounted, h.State(OnDemandKey));
        var resumeMark = h.LastMarkIndex("MARK|resume");

        await h.AdvanceAsync(TimeSpan.FromMinutes(1));

        Assert.Equal(MountState.Mounted, h.State(OnDemandKey));
        Assert.Equal(1, MountsAfter(h, OnDemandDrive, resumeMark));
        AssertEveryMountPrecededByPassingDeepProbe(h, OnDemandKey, OnDemandDrive, resumeMark);
        var afterDrain = h.Transitions(OnDemandKey)
            .SkipWhile(t => !(t.From == MountState.Draining && t.To == MountState.Disabled))
            .ToList();
        Assert.Equal(
            [
                (MountState.Draining, MountState.Disabled),
                (MountState.Disabled, MountState.Probing),
                (MountState.Probing, MountState.Ready),
                (MountState.Ready, MountState.Mounting),
                (MountState.Mounting, MountState.Mounted),
            ],
            Pairs(afterDrain));
    }

    /// <summary>
    /// <b>Protects:</b> the late-drain path of rule 9a together with Decision 3: if the first probe
    /// after the late-confirming drain fails, the record is gone and the host never mounts later.
    /// <b>Catches:</b> the late-drain path keeping the record past a failed first probe.
    /// </summary>
    [Fact]
    public async Task A_late_confirming_suspend_drain_whose_first_probe_fails_drops_the_intent()
    {
        var h = await UserMountedOnDemandAsync();
        h.Rclone.ConfirmUnmountAtCall(OnDemandDrive, 2);
        await h.SuspendAsync();
        await h.ResumeAsync();
        h.Probe.SetShallow(OnDemandHostname, ShallowProbeOutcome.ConnectionRefused);

        await h.AdvanceAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(MountState.Unreachable, h.State(OnDemandKey));

        h.Probe.SetShallow(OnDemandHostname, ShallowProbeOutcome.Success);
        await h.RunAsync(() => h.Supervisor.NetworkChangedAsync());
        await h.AdvanceAsync(TimeSpan.FromHours(1));

        Assert.Equal(1, h.Rclone.MountCount(OnDemandDrive));
        Assert.NotEqual(MountState.Mounted, h.State(OnDemandKey));
    }
}
