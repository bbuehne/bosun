using Bosun.Supervisor;

namespace Bosun.Tests.Supervisor.Independent.IdleActivity;

/// <summary>
/// docs/ARCHITECTURE.md §4 rule 7, "What counts as activity": the idle clock restarts whenever any
/// of six <c>vfs/stats</c> counters changed since the previous sample; the first sample (at the
/// mount) is the baseline and is thrown away on every unmount. OPERATIONS T5/T5b are the manual
/// form of these.
/// </summary>
/// <remarks>
/// Timing used throughout: mount at +0, mounted-probe interval 60 s, so samples land at +60, +120,
/// ... An idle period of 290 s keeps every idle deadline off a sample tick, so "the clock restarted
/// at the sample" gives one exact expected second rather than a coincidence of two timers.
/// </remarks>
public sealed class IdleActivityResetTests
{
    private const int Idle = 290;
    private const int Interval = 60;

    public static TheoryData<string> ActivityCounters => new()
    {
        "diskCache.bytesUsed",
        "diskCache.files",
        "diskCache.uploadsInProgress",
        "diskCache.uploadsQueued",
        "metadataCache.dirs",
        "metadataCache.files",
    };

    /// <summary>
    /// The control: nothing changes, so the drive unmounts one idle period after the mount, with the
    /// idle cause. Bug: a sampler that treats every sample as activity (comparing against nothing,
    /// or against <c>inUse</c>), so the drive never unmounts at all -- the pre-bs-obf T5 passing
    /// and T5 failing after the change.
    /// </summary>
    [Fact]
    public async Task With_no_counter_changing_the_drive_unmounts_one_idle_period_after_the_mount()
    {
        var harness = IdleActivityHarness.OnDemand(Idle, Interval);
        await harness.StartAsync();
        await harness.MountAsync("archive");

        await harness.AdvanceToAsync(Idle - 5);
        Assert.Equal(MountState.Mounted, harness.State("archive"));

        await harness.AdvanceToAsync(Idle + 5);
        var at = harness.DrainTime("archive", DrainCause.IdleTimeout);
        Assert.True(at is >= Idle and <= Idle + 1, $"expected the idle drain at +{Idle}s.{Environment.NewLine}{harness.Dump("archive")}");
    }

    /// <summary>
    /// Non-zero counters that do not MOVE are not activity: a VFS that already holds 40 cached
    /// files and 12 listed directories at the mount is idle if those numbers stay put. Bug: testing
    /// "&gt; 0" instead of "changed", which keeps any drive with a warm cache mounted forever.
    /// </summary>
    [Fact]
    public async Task Constant_non_zero_counters_are_not_activity()
    {
        var harness = IdleActivityHarness.OnDemand(Idle, Interval);
        harness.Rclone.DefaultStats = Vfs.Stats(bytesUsed: 5_000_000, files: 40, dirs: 12, metadataFiles: 300);
        await harness.StartAsync();
        await harness.MountAsync("archive");

        await harness.AdvanceToAsync(Idle + 5);

        var at = harness.DrainTime("archive", DrainCause.IdleTimeout);
        Assert.True(at is >= Idle and <= Idle + 1, $"expected the idle drain at +{Idle}s.{Environment.NewLine}{harness.Dump("archive")}");
    }

    /// <summary>
    /// Each of the six counters, moved and moved back, restarts the clock at the sample that sees
    /// each move. The move at +150 is sampled at +180; the move back at +190 is sampled at +240, so
    /// the drain is due at +240 + 290 = +530. Bug: a counter left out of the comparison (the drive
    /// then unmounts at +290 under an active user, which is exactly bs-obf), or "changed" tested as
    /// "increased" (the move back is missed and the drain comes at +470).
    /// </summary>
    [Theory]
    [MemberData(nameof(ActivityCounters))]
    public async Task A_change_in_any_activity_counter_restarts_the_idle_clock_at_the_sample_that_sees_it(string counter)
    {
        var harness = IdleActivityHarness.OnDemand(Idle, Interval);
        await harness.StartAsync();
        await harness.MountAsync("archive");
        var quiet = harness.Rclone.StatsFor("Q:");

        await harness.AdvanceToAsync(150);
        harness.Rclone.SetStats("Q:", Vfs.Bump(quiet, counter, +3));
        await harness.AdvanceToAsync(190);
        harness.Rclone.SetStats("Q:", quiet);

        await harness.AdvanceToAsync(525);
        Assert.True(
            harness.State("archive") == MountState.Mounted,
            $"{counter}: the drive left Mounted before +525s although the counter moved at +150 and +190.{Environment.NewLine}{harness.Dump("archive")}");

        await harness.AdvanceToAsync(535);
        var at = harness.DrainTime("archive", DrainCause.IdleTimeout);
        Assert.True(at is >= 530 and <= 531, $"{counter}: expected the idle drain at +530s.{Environment.NewLine}{harness.Dump("archive")}");
    }

    /// <summary>
    /// One change, sampled once: the clock restarts at that sample (+180), so the drain is at
    /// +180 + 290 = +470, not later. Bug: the restart counted from the NEXT sample, or a full extra
    /// period added, keeping drives up longer than T5b's documented window.
    /// </summary>
    [Fact]
    public async Task A_single_change_moves_the_deadline_to_one_idle_period_after_the_sample_that_saw_it()
    {
        var harness = IdleActivityHarness.OnDemand(Idle, Interval);
        await harness.StartAsync();
        await harness.MountAsync("archive");

        await harness.AdvanceToAsync(150);
        harness.Rclone.SetStats("Q:", Vfs.Bump(harness.Rclone.StatsFor("Q:"), "diskCache.bytesUsed", 4096));

        await harness.AdvanceToAsync(465);
        Assert.Equal(MountState.Mounted, harness.State("archive"));

        await harness.AdvanceToAsync(475);
        var at = harness.DrainTime("archive", DrainCause.IdleTimeout);
        Assert.True(at is >= 470 and <= 471, $"expected the idle drain at +470s.{Environment.NewLine}{harness.Dump("archive")}");
    }

