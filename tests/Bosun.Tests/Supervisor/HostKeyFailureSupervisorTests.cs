using Bosun.Probe;
using Bosun.Rclone;
using Bosun.Status;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor;

/// <summary>
/// ADR-025 (bs-k9x): a host whose SSH key rclone refuses is handled by the EXISTING mount-failure
/// path -- not Mounted (Invariant I1), counted, drained and retried on the mount-retry ladder
/// (ADR-005) -- and differs only in the reason it reports. No new state, no new route into
/// <c>Mounting</c>. Fakes only: no rclone, no ssh, no known_hosts file.
/// </summary>
public sealed class HostKeyFailureSupervisorTests
{
    private const string UnknownKeyText =
        "rc call to 'operations/list' failed: failed to create file system for \"bosun-prod:\": " +
        "couldn't connect SSH: ssh: handshake failed: knownhosts: key is unknown";

    private const string ChangedKeyText =
        "rc call to 'operations/list' failed: failed to create file system for \"bosun-prod:\": " +
        "couldn't connect SSH: ssh: handshake failed: knownhosts: key mismatch";

    private const string MissingFileText =
        "rc call to 'operations/list' failed: failed to create file system for \"bosun-prod:\": " +
        @"couldn't parse known_hosts_file: open C:\Users\someone\.ssh\known_hosts: The system cannot find the file specified.";

