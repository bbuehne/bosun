using Bosun.Rclone;
using Bosun.Status;
using Bosun.Supervisor;

namespace Bosun.Tests.Supervisor.Independent.IdleActivity;

/// <summary>
/// docs/ARCHITECTURE.md §4 rule 7, "Safety when the clock fires" and "Failed uploads": the
/// supervisor re-reads <c>vfs/stats</c> and <c>vfs/queue</c> and unmounts only if the VFS can be
/// read, nothing is uploading or queued, the queue is empty, <c>erroredFiles</c> is zero, and nothing
/// changed. Otherwise it skips, and restarts the clock for a FULL period. Failing to read counts as
/// a reason (fail closed: unmounting a drive with pending writes loses them).
/// </summary>
/// <remarks>
/// Every blocker below is held CONSTANT from the mount onwards, so no sample ever sees a change:
/// only the fire-time safety check can be what keeps the drive up. (A blocker that appeared mid-way
/// would also be "activity", and a test built that way could pass on change detection alone.)
/// </remarks>
public sealed class IdleFireSafetyTests
{
    private const int Idle = 290;
    private const int Interval = 60;

    public static TheoryData<string> Blockers => new()
    {
        "uploadsInProgress",
        "uploadsQueued",
        "queue non-empty",
        "erroredFiles",
    };

    public static TheoryData<string> UnreadableVfs => new()
    {
        "vfs/stats fails",
        "vfs/queue fails",
        "vfs/list fails",
        "diskCache absent (cache mode off)",
        "no vfs/list name equals the mounted fs",
        "only an indexed name[0] is listed",
        "only a trailing-slash variant is listed",
    };

    /// <summary>
    /// Bug: the fire unmounts on "no change since the last sample" alone, dropping writes that are
    /// still waiting to go up (or that rclone is still retrying) -- the data-loss case bs-obf's
    /// description names. Six idle periods is long enough that an implementation which only checks
    /// the first fire, or re-arms for a shorter period and then gives up, is caught.
    /// </summary>
    [Theory]
    [MemberData(nameof(Blockers))]
    public async Task A_drive_with_pending_or_failed_uploads_is_never_idle_unmounted(string blocker)
    {
        var harness = IdleActivityHarness.OnDemand(Idle, Interval);
        ApplyBlocker(harness, blocker);
        await harness.StartAsync();
        await harness.MountAsync("archive");

        await harness.AdvanceSecondsAsync(6 * Idle);

        Assert.True(
            harness.State("archive") == MountState.Mounted,
            $"{blocker}: the drive was unmounted with uploads pending or failed.{Environment.NewLine}{harness.Dump("archive")}");
        Assert.Equal(0, harness.Rclone.UnmountCount("Q:"));
    }

    /// <summary>
    /// The skip must look at <c>vfs/queue</c> as well as the counters: "the upload queue is empty"
    /// is its own condition. The queue is checked at fire time, so <c>vfs/queue</c> must have been
    /// called for the mounted fs. Bug: the queue read is left out because the counters looked
    /// sufficient.
    /// </summary>
    [Fact]
    public async Task The_fire_reads_vfs_queue_for_the_mounted_fs()
    {
        var harness = IdleActivityHarness.OnDemand(Idle, Interval);
        await harness.StartAsync();
        await harness.MountAsync("archive");
        var fs = harness.Rclone.LastFsFor("Q:");

        await harness.AdvanceToAsync(Idle + 5);

        Assert.Contains($"vfs/queue|{fs}", harness.Rclone.VfsCalls);
        Assert.NotNull(harness.DrainTime("archive", DrainCause.IdleTimeout));
    }

