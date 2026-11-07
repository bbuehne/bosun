using Bosun.Probe;
using Bosun.Rclone;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Independent.SuspendPreempt;
using Bosun.Tests.Supervisor.Support;
using static Bosun.Tests.Supervisor.Independent.SuspendBaselineAndRepairStop.BaselineSpec;

namespace Bosun.Tests.Supervisor.Independent.SuspendBaselineAndRepairStop;

/// <summary>
/// bs-5ga.3: a suspend preempts the post-mount VFS baseline sample. Written from ADR-023's amendment of
/// 2026-11-07 and docs/ARCHITECTURE.md §4 rule 5 ("A mount already under way when the suspend arrives") and
/// rule 7 ("What counts as activity", "Which VFS", "Safety when the clock fires"), not from the
/// implementation. Invariants: I8 (the suspend is not kept waiting, every drive is off before sleep), I2 (a
/// preempted sample is not a failure), I1 (nothing mounts between the suspend and the resume).
/// </summary>
/// <remarks>
/// The spec quotes these tests rest on:
/// <list type="bullet">
/// <item>"is cancelled if one arrives while its <c>vfs/list</c> or <c>vfs/stats</c> call is in flight: the host
/// stays <c>Mounted</c> with no baseline taken, and the suspend drains it. A cancelled sample is not a failed
/// poll and changes nothing" (§4 rule 5).</item>
/// <item>"It is discarded before any bookkeeping: no baseline, no <c>UploadFailureCount</c>, no
/// <c>VfsPollFaultLogged</c>, no idle-clock restart, no counter, no backoff, no warning (one Information line)
/// ... Only a suspend preempts the sample; a sample that fails any other way is the failed poll it always was
/// ... A suspend already pending still skips the sample" (ADR-023 amendment bs-5ga.3).</item>
/// </list>
/// </remarks>
public sealed class BaselinePreemptionSpecTests
{
    private const string Key = "archive";
    private const string Drive = "Q:";

    private static async Task<PreemptSpecHarness> StartedAsync()
    {
        var h = NewHarness(OnDemand(Key, Drive));
        await h.StartAsync();
        Assert.Equal(MountState.Ready, h.State(Key));
        Assert.Equal(0, SampleCalls(h));
        return h;
    }

    /// <summary>
    /// <b>Protects:</b> I8 and the amendment, for each of the sample's two calls. The first call
    /// (<c>vfs/list</c>, "Which VFS") and the second (<c>vfs/stats</c>) are both named.
    /// <b>Catches:</b> the sample left out of the suspend's prologue (the residual bs-5ga.3 closed): the suspend
    /// waits behind a hung rcd for a whole probe timeout, here forever, so the guard fails. Also a source
    /// published only around one of the two calls.
    /// </summary>
    [Theory]
    [InlineData("vfslist")]
    [InlineData("vfsstats")]
    public async Task A_suspend_during_the_baseline_sample_cancels_it_and_drains_the_host_as_Suspend(string operation)
    {
        var h = await StartedAsync();

        // The helper fails on its guard if the suspend waited for the held call, which nothing else releases.
        await MountWithSuspendDuringSampleAsync(h, Key, operation);

        Assert.Equal([DrainCause.Suspend], h.DrainCauses(Key));
        Assert.Equal(MountState.Disabled, h.State(Key));
        Assert.False(h.IsMountedInRclone(Drive), "the drive is still mounted after the suspend (I8)");
        Assert.Empty(h.Errors);
    }

    /// <summary>
    /// <b>Protects:</b> "the host stays <c>Mounted</c> with no baseline taken": the mount itself succeeded. The
    /// caller of the mount (a tray click) is told it succeeded, and the mount is not booked as a mount failure.
    /// <b>Catches:</b> the preemption's <see cref="OperationCanceledException"/> escaping the mount command (the
    /// click reports a cancelled or failed mount for a drive that was mounted), or the cancellation routed
    /// through the mount-failure path (<c>ConsecutiveMountFailures</c>, <c>LastMountFailureReason</c>, a
    /// <c>MountFailure</c> drain and its paced retry).
    /// </summary>
    [Theory]
    [InlineData("vfslist")]
    [InlineData("vfsstats")]
    public async Task A_preempted_sample_leaves_the_mount_a_success_and_books_no_failure(string operation)
    {
        var h = await StartedAsync();

        var (mount, _, _) = await MountWithSuspendDuringSampleAsync(h, Key, operation);

        await mount; // throws the escaped exception, if any, with its own message
        Assert.True(mount.IsCompletedSuccessfully);

        var transitions = h.Transitions(Key);
        Assert.Contains(transitions, t => t.From == MountState.Mounting && t.To == MountState.Mounted);
        Assert.DoesNotContain(transitions, t => t.From == MountState.Mounting && t.To == MountState.Draining);

        var snapshot = h.Snapshot(Key);
        Assert.Equal(0, snapshot.ConsecutiveMountFailures);
        Assert.Null(snapshot.LastMountFailureReason);
        Assert.Equal(0, snapshot.ConsecutiveMountedFailures);
        Assert.Equal(0, snapshot.ConsecutiveDeepProbeFailures);
        Assert.DoesNotContain(DrainCause.MountFailure, h.DrainCauses(Key));
        Assert.DoesNotContain(DrainCause.Automatic, h.DrainCauses(Key));
    }

