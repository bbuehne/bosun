using Bosun.Rclone;
using Bosun.Status;
using Bosun.Supervisor;
using Bosun.Tests.Rclone.Fakes;
using Bosun.Tests.Supervisor.Fakes;
using Bosun.Tests.Supervisor.Support;
using Microsoft.Extensions.Logging;

namespace Bosun.Tests.Supervisor;

/// <summary>
/// bs-obf, docs/ARCHITECTURE.md §4 rule 7: an on-demand host idle-unmounts after
/// <c>idle_unmount_seconds</c> WITHOUT ACTIVITY, where activity is a change in rclone's own VFS
/// counters (<c>vfs/stats</c>), and never while uploads are pending, queued or failed, and never when
/// the VFS cannot be read. Everything here runs on <see cref="FakeRcloneMountClient"/> and fake time:
/// no real rclone, drive letter or clock.
/// </summary>
/// <remarks>
/// The default global config probes a mounted host every 60 s, so the activity sample (which rides
/// that probe) is taken at 60 s, 120 s and so on after the mount. Tests move the clock in 60 s steps
/// (<see cref="StepAsync"/>) because <c>FakeTimeProvider.Advance</c> fires every due timer in one go
/// and re-arms only afterwards: one 240 s jump would be a single probe tick, not four.
/// </remarks>
public sealed class IdleActivityTests
{
    private const string HostKey = "archive";
    private const string Drive = "Q:";

    /// <summary>What <c>MountSupervisor</c> passes to <c>mount/mount</c> for the fixture host.</summary>
    private const string ExpectedFs = "bosun-archive:/srv/share";

    private const int IdleSeconds = 120;