    /// <summary>
    /// Fail closed: when the VFS cannot be read, or the one rcd lists is not EXACTLY the fs that was
    /// mounted, nothing is known about pending writes, so the drive stays up. Bug classes: a failed
    /// read treated as "no activity" (unmount on the first rcd hiccup); <c>diskCache</c> missing read
    /// as zeros; a fuzzy name match (prefix, case, <c>[0]</c>) that guesses at a VFS rule 7 says
    /// must not be guessed at.
    /// </summary>
    [Theory]
    [MemberData(nameof(UnreadableVfs))]
    public async Task An_unreadable_or_unmatched_VFS_is_never_idle_unmounted(string failure)
    {
        var harness = IdleActivityHarness.OnDemand(Idle, Interval);
        ApplyUnreadable(harness, failure);
        await harness.StartAsync();
        await harness.MountAsync("archive");

        await harness.AdvanceSecondsAsync(6 * Idle);

        Assert.True(
            harness.State("archive") == MountState.Mounted,
            $"{failure}: the drive was idle-unmounted although its VFS could not be read.{Environment.NewLine}{harness.Dump("archive")}");
        Assert.Equal(0, harness.Rclone.UnmountCount("Q:"));
    }

    /// <summary>
    /// "Skips the unmount ... and restarts the clock for a FULL period", and the brief's case
    /// "erroredFiles clearing, then the drive unmounting on the next idle period". The blocker is
    /// there at the fire at +290 and gone at +300; a change in <c>erroredFiles</c> or in the queue
    /// is not one of the six activity counters, so nothing resets the clock again and the next fire
    /// is at +290 + 290 = +580. Bug classes: re-arming for one probe interval instead of a full
    /// period (drain at +350), or never re-arming at all (the drive stays mounted forever after one
    /// skip).
    /// </summary>
    [Theory]
    [InlineData("erroredFiles")]
    [InlineData("queue non-empty")]
    public async Task After_a_skipped_fire_the_drive_unmounts_one_full_idle_period_later_once_the_blocker_has_cleared(string blocker)
    {
        var harness = IdleActivityHarness.OnDemand(Idle, Interval);
        ApplyBlocker(harness, blocker);
        await harness.StartAsync();
        await harness.MountAsync("archive");

        await harness.AdvanceToAsync(Idle + 10);
        Assert.Equal(MountState.Mounted, harness.State("archive"));

        ClearBlocker(harness, blocker);

        await harness.AdvanceToAsync((2 * Idle) - 5);
        Assert.True(
            harness.State("archive") == MountState.Mounted,
            $"{blocker}: after the skip at +{Idle}s the drive went before a full period (+{2 * Idle}s).{Environment.NewLine}{harness.Dump("archive")}");

        await harness.AdvanceToAsync((2 * Idle) + 5);
        var at = harness.DrainTime("archive", DrainCause.IdleTimeout);
        Assert.True(
            at is >= 2 * Idle and <= (2 * Idle) + 1,
            $"{blocker}: expected the idle drain at +{2 * Idle}s, one full period after the skip.{Environment.NewLine}{harness.Dump("archive")}");
    }

    /// <summary>
    /// A poll that failed at the fire, and only then, keeps the drive up for one more period; the
    /// next fire, with the VFS readable again and nothing changed, unmounts it. Bug: the fail-closed
    /// branch never re-arms (one rcd hiccup at the wrong second and the drive is up for good).
    /// </summary>
    [Fact]
    public async Task A_one_off_poll_failure_at_the_fire_postpones_the_unmount_by_one_full_period()
    {
        var harness = IdleActivityHarness.OnDemand(Idle, Interval);
        await harness.StartAsync();
        await harness.MountAsync("archive");

        await harness.AdvanceToAsync(Idle - 1);
        harness.Rclone.FailNextVfsStats = 1;
        await harness.AdvanceToAsync(Idle + 5);
        Assert.Equal(MountState.Mounted, harness.State("archive"));
        Assert.Equal(0, harness.Rclone.FailNextVfsStats);

        await harness.AdvanceToAsync((2 * Idle) + 5);
        var at = harness.DrainTime("archive", DrainCause.IdleTimeout);
        Assert.True(
            at is >= 2 * Idle and <= (2 * Idle) + 1,
            $"expected the idle drain at +{2 * Idle}s.{Environment.NewLine}{harness.Dump("archive")}");
    }

