using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor;

/// <summary>
/// bs-ww9.4: <see cref="HostMountSnapshot.ConsecutiveMountFailures"/> and
/// <see cref="HostMountSnapshot.LastMountFailureReason"/> -- the previously-invisible signal for a
/// host that answers TCP fine but cannot be mounted (expired key, disabled SFTP subsystem, drive
/// letter already claimed). docs/ARCHITECTURE.md §4 "A failed mount attempt is paced too, on its
/// own ladder" is the existing PACING behaviour this only adds visibility to -- these tests do not
/// re-verify the pacing itself (see <c>BackoffLadderTests</c>), only that the new snapshot fields
/// are populated/reset at exactly the same two failure sites and the one success site that already
/// drive <c>MountRetryBackoff</c>.
/// </summary>
public sealed class MountFailureVisibilityTests
{
    [Fact]
    public async Task A_deep_probe_failure_entering_Mounting_is_captured_on_the_snapshot()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        harness.Probe.EnqueueDeep("prod", DeepProbeOutcome.Failed);

        await harness.StartAsync();

        var snapshot = harness.Snapshot("prod");
        Assert.Equal(1, snapshot.ConsecutiveMountFailures);
        Assert.NotNull(snapshot.LastMountFailureReason);
        Assert.Contains("deep probe failed entering Mounting", snapshot.LastMountFailureReason);
    }

    [Fact]
    public async Task A_mount_mount_call_failure_is_captured_on_the_snapshot()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        harness.Rclone.MakeMountFail("P:", new InvalidOperationException("drive letter already in use"));

        await harness.StartAsync();

        var snapshot = harness.Snapshot("prod");
        Assert.Equal(1, snapshot.ConsecutiveMountFailures);
        Assert.NotNull(snapshot.LastMountFailureReason);
        Assert.Contains("mount/mount failed", snapshot.LastMountFailureReason);
        Assert.Contains("drive letter already in use", snapshot.LastMountFailureReason);
    }

    [Fact]
    public async Task Repeated_mount_failures_accumulate_the_count_rather_than_resetting_between_attempts()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var global = HostFixtures.Global(backoffSeconds: [5, 15, 30]);
        var harness = new SupervisorHarness(HostFixtures.Build(global, host));
        harness.Probe.EnqueueDeep("prod", DeepProbeOutcome.Failed);
        harness.Probe.EnqueueDeep("prod", DeepProbeOutcome.Failed);

        await harness.StartAsync();
        Assert.Equal(1, harness.Snapshot("prod").ConsecutiveMountFailures);

        // First mount-retry rung is 5s (see docs/ARCHITECTURE.md "A failed mount attempt is paced
        // too" -- the failing attempt increments the ladder before the drain begins, so the first
        // retry already sits on rung one).
        await harness.AdvanceAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, harness.Snapshot("prod").ConsecutiveMountFailures);
    }

    [Fact]
    public async Task A_successful_mount_after_prior_failures_resets_both_fields_exactly_like_MountRetryBackoff()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var global = HostFixtures.Global(backoffSeconds: [5, 15, 30]);
        var harness = new SupervisorHarness(HostFixtures.Build(global, host));
        harness.Probe.EnqueueDeep("prod", DeepProbeOutcome.Failed);

        await harness.StartAsync();
        Assert.Equal(1, harness.Snapshot("prod").ConsecutiveMountFailures);
        Assert.NotNull(harness.Snapshot("prod").LastMountFailureReason);

        // The queued deep-probe failure is consumed; the retry after backoff succeeds.
        await harness.AdvanceAsync(TimeSpan.FromSeconds(5));

        var snapshot = harness.Snapshot("prod");
        Assert.Equal(MountState.Mounted, snapshot.State);
        Assert.Equal(0, snapshot.ConsecutiveMountFailures);
        Assert.Null(snapshot.LastMountFailureReason);
    }

    [Fact]
    public async Task A_host_that_mounts_cleanly_on_the_first_attempt_never_sets_either_field()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));

        await harness.StartAsync();

        var snapshot = harness.Snapshot("prod");
        Assert.Equal(MountState.Mounted, snapshot.State);
        Assert.Equal(0, snapshot.ConsecutiveMountFailures);
        Assert.Null(snapshot.LastMountFailureReason);
    }
}