    /// <summary>
    /// <b>Protects:</b> "no warning (one Information line)": a preempted sample is not a failed poll and must not
    /// say it is.
    /// <b>Catches:</b> the cancellation caught by the sample's ordinary failure handler, which logs the poll
    /// fault (the fail-closed "cannot read the VFS" warning of rule 7) every time the machine sleeps during a
    /// mount. <see cref="A_baseline_sample_that_fails_for_any_other_reason_is_still_a_failed_poll"/> shows the
    /// same call failing for a non-suspend reason does warn, so this is not vacuous.
    /// </summary>
    [Theory]
    [InlineData("vfslist", OnCancel.LikeProduction)]
    [InlineData("vfsstats", OnCancel.LikeProduction)]
    [InlineData("vfsstats", OnCancel.Throw)]
    [InlineData("vfsstats", OnCancel.Ignore)]
    public async Task A_preempted_sample_logs_no_warning(string operation, OnCancel onCancel)
    {
        var h = await StartedAsync();

        var (_, _, mark) = await MountWithSuspendDuringSampleAsync(h, Key, operation, onCancel);

        var warnings = WarningsSince(h, mark);
        Assert.True(
            warnings.Count == 0,
            $"a suspend that cancelled the baseline sample produced Warning-or-above log lines:{Environment.NewLine}{Describe(warnings)}");
    }

    /// <summary>
    /// <b>Protects:</b> "Only a suspend preempts the sample; a sample that fails any other way is the failed poll
    /// it always was", and rule 7's "a baseline that could not be taken": the clock at mount + 30 s takes one and
    /// waits one more period, so the idle drive unmounts at + 60 s, never at + 30 s (no blind unmount) and not
    /// never. A failed poll is not a mount failure: the drive stays mounted until then.
    /// <b>Catches:</b> the bs-5ga.3 change over-reaching, so that an ordinary rc error from the baseline sample is
    /// silently treated as "preempted" (no warning, or, worse, recorded as a baseline so the drive unmounts at
    /// + 30 s on nothing). Also anchors <see cref="A_preempted_sample_logs_no_warning"/>.
    /// </summary>
    [Fact]
    public async Task A_baseline_sample_that_fails_for_any_other_reason_is_still_a_failed_poll()
    {
        var h = await StartedAsync();
        var hold = h.HRclone.HoldNext(OnCancel.LikeProduction, "vfsstats");
        var mount = h.Supervisor.RequestMountAsync(Key);
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(hold, "the baseline vfs/stats call");
        var mark = h.Logger.Entries.Count;

        hold.Fail(new RcloneRcException("vfs/stats", 500, "rcd answered with an error"));
        await h.FinishAsync(pump, "the mount command never finished", mount);
        await mount;

        Assert.Equal(MountState.Mounted, h.State(Key));
        Assert.Equal(0, h.Snapshot(Key).ConsecutiveMountFailures);
        Assert.NotEmpty(WarningsSince(h, mark));

        var mountedAt = LastMountedAt(h, Key);
        await h.AdvanceSecondsAsync(IdleSeconds + 5);
        Assert.Empty(DrainTimes(h, Key, DrainCause.IdleTimeout));
        Assert.True(h.IsMountedInRclone(Drive), "unmounted on the first clock although no baseline had been taken (rule 7 fails closed)");

        await h.AdvanceSecondsAsync((2 * IdleSeconds) + 2 - (IdleSeconds + 5)); // to mount + 62 s
        var drains = DrainTimes(h, Key, DrainCause.IdleTimeout);
        Assert.True(drains.Count == 1, $"expected one idle unmount by mount + {2 * IdleSeconds + 2}s:{Environment.NewLine}{Dump(h, Key)}");
        var after = (drains[0] - mountedAt).TotalSeconds;
        Assert.InRange(after, 2 * IdleSeconds, (2 * IdleSeconds) + 1);
        Assert.Empty(h.Errors);
    }