    /// <summary>
    /// An idle period shorter than the probe interval: the baseline taken at the mount is the
    /// "earlier sample", so the fire at +30 can decide and the drive goes at +30. Bug: the fire
    /// insists on a periodic sample having happened first (the drive then outlives its period by
    /// the probe interval for every short-idle host).
    /// </summary>
    [Fact]
    public async Task An_idle_period_shorter_than_the_probe_interval_unmounts_on_time_when_the_baseline_was_taken()
    {
        var harness = IdleActivityHarness.OnDemand(idleUnmountSeconds: 30, mountedProbeIntervalSeconds: 60);
        await harness.StartAsync();
        await harness.MountAsync("archive");

        await harness.AdvanceToAsync(35);

        var at = harness.DrainTime("archive", DrainCause.IdleTimeout);
        Assert.True(at is >= 30 and <= 31, $"expected the idle drain at +30s.{Environment.NewLine}{harness.Dump("archive")}");
    }

    /// <summary>
    /// Rule 7: "A clock that fires with no earlier sample to compare against (an idle period shorter
    /// than the probe interval, and a baseline that could not be taken) takes one and waits one more
    /// period." The baseline read at the mount fails; the fire at +30 takes the first sample and
    /// must NOT unmount; the next fire (+60) compares and unmounts. Bug: a fire with nothing to
    /// compare against treating "no previous sample" as "no change" and unmounting at +30.
    /// </summary>
    [Fact]
    public async Task A_fire_with_no_earlier_sample_takes_one_and_waits_one_more_period()
    {
        var harness = IdleActivityHarness.OnDemand(idleUnmountSeconds: 30, mountedProbeIntervalSeconds: 60);
        await harness.StartAsync();
        harness.Rclone.FailNextVfsStats = 1;
        await harness.MountAsync("archive");
        Assert.Equal(0, harness.Rclone.FailNextVfsStats);

        await harness.AdvanceToAsync(35);
        Assert.True(
            harness.State("archive") == MountState.Mounted,
            $"the first fire had no earlier sample to compare against and unmounted anyway.{Environment.NewLine}{harness.Dump("archive")}");

        await harness.AdvanceToAsync(65);
        var at = harness.DrainTime("archive", DrainCause.IdleTimeout);
        Assert.True(at is >= 60 and <= 61, $"expected the idle drain at +60s.{Environment.NewLine}{harness.Dump("archive")}");
    }

    /// <summary>
    /// "Failed uploads": <c>erroredFiles &gt; 0</c> is shown on the host's row as "N files failed to
    /// upload", from the snapshot. Bug classes: the count never reaches the snapshot (the user sees
    /// a healthy drive that will not unmount and never learns why), or it lingers after the count
    /// clears.
    /// </summary>
    [Fact]
    public async Task ErroredFiles_reaches_the_snapshot_and_the_row_and_clears_when_rclone_clears_it()
    {
        var harness = IdleActivityHarness.OnDemand(idleUnmountSeconds: 3600, mountedProbeIntervalSeconds: Interval);
        harness.Rclone.SetStats("Q:", Vfs.Stats(erroredFiles: 3));
        await harness.StartAsync();
        await harness.MountAsync("archive");

        await harness.AdvanceSecondsAsync(Interval);
        var snapshot = harness.Snapshot("archive");
        Assert.Equal(3, snapshot.UploadFailureCount);

        var hostConfig = harness.Store.Current.Hosts["archive"];
        var row = StatusDerivation.DeriveRow(snapshot, hostConfig, sessionCount: 0);
        Assert.Contains("3 files failed to upload", row.StatusText, StringComparison.Ordinal);

        harness.Rclone.SetStats("Q:", Vfs.Stats(erroredFiles: 0));
        await harness.AdvanceSecondsAsync(Interval);
        Assert.Equal(0, harness.Snapshot("archive").UploadFailureCount);
    }

