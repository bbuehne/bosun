using Bosun.Supervisor;

namespace Bosun.Tests.Supervisor.Independent.IdleActivity;

/// <summary>
/// The idle unmount across a sleep: docs/ARCHITECTURE.md §4 rule 5 (suspend drains; bs-bv0, no
/// mount while suspended), rule 9a (ADR-022: an on-demand drive the user had mounted comes back
/// after resume, and "the idle timer starts fresh ... `idle_unmount_seconds` counts from it, not
/// from the time already idle before the sleep") and rule 7 (the baseline is thrown away on every
/// unmount).
/// </summary>
/// <remarks>
/// The fake clock runs through the night while the supervisor is suspended, so every timer armed
/// before the suspend fires during the "sleep" -- the worst case for a stale idle fire.
/// </remarks>
public sealed class IdleAcrossSuspendTests
{
    private const int Idle = 290;
    private const int Interval = 60;
    private const int Night = 8 * 3600;

    /// <summary>
    /// An idle timer pending across a suspend, and the ADR-022 remount after resume. Three bugs in
    /// one scenario, each with its own assertion:
    /// (1) the pre-suspend idle fire, delivered during the night, acts on the host -- it must find
    /// nothing Mounted and do nothing, so no IdleTimeout drain before the resume;
    /// (2) the remount inherits the old idle clock (eight hours idle &gt; 290 s) and is unmounted
    /// straight after the resume -- it must stay up until one idle period after the remount;
    /// (3) the remount compares against the pre-suspend baseline (counters A before the sleep, B
    /// after), sees A -&gt; B as activity and stays up an extra probe interval -- the drain must come
    /// one idle period after the remount, not one interval later.
    /// </summary>
    [Fact]
    public async Task A_remount_after_resume_gets_a_fresh_baseline_and_a_fresh_idle_clock()
    {
        var harness = IdleActivityHarness.OnDemand(Idle, Interval);
        harness.Rclone.DefaultStats = Vfs.Stats(bytesUsed: 100, files: 1, dirs: 2, metadataFiles: 5);
        await harness.StartAsync();
        await harness.MountAsync("archive");

        await harness.AdvanceToAsync(200);
        await harness.SuspendAsync();
        Assert.NotEqual(MountState.Mounted, harness.State("archive"));
        Assert.False(harness.Rclone.IsMounted("Q:"));

        // The night. The idle deadline (+290) passes while suspended.
        await harness.AdvanceSecondsAsync(Night);
        Assert.True(
            harness.DrainsWith("archive", DrainCause.IdleTimeout).Count == 0,
            $"(1) the pre-suspend idle timer acted on the host during the sleep.{Environment.NewLine}{harness.Dump("archive")}");
        Assert.Equal(1, harness.Rclone.MountCount("Q:"));

        harness.Rclone.DefaultStats = Vfs.Stats(bytesUsed: 777, files: 9, dirs: 40, metadataFiles: 61);
        await harness.ResumeAsync();
        Assert.True(
            harness.State("archive") == MountState.Mounted,
            $"ADR-022: the on-demand drive the user had mounted was not remounted at resume.{Environment.NewLine}{harness.Dump("archive")}");
        var resumedAt = harness.Now;

        await harness.AdvanceToAsync(resumedAt + Idle - 5);
        Assert.True(
            harness.State("archive") == MountState.Mounted,
            $"(2) the remounted drive was unmounted before its own idle period (+{resumedAt + Idle}s) was up.{Environment.NewLine}{harness.Dump("archive")}");

        await harness.AdvanceToAsync(resumedAt + Idle + 5);
        var at = harness.DrainTime("archive", DrainCause.IdleTimeout, notBefore: resumedAt);
        Assert.True(
            at is not null && at <= resumedAt + Idle + 1,
            $"(3) expected the idle drain at +{resumedAt + Idle}s, one idle period after the remount.{Environment.NewLine}{harness.Dump("archive")}");
    }

