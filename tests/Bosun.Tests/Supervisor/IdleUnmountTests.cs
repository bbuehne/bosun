using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor;

/// <summary>
/// docs/ARCHITECTURE.md §4 rule 7: "On-demand hosts with idle_unmount_seconds &gt; 0 transition
/// Mounted -&gt; Draining after that period without filesystem activity."
/// </summary>
/// <remarks>
/// There is no filesystem-activity signal wired into this codebase yet (no VFS-stats poll, no
/// session-monitor correlation) -- see the delivery report. What IS built and tested here is the
/// timer mechanics: the clock starts at Mounted, elapses after <c>idle_unmount_seconds</c>, and
/// <see cref="IMountSupervisor.RecordActivityAsync"/> is the seam a future activity signal would
/// call to reset it.
/// </remarks>
public sealed class IdleUnmountTests
{
    [Fact]
    public async Task OnDemand_host_drains_after_idle_unmount_seconds_without_activity()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:", idleUnmountSeconds: 120);
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));
        Assert.Equal(MountState.Mounted, harness.Snapshot("archive").State);

        await harness.AdvanceAsync(TimeSpan.FromSeconds(119));
        Assert.Equal(MountState.Mounted, harness.Snapshot("archive").State);

        await harness.AdvanceAsync(TimeSpan.FromSeconds(1));

        // On-demand: the fakes default to success, so within this Advance+Drain pass the host
        // completes Mounted -> Draining -> Disabled -> Probing -> Ready (rule 6: on-demand rests
        // in Ready, it does not auto-mount) -- the unmount call is the proof the idle-timeout
        // drain actually fired.
        Assert.Equal(1, harness.Rclone.UnmountCallCountFor("Q:"));
        Assert.NotEqual(MountState.Mounted, harness.Snapshot("archive").State);
    }

    [Fact]
    public async Task RecordActivityAsync_resets_the_idle_unmount_clock()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:", idleUnmountSeconds: 120);
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));

        await harness.AdvanceAsync(TimeSpan.FromSeconds(100));
        await harness.RunAsync(() => harness.Supervisor.RecordActivityAsync("archive"));

        await harness.AdvanceAsync(TimeSpan.FromSeconds(100)); // 100s since reset, still < 120
        Assert.Equal(MountState.Mounted, harness.Snapshot("archive").State);

        await harness.AdvanceAsync(TimeSpan.FromSeconds(21)); // 121s since reset
        Assert.Equal(1, harness.Rclone.UnmountCallCountFor("Q:"));
        Assert.NotEqual(MountState.Mounted, harness.Snapshot("archive").State);
    }

    [Fact]
    public async Task Zero_idle_unmount_seconds_means_never_auto_unmount()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:", idleUnmountSeconds: 0);
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));

        await harness.AdvanceAsync(TimeSpan.FromDays(1));

        Assert.Equal(MountState.Mounted, harness.Snapshot("archive").State);
    }

    [Fact]
    public async Task Persistent_host_never_idle_unmounts_even_if_idle_unmount_seconds_is_set()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:", idleUnmountSeconds: 10);
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        await harness.StartAsync();
        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);

        await harness.AdvanceAsync(TimeSpan.FromSeconds(1000));

        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);
    }
}
