using Bosun.Probe;
using Bosun.Rclone;
using Bosun.Supervisor;
using Bosun.Tests.Configuration.Fakes;
using Bosun.Tests.Probe.Fakes;
using Bosun.Tests.SessionMonitor.Fakes;
using Bosun.Tests.Supervisor.Fakes;
using Bosun.Tests.Supervisor.Support;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bosun.Tests.Supervisor;

/// <summary>
/// bs-x57. An rc/HTTP timeout surfaces as <see cref="TaskCanceledException"/>, an
/// <see cref="OperationCanceledException"/> that is NOT the supervisor's own shutdown. Every rc
/// catch in <see cref="MountSupervisor"/> used to filter
/// <c>when (ex is not OperationCanceledException)</c>, so that timeout escaped the action and
/// ended the channel loop for good. These tests inject exactly that exception from each
/// <see cref="IRcloneClient"/> call the supervisor makes and assert it is handled like any other
/// rc failure -- logged, the state machine keeps its guarantees, and work for OTHER hosts still
/// runs. They pump via <c>DrainAsync</c>, which goes through the same per-action wrapper as the
/// production loop; <c>SupervisorLoopSurvivalTests</c> covers the real <c>RunAsync</c>.
/// </summary>
public sealed class RcTimeoutHandlingTests
{
    private static TaskCanceledException RcTimeout() => new("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.");

    [Fact]
    public async Task Unmount_timeout_keeps_the_drain_retrying_and_does_not_stop_other_hosts()
    {
        var archive = HostFixtures.OnDemand("archive", drive: "Q:");
        var other = HostFixtures.OnDemand("other", drive: "R:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), archive, other));
        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));
        harness.Rclone.MakeUnmountThrow(RcTimeout());

        // The unmount call times out. Before the fix this exception escaped the drain step.
        await harness.RunAsync(() => harness.Supervisor.RequestUnmountAsync("archive"));

        Assert.Equal(MountState.Draining, harness.Snapshot("archive").State);
        Assert.Equal(1, harness.Rclone.UnmountCallCountFor("Q:"));
        Assert.Contains(harness.Log.Entries, e => e.Level == LogLevel.Warning && e.Exception is TaskCanceledException);

        // The drain's retry timer must still be armed: it keeps retrying on its 5 s cadence.
        await harness.AdvanceAsync(TimeSpan.FromSeconds(5));
        await harness.AdvanceAsync(TimeSpan.FromSeconds(5));
        Assert.True(harness.Rclone.UnmountCallCountFor("Q:") >= 3, "the drain stopped retrying after the timeout");
        Assert.Equal(MountState.Draining, harness.Snapshot("archive").State);