    /// <summary>
    /// Rule 7: the baseline "is thrown away on every unmount, so a remount never compares against
    /// the previous mount". The first mount ends with counters A; the remounted VFS reports B
    /// throughout. With the baseline reset, B is the new baseline and nothing ever changes, so the
    /// drain is one idle period after the remount. Bug: the old baseline survives, the first sample
    /// after the remount sees A -&gt; B as activity, and the drive stays up an extra interval.
    /// </summary>
    [Fact]
    public async Task A_remount_takes_a_fresh_baseline_rather_than_comparing_against_the_previous_mount()
    {
        var harness = IdleActivityHarness.OnDemand(Idle, Interval);
        harness.Rclone.DefaultStats = Vfs.Stats(bytesUsed: 1000, files: 2, dirs: 3, metadataFiles: 10);
        await harness.StartAsync();
        await harness.MountAsync("archive");

        await harness.AdvanceToAsync(Idle + 30);
        Assert.Equal(MountState.Ready, harness.State("archive"));

        harness.Rclone.DefaultStats = Vfs.Stats(bytesUsed: 9000, files: 7, dirs: 30, metadataFiles: 99);
        await harness.MountAsync("archive");
        var remountedAt = harness.Now;

        await harness.AdvanceToAsync(remountedAt + Idle - 5);
        Assert.Equal(MountState.Mounted, harness.State("archive"));

        await harness.AdvanceToAsync(remountedAt + Idle + 5);
        var at = harness.DrainTime("archive", DrainCause.IdleTimeout, notBefore: remountedAt);
        Assert.True(
            at is not null && at <= remountedAt + Idle + 1,
            $"expected the second idle drain at +{remountedAt + Idle}s (one idle period after the remount at +{remountedAt}s).{Environment.NewLine}{harness.Dump("archive")}");
    }

    /// <summary>
    /// The same rule where it is not masked: the remount's own baseline read FAILS, so the first
    /// periodic sample (+60 into the remount) is the first reading of the new mount. With the old
    /// baseline thrown away, that sample only records; nothing changes afterwards, and the drain is
    /// one idle period after the remount. Bug: the previous mount's last sample survives the unmount
    /// and the first new sample is compared against it, so A -&gt; B counts as activity and the drive
    /// stays up an extra interval. (When the baseline read at the mount succeeds, a stale baseline
    /// is invisible: the "change" it reports restarts the clock at the mount instant, which is where
    /// the clock starts anyway. Mutation-checked: that variant is an equivalent mutant.)
    /// </summary>
    [Fact]
    public async Task A_remount_whose_baseline_read_failed_still_never_compares_against_the_previous_mount()
    {
        var harness = IdleActivityHarness.OnDemand(Idle, Interval);
        harness.Rclone.DefaultStats = Vfs.Stats(bytesUsed: 1000, files: 2, dirs: 3, metadataFiles: 10);
        await harness.StartAsync();
        await harness.MountAsync("archive");
        await harness.AdvanceToAsync(Idle + 30);
        Assert.Equal(MountState.Ready, harness.State("archive"));

        harness.Rclone.DefaultStats = Vfs.Stats(bytesUsed: 9000, files: 7, dirs: 30, metadataFiles: 99);
        harness.Rclone.FailNextVfsStats = 1;
        await harness.MountAsync("archive");
        Assert.Equal(0, harness.Rclone.FailNextVfsStats);
        var remountedAt = harness.Now;

        await harness.AdvanceToAsync(remountedAt + Idle + 5);
        var at = harness.DrainTime("archive", DrainCause.IdleTimeout, notBefore: remountedAt);
        Assert.True(
            at is not null && at <= remountedAt + Idle + 1,
            $"expected the second idle drain at +{remountedAt + Idle}s (one idle period after the remount at +{remountedAt}s).{Environment.NewLine}{harness.Dump("archive")}");
    }

    /// <summary>
    /// A user clicking Unmount and then Mount again before the idle period is up starts a new mount:
    /// the idle clock counts from the remount, not from the first mount. Bug: the first mount's timer
    /// survives the user unmount and fires into the new mount (a stale fire after a remount).
    /// </summary>
    [Fact]
    public async Task A_stale_idle_fire_from_a_previous_mount_does_not_unmount_the_new_mount()
    {
        var harness = IdleActivityHarness.OnDemand(Idle, Interval);
        await harness.StartAsync();
        await harness.MountAsync("archive");

        await harness.AdvanceToAsync(100);
        await harness.UnmountAsync("archive");
        Assert.Equal(MountState.Ready, harness.State("archive"));

        await harness.AdvanceToAsync(150);
        await harness.MountAsync("archive");
        Assert.Equal(MountState.Mounted, harness.State("archive"));

        await harness.AdvanceToAsync(150 + Idle - 5);
        Assert.True(
            harness.State("archive") == MountState.Mounted,
            $"the remounted drive left Mounted before its own idle period was up (+{150 + Idle}s).{Environment.NewLine}{harness.Dump("archive")}");
        Assert.Empty(harness.DrainsWith("archive", DrainCause.IdleTimeout));

        await harness.AdvanceToAsync(150 + Idle + 5);
        Assert.Single(harness.DrainsWith("archive", DrainCause.IdleTimeout));
    }
}
