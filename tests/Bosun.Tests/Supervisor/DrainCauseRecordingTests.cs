using Bosun.Configuration;
using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;
using Bosun.UI.Tray;

namespace Bosun.Tests.Supervisor;

/// <summary>
/// bs-ynb: every drain the supervisor begins records WHY on the <c>-&gt; Draining</c> transition
/// (<see cref="MountTransitionEntry.DrainCause"/>), so UI consumers classify by value instead of
/// by matching <see cref="MountTransitionEntry.Trigger"/> text. One test per drain path: each
/// drives the real <see cref="MountSupervisor"/> through that path (fakes only) and asserts the
/// cause on the resulting history entry, so a call site that passes the wrong cause fails here.
/// </summary>
public sealed class DrainCauseRecordingTests
{
    private static SupervisorHarness Harness(params HostConfig[] hosts) =>
        new(HostFixtures.Build(HostFixtures.Global(), hosts));

    /// <summary>The <c>-&gt; Draining</c> entries for <paramref name="hostKey"/>, oldest first.</summary>
    private static List<MountTransitionEntry> DrainStarts(SupervisorHarness harness, string hostKey) =>
        harness.Supervisor.GetTransitionHistory()
            .Where(e => e.HostKey == hostKey && e.To == MountState.Draining)
            .Reverse()
            .ToList();

    [Fact]
    public async Task A_user_unmount_records_UserUnmount()
    {
        var harness = Harness(HostFixtures.Persistent("prod", drive: "P:"));
        await harness.StartAsync();

        await harness.RunAsync(() => harness.Supervisor.RequestUnmountAsync("prod"));

        var entry = Assert.Single(DrainStarts(harness, "prod"));
        Assert.Equal(MountState.Mounted, entry.From);
        Assert.Equal(DrainCause.UserUnmount, entry.DrainCause);
    }

    [Fact]
    public async Task A_system_suspend_records_Suspend()
    {
        var harness = Harness(HostFixtures.Persistent("prod", drive: "P:"));
        await harness.StartAsync();

        await harness.RunAsync(() => harness.Supervisor.SuspendAsync());

        var entry = Assert.Single(DrainStarts(harness, "prod"));
        Assert.Equal(MountState.Mounted, entry.From);
        Assert.Equal(DrainCause.Suspend, entry.DrainCause);
    }

    [Fact]
    public async Task A_mounted_probe_failure_threshold_records_Automatic()
    {
        var harness = new SupervisorHarness(HostFixtures.Build(
            HostFixtures.Global(failuresBeforeUnmount: 1, mountedProbeIntervalSeconds: 30),
            HostFixtures.Persistent("prod", probeIntervalSeconds: 30, drive: "P:")));
        await harness.StartAsync();
        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);
        harness.Probe.SetDefaultShallow(ShallowProbeOutcome.OtherFailure);

        await harness.AdvanceAsync(TimeSpan.FromSeconds(60));

