using Bosun.Probe;
using Bosun.Supervisor;

namespace Bosun.Tests.Supervisor.Independent.IdleActivity;

/// <summary>
/// Invariant I2 (unmount on failure; do not retry through failure) against rule 7's fail-closed
/// skip. The idle skip exists to protect pending writes from a VOLUNTARY unmount; it must never
/// stand in the way of the INVOLUNTARY one. A host whose probes fail is unmounted on the I2
/// schedule (rule 3: <c>failures_before_unmount</c> shallow failures; rule 3a: two deep failures)
/// whatever its upload queue says -- the writes are lost either way, and a wedged drive letter
/// hangs Explorer for every process on the machine.
/// </summary>
public sealed class IdleSkipNeverBlocksFailureUnmountTests
{
    private const int Interval = 60;

    public static TheoryData<string> Blockers => new()
    {
        "uploads queued",
        "uploads in progress",
        "errored files",
        "vfs/stats failing",
        "no matching VFS name",
    };

    /// <summary>
    /// Idle period 120 s, so the idle fires (+120, +240) land on the same ticks as the failing
    /// probes (+120, +180, +240): the third failure and a skipped fire coincide at +240. The drain
    /// must begin at the third failure, with the automatic (failure) cause. Bug classes: the skip
    /// re-arming in a way that resets or suppresses the failure counter; the failure drain itself
    /// routed through the idle safety check; the probe-failure path waiting on the upload queue.
    /// </summary>
    [Theory]
    [MemberData(nameof(Blockers))]
    public async Task A_host_whose_shallow_probes_fail_is_unmounted_on_the_I2_schedule_even_while_the_idle_unmount_is_being_skipped(string blocker)
    {
        var harness = IdleActivityHarness.OnDemand(idleUnmountSeconds: 120, mountedProbeIntervalSeconds: Interval);
        ApplyBlocker(harness, blocker);
        await harness.StartAsync();
        await harness.MountAsync("archive");

        await harness.AdvanceToAsync(100);
        harness.Probe.SetShallow(IdleActivityHarness.HostnameOf("archive"), ShallowProbeOutcome.Timeout);

        await harness.AdvanceToAsync(245);

        var drains = harness.Transitions("archive").Where(t => t.To == MountState.Draining).ToList();
        Assert.True(
            drains.Count == 1,
            $"{blocker}: expected one failure drain by +245s (three failed probes at +120/+180/+240).{Environment.NewLine}{harness.Dump("archive")}");
        Assert.Equal(DrainCause.Automatic, drains[0].DrainCause);
        Assert.False(harness.Rclone.IsMounted("Q:"), $"{blocker}: the drive letter is still mounted after the failure drain");
        Assert.True(harness.Rclone.UnmountCount("Q:") >= 1);
        Assert.Empty(harness.DrainsWith("archive", DrainCause.IdleTimeout));
    }

    /// <summary>
    /// The same for the deep probe (rule 3a): two consecutive deep-probe failures drain a mount whose
    /// SSH channel is dead even though TCP still answers -- the T7 case -- while uploads are queued.
    /// Bug: a VFS that reports queued uploads (rclone is retrying them against a dead channel)
    /// treated as "in use" by the failure path.
    /// </summary>
    [Fact]
    public async Task A_host_whose_deep_probes_fail_is_unmounted_even_while_uploads_are_queued()
    {
        var harness = IdleActivityHarness.OnDemand(idleUnmountSeconds: 120, mountedProbeIntervalSeconds: Interval, mountedDeepProbeIntervalSeconds: 300);
        ApplyBlocker(harness, "uploads queued");
        await harness.StartAsync();
        await harness.MountAsync("archive");

        harness.Probe.SetDeep("archive", DeepProbeOutcome.Failed);
        await harness.AdvanceToAsync(605);

        var drains = harness.Transitions("archive").Where(t => t.To == MountState.Draining).ToList();
        Assert.True(drains.Count == 1, $"expected one deep-probe failure drain by +605s.{Environment.NewLine}{harness.Dump("archive")}");
        Assert.Equal(DrainCause.Automatic, drains[0].DrainCause);
        Assert.False(harness.Rclone.IsMounted("Q:"));
    }

    private static void ApplyBlocker(IdleActivityHarness harness, string blocker)
    {
        switch (blocker)
        {
            case "uploads queued":
                harness.Rclone.SetStats("Q:", Vfs.Stats(uploadsQueued: 4));
                break;
            case "uploads in progress":
                harness.Rclone.SetStats("Q:", Vfs.Stats(uploadsInProgress: 1));
                break;
            case "errored files":
                harness.Rclone.SetStats("Q:", Vfs.Stats(erroredFiles: 2));
                break;
            case "vfs/stats failing":
                harness.Rclone.FailVfsStats = true;
                break;
            case "no matching VFS name":
                harness.Rclone.VfsListOverride = _ => [];
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(blocker), blocker, null);
        }
    }
}
