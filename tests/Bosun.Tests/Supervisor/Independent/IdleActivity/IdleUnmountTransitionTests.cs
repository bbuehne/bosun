using Bosun.Supervisor;
using Bosun.UI.Tray;

namespace Bosun.Tests.Supervisor.Independent.IdleActivity;

/// <summary>
/// What the idle unmount looks like from outside: the transition log (the primary triage artifact,
/// docs/OPERATIONS.md T5), the drain cause, the unexpected-unmount toast, parking, and what the
/// host does afterwards. Plus the race in which a user Unmount and an idle fire are both queued.
/// </summary>
public sealed class IdleUnmountTransitionTests
{
    private const int Idle = 290;
    private const int Interval = 60;

    /// <summary>
    /// docs/OPERATIONS.md T5 expected lines: <c>Mounted -&gt; Draining</c> (idle cause), then
    /// <c>Draining -&gt; Disabled</c>, <c>Disabled -&gt; Probing</c>, <c>Probing -&gt; Ready</c>, and
    /// nothing else. Bug classes: a skipped fire that logs a transition (rule 10: a transition in
    /// the log must mean something happened); the idle drain recorded with the wrong cause; the host
    /// ending parked or Unreachable after an idle unmount (T5's FAIL conditions); an on-demand host
    /// remounted automatically afterwards (rule 6).
    /// </summary>
    [Fact]
    public async Task The_idle_unmount_logs_exactly_the_T5_transitions_after_a_skipped_fire_logged_none()
    {
        var harness = IdleActivityHarness.OnDemand(Idle, Interval);
        harness.Rclone.SetStats("Q:", Vfs.Stats(erroredFiles: 1));
        await harness.StartAsync();
        await harness.MountAsync("archive");
        var mountedEntries = harness.Transitions("archive").Count;

        // Two skipped fires (+290, +580): nothing may appear in the log.
        await harness.AdvanceToAsync((2 * Idle) + 10);
        Assert.True(
            harness.Transitions("archive").Count == mountedEntries,
            $"a skipped idle fire produced a transition.{Environment.NewLine}{harness.Dump("archive")}");

        harness.Rclone.SetStats("Q:", Vfs.Stats(erroredFiles: 0));
        await harness.AdvanceToAsync((3 * Idle) + 10);

        var after = harness.Transitions("archive").Skip(mountedEntries).ToList();
        Assert.True(
            after.Count == 4,
            $"expected exactly four transitions for the idle unmount.{Environment.NewLine}{harness.Dump("archive")}");
        Assert.Equal((MountState.Mounted, MountState.Draining), (after[0].From, after[0].To));
        Assert.Equal(DrainCause.IdleTimeout, after[0].DrainCause);
        Assert.Equal((MountState.Draining, MountState.Disabled), (after[1].From, after[1].To));
        Assert.Equal((MountState.Disabled, MountState.Probing), (after[2].From, after[2].To));
        Assert.Equal((MountState.Probing, MountState.Ready), (after[3].From, after[3].To));

        var snapshot = harness.Snapshot("archive");
        Assert.Equal(MountState.Ready, snapshot.State);
        Assert.False(snapshot.UserParked, "an idle unmount parked the host (T5: must end in Ready, not parked)");
        Assert.Equal(1, harness.Rclone.MountCount("Q:"));
        Assert.False(harness.Rclone.IsMounted("Q:"));
    }

    /// <summary>
    /// The toast detector decides by the typed cause; an idle unmount is one the user configured
    /// and must be silent (docs/ARCHITECTURE.md §3 "Tray notifications"). Bug: the idle drain begun
    /// through the generic automatic-drain path (cause <c>Automatic</c> or null), so every quiet
    /// on-demand drive pops a "drive lost" balloon.
    /// </summary>
    [Fact]
    public async Task The_unexpected_unmount_detector_stays_silent_for_an_idle_unmount()
    {
        var harness = IdleActivityHarness.OnDemand(Idle, Interval);
        await harness.StartAsync();
        await harness.MountAsync("archive");

        await harness.AdvanceToAsync(Idle + 30);
        Assert.Single(harness.DrainsWith("archive", DrainCause.IdleTimeout));

        var toasts = UnexpectedUnmountDetector.SelectNewUnexpectedUnmounts(
            harness.Supervisor.GetTransitionHistory(), DateTimeOffset.UnixEpoch.AddTicks(-1));
        Assert.Empty(toasts);
    }

    /// <summary>
    /// A user Unmount queued AHEAD of an idle fire (the click lands in the channel a moment before
    /// the timer does). The user's drain wins: cause <c>UserUnmount</c>, host parked (ADR-015). The
    /// fire that follows finds no Mounted host and must do nothing. Bug classes: the stale fire
    /// starting a second drain or a second <c>mount/unmount</c>; the fire overwriting the cause to
    /// IdleTimeout; the fire un-parking the host.
    /// </summary>
    [Fact]
    public async Task A_user_unmount_queued_ahead_of_an_idle_fire_wins_and_the_fire_is_a_no_op()
    {
        var harness = IdleActivityHarness.OnDemand(Idle, Interval);
        await harness.StartAsync();
        await harness.MountAsync("archive");

        await harness.AdvanceToAsync(Idle - 1);
        Assert.Equal(MountState.Mounted, harness.State("archive"));

        // Enqueue the click, THEN let the clock reach the idle deadline so its timer queues the fire
        // behind the click, and only then pump.
        var click = harness.Supervisor.RequestUnmountAsync("archive");
        harness.Time.Advance(TimeSpan.FromSeconds(2));
        await harness.Supervisor.DrainAsync();
        await click;
        await harness.AdvanceSecondsAsync(Idle);

        Assert.Empty(harness.DrainsWith("archive", DrainCause.IdleTimeout));
        Assert.Single(harness.DrainsWith("archive", DrainCause.UserUnmount));
        Assert.Equal(1, harness.Rclone.UnmountCount("Q:"));
        var snapshot = harness.Snapshot("archive");
        Assert.True(snapshot.UserParked, $"the user's unmount did not park the host.{Environment.NewLine}{harness.Dump("archive")}");
        Assert.Equal(MountState.Ready, snapshot.State);
        Assert.Equal(1, harness.Rclone.MountCount("Q:"));
    }