        // Other hosts' work is still processed while this host is stuck draining.
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("other"));
        Assert.Equal(MountState.Mounted, harness.Snapshot("other").State);

        // And once unmount works again, the drain completes (it never gave up).
        harness.Rclone.StopThrowingFromUnmount();
        await harness.AdvanceAsync(TimeSpan.FromSeconds(5));
        Assert.NotEqual(MountState.Draining, harness.Snapshot("archive").State);
    }

    [Fact]
    public async Task Forced_unmount_escalation_timeout_does_not_escape_and_the_drain_keeps_retrying()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));
        harness.Rclone.MakeUnmountThrow(RcTimeout());
        await harness.RunAsync(() => harness.Supervisor.RequestUnmountAsync("archive"));

        // Past the 10 s drain-confirm timeout, so the forced re-attempt (its own catch site) runs.
        await harness.AdvanceAsync(TimeSpan.FromSeconds(5));
        await harness.AdvanceAsync(TimeSpan.FromSeconds(5));
        await harness.AdvanceAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(MountState.Draining, harness.Snapshot("archive").State);
        Assert.Contains(harness.Log.Entries, e => e.Message.Contains("Forced unmount re-attempt failed", StringComparison.Ordinal));
        var callsBefore = harness.Rclone.UnmountCallCountFor("Q:");
        await harness.AdvanceAsync(TimeSpan.FromSeconds(5));
        Assert.True(harness.Rclone.UnmountCallCountFor("Q:") > callsBefore, "the drain stopped retrying after escalating");
    }

    [Fact]
    public async Task Incident_replay_deep_probe_drain_then_unmount_timeout_leaves_supervision_alive()
    {
        // 2026-09-28: a Mounted host hit its deep-probe failure threshold (2) and went Draining;
        // the unmount over the dead SSH channel then timed out and supervision went silent.
        var traininggrounds = HostFixtures.Persistent("traininggrounds", drive: "T:");
        var other = HostFixtures.OnDemand("other", drive: "R:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), traininggrounds, other));
        await harness.StartAsync();
        Assert.Equal(MountState.Mounted, harness.Snapshot("traininggrounds").State);

        harness.Rclone.MakeUnmountThrow(RcTimeout());
        harness.Probe.EnqueueDeep("traininggrounds", DeepProbeOutcome.Failed);
        harness.Probe.EnqueueDeep("traininggrounds", DeepProbeOutcome.Failed);

        await harness.AdvanceAsync(TimeSpan.FromSeconds(300)); // first deep-probe failure
        await harness.AdvanceAsync(TimeSpan.FromSeconds(300)); // second: threshold -> Draining -> unmount times out

        Assert.Equal(MountState.Draining, harness.Snapshot("traininggrounds").State);
        Assert.Contains(harness.Log.Entries, e => e.Message.Contains("mount/unmount call failed", StringComparison.Ordinal));

        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("other"));
        Assert.Equal(MountState.Mounted, harness.Snapshot("other").State);

        harness.Rclone.StopThrowingFromUnmount();
        await harness.AdvanceAsync(TimeSpan.FromSeconds(5));
        Assert.NotEqual(MountState.Draining, harness.Snapshot("traininggrounds").State);
    }

    [Fact]
    public async Task Listmounts_timeout_while_verifying_an_unmount_is_treated_as_still_mounted_and_retried()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));
        harness.Rclone.MakeListMountsThrow(RcTimeout());

        await harness.RunAsync(() => harness.Supervisor.RequestUnmountAsync("archive"));

        // rule 4: when it cannot ask, it must NOT assume the drive is gone.
        Assert.Equal(MountState.Draining, harness.Snapshot("archive").State);
        Assert.Contains(harness.Log.Entries, e => e.Message.Contains("mount/listmounts failed while verifying", StringComparison.Ordinal));

        await harness.AdvanceAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(MountState.Draining, harness.Snapshot("archive").State);

        harness.Rclone.StopThrowingFromListMounts();
        await harness.AdvanceAsync(TimeSpan.FromSeconds(5));
        Assert.NotEqual(MountState.Draining, harness.Snapshot("archive").State);
    }

    [Fact]
    public async Task Listmounts_timeout_during_reconciliation_skips_the_tick_and_the_next_tick_still_runs()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        await harness.StartAsync();
        harness.Rclone.MakeListMountsThrow(RcTimeout());

        await harness.AdvanceAsync(TimeSpan.FromSeconds(30)); // reconciliation tick, times out

        Assert.Contains(harness.Log.Entries, e => e.Message.Contains("Reconciliation: mount/listmounts failed", StringComparison.Ordinal));

        harness.Rclone.StopThrowingFromListMounts();
        var callsBefore = harness.Rclone.ListMountsCallCount;
        await harness.AdvanceAsync(TimeSpan.FromSeconds(30));
        Assert.True(harness.Rclone.ListMountsCallCount > callsBefore, "reconciliation stopped ticking after a timeout");
    }

    [Fact]
    public async Task Listmounts_timeout_during_startup_crash_recovery_does_not_fail_StartAsync()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        harness.Rclone.MakeListMountsThrow(RcTimeout());

        await harness.StartAsync();

        Assert.Equal(MountState.Ready, harness.Snapshot("archive").State);
        Assert.Contains(harness.Log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("startup crash recovery", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unmount_timeout_while_clearing_an_orphan_at_startup_does_not_fail_StartAsync()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        harness.Rclone.SeedExistingMount("Z:", "someone-elses-remote:");
        harness.Rclone.MakeUnmountThrow(RcTimeout());

        await harness.StartAsync();

        Assert.Equal(MountState.Ready, harness.Snapshot("archive").State);
        Assert.Contains(harness.Log.Entries, e => e.Message.Contains("Failed to clear orphaned mount", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unmount_timeout_while_clearing_a_reconciliation_orphan_is_logged_and_survived()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        await harness.StartAsync();
        harness.Rclone.SeedExistingMount("Q:", "bosun-archive:/srv/share");
        harness.Rclone.MakeUnmountThrow(RcTimeout());

        await harness.AdvanceAsync(TimeSpan.FromSeconds(30)); // reconciliation sees an orphan, unmount times out

        Assert.Contains(harness.Log.Entries, e => e.Message.Contains("failed to clear orphaned mount", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(MountState.Ready, harness.Snapshot("archive").State);
    }

    [Fact]
    public async Task Mount_timeout_is_a_mount_failure_that_drains_not_an_escaped_exception()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var other = HostFixtures.OnDemand("other", drive: "R:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host, other));
        await harness.StartAsync();
        harness.Rclone.MakeMountFail("Q:", RcTimeout());

        // Before the fix the TaskCanceledException escaped TryBeginMountAsync with the host
        // stranded in Mounting.
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));

        var snapshot = harness.Snapshot("archive");
        Assert.NotEqual(MountState.Mounting, snapshot.State);
        Assert.NotEqual(MountState.Mounted, snapshot.State);
        Assert.Equal(1, snapshot.ConsecutiveMountFailures);
        Assert.Contains("mount/mount failed", snapshot.LastMountFailureReason, StringComparison.Ordinal);
        Assert.Contains(harness.Log.Entries, e => e.Level == LogLevel.Error && e.Exception is TaskCanceledException);

        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("other"));
        Assert.Equal(MountState.Mounted, harness.Snapshot("other").State);
    }

    [Fact]
    public async Task Rc_deep_probe_timeout_through_the_real_probe_is_a_failed_probe_that_drains_before_mounting()
    {
        // Real HostProbe + RemoteRootLister over a fake rclone client whose operations/list times
        // out: the deep-probe path end to end, with only the transport faked.
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var config = HostFixtures.Build(HostFixtures.Global(), host);
        var store = new FakeHostConfigStore(config);
        var time = new FakeTimeProvider();
        var rclone = new FakeRcloneMountClient();
        var tcp = new FakeTcpProbeTransport();
        tcp.SucceedsImmediately();
        var probe = new HostProbe(tcp, new RemoteRootLister(rclone, store), time, NullLogger<HostProbe>.Instance);
        var supervisor = new MountSupervisor(store, rclone, probe, time, NullLogger<MountSupervisor>.Instance);

        var start = supervisor.StartAsync();
        await supervisor.DrainAsync();
        await start;
        rclone.MakeListThrow(RcTimeout());

        var request = supervisor.RequestMountAsync("archive");
        await supervisor.DrainAsync();
        await request;

        // I1: a failed deep probe never reaches Mounted, and nothing was mounted.
        Assert.Empty(rclone.MountCalls);
        Assert.NotEqual(MountState.Mounted, supervisor.GetSnapshot().Single().State);
        Assert.NotEqual(MountState.Mounting, supervisor.GetSnapshot().Single().State);
    }

    [Fact]
    public async Task A_probe_that_throws_a_timeout_instead_of_returning_a_result_drains_rather_than_stranding_Mounting()
    {
        // Defence against an IProbe that breaks its never-throws contract: without it the host
        // would be left in Mounting forever (nothing re-arms it).
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        await harness.StartAsync();
        harness.Probe.MakeDeepThrow(RcTimeout());

        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));

        Assert.Empty(harness.Rclone.MountCalls);
        Assert.NotEqual(MountState.Mounting, harness.Snapshot("archive").State);
        Assert.NotEqual(MountState.Mounted, harness.Snapshot("archive").State);
        Assert.Equal(1, harness.Snapshot("archive").ConsecutiveMountFailures);
    }

    [Fact]
    public async Task A_mounted_deep_probe_that_throws_a_timeout_counts_as_a_failure_and_the_probe_timer_stays_armed()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        await harness.StartAsync();
        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);
        harness.Probe.MakeDeepThrow(RcTimeout());

        await harness.AdvanceAsync(TimeSpan.FromSeconds(300));
        Assert.Equal(1, harness.Snapshot("prod").ConsecutiveDeepProbeFailures);
        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);

        // Re-armed: the second failure arrives on the next interval and trips ADR-016's threshold.
        await harness.AdvanceAsync(TimeSpan.FromSeconds(300));
        Assert.NotEqual(MountState.Mounted, harness.Snapshot("prod").State);
    }

    [Fact]
    public async Task Core_version_timeout_during_re_derivation_skips_it_without_throwing()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        await harness.StartAsync();
        harness.Rclone.MakeGetVersionThrowOnce(RcTimeout());

        await harness.RunAsync(() => harness.Supervisor.NetworkChangedAsync());

        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);
        Assert.Contains(harness.Log.Entries, e => e.Message.Contains("did not answer core/version", StringComparison.Ordinal));
    }
}
