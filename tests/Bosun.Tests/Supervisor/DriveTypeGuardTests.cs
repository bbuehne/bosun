using Bosun.Status;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;
using Microsoft.Extensions.Logging;

namespace Bosun.Tests.Supervisor;

/// <summary>
/// bs-dtk: the post-mount Invariant I7 runtime guard. <c>mount/mount</c> succeeding does not prove
/// the drive is presented as a network drive (rclone could keep accepting <c>network_mode</c> and
/// stop acting on it), and a fixed-disk presentation of an SFTP mount produces pathological
/// Explorer behaviour. So after every confirmed mount the supervisor reads the drive type through
/// <see cref="IDriveTypeReader"/>; anything but <see cref="DriveType.Network"/> is a failed mount:
/// it takes the existing mount-failure route (Mounting -&gt; Draining, then unmount) and surfaces
/// through the same <c>LastMountFailureReason</c> / <c>MountFailing</c> path as every other failed
/// mount (ADR-012 Decision 3). All tests use fakes -- no real drive letter is ever read.
/// </summary>
public sealed class DriveTypeGuardTests
{
    private static SupervisorHarness PersistentHarness() =>
        new(HostFixtures.Build(
            HostFixtures.Global(backoffSeconds: [5, 15, 30]),
            HostFixtures.Persistent("prod", drive: "P:")));

    [Fact]
    public async Task A_Network_drive_passes_and_the_host_reaches_Mounted()
    {
        var harness = PersistentHarness();

        await harness.StartAsync();

        var snapshot = harness.Snapshot("prod");
        Assert.Equal(MountState.Mounted, snapshot.State);
        Assert.Equal(0, snapshot.ConsecutiveMountFailures);
        Assert.Null(snapshot.LastMountFailureReason);
        Assert.Empty(harness.Rclone.UnmountCalls);
        Assert.Equal(["P:"], harness.DriveTypes.Reads);
    }