    private static async Task<SupervisorHarness> MountedHostAsync(int idleSeconds = IdleSeconds, Action<SupervisorHarness>? beforeMount = null)
    {
        var host = HostFixtures.OnDemand(HostKey, drive: Drive, idleUnmountSeconds: idleSeconds);
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        beforeMount?.Invoke(harness);
        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync(HostKey));
        Assert.Equal(MountState.Mounted, harness.Snapshot(HostKey).State);
        return harness;
    }

    private static async Task StepAsync(SupervisorHarness harness, int steps, int stepSeconds = 60)
    {
        for (var i = 0; i < steps; i++)
        {
            await harness.AdvanceAsync(TimeSpan.FromSeconds(stepSeconds));
        }
    }

    private static bool IsMounted(SupervisorHarness harness) => harness.Snapshot(HostKey).State == MountState.Mounted;

    // ------------------------------------------------------------------------------------------
    // Activity resets the idle clock
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task With_no_change_in_the_VFS_the_host_unmounts_at_the_timeout()
    {
        var harness = await MountedHostAsync();

        await StepAsync(harness, 1);
        Assert.True(IsMounted(harness));
        await harness.AdvanceAsync(TimeSpan.FromSeconds(59));
        Assert.True(IsMounted(harness));
        await harness.AdvanceAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, harness.Rclone.UnmountCallCountFor(Drive));
        Assert.NotEqual(MountState.Mounted, harness.Snapshot(HostKey).State);
        Assert.Contains(
            harness.Supervisor.GetTransitionHistory(),
            t => t.To == MountState.Draining && t.DrainCause == DrainCause.IdleTimeout);
    }

    public static TheoryData<string> Counters => new()
    {
        "diskCache.bytesUsed",
        "diskCache.files",
        "diskCache.uploadsInProgress",
        "diskCache.uploadsQueued",
        "metadataCache.dirs",
        "metadataCache.files",
    };

    private static RcloneVfsStats WithCounterChanged(string counter) => counter switch
    {
        "diskCache.bytesUsed" => IdleVfs.Stats(bytesUsed: 4096),
        "diskCache.files" => IdleVfs.Stats(files: 1),
        "diskCache.uploadsInProgress" => IdleVfs.Stats(uploadsInProgress: 1),
        "diskCache.uploadsQueued" => IdleVfs.Stats(uploadsQueued: 1),
        "metadataCache.dirs" => IdleVfs.Stats(dirs: 9),
        "metadataCache.files" => IdleVfs.Stats(metadataFiles: 9),
        _ => throw new ArgumentOutOfRangeException(nameof(counter), counter, null),
    };

    [Theory]
    [MemberData(nameof(Counters))]
    public async Task A_change_in_each_counter_resets_the_idle_clock(string counter)
    {
        // The counter moves for exactly one sample (the one at 60 s) and is back by the next (120 s).
        // Each of those two samples sees a change, so the idle clock restarts at 60 s and again at
        // 120 s: the host must outlive the original 120 s deadline and the first restart's 180 s one,
        // and unmount 120 s after the LAST change -- at 240 s. A counter that is not watched leaves
        // the original timer in force, and the host is gone at 120 s.
        var harness = await MountedHostAsync();

        harness.Rclone.SetVfsStats(WithCounterChanged(counter));
        await StepAsync(harness, 1);
        harness.Rclone.SetVfsStats(IdleVfs.Stats());
        await StepAsync(harness, 1);
        Assert.True(IsMounted(harness), $"unmounted at 120 s although {counter} changed");

        await StepAsync(harness, 1);
        Assert.True(IsMounted(harness), $"unmounted at 180 s although {counter} last changed at 120 s");
        Assert.Equal(0, harness.Rclone.UnmountCallCountFor(Drive));

        await StepAsync(harness, 1);
        Assert.Equal(1, harness.Rclone.UnmountCallCountFor(Drive));
        Assert.NotEqual(MountState.Mounted, harness.Snapshot(HostKey).State);
    }

    [Fact]
    public async Task A_change_that_stays_in_place_is_one_burst_of_activity_not_a_standing_one()
    {
        // bytesUsed jumps once at the 60 s sample and then holds still: activity at 60 s, so the
        // unmount is 120 s later, at 180 s.
        var harness = await MountedHostAsync();

        harness.Rclone.SetVfsStats(IdleVfs.Stats(bytesUsed: 100));
        await StepAsync(harness, 2);
        Assert.True(IsMounted(harness));
        await harness.AdvanceAsync(TimeSpan.FromSeconds(59));
        Assert.True(IsMounted(harness));
        await harness.AdvanceAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, harness.Rclone.UnmountCallCountFor(Drive));
    }

    [Fact]
    public async Task Activity_at_the_instant_the_idle_clock_fires_wins_over_the_fire()
    {
        // The idle timer (120 s) and the second mounted probe (120 s) are due together and both are
        // queued before either runs. Whichever runs first, the host must stay up: activity restarts
        // the clock, and a fire that was already queued must not unmount it.
        var harness = await MountedHostAsync();
        await StepAsync(harness, 1);

        harness.Rclone.SetVfsStats(IdleVfs.Stats(bytesUsed: 1));
        await StepAsync(harness, 1);

        Assert.True(IsMounted(harness));
        Assert.Equal(0, harness.Rclone.UnmountCallCountFor(Drive));
    }

    [Fact]
    public async Task The_sample_rides_the_mounted_probe_cadence_one_poll_per_probe()
    {
        var harness = await MountedHostAsync();
        var afterMount = harness.Rclone.VfsStatsCallCount;
        Assert.Equal(1, afterMount); // the baseline, taken at mount

        await StepAsync(harness, 1);

        Assert.Equal(afterMount + 1, harness.Rclone.VfsStatsCallCount);
        Assert.Equal(0, harness.Rclone.VfsQueueCallCount); // the queue is read only when the clock fires
    }

    // ------------------------------------------------------------------------------------------
    // Which VFS: the exact fs string given to mount/mount
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_VFS_is_found_by_the_exact_fs_string_that_was_passed_to_mount_mount()
    {
        var harness = await MountedHostAsync();
        await StepAsync(harness, 2);

        var mountedFs = Assert.Single(harness.Rclone.MountCalls).Fs;
        Assert.Equal(ExpectedFs, mountedFs);
        Assert.NotEmpty(harness.Rclone.VfsStatsFsArguments);
        Assert.All(harness.Rclone.VfsStatsFsArguments, fs => Assert.Equal(mountedFs, fs));
    }

    [Theory]
    [InlineData("bosun-archive:/srv/share/")]   // trailing slash
    [InlineData("bosun-archive:srv/share")]     // root without the leading slash
    [InlineData("BOSUN-ARCHIVE:/srv/share")]    // case
    [InlineData("bosun-archive:/srv/share[0]")] // rclone's name for a second VFS on the same fs
    [InlineData("bosun-other:/srv/share")]      // someone else's VFS
    public async Task A_VFS_name_that_is_not_exactly_the_fs_blocks_the_idle_unmount_and_is_never_guessed(string listedName)
    {
        var harness = await MountedHostAsync(beforeMount: h => h.Rclone.SetVfsNames(listedName));

        await StepAsync(harness, 4); // 240 s: two idle periods

        Assert.True(IsMounted(harness));
        Assert.Equal(0, harness.Rclone.UnmountCallCountFor(Drive));
        Assert.Equal(0, harness.Rclone.VfsStatsCallCount); // no stats for a VFS we cannot identify
        Assert.Contains(
            harness.Log.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains("could not be read") && e.Message.Contains(ExpectedFs));
    }

    [Fact]
    public async Task No_active_VFS_at_all_blocks_the_idle_unmount()
    {
        var harness = await MountedHostAsync(beforeMount: h => h.Rclone.SetVfsNames());

        await StepAsync(harness, 4);

        Assert.True(IsMounted(harness));
    }

    // ------------------------------------------------------------------------------------------
    // Safety at fire time
    // ------------------------------------------------------------------------------------------

    public static TheoryData<string> BlockingConditions => new()
    {
        "uploadsInProgress",
        "uploadsQueued",
        "queue non-empty",
        "erroredFiles",
    };

    private static void ApplyBlockingCondition(SupervisorHarness harness, string condition)
    {
        switch (condition)
        {
            case "uploadsInProgress":
                harness.Rclone.SetVfsStats(IdleVfs.Stats(uploadsInProgress: 1));
                break;
            case "uploadsQueued":
                harness.Rclone.SetVfsStats(IdleVfs.Stats(uploadsQueued: 2));
                break;
            case "queue non-empty":
                // The counters say nothing is queued; only vfs/queue knows.
                harness.Rclone.SetVfsQueue(new RcloneVfsQueueItem { Name = "pending.txt", Uploading = false });
                break;
            case "erroredFiles":
                harness.Rclone.SetVfsStats(IdleVfs.Stats(erroredFiles: 3));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(condition), condition, null);
        }
    }

    private static void ClearBlockingCondition(SupervisorHarness harness)
    {
        harness.Rclone.SetVfsStats(IdleVfs.Stats());
        harness.Rclone.SetVfsQueue();
    }

    [Theory]
    [MemberData(nameof(BlockingConditions))]
    public async Task Each_safety_condition_blocks_the_idle_unmount_and_re_arms_the_clock(string condition)
    {
        var harness = await MountedHostAsync();
        ApplyBlockingCondition(harness, condition);

        // Past three idle periods the condition is still there, so the host is still up -- the clock
        // was restarted each time rather than left spent.
        await StepAsync(harness, 7);

        Assert.True(IsMounted(harness), $"unmounted while {condition}");
        Assert.Equal(0, harness.Rclone.UnmountCallCountFor(Drive));
        Assert.True(harness.Rclone.VfsQueueCallCount >= 2, "the upload queue was not consulted when the clock fired");

        // Once it clears, the next quiet idle period unmounts it: the block was a postponement.
        ClearBlockingCondition(harness);
        await StepAsync(harness, 6);
        Assert.Equal(1, harness.Rclone.UnmountCallCountFor(Drive));
    }

    [Fact]
    public async Task A_VFS_poll_that_fails_at_fire_time_blocks_and_re_arms_whichever_call_failed()
    {
        foreach (var fail in new Action<FakeRcloneMountClient>[]
                 {
                     rc => rc.MakeVfsListThrow(new InvalidOperationException("list down")),
                     rc => rc.MakeVfsStatsThrow(new RcloneRcException("vfs/stats", 500, "stats down")),
                     rc => rc.MakeVfsQueueThrow(new HttpRequestException("queue down")),
                     rc => rc.MakeVfsStatsThrow(new TaskCanceledException("an rc timeout is not shutdown")),
                 })
        {
            var harness = await MountedHostAsync();
            fail(harness.Rclone);

            await StepAsync(harness, 5);

            Assert.True(IsMounted(harness), "unmounted although the VFS could not be read");
            Assert.Equal(0, harness.Rclone.UnmountCallCountFor(Drive));
            Assert.Contains(harness.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("could not be read"));

            harness.Rclone.StopThrowingFromVfs();
            await StepAsync(harness, 5);
            Assert.Equal(1, harness.Rclone.UnmountCallCountFor(Drive));
        }
    }

    [Fact]
    public async Task A_stats_response_with_no_diskCache_blocks_the_idle_unmount()
    {
        // Cache mode off: diskCache is omitted. Zeros would read as "nothing happening".
        var harness = await MountedHostAsync();
        harness.Rclone.SetVfsStats(new RcloneVfsStats { DiskCache = null, MetadataCache = new RcloneVfsMetadataCache { Dirs = 1, Files = 0 } });

        await StepAsync(harness, 4);

        Assert.True(IsMounted(harness));
        Assert.Contains(harness.Log.Entries, e => e.Message.Contains("no diskCache"));
    }

    [Fact]
    public async Task A_stats_response_with_no_metadataCache_blocks_the_idle_unmount()
    {
        var harness = await MountedHostAsync();
        harness.Rclone.SetVfsStats(new RcloneVfsStats
        {
            DiskCache = new RcloneVfsDiskCache { BytesUsed = 0, Files = 0, UploadsInProgress = 0, UploadsQueued = 0, ErroredFiles = 0 },
            MetadataCache = null,
        });

        await StepAsync(harness, 4);

        Assert.True(IsMounted(harness));
    }

    [Fact]
    public async Task A_failed_periodic_sample_records_no_activity_and_does_not_unmount_on_its_own()
    {
        // The sample at 60 s fails; the idle clock is untouched by it. At 120 s the fire-time poll
        // also fails, so the host stays. The failure is logged once, not on every tick.
        var harness = await MountedHostAsync();
        harness.Rclone.MakeVfsStatsThrow(new InvalidOperationException("stats down"));

        await StepAsync(harness, 6);

        Assert.True(IsMounted(harness));
        var sampleWarnings = harness.Log.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains("Could not sample VFS activity"));
        Assert.Equal(1, sampleWarnings);
    }

    [Fact]
    public async Task A_hung_rcd_holds_the_channel_no_longer_than_the_probe_timeout()
    {
        var harness = await MountedHostAsync();
        harness.Rclone.HangVfsCalls = true;

        // Reach the instant the clock fires (the 60 s sample and the 120 s fire are both due; the
        // sample comes first and parks on the hung call).
        harness.Time.Advance(TimeSpan.FromSeconds(60));
        var drain = harness.Supervisor.DrainAsync();
        Assert.False(drain.IsCompleted, "the hung call should be holding the channel");

        harness.Time.Advance(TimeSpan.FromSeconds(4)); // probe timeout is 5 s: not yet
        Assert.False(drain.IsCompleted);

        harness.Time.Advance(TimeSpan.FromSeconds(1)); // 5 s: the budget is spent
        await drain;

        Assert.True(IsMounted(harness));
        // And the supervisor is still serving: a fresh command runs.
        await harness.RunAsync(() => harness.Supervisor.RecordActivityAsync(HostKey));
    }

    [Fact]
    public async Task Every_hung_poll_in_a_row_is_cut_off_after_one_probe_timeout_each()
    {
        var harness = await MountedHostAsync();
        await StepAsync(harness, 1);
        harness.Rclone.HangVfsCalls = true;

        // Both the second sample and the fire are due at 120 s; each polls, and each poll hangs.
        var callsBefore = harness.Rclone.VfsCallTotal;
        harness.Time.Advance(TimeSpan.FromSeconds(60));
        var drain = harness.Supervisor.DrainAsync();

        await harness.Rclone.WaitForVfsCallAsync(callsBefore + 1); // the first poll is hung
        harness.Time.Advance(TimeSpan.FromSeconds(5));             // its budget is spent
        await harness.Rclone.WaitForVfsCallAsync(callsBefore + 2); // the second poll has begun
        Assert.False(drain.IsCompleted, "the second hung poll should be holding the channel");

        harness.Time.Advance(TimeSpan.FromSeconds(5));
        await drain;

        Assert.True(IsMounted(harness));
        Assert.Equal(0, harness.Rclone.UnmountCallCountFor(Drive));
    }

    // ------------------------------------------------------------------------------------------
    // Baselines
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_first_sample_after_a_remount_is_the_baseline_and_is_not_compared_with_the_last_mount()
    {
        var harness = await MountedHostAsync();
        await StepAsync(harness, 1); // baseline from the first mount is in place (all zeros)

        await harness.RunAsync(() => harness.Supervisor.RequestUnmountAsync(HostKey));
        Assert.NotEqual(MountState.Mounted, harness.Snapshot(HostKey).State);
        var unmountsBefore = harness.Rclone.UnmountCallCountFor(Drive);

        // The VFS now reads differently (a fresh rcd, say), and the baseline cannot be taken at
        // mount time because vfs/list fails for that one call.
        harness.Rclone.SetVfsStats(IdleVfs.Stats(bytesUsed: 500, files: 2));
        harness.Rclone.MakeVfsListThrow(new InvalidOperationException("not yet"));
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync(HostKey));
        Assert.True(IsMounted(harness));
        harness.Rclone.StopThrowingFromVfs();

        // 60 s: the first sample after the mount. If it were compared with the previous mount's
        // zeros it would count as activity and push the unmount from 120 s out to 180 s.
        await StepAsync(harness, 2);

        Assert.Equal(unmountsBefore + 1, harness.Rclone.UnmountCallCountFor(Drive));
    }

    [Fact]
    public async Task A_fire_with_no_baseline_re_arms_once_rather_than_unmounting_blind()
    {
        // Idle 30 s, probe every 60 s: the clock fires before any periodic sample. The mount-time
        // baseline failed, so there is nothing to compare with; the host gets one more period.
        var harness = await MountedHostAsync(idleSeconds: 30, beforeMount: h => h.Rclone.MakeVfsListThrow(new InvalidOperationException("not yet")));
        harness.Rclone.StopThrowingFromVfs();

        await harness.AdvanceAsync(TimeSpan.FromSeconds(30));
        Assert.True(IsMounted(harness));
        Assert.Equal(0, harness.Rclone.UnmountCallCountFor(Drive));

        await harness.AdvanceAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, harness.Rclone.UnmountCallCountFor(Drive));
    }

    [Fact]
    public async Task A_remount_starts_a_fresh_idle_clock_and_the_VFS_state_left_by_the_last_mount_is_not_activity()
    {
        var harness = await MountedHostAsync();
        harness.Rclone.SetVfsStats(IdleVfs.Stats(bytesUsed: 100));
        await StepAsync(harness, 1); // activity at 60 s
        await harness.RunAsync(() => harness.Supervisor.RequestUnmountAsync(HostKey));
        var unmountsBefore = harness.Rclone.UnmountCallCountFor(Drive);

        harness.Rclone.SetVfsStats(IdleVfs.Stats(bytesUsed: 100));
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync(HostKey));
        await StepAsync(harness, 2);

        Assert.Equal(unmountsBefore + 1, harness.Rclone.UnmountCallCountFor(Drive));
    }

    // ------------------------------------------------------------------------------------------
    // Who is polled
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_persistent_host_is_never_polled_even_with_idle_unmount_seconds_set()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:", idleUnmountSeconds: 10);
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        await harness.StartAsync();
        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);

        await StepAsync(harness, 10);

        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);
        AssertNeverPolled(harness);
    }

    [Fact]
    public async Task An_on_demand_host_with_idle_zero_is_never_polled()
    {
        var harness = await MountedHostAsync(idleSeconds: 0);

        await StepAsync(harness, 10);

        Assert.True(IsMounted(harness));
        AssertNeverPolled(harness);
    }

    private static void AssertNeverPolled(SupervisorHarness harness)
    {
        Assert.Equal(0, harness.Rclone.VfsListCallCount);
        Assert.Equal(0, harness.Rclone.VfsStatsCallCount);
        Assert.Equal(0, harness.Rclone.VfsQueueCallCount);
    }

    [Fact]
    public async Task A_mount_adopted_at_startup_is_sampled_and_idle_unmounts_like_any_other()
    {
        var host = HostFixtures.OnDemand(HostKey, drive: Drive, idleUnmountSeconds: IdleSeconds);
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        harness.Rclone.SeedExistingMount(Drive, ExpectedFs);

        await harness.StartAsync();
        Assert.True(IsMounted(harness));
        Assert.Equal(1, harness.Rclone.VfsStatsCallCount); // the baseline

        await StepAsync(harness, 2);

        Assert.Equal(1, harness.Rclone.UnmountCallCountFor(Drive));
    }

    // ------------------------------------------------------------------------------------------
    // Files that failed to upload: on the host's own row
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Errored_files_appear_on_the_hosts_snapshot_and_then_on_its_status_row()
    {
        var harness = await MountedHostAsync();
        harness.Rclone.SetVfsStats(IdleVfs.Stats(erroredFiles: 3));
        await StepAsync(harness, 1);

        var snapshot = harness.Snapshot(HostKey);
        Assert.Equal(3, snapshot.UploadFailureCount);

        var host = HostFixtures.OnDemand(HostKey, drive: Drive, idleUnmountSeconds: IdleSeconds);
        var row = StatusDerivation.DeriveRow(snapshot, host, sessionCount: 0);
        Assert.Equal(StatusCategory.UploadsFailing, row.Category);
        Assert.Equal("Q: is mounted, but 3 files failed to upload", row.StatusText);
    }

    [Fact]
    public async Task The_upload_failure_count_clears_when_the_files_are_no_longer_errored_and_when_the_host_leaves_Mounted()
    {
        // A long idle period, so that the host is not idle-unmounted while the test moves the clock.
        var harness = await MountedHostAsync(idleSeconds: 100_000);
        harness.Rclone.SetVfsStats(IdleVfs.Stats(erroredFiles: 1));
        await StepAsync(harness, 1);
        Assert.Equal(1, harness.Snapshot(HostKey).UploadFailureCount);

        harness.Rclone.SetVfsStats(IdleVfs.Stats());
        await StepAsync(harness, 1);
        Assert.Equal(0, harness.Snapshot(HostKey).UploadFailureCount);

        harness.Rclone.SetVfsStats(IdleVfs.Stats(erroredFiles: 2));
        await StepAsync(harness, 1);
        Assert.Equal(2, harness.Snapshot(HostKey).UploadFailureCount);
        await harness.RunAsync(() => harness.Supervisor.RequestUnmountAsync(HostKey));
        Assert.Equal(0, harness.Snapshot(HostKey).UploadFailureCount);
    }

    [Fact]
    public async Task Upload_failures_on_one_host_are_not_reported_for_another()
    {
        var archive = HostFixtures.OnDemand(HostKey, drive: Drive, idleUnmountSeconds: IdleSeconds);
        var other = HostFixtures.OnDemand("other", drive: "R:", idleUnmountSeconds: 0);
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), archive, other));
        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync(HostKey));
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("other"));
        harness.Rclone.SetVfsStats(IdleVfs.Stats(erroredFiles: 4));

        await StepAsync(harness, 1);

        Assert.Equal(4, harness.Snapshot(HostKey).UploadFailureCount);
        Assert.Equal(0, harness.Snapshot("other").UploadFailureCount); // idle 0: never polled
    }
}