    /// <summary>
    /// <b>Protects:</b> "the suspend drains it" after a sample preempted in its FIRST call: the sample is over.
    /// It does not go on to make its second call while the suspend waits.
    /// <b>Catches:</b> a cancelled <c>vfs/list</c> caught and followed by <c>vfs/stats</c> anyway, which against a
    /// hung rcd is a second wait the suspend sits behind.
    /// </summary>
    [Fact]
    public async Task A_sample_preempted_in_vfs_list_never_goes_on_to_call_vfs_stats()
    {
        var h = await StartedAsync();

        await MountWithSuspendDuringSampleAsync(h, Key, "vfslist");

        Assert.Equal(1, h.HRclone.Count("vfslist"));
        Assert.Equal(0, h.HRclone.Count("vfsstats"));
    }

    /// <summary>
    /// <b>Protects:</b> I1 and I8 around the preemption: from the suspend's arrival to the resume, no host
    /// enters <c>Mounting</c> and nothing is left mounted; the preempted sample is not a reason to remount.
    /// <b>Catches:</b> a preempted sample that leaves the host looking un-mounted or failed so that the drain's
    /// completion re-enables and remounts it while the machine is going to sleep.
    /// </summary>
    [Fact]
    public async Task Nothing_mounts_after_a_preempted_sample_until_the_resume()
    {
        var h = await StartedAsync();
        var hold = h.HRclone.HoldNext(OnCancel.LikeProduction, "vfsstats");
        var mount = h.Supervisor.RequestMountAsync(Key);
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(hold, "the baseline vfs/stats call");
        var mountingBefore = MountingEntries(h);
        var mountCallsBefore = h.TotalMountCalls;

        var suspend = SuspendCaller.Call(h.Supervisor);
        await h.FinishAsync(pump, "the suspend waited for the hung baseline sample", suspend, mount);
        await h.AdvanceSecondsAsync(30);

        Assert.Equal(mountingBefore, MountingEntries(h));
        Assert.Equal(mountCallsBefore, h.TotalMountCalls);
        Assert.False(h.IsMountedInRclone(Drive));
    }

    /// <summary>
    /// <b>Protects:</b> decision 3 applied to the sample: "When a tick's preemption token is cancelled, whatever
    /// its I/O returned is discarded". Here the suspend lands just as the sample completes: the call ignores its
    /// cancelled token and returns a real, healthy answer. Either way round the outcome is the same one drain,
    /// as <c>Suspend</c>, and a successful mount.
    /// <b>Catches:</b> a check on the outcome rather than the token, which records the answer as a baseline (and
    /// restarts the idle clock) under a suspend, or treats a completed call with a cancelled token as a fault.
    /// </summary>
    [Fact]
    public async Task A_sample_that_returns_after_the_suspend_cancelled_it_is_discarded_and_the_outcome_is_the_same()
    {
        var h = await StartedAsync();

        var (mount, _, mark) = await MountWithSuspendDuringSampleAsync(h, Key, "vfsstats", OnCancel.Ignore);

        await mount;
        Assert.Equal([DrainCause.Suspend], h.DrainCauses(Key));
        Assert.Equal(MountState.Disabled, h.State(Key));
        Assert.False(h.IsMountedInRclone(Drive));
        Assert.Empty(WarningsSince(h, mark));
        Assert.Empty(h.Errors);
    }

    /// <summary>
    /// <b>Protects:</b> decision 5's last sentence, kept by the amendment: "A suspend already pending still skips
    /// the sample." The suspend arrives while <c>mount/mount</c> is in flight (which is not cancelled); when the
    /// mount completes, no <c>vfs/*</c> call is made at all.
    /// <b>Catches:</b> the new cancellable sample started regardless of the pending suspend (it would then be
    /// cancelled at once, or worse, run if the source is published after the prologue already ran).
    /// </summary>
    [Fact]
    public async Task A_suspend_pending_before_the_sample_starts_skips_it_entirely()
    {
        var h = await StartedAsync();
        var mountHold = h.HRclone.HoldNextAt(OnCancel.Ignore, "mount", Drive);
        var mount = h.Supervisor.RequestMountAsync(Key);
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(mountHold, "mount/mount");

        var suspend = SuspendCaller.Call(h.Supervisor);
        Assert.False(mountHold.CancellationRequested, "a mount/mount already sent was cancelled (ADR-023 decision 4)");
        mountHold.Release();
        await h.FinishAsync(pump, "the mount command never finished", suspend, mount);

        Assert.Equal(0, SampleCalls(h));
        Assert.Equal([DrainCause.Suspend], h.DrainCauses(Key));
        Assert.False(h.IsMountedInRclone(Drive));
        Assert.Empty(h.Errors);
    }