    [Fact]
    public async Task A_Fixed_drive_is_treated_as_a_failed_mount_and_unmounted()
    {
        var harness = PersistentHarness();
        harness.DriveTypes.Reports("P:", DriveType.Fixed);

        await harness.StartAsync();

        // Unmounted through the rc API (the supervisor is the only caller), and never Mounted.
        Assert.Contains("P:", harness.Rclone.UnmountCalls);
        Assert.NotEqual(MountState.Mounted, harness.Snapshot("prod").State);
        Assert.DoesNotContain(
            harness.Supervisor.GetTransitionHistory(),
            t => t.HostKey == "prod" && t.To == MountState.Mounted);
        Assert.Empty(await harness.Rclone.ListMountsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_Fixed_drive_enters_Draining_straight_from_Mounting_with_a_causal_reason()
    {
        var harness = PersistentHarness();
        harness.DriveTypes.Reports("P:", DriveType.Fixed);

        await harness.StartAsync();

        // No new transition: Mounting -> Draining is the existing mount-failure edge.
        var toDraining = Assert.Single(
            harness.Supervisor.GetTransitionHistory(),
            t => t.HostKey == "prod" && t.To == MountState.Draining);
        Assert.Equal(MountState.Mounting, toDraining.From);
        Assert.Contains("Fixed", toDraining.Trigger, StringComparison.Ordinal);
        Assert.Contains("network", toDraining.Trigger, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_Fixed_drive_is_surfaced_through_the_ADR_012_mount_failure_route_not_just_a_log_line()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(backoffSeconds: [5, 15, 30]), host));
        harness.DriveTypes.Reports("P:", DriveType.Fixed);

        await harness.StartAsync();

        var snapshot = harness.Snapshot("prod");
        Assert.Equal(1, snapshot.ConsecutiveMountFailures);
        Assert.NotNull(snapshot.LastMountFailureReason);
        Assert.Contains("Fixed", snapshot.LastMountFailureReason, StringComparison.Ordinal);

        // The same derivation the window and tray use: a persistent host that cannot be mounted
        // is MountFailing, its row text carries the reason, and the aggregate icon is Error.
        var row = StatusDerivation.DeriveRow(snapshot, host, sessionCount: 0);
        Assert.Equal(StatusCategory.MountFailing, row.Category);
        Assert.Contains("Fixed", row.StatusText, StringComparison.Ordinal);
        Assert.Equal(AggregateHealth.Error, StatusDerivation.DeriveAggregateHealth([row]));

        Assert.Contains(harness.Log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("Fixed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_Fixed_drive_on_an_on_demand_host_is_rejected_too()
    {
        var harness = new SupervisorHarness(HostFixtures.Build(
            HostFixtures.Global(),
            HostFixtures.OnDemand("archive", drive: "Q:")));
        harness.DriveTypes.Reports("Q:", DriveType.Fixed);
        await harness.StartAsync();

        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));

        Assert.Contains("Q:", harness.Rclone.UnmountCalls);
        Assert.NotEqual(MountState.Mounted, harness.Snapshot("archive").State);
        Assert.Equal(1, harness.Snapshot("archive").ConsecutiveMountFailures);
    }

    [Theory]
    [InlineData(DriveType.Removable)]
    [InlineData(DriveType.CDRom)]
    [InlineData(DriveType.Ram)]
    [InlineData(DriveType.Unknown)]
    public async Task Any_drive_type_other_than_Network_is_rejected(DriveType reported)
    {
        var harness = PersistentHarness();
        harness.DriveTypes.Reports("P:", reported);

        await harness.StartAsync();

        Assert.NotEqual(MountState.Mounted, harness.Snapshot("prod").State);
        Assert.Contains("P:", harness.Rclone.UnmountCalls);
        Assert.Contains(reported.ToString(), harness.Snapshot("prod").LastMountFailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoRootDirectory_after_a_successful_mount_call_is_a_failed_mount_with_its_own_reason()
    {
        var harness = PersistentHarness();
        harness.DriveTypes.Reports("P:", DriveType.NoRootDirectory);

        await harness.StartAsync();

        var snapshot = harness.Snapshot("prod");
        Assert.NotEqual(MountState.Mounted, snapshot.State);
        Assert.Equal(1, snapshot.ConsecutiveMountFailures);
        Assert.Contains("does not exist", snapshot.LastMountFailureReason, StringComparison.Ordinal);
        Assert.Contains("NoRootDirectory", snapshot.LastMountFailureReason, StringComparison.Ordinal);
        Assert.Contains("P:", harness.Rclone.UnmountCalls);
    }

    [Fact]
    public async Task A_drive_type_read_that_throws_fails_closed_and_the_reason_names_the_exception()
    {
        var harness = PersistentHarness();
        harness.DriveTypes.Throws("P:", new IOException("simulated GetDriveType failure"));

        await harness.StartAsync();

        var snapshot = harness.Snapshot("prod");
        Assert.NotEqual(MountState.Mounted, snapshot.State);
        Assert.Equal(1, snapshot.ConsecutiveMountFailures);
        Assert.Contains("could not verify", snapshot.LastMountFailureReason, StringComparison.Ordinal);
        Assert.Contains("simulated GetDriveType failure", snapshot.LastMountFailureReason, StringComparison.Ordinal);
        Assert.Contains("P:", harness.Rclone.UnmountCalls);

        // The exception must not escape and kill the supervisor loop: the host is still supervised.
        Assert.Contains(harness.Supervisor.GetSnapshot(), h => h.HostKey == "prod");
        Assert.Contains(harness.Log.Entries, e => e.Level == LogLevel.Error && e.Exception is IOException);
    }

    [Fact]
    public async Task A_rejected_mount_is_retried_on_the_mount_retry_ladder_not_immediately()
    {
        var harness = PersistentHarness();
        harness.DriveTypes.Reports("P:", DriveType.Fixed);

        await harness.StartAsync();
        Assert.Single(harness.Rclone.MountCalls);

        await harness.AdvanceAsync(TimeSpan.FromSeconds(4));
        Assert.Single(harness.Rclone.MountCalls);

        await harness.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(2, harness.Rclone.MountCalls.Count);
        Assert.Equal(2, harness.Snapshot("prod").ConsecutiveMountFailures);
    }

    [Fact]
    public async Task Once_the_drive_reads_Network_again_the_retry_mounts_and_clears_the_failure()
    {
        var harness = PersistentHarness();
        harness.DriveTypes.Reports("P:", DriveType.Fixed);
        await harness.StartAsync();
        Assert.Equal(1, harness.Snapshot("prod").ConsecutiveMountFailures);

        harness.DriveTypes.Clear("P:");
        await harness.AdvanceAsync(TimeSpan.FromSeconds(5));

        var snapshot = harness.Snapshot("prod");
        Assert.Equal(MountState.Mounted, snapshot.State);
        Assert.Equal(0, snapshot.ConsecutiveMountFailures);
        Assert.Null(snapshot.LastMountFailureReason);
    }

    [Fact]
    public async Task The_drive_type_is_not_read_when_mount_mount_itself_failed()
    {
        var harness = PersistentHarness();
        harness.Rclone.MakeMountFail("P:", new InvalidOperationException("drive letter already in use"));

        await harness.StartAsync();

        Assert.Empty(harness.DriveTypes.Reads);
        Assert.Contains("mount/mount failed", harness.Snapshot("prod").LastMountFailureReason, StringComparison.Ordinal);
    }
}