    [Theory]
    [InlineData(UnknownKeyText, HostKeyFailureKind.UnknownKey)]
    [InlineData(ChangedKeyText, HostKeyFailureKind.ChangedKey)]
    [InlineData(MissingFileText, HostKeyFailureKind.KnownHostsMissing)]
    public async Task A_deep_probe_refused_for_its_host_key_ends_not_mounted_with_the_host_key_reason(
        string rcloneText, HostKeyFailureKind expected)
    {
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), HostFixtures.Persistent("prod", drive: "P:")));
        harness.Probe.EnqueueDeep("prod", DeepProbeOutcome.Failed, rcloneText);

        await harness.StartAsync();

        var snapshot = harness.Snapshot("prod");
        Assert.NotEqual(MountState.Mounted, snapshot.State);
        Assert.Equal(expected, snapshot.LastMountFailureHostKey);
        Assert.Equal(HostKeyFailure.Describe(expected), snapshot.LastMountFailureReason);
        Assert.Equal(1, snapshot.ConsecutiveMountFailures);

        // I1: it never reached Mounted, and mount/mount was never sent.
        Assert.DoesNotContain(harness.Supervisor.GetTransitionHistory(), t => t.To == MountState.Mounted);
        Assert.Empty(harness.Rclone.MountCalls);
    }

    [Fact]
    public async Task The_reason_that_reaches_the_snapshot_carries_none_of_rclones_text()
    {
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), HostFixtures.Persistent("prod", drive: "P:")));
        harness.Probe.EnqueueDeep("prod", DeepProbeOutcome.Failed, MissingFileText);

        await harness.StartAsync();

        var reason = harness.Snapshot("prod").LastMountFailureReason!;
        Assert.DoesNotContain(@"\Users\", reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bosun-prod", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("rc call", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(UnknownKeyText, HostKeyFailureKind.UnknownKey)]
    [InlineData(ChangedKeyText, HostKeyFailureKind.ChangedKey)]
    public async Task A_mount_mount_call_refused_for_its_host_key_is_classified_the_same_way(
        string rcloneText, HostKeyFailureKind expected)
    {
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), HostFixtures.Persistent("prod", drive: "P:")));
        harness.Rclone.MakeMountFail("P:", new RcloneRcException("mount/mount", 500, rcloneText));

        await harness.StartAsync();

        var snapshot = harness.Snapshot("prod");
        Assert.NotEqual(MountState.Mounted, snapshot.State);
        Assert.Equal(expected, snapshot.LastMountFailureHostKey);
        Assert.Equal(HostKeyFailure.Describe(expected), snapshot.LastMountFailureReason);
    }

    [Fact]
    public async Task An_ordinary_deep_probe_failure_is_not_given_a_host_key_reason()
    {
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), HostFixtures.Persistent("prod", drive: "P:")));
        harness.Probe.EnqueueDeep("prod", DeepProbeOutcome.Failed, "couldn't connect SSH: dial tcp: lookup knownhosts.example.net: no such host");

        await harness.StartAsync();

        var snapshot = harness.Snapshot("prod");
        Assert.Null(snapshot.LastMountFailureHostKey);
        Assert.Contains("deep probe failed entering Mounting", snapshot.LastMountFailureReason);
    }

    [Fact]
    public async Task A_host_key_failure_is_retried_on_the_ordinary_mount_retry_ladder_and_recovers_once_the_key_is_known()
    {
        // The maintainer connects once through the Terminal profile; the next scheduled attempt's deep
        // probe then passes. Nothing here retries faster than any other failed mount (ADR-005).
        var global = HostFixtures.Global(backoffSeconds: [5, 15, 30]);
        var harness = new SupervisorHarness(HostFixtures.Build(global, HostFixtures.Persistent("prod", drive: "P:")));
        harness.Probe.EnqueueDeep("prod", DeepProbeOutcome.Failed, UnknownKeyText);

        await harness.StartAsync();
        Assert.Equal(HostKeyFailureKind.UnknownKey, harness.Snapshot("prod").LastMountFailureHostKey);
        Assert.Empty(harness.Rclone.MountCalls);

        await harness.AdvanceAsync(TimeSpan.FromSeconds(4));
        Assert.NotEqual(MountState.Mounted, harness.Snapshot("prod").State);
        Assert.Empty(harness.Rclone.MountCalls);

        await harness.AdvanceAsync(TimeSpan.FromSeconds(1));

        var snapshot = harness.Snapshot("prod");
        Assert.Equal(MountState.Mounted, snapshot.State);
        Assert.Null(snapshot.LastMountFailureHostKey);
        Assert.Null(snapshot.LastMountFailureReason);
        Assert.Equal(0, snapshot.ConsecutiveMountFailures);
    }

    [Fact]
    public async Task A_host_key_failure_that_repeats_stays_unmounted_and_keeps_counting()
    {
        var global = HostFixtures.Global(backoffSeconds: [5, 15, 30]);
        var harness = new SupervisorHarness(HostFixtures.Build(global, HostFixtures.Persistent("prod", drive: "P:")));
        harness.Probe.EnqueueDeep("prod", DeepProbeOutcome.Failed, ChangedKeyText);
        harness.Probe.EnqueueDeep("prod", DeepProbeOutcome.Failed, ChangedKeyText);

        await harness.StartAsync();
        await harness.AdvanceAsync(TimeSpan.FromSeconds(5));

        var snapshot = harness.Snapshot("prod");
        Assert.NotEqual(MountState.Mounted, snapshot.State);
        Assert.Equal(2, snapshot.ConsecutiveMountFailures);
        Assert.Equal(HostKeyFailureKind.ChangedKey, snapshot.LastMountFailureHostKey);
        Assert.Empty(harness.Rclone.MountCalls);
    }

    [Fact]
    public async Task The_status_row_for_a_host_key_failure_is_a_mount_failure_with_the_host_key_text_and_never_unreachable()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        harness.Probe.EnqueueDeep("prod", DeepProbeOutcome.Failed, UnknownKeyText);

        await harness.StartAsync();

        var row = StatusDerivation.DeriveRow(harness.Snapshot("prod"), host, sessionCount: 0);

        Assert.Equal(StatusCategory.MountFailing, row.Category);
        Assert.Equal($"P: is not mounted -- {HostKeyFailure.Describe(HostKeyFailureKind.UnknownKey)}", row.StatusText);
        Assert.DoesNotContain("unreachable", row.StatusText, StringComparison.OrdinalIgnoreCase);
    }
}