    /// <summary>
    /// <b>Protects:</b> the sample's source is per sample: the second of two mounts in sequence is preempted as
    /// the first would have been.
    /// <b>Catches:</b> a source published once and never re-published (the first sample's, already disposed or
    /// unpublished, is what the prologue finds), so a suspend during the SECOND mount's sample waits for it.
    /// Also the first mount's completed sample being cancelled or disturbed by the later suspend.
    /// </summary>
    [Fact]
    public async Task A_suspend_during_the_second_mounts_sample_cancels_that_sample_after_a_first_completed_normally()
    {
        var h = NewHarness(OnDemand("first", "Q:"), OnDemand("second", "R:"));
        await h.StartAsync();
        await h.RunAsync(() => h.Supervisor.RequestMountAsync("first"));
        Assert.Equal(MountState.Mounted, h.State("first"));
        Assert.True(h.HRclone.Count("vfsstats", FsOf("first")) >= 1, "setup: the first mount took no baseline");

        var hold = h.HRclone.HoldNextAt(OnCancel.LikeProduction, "vfsstats", FsOf("second"));
        var mount = h.Supervisor.RequestMountAsync("second");
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(hold, "the second mount's baseline vfs/stats");
        var mark = h.Logger.Entries.Count;

        var suspend = SuspendCaller.Call(h.Supervisor);
        AssertSuspendCancelled(hold, "the second mount's baseline sample");
        await h.FinishAsync(pump, "the suspend waited for the second mount's hung baseline sample", suspend, mount);
        await mount;

        Assert.Equal([DrainCause.Suspend], h.DrainCauses("first"));
        Assert.Equal([DrainCause.Suspend], h.DrainCauses("second"));
        Assert.False(h.IsMountedInRclone("Q:"));
        Assert.False(h.IsMountedInRclone("R:"));
        Assert.Empty(WarningsSince(h, mark));
        Assert.Empty(h.Errors);
    }

    /// <summary>
    /// <b>Protects:</b> the cancellation stays on the caller's thread without running callbacks there (decision 2:
    /// "It is <c>CancelAsync</c>, never <c>Cancel</c>"; the amendment: "the same <c>CancelAsync</c> on the
    /// caller's thread ... nothing about it is a second mechanism").
    /// <b>Catches:</b> the sample's source cancelled with <c>Cancel()</c>, which runs the linked registrations, and
    /// with them the rc call's unwinding, on <c>SystemEvents</c>' single notification thread.
    /// </summary>
    [Fact]
    public async Task The_sample_is_cancelled_without_running_its_callbacks_on_the_suspend_callers_thread()
    {
        var h = await StartedAsync();

        var (_, hold, _) = await MountWithSuspendDuringSampleAsync(h, Key, "vfsstats");

        Assert.False(hold.CancelCallbackRanInsideSuspendCall, "the sample's cancellation callbacks ran inline inside SuspendAsync");
    }