    /// <summary>
    /// The suspend handler and the idle fire queued in the same instant, suspend first. Suspend's
    /// drain (cause Suspend) must stand, and ADR-022's record must be kept so the drive comes back:
    /// the fire that follows finds a host already draining and must neither re-cause it to
    /// IdleTimeout (rule 9a: "a drain for any other cause ... never sets the record" -- re-causing
    /// would silently lose the user's drive after the sleep) nor issue its own unmount.
    /// </summary>
    [Fact]
    public async Task An_idle_fire_queued_behind_the_suspend_does_not_cost_the_user_the_remount()
    {
        var harness = IdleActivityHarness.OnDemand(Idle, Interval);
        await harness.StartAsync();
        await harness.MountAsync("archive");

        await harness.AdvanceToAsync(Idle - 1);
        var suspend = harness.Supervisor.SuspendAsync();
        harness.Time.Advance(TimeSpan.FromSeconds(2));
        await harness.Supervisor.DrainAsync();
        await suspend;

        await harness.AdvanceSecondsAsync(3600);
        Assert.Empty(harness.DrainsWith("archive", DrainCause.IdleTimeout));
        Assert.Single(harness.DrainsWith("archive", DrainCause.Suspend));

        await harness.ResumeAsync();

        Assert.True(
            harness.State("archive") == MountState.Mounted,
            $"the drive was not remounted after resume although the user had it mounted at suspend.{Environment.NewLine}{harness.Dump("archive")}");
        Assert.Equal(2, harness.Rclone.MountCount("Q:"));
    }

    /// <summary>
    /// The opposite order: the idle fire was processed first, so the drive was idle-unmounted before
    /// the suspend arrived. Rule 9a: a host already Draining or Disabled at suspend is not noted, and
    /// an idle drain never sets the record -- so resume must NOT bring the drive back. Bug: the
    /// remount record keyed on "was mounted recently" rather than on the state at suspend, making an
    /// idle unmount undo itself on every sleep.
    /// </summary>
    [Fact]
    public async Task A_drive_idle_unmounted_before_the_suspend_is_not_remounted_by_resume()
    {
        var harness = IdleActivityHarness.OnDemand(Idle, Interval);
        await harness.StartAsync();
        await harness.MountAsync("archive");

        await harness.AdvanceToAsync(Idle + 30);
        Assert.Single(harness.DrainsWith("archive", DrainCause.IdleTimeout));
        Assert.Equal(MountState.Ready, harness.State("archive"));

        await harness.SuspendAsync();
        await harness.AdvanceSecondsAsync(3600);
        await harness.ResumeAsync();
        await harness.AdvanceSecondsAsync(120);

        Assert.Equal(1, harness.Rclone.MountCount("Q:"));
        Assert.Equal(MountState.Ready, harness.State("archive"));
    }

    /// <summary>
    /// After a resume the activity sampler must be running for the remounted drive, or the drive is
    /// unmounted under an active user exactly as before bs-obf, but only after a sleep. Activity
    /// after the remount (+100 s into it) must push the drain out to one idle period after the
    /// sample that saw it. Bug: the sampler is started only by the user's Mount path and not by the
    /// ADR-022 remount path.
    /// </summary>
    [Fact]
    public async Task Activity_after_a_resume_remount_keeps_the_drive_up()
    {
        var harness = IdleActivityHarness.OnDemand(Idle, Interval);
        await harness.StartAsync();
        await harness.MountAsync("archive");
        await harness.AdvanceToAsync(100);
        await harness.SuspendAsync();
        await harness.AdvanceSecondsAsync(3600);
        await harness.ResumeAsync();
        Assert.Equal(MountState.Mounted, harness.State("archive"));
        var r = harness.Now;

        await harness.AdvanceToAsync(r + 100);
        harness.Rclone.SetStats("Q:", Vfs.Bump(harness.Rclone.StatsFor("Q:"), "diskCache.files", 1));

        await harness.AdvanceToAsync(r + Idle + 30);
        Assert.True(
            harness.State("archive") == MountState.Mounted,
            $"activity after the resume remount did not keep the drive up.{Environment.NewLine}{harness.Dump("archive")}");

        await harness.AdvanceToAsync(r + 120 + Idle + 5);
        Assert.NotNull(harness.DrainTime("archive", DrainCause.IdleTimeout, notBefore: r));
    }
}