        var entry = Assert.Single(DrainStarts(harness, "prod"));
        Assert.Equal(MountState.Mounted, entry.From);
        Assert.Equal(DrainCause.Automatic, entry.DrainCause);
    }

    [Fact]
    public async Task A_mounted_deep_probe_failure_records_Automatic()
    {
        var host = HostFixtures.OnDemand("nas", probeIntervalSeconds: 30, drive: "Q:");
        var global = HostFixtures.Global(mountedProbeIntervalSeconds: 30, mountedDeepProbeIntervalSeconds: 100);
        var harness = new SupervisorHarness(HostFixtures.Build(global, host));
        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("nas"));
        harness.Probe.SetDefaultDeep(DeepProbeOutcome.Failed);

        await harness.AdvanceAsync(TimeSpan.FromSeconds(100));
        await harness.AdvanceAsync(TimeSpan.FromSeconds(100)); // second deep failure = the threshold

        var entry = Assert.Single(DrainStarts(harness, "nas"));
        Assert.Equal(MountState.Mounted, entry.From);
        Assert.Equal(DrainCause.Automatic, entry.DrainCause);
    }

    [Fact]
    public async Task Reconciliation_drift_records_Automatic()
    {
        var harness = Harness(HostFixtures.Persistent("prod", drive: "P:"));
        await harness.StartAsync();
        harness.Rclone.SimulateMountLost("P:");

        await harness.AdvanceAsync(TimeSpan.FromSeconds(30)); // ReconciliationInterval

        var entry = Assert.Single(DrainStarts(harness, "prod"));
        Assert.Equal(DrainCause.Automatic, entry.DrainCause);
    }

    [Fact]
    public async Task An_idle_timeout_records_IdleTimeout()
    {
        var harness = Harness(HostFixtures.OnDemand("archive", drive: "Q:", idleUnmountSeconds: 120));
        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));

        await harness.AdvanceAsync(TimeSpan.FromSeconds(120));

        var entry = Assert.Single(DrainStarts(harness, "archive"));
        Assert.Equal(DrainCause.IdleTimeout, entry.DrainCause);
    }

    [Fact]
    public async Task A_mount_affecting_config_change_records_ConfigChanged()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var harness = Harness(host);
        await harness.StartAsync();
        var changed = host with { Mount = host.Mount with { Drive = "Q:" } };

        await harness.RunAsync(
            () => harness.Supervisor.ConfigChangedAsync(HostFixtures.Build(HostFixtures.Global(), changed)));

        var entry = Assert.Single(DrainStarts(harness, "prod"));
        Assert.Equal(DrainCause.ConfigChanged, entry.DrainCause);
    }

    [Fact]
    public async Task A_change_to_mode_none_records_ConfigChanged()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var harness = Harness(host);
        await harness.StartAsync();
        var changed = host with { Mount = new MountConfig { Mode = MountMode.None } };

        await harness.RunAsync(
            () => harness.Supervisor.ConfigChangedAsync(HostFixtures.Build(HostFixtures.Global(), changed)));

        var entry = Assert.Single(DrainStarts(harness, "prod"));
        Assert.Equal(DrainCause.ConfigChanged, entry.DrainCause);
    }

    [Fact]
    public async Task A_host_removed_from_config_while_mounted_records_ConfigRemoved()
    {
        var harness = Harness(HostFixtures.Persistent("prod", drive: "P:"));
        await harness.StartAsync();

        await harness.RunAsync(
            () => harness.Supervisor.ConfigChangedAsync(HostFixtures.Build(HostFixtures.Global())));

        var entry = Assert.Single(DrainStarts(harness, "prod"));
        Assert.Equal(MountState.Mounted, entry.From);
        Assert.Equal(DrainCause.ConfigRemoved, entry.DrainCause);
    }

    [Fact]
    public async Task A_repair_records_Repair()
    {
        var harness = Harness(HostFixtures.Persistent("prod", drive: "P:"));
        await harness.StartAsync();

        await harness.RunAsync(() => harness.Supervisor.RepairAllAsync());

        var entry = Assert.Single(DrainStarts(harness, "prod"));
        Assert.Equal(MountState.Mounted, entry.From);
        Assert.Equal(DrainCause.Repair, entry.DrainCause);
    }

    [Fact]
    public async Task A_deep_probe_failure_entering_Mounting_records_MountFailure()
    {
        var harness = Harness(HostFixtures.OnDemand("archive", drive: "Q:"));
        await harness.StartAsync();
        harness.Probe.EnqueueDeep("archive", DeepProbeOutcome.Failed);

        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));

        var entry = Assert.Single(DrainStarts(harness, "archive"));
        Assert.Equal(MountState.Mounting, entry.From);
        Assert.Equal(DrainCause.MountFailure, entry.DrainCause);
    }

    [Fact]
    public async Task A_failed_mount_call_records_MountFailure()
    {
        var harness = Harness(HostFixtures.OnDemand("archive", drive: "Q:"));
        await harness.StartAsync();
        harness.Rclone.MakeMountFail("Q:", new InvalidOperationException("drive letter already in use"));

        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));

        var entry = Assert.Single(DrainStarts(harness, "archive"));
        Assert.Equal(MountState.Mounting, entry.From);
        Assert.Equal(DrainCause.MountFailure, entry.DrainCause);
    }

    /// <summary>
    /// The recorded decision (see <see cref="MountTransitionEntry.DrainCause"/>'s remarks): the
    /// field is "why did this drain begin", so it sits on the entry into Draining and nowhere else.
    /// A host removed from config while already draining has its internal cause upgraded to
    /// ConfigRemoved, but the Draining entry already written keeps the cause the drain began with,
    /// and the completing Draining -&gt; Disabled entry carries none.
    /// </summary>
    [Fact]
    public async Task Only_the_entry_into_Draining_carries_a_cause_and_it_is_the_cause_the_drain_began_with()
    {
        var harness = Harness(HostFixtures.Persistent("prod", drive: "P:"));
        await harness.StartAsync();
        harness.Rclone.ConfirmUnmountAfterCall("P:", callNumber: 3); // the first attempts do not confirm
        await harness.RunAsync(() => harness.Supervisor.RequestUnmountAsync("prod"));
        Assert.Equal(MountState.Draining, harness.Snapshot("prod").State);

        // Removing the host while it drains upgrades the supervisor's internal cause to ConfigRemoved.
        await harness.RunAsync(
            () => harness.Supervisor.ConfigChangedAsync(HostFixtures.Build(HostFixtures.Global())));
        await harness.AdvanceAsync(TimeSpan.FromMinutes(5));
        Assert.Empty(harness.Supervisor.GetSnapshot()); // the drain completed and the host was evicted

        var history = harness.Supervisor.GetTransitionHistory().Where(e => e.HostKey == "prod").ToList();
        var start = Assert.Single(history, e => e.To == MountState.Draining);
        Assert.Equal(DrainCause.UserUnmount, start.DrainCause);

        var completion = Assert.Single(history, e => e.From == MountState.Draining && e.To == MountState.Disabled);
        Assert.Null(completion.DrainCause);

        Assert.All(
            history.Where(e => e.To != MountState.Draining),
            e => Assert.Null(e.DrainCause));
    }

    [Fact]
    public async Task A_transition_that_is_not_a_drain_start_carries_no_cause()
    {
        var harness = Harness(HostFixtures.Persistent("prod", drive: "P:"));
        await harness.StartAsync(); // Disabled -> Probing -> Ready -> Mounting -> Mounted

        var history = harness.Supervisor.GetTransitionHistory();

        Assert.NotEmpty(history);
        Assert.All(history, e => Assert.Null(e.DrainCause));
    }

    /// <summary>
    /// Through the real supervisor history (no hand-built entries): the tray detector treats the
    /// user's own unmount as expected and a probe-failure drain as unexpected, the verdict coming
    /// from the recorded cause.
    /// </summary>
    [Fact]
    public async Task The_detector_classifies_real_supervisor_history_by_cause()
    {
        var user = Harness(HostFixtures.Persistent("prod", drive: "P:"));
        await user.StartAsync();
        await user.RunAsync(() => user.Supervisor.RequestUnmountAsync("prod"));
        Assert.DoesNotContain(user.Supervisor.GetTransitionHistory(), UnexpectedUnmountDetector.IsUnexpectedUnmount);

        var failing = new SupervisorHarness(HostFixtures.Build(
            HostFixtures.Global(failuresBeforeUnmount: 1, mountedProbeIntervalSeconds: 30),
            HostFixtures.Persistent("prod", probeIntervalSeconds: 30, drive: "P:")));
        await failing.StartAsync();
        failing.Probe.SetDefaultShallow(ShallowProbeOutcome.OtherFailure);
        await failing.AdvanceAsync(TimeSpan.FromSeconds(60));
        var unexpected = Assert.Single(
            failing.Supervisor.GetTransitionHistory(), UnexpectedUnmountDetector.IsUnexpectedUnmount);
        Assert.Equal(DrainCause.Automatic, unexpected.DrainCause);
    }

    /// <summary>
    /// bs-p97, through the real supervisor history: the drains the maintainer decided are not
    /// unexpected (idle timeout, a mount-affecting edit, a removed host, the user's repair, a
    /// failed mount attempt) never satisfy the toast detector, while a lost drive (reconciliation
    /// drift) does.
    /// </summary>
    [Fact]
    public async Task The_detector_is_silent_for_idle_timeout_config_edit_removal_repair_and_failed_mount()
    {
        var idle = Harness(HostFixtures.OnDemand("archive", drive: "Q:", idleUnmountSeconds: 120));
        await idle.StartAsync();
        await idle.RunAsync(() => idle.Supervisor.RequestMountAsync("archive"));
        await idle.AdvanceAsync(TimeSpan.FromSeconds(120));
        Assert.Single(DrainStarts(idle, "archive"));
        Assert.DoesNotContain(idle.Supervisor.GetTransitionHistory(), UnexpectedUnmountDetector.IsUnexpectedUnmount);

        var host = HostFixtures.Persistent("prod", drive: "P:");
        var edited = Harness(host);
        await edited.StartAsync();
        await edited.RunAsync(() => edited.Supervisor.ConfigChangedAsync(
            HostFixtures.Build(HostFixtures.Global(), host with { Mount = host.Mount with { Drive = "Q:" } })));
        Assert.Single(DrainStarts(edited, "prod"));
        Assert.DoesNotContain(edited.Supervisor.GetTransitionHistory(), UnexpectedUnmountDetector.IsUnexpectedUnmount);

        var removed = Harness(HostFixtures.Persistent("prod", drive: "P:"));
        await removed.StartAsync();
        await removed.RunAsync(() => removed.Supervisor.ConfigChangedAsync(HostFixtures.Build(HostFixtures.Global())));
        Assert.Single(DrainStarts(removed, "prod"));
        Assert.DoesNotContain(removed.Supervisor.GetTransitionHistory(), UnexpectedUnmountDetector.IsUnexpectedUnmount);

        var repaired = Harness(HostFixtures.Persistent("prod", drive: "P:"));
        await repaired.StartAsync();
        await repaired.RunAsync(() => repaired.Supervisor.RepairAllAsync());
        Assert.Single(DrainStarts(repaired, "prod"));
        Assert.DoesNotContain(repaired.Supervisor.GetTransitionHistory(), UnexpectedUnmountDetector.IsUnexpectedUnmount);

        var failedMount = Harness(HostFixtures.OnDemand("archive", drive: "Q:"));
        await failedMount.StartAsync();
        failedMount.Rclone.MakeMountFail("Q:", new InvalidOperationException("drive letter already in use"));
        await failedMount.RunAsync(() => failedMount.Supervisor.RequestMountAsync("archive"));
        Assert.Single(DrainStarts(failedMount, "archive"));
        Assert.DoesNotContain(failedMount.Supervisor.GetTransitionHistory(), UnexpectedUnmountDetector.IsUnexpectedUnmount);

        var drift = Harness(HostFixtures.Persistent("prod", drive: "P:"));
        await drift.StartAsync();
        drift.Rclone.SimulateMountLost("P:");
        await drift.AdvanceAsync(TimeSpan.FromSeconds(30));
        Assert.Single(drift.Supervisor.GetTransitionHistory(), UnexpectedUnmountDetector.IsUnexpectedUnmount);
    }
}