    /// <summary>
    /// <b>Protects:</b> rule 7 after a preempted sample, end to end through the ADR-022 remount: the suspend
    /// drained an on-demand host the user had mounted, so resume remounts it, and "the first sample after a mount
    /// (taken at the mount itself) is the baseline". With that baseline the idle drive unmounts at remount + 30 s:
    /// not earlier (no unmount on nothing), and not at + 60 s (which is what a remount with no baseline does).
    /// <b>Catches:</b> state left behind by the preempted sample that spoils the next mount's sample: a source left
    /// cancelled and reused (every later baseline is "preempted" at once), a stale "no baseline" or "preempted"
    /// flag that survives the unmount, or idle eligibility lost so the remount never idles out.
    /// </summary>
    [Fact]
    public async Task After_a_preempted_sample_the_remount_at_resume_takes_a_baseline_and_idles_out_on_time()
    {
        var h = await StartedAsync();
        await MountWithSuspendDuringSampleAsync(h, Key, "vfsstats");
        var statsBefore = h.HRclone.Count("vfsstats");

        await h.ResumeAsync();

        Assert.True(
            h.State(Key) == MountState.Mounted,
            $"setup: ADR-022 should remount the on-demand host the suspend drained:{Environment.NewLine}{Dump(h, Key)}");
        Assert.Equal(2, h.MountCalls(Drive));
        Assert.True(h.HRclone.Count("vfsstats") > statsBefore, "the remount took no baseline sample");

        var remountedAt = LastMountedAt(h, Key);
        await h.AdvanceSecondsAsync(IdleSeconds - 1);
        Assert.Empty(DrainTimes(h, Key, DrainCause.IdleTimeout));

        await h.AdvanceSecondsAsync(3);
        var drains = DrainTimes(h, Key, DrainCause.IdleTimeout);
        Assert.True(drains.Count == 1, $"the remounted idle drive did not unmount at remount + {IdleSeconds}s:{Environment.NewLine}{Dump(h, Key)}");
        Assert.InRange((drains[0] - remountedAt).TotalSeconds, IdleSeconds, IdleSeconds + 1);
        Assert.Empty(h.Errors);
    }

    /// <summary>
    /// <b>Protects:</b> the amendment's "the tail of a mount command (or of the idle tick that began the mount)":
    /// the remount at resume is a command too, and a second suspend during ITS sample is preempted the same way.
    /// The host is drained as <c>Suspend</c> again, keeps its ADR-022 record, and comes back at the next wake.
    /// <b>Catches:</b> the source wired into the click's mount path only, not the shared mount path the ADR-022
    /// remount uses (suspend, resume, suspend: the second suspend waits behind the hung sample).
    /// </summary>
    [Fact]
    public async Task A_second_suspend_during_the_resume_remounts_sample_is_preempted_too_and_the_host_returns_at_the_next_wake()
    {
        var h = await StartedAsync();
        await h.RunAsync(() => h.Supervisor.RequestMountAsync(Key));
        await h.SuspendAsync();
        Assert.Equal([DrainCause.Suspend], h.DrainCauses(Key));

        var hold = h.HRclone.HoldNext(OnCancel.LikeProduction, "vfsstats");
        var resume = h.Supervisor.ResumeAsync();
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(hold, "the ADR-022 remount's baseline vfs/stats");
        Assert.Equal(2, h.MountCalls(Drive));

        var suspend = SuspendCaller.Call(h.Supervisor);
        AssertSuspendCancelled(hold, "the remount's baseline sample");
        await h.FinishAsync(pump, "the second suspend waited for the remount's hung baseline sample", suspend, resume);
        await resume;

        Assert.Equal([DrainCause.Suspend, DrainCause.Suspend], h.DrainCauses(Key));
        Assert.False(h.IsMountedInRclone(Drive));
        Assert.Empty(h.Errors);

        await h.ResumeAsync();
        Assert.Equal(MountState.Mounted, h.State(Key));
        Assert.Equal(3, h.MountCalls(Drive));
    }

    /// <summary>
    /// <b>Protects:</b> "Only on-demand hosts with <c>idle_unmount_seconds &gt; 0</c> are polled at all": a
    /// persistent mount and an on-demand mount with no idle timeout take no baseline, so there is nothing for a
    /// suspend to preempt, and the suspend drains them as before.
    /// <b>Catches:</b> the baseline sample (and with it a cancellable source) extended to mounts that are never
    /// idle-unmounted.
    /// </summary>
    [Fact]
    public async Task Mounts_that_are_never_idle_unmounted_take_no_baseline_and_drain_as_Suspend()
    {
        var h = NewHarness(
            HostFixtures.Persistent("always", probeIntervalSeconds: 60, drive: "P:"),
            OnDemand("noidle", "N:", idleSeconds: 0));
        await h.StartAsync();
        Assert.Equal(MountState.Mounted, h.State("always"));
        await h.RunAsync(() => h.Supervisor.RequestMountAsync("noidle"));
        Assert.Equal(MountState.Mounted, h.State("noidle"));
        Assert.Equal(0, SampleCalls(h));

        await h.SuspendAsync();

        Assert.Equal([DrainCause.Suspend], h.DrainCauses("always"));
        Assert.Equal([DrainCause.Suspend], h.DrainCauses("noidle"));
        Assert.Equal(0, SampleCalls(h));
        Assert.Empty(h.Errors);
    }
}
