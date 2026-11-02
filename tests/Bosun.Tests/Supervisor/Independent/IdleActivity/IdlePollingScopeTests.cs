using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor.Independent.IdleActivity;

/// <summary>
/// docs/ARCHITECTURE.md §4 rule 7, last sentence of "What counts as activity": "Only on-demand hosts
/// with <c>idle_unmount_seconds &gt; 0</c> are polled at all", on "the mounted shallow-probe cadence
/// (rule 3; no extra timer per host)". ADR-008: on-demand hosts generate no traffic until mounted.
/// </summary>
/// <remarks>
/// Only exact call counts can express "not polled": a host polled every second whose counters never
/// change looks the same, from its state, as one never polled.
/// </remarks>
public sealed class IdlePollingScopeTests
{
    private const int Interval = 60;

    /// <summary>
    /// Bug: the poll is gated on <c>idle_unmount_seconds &gt; 0</c> alone, not on the tier, so a
    /// persistent host that carries a leftover idle value is sampled (and, worse, idle-unmounted --
    /// rule 7 is on-demand only, and a persistent drive that vanished after five quiet minutes would
    /// be remounted by the tier rule in a loop).
    /// </summary>
    [Fact]
    public async Task A_mounted_persistent_host_with_an_idle_value_is_never_polled_and_never_idle_unmounted()
    {
        var host = HostFixtures.Persistent("vault", probeIntervalSeconds: 0, drive: "P:", idleUnmountSeconds: 120);
        var harness = new IdleActivityHarness(HostFixtures.Build(HostFixtures.Global(mountedProbeIntervalSeconds: Interval), host));

        await harness.StartAsync();
        Assert.Equal(MountState.Mounted, harness.State("vault"));

        await harness.AdvanceSecondsAsync(2 * 3600);

        Assert.Equal(0, harness.Rclone.VfsCallCount);
        Assert.Equal(MountState.Mounted, harness.State("vault"));
        Assert.Empty(harness.DrainsWith("vault", DrainCause.IdleTimeout));
        Assert.Equal(0, harness.Rclone.UnmountCount("P:"));
    }

    /// <summary>
    /// Bug: every mounted on-demand host is sampled, including one configured never to idle-unmount
    /// (<c>idle_unmount_seconds = 0</c>) -- rc traffic for a decision that can never be taken, and a
    /// hung rcd that can stall the channel for nothing.
    /// </summary>
    [Fact]
    public async Task A_mounted_on_demand_host_with_idle_zero_is_never_polled_and_stays_mounted()
    {
        var harness = IdleActivityHarness.OnDemand(idleUnmountSeconds: 0, mountedProbeIntervalSeconds: Interval);

        await harness.StartAsync();
        await harness.MountAsync("archive");
        Assert.Equal(MountState.Mounted, harness.State("archive"));

        await harness.AdvanceSecondsAsync(2 * 3600);

        Assert.Equal(0, harness.Rclone.VfsCallCount);
        Assert.Equal(MountState.Mounted, harness.State("archive"));
    }

    /// <summary>
    /// Bug: the VFS sampler is started for every configured on-demand host with an idle value
    /// rather than for the mounted ones -- polling rcd for a VFS that does not exist, before the
    /// user has mounted anything (ADR-008: no traffic until mounted), and again after it is
    /// unmounted (a sampler that survives the drain).
    /// </summary>
    [Fact]
    public async Task An_on_demand_host_is_not_polled_before_it_is_mounted_or_after_it_is_unmounted()
    {
        var harness = IdleActivityHarness.OnDemand(idleUnmountSeconds: 600, mountedProbeIntervalSeconds: Interval);

        await harness.StartAsync();
        await harness.AdvanceSecondsAsync(3600);
        Assert.Equal(MountState.Ready, harness.State("archive"));
        Assert.Equal(0, harness.Rclone.VfsCallCount);

        await harness.MountAsync("archive");
        await harness.AdvanceSecondsAsync(3 * Interval);
        Assert.True(harness.Rclone.VfsCallCount > 0, "a mounted on-demand host with an idle timeout was never sampled");

        await harness.UnmountAsync("archive");
        Assert.Equal(MountState.Ready, harness.State("archive"));
        var afterUnmount = harness.Rclone.VfsCallCount;

        await harness.AdvanceSecondsAsync(3600);

        Assert.Equal(afterUnmount, harness.Rclone.VfsCallCount);
    }

    /// <summary>
    /// Rule 7: sampled "on the mounted shallow-probe cadence", with "the first sample after a mount
    /// (taken at the mount itself)" as the baseline. Bug classes: a separate fast timer per host
    /// (rcd hammered every second), or a sampler tied to the 300 s deep-probe cadence (activity seen
    /// five times later than documented, and OPERATIONS T5b's 200-265 s window missed).
    /// </summary>
    [Fact]
    public async Task Vfs_stats_is_read_once_at_the_mount_and_once_per_mounted_probe_interval()
    {
        var harness = IdleActivityHarness.OnDemand(idleUnmountSeconds: 3600, mountedProbeIntervalSeconds: Interval);

        await harness.StartAsync();
        await harness.MountAsync("archive");
        var atMount = harness.Rclone.VfsCallCountFor("vfs/stats");
        Assert.True(atMount >= 1, "no baseline vfs/stats sample was taken at the mount (rule 7)");

        await harness.AdvanceSecondsAsync(5 * Interval);

        Assert.Equal(MountState.Mounted, harness.State("archive"));
        Assert.Equal(atMount + 5, harness.Rclone.VfsCallCountFor("vfs/stats"));
        Assert.Equal(1, atMount);
    }

    /// <summary>
    /// Rule 7 "Which VFS": the supervisor asks rcd for the exact <c>fs</c> it passed to
    /// <c>mount/mount</c>. Bug: stats requested for a re-derived or differently formatted fs (or
    /// for the drive letter), which on a real rcd matches no VFS and silently disables the rule.
    /// </summary>
    [Fact]
    public async Task Vfs_stats_is_asked_for_the_exact_fs_string_that_was_passed_to_mount_mount()
    {
        var harness = IdleActivityHarness.OnDemand(idleUnmountSeconds: 3600, mountedProbeIntervalSeconds: Interval);

        await harness.StartAsync();
        await harness.MountAsync("archive");
        await harness.AdvanceSecondsAsync(2 * Interval);

        var fs = harness.Rclone.LastFsFor("Q:");
        var statsCalls = harness.Rclone.VfsCalls.Where(c => c.StartsWith("vfs/stats|", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(statsCalls);
        Assert.All(statsCalls, c => Assert.Equal($"vfs/stats|{fs}", c));
    }
}