    /// <summary>
    /// A stale fire queued BEHIND a suspend and a resume: the old timer's callback has already run
    /// (the fire is in the channel) when the suspend drains the host, and the resume remounts it
    /// (ADR-022) before the fire is pumped. The fire then sees a Mounted host, an unchanged VFS and
    /// nothing pending -- everything it needs to unmount -- but it belongs to the previous mount.
    /// Bug: a fire that checks only "is the host Mounted" and takes down the drive the resume has
    /// just brought back (disposing a timer does not retract a callback that already queued work).
    /// </summary>
    /// <remarks>
    /// A user Unmount + Mount pair cannot set this up: the drain's later steps are queued behind the
    /// fire, so the fire meets a host that is not Mounted. Suspend + resume is the route on which
    /// the remount completes ahead of the stale fire; the precondition is asserted so the test
    /// cannot pass vacuously if that ever changes.
    /// </remarks>
    [Fact]
    public async Task A_stale_idle_fire_queued_behind_a_suspend_and_resume_does_not_unmount_the_remounted_drive()
    {
        var harness = IdleActivityHarness.OnDemand(Idle, Interval);
        await harness.StartAsync();
        await harness.MountAsync("archive");
        await harness.AdvanceToAsync(Idle - 1);

        var suspend = harness.Supervisor.SuspendAsync();
        var resume = harness.Supervisor.ResumeAsync();
        harness.Time.Advance(TimeSpan.FromSeconds(2));
        await harness.Supervisor.DrainAsync();
        await suspend;
        await resume;

        Assert.True(
            harness.Rclone.MountCount("Q:") == 2,
            $"precondition (test construction): the resume did not remount the drive.{Environment.NewLine}{harness.Dump("archive")}");
        Assert.True(
            harness.State("archive") == MountState.Mounted && harness.Rclone.IsMounted("Q:"),
            $"the stale fire from the first mount unmounted the drive the resume had just remounted.{Environment.NewLine}{harness.Dump("archive")}");
        Assert.Empty(harness.DrainsWith("archive", DrainCause.IdleTimeout));
        var remountedAt = harness.Now;

        await harness.AdvanceToAsync(remountedAt + Idle - 5);
        Assert.Equal(MountState.Mounted, harness.State("archive"));
        Assert.Empty(harness.DrainsWith("archive", DrainCause.IdleTimeout));
    }

    /// <summary>
    /// <see cref="IMountSupervisor.RecordActivityAsync"/> ("resets the idle-unmount clock") queued
    /// just ahead of a fire whose timer has already gone off. The reset is processed first, so the
    /// clock now runs to one full period after it; the fire behind it is stale. Bug: the stale fire
    /// still acts, and because the VFS shows no change it unmounts the drive a moment after the
    /// clock was reset.
    /// </summary>
    [Fact]
    public async Task An_activity_reset_processed_before_a_queued_fire_wins()
    {
        var harness = IdleActivityHarness.OnDemand(Idle, Interval);
        await harness.StartAsync();
        await harness.MountAsync("archive");
        await harness.AdvanceToAsync(Idle - 1);

        var reset = harness.Supervisor.RecordActivityAsync("archive");
        harness.Time.Advance(TimeSpan.FromSeconds(2));
        await harness.Supervisor.DrainAsync();
        await reset;

        Assert.True(
            harness.State("archive") == MountState.Mounted,
            $"a fire queued behind an activity reset unmounted the drive.{Environment.NewLine}{harness.Dump("archive")}");

        await harness.AdvanceToAsync((2 * Idle) - 10);
        Assert.Equal(MountState.Mounted, harness.State("archive"));
        await harness.AdvanceToAsync((2 * Idle) + 5);
        Assert.Single(harness.DrainsWith("archive", DrainCause.IdleTimeout));
    }

    /// <summary>
    /// The other order: the idle fire is processed first and the host is Draining when the user's
    /// click arrives. Only one drain, one cause (IdleTimeout, the one that began it), one
    /// <c>mount/unmount</c> confirmed, and the host comes to rest unmounted. Bug: the click starting
    /// a second, overlapping drain of a drive already being taken down.
    /// </summary>
    [Fact]
    public async Task A_user_unmount_arriving_after_the_idle_fire_does_not_start_a_second_drain()
    {
        var harness = IdleActivityHarness.OnDemand(Idle, Interval);
        await harness.StartAsync();
        await harness.MountAsync("archive");

        await harness.AdvanceToAsync(Idle - 1);
        harness.Time.Advance(TimeSpan.FromSeconds(2));
        var click = harness.Supervisor.RequestUnmountAsync("archive");
        await harness.Supervisor.DrainAsync();
        await click;
        await harness.AdvanceSecondsAsync(60);

        var drains = harness.Transitions("archive").Where(t => t.To == MountState.Draining).ToList();
        Assert.True(drains.Count == 1, $"expected exactly one drain.{Environment.NewLine}{harness.Dump("archive")}");
        Assert.Equal(DrainCause.IdleTimeout, drains[0].DrainCause);
        Assert.False(harness.Rclone.IsMounted("Q:"));
        Assert.NotEqual(MountState.Mounted, harness.State("archive"));
        Assert.Equal(1, harness.Rclone.MountCount("Q:"));
    }
}