    /// <summary>
    /// "It is host-specific, so it is on the row, not in the app-wide banner, and it does not change
    /// the tray icon." Bug: the new row category given a Degraded or Error contribution, turning
    /// the tray icon amber for a condition rule 7 says belongs only on the row.
    /// </summary>
    [Fact]
    public async Task Failed_uploads_do_not_change_the_aggregate_health()
    {
        var harness = IdleActivityHarness.OnDemand(idleUnmountSeconds: 3600, mountedProbeIntervalSeconds: Interval);
        harness.Rclone.SetStats("Q:", Vfs.Stats(erroredFiles: 1));
        await harness.StartAsync();
        await harness.MountAsync("archive");
        await harness.AdvanceSecondsAsync(Interval);

        var hostConfig = harness.Store.Current.Hosts["archive"];
        var row = StatusDerivation.DeriveRow(harness.Snapshot("archive"), hostConfig, sessionCount: 0);
        Assert.Contains("1 file failed to upload", row.StatusText, StringComparison.Ordinal);
        Assert.Equal(AggregateHealth.Healthy, StatusDerivation.ContributionOf(row));
    }

    /// <summary>
    /// "Zero in every other state": after the drive is gone the count is not left on the row of a
    /// host that is no longer mounted. Bug: the field is only ever written by a successful poll, so
    /// the last value sticks to a Ready host.
    /// </summary>
    [Fact]
    public async Task The_upload_failure_count_is_zero_once_the_host_is_no_longer_mounted()
    {
        var harness = IdleActivityHarness.OnDemand(idleUnmountSeconds: 3600, mountedProbeIntervalSeconds: Interval);
        harness.Rclone.SetStats("Q:", Vfs.Stats(erroredFiles: 2));
        await harness.StartAsync();
        await harness.MountAsync("archive");
        await harness.AdvanceSecondsAsync(Interval);
        Assert.Equal(2, harness.Snapshot("archive").UploadFailureCount);

        await harness.UnmountAsync("archive");

        Assert.Equal(MountState.Ready, harness.State("archive"));
        Assert.Equal(0, harness.Snapshot("archive").UploadFailureCount);
    }

    private static void ApplyBlocker(IdleActivityHarness harness, string blocker)
    {
        switch (blocker)
        {
            case "uploadsInProgress":
                harness.Rclone.SetStats("Q:", Vfs.Stats(uploadsInProgress: 1));
                break;
            case "uploadsQueued":
                harness.Rclone.SetStats("Q:", Vfs.Stats(uploadsQueued: 2));
                break;
            case "queue non-empty":
                harness.Rclone.SetQueue("Q:", [new RcloneVfsQueueItem { Name = "notes/todo.md", Uploading = false }]);
                break;
            case "erroredFiles":
                harness.Rclone.SetStats("Q:", Vfs.Stats(erroredFiles: 1));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(blocker), blocker, null);
        }
    }

    private static void ClearBlocker(IdleActivityHarness harness, string blocker)
    {
        switch (blocker)
        {
            case "queue non-empty":
                harness.Rclone.SetQueue("Q:", []);
                break;
            case "erroredFiles":
                harness.Rclone.SetStats("Q:", Vfs.Stats(erroredFiles: 0));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(blocker), blocker, "clearing this blocker moves an activity counter");
        }
    }

    private static void ApplyUnreadable(IdleActivityHarness harness, string failure)
    {
        switch (failure)
        {
            case "vfs/stats fails":
                harness.Rclone.FailVfsStats = true;
                break;
            case "vfs/queue fails":
                harness.Rclone.FailVfsQueue = true;
                break;
            case "vfs/list fails":
                harness.Rclone.FailVfsList = true;
                break;
            case "diskCache absent (cache mode off)":
                harness.Rclone.SetStats("Q:", Vfs.Stats() with { DiskCache = null });
                break;
            case "no vfs/list name equals the mounted fs":
                harness.Rclone.VfsListOverride = _ => ["bosun-somethingelse:/srv/share"];
                break;
            case "only an indexed name[0] is listed":
                harness.Rclone.VfsListOverride = mounted => mounted.Select(fs => fs + "[0]").ToList();
                break;
            case "only a trailing-slash variant is listed":
                harness.Rclone.VfsListOverride = mounted => mounted.Select(fs => fs + "/").ToList();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(failure), failure, null);
        }
    }
}
