using Bosun.Configuration;
using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Independent.SuspendBudget;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor;

/// <summary>
/// bs-oly (maintainer decision 2026-11-01): while the system is suspended, EVERY
/// <see cref="IMountSupervisor.RequestMountAsync"/> throws <see cref="MountRequestRefusedException"/>
/// ("the system is suspended") whatever the host's state, and a refused request does not clear
/// <c>UserParked</c>. Before the fix a Ready host's request was a silent no-op (refused only by the
/// <c>TryBeginMountAsync</c> gate, with a log line) and every request cleared UserParked first.
/// Fakes and a fake clock only: no rclone, drive letter, or SystemEvents.
/// </summary>
public sealed class SuspendedMountRequestTests
{
    private const string Reason = "the system is suspended";

    private static BosunConfig Config(params HostConfig[] hosts) =>
        HostFixtures.Build(HostFixtures.Global(suspendUnmountTimeoutSeconds: 3), hosts);

    private static string Hostname(string key) => SuspendBudgetHarness.HostnameOf(key);

    private static async Task<MountRequestRefusedException> RefusedAsync(SuspendBudgetHarness h, string key) =>
        await Assert.ThrowsAsync<MountRequestRefusedException>(
            () => h.RunAsync(() => h.Supervisor.RequestMountAsync(key)));

    /// <summary>
    /// <b>Protects:</b> the bs-oly headline. A Ready host's mount request while suspended is a loud
    /// refusal naming the cause, not a silent no-op, and nothing is mounted.
    /// <b>Catches:</b> reverting to the TryBeginMountAsync-only gate (returns quietly), or a refusal
    /// with a different reason (the tray shows "&lt;host&gt;: &lt;reason&gt;").
    /// </summary>
    [Fact]
    public async Task A_Ready_host_mount_request_while_suspended_throws_the_suspended_reason()
    {
        var h = new SuspendBudgetHarness(Config(HostFixtures.OnDemand("archive", drive: "Q:")));
        await h.StartAsync();
        Assert.Equal(MountState.Ready, h.State("archive"));
        await h.SuspendAsync();

        var ex = await RefusedAsync(h, "archive");

        Assert.Equal("archive", ex.HostKey);
        Assert.Equal(Reason, ex.Reason);
        Assert.Equal(MountState.Ready, h.State("archive"));
        Assert.Equal(0, h.Rclone.TotalMountCalls);
    }

    /// <summary>
    /// <b>Protects:</b> the Unreachable half, which already refused: it still does, with the same
    /// reason, and still without a probe (I8 in reverse).
    /// <b>Catches:</b> moving the check below the rule-8 probe, which would issue a probe around a
    /// suspend.
    /// </summary>
    [Fact]
    public async Task An_Unreachable_host_mount_request_while_suspended_throws_the_suspended_reason_without_probing()
    {
        var h = new SuspendBudgetHarness(Config(HostFixtures.OnDemand("archive", drive: "Q:")));
        h.Probe.SetShallow(Hostname("archive"), ShallowProbeOutcome.ConnectionRefused);
        await h.StartAsync();
        Assert.Equal(MountState.Unreachable, h.State("archive"));
        await h.SuspendAsync();
        var probesBefore = h.Probe.ShallowCount(Hostname("archive"));

        var ex = await RefusedAsync(h, "archive");

        Assert.Equal(Reason, ex.Reason);
        Assert.Equal(probesBefore, h.Probe.ShallowCount(Hostname("archive")));
        Assert.Equal(MountState.Unreachable, h.State("archive"));
        Assert.Equal(0, h.Rclone.TotalMountCalls);
    }

    /// <summary>
    /// <b>Protects:</b> "whatever the host's state" for a Disabled host (its mount failed and the
    /// retry timer is pending): refused, and no probe or re-enable happens on the user's behalf.
    /// <b>Catches:</b> a check placed inside only the Ready/Unreachable branches.
    /// </summary>
    [Fact]
    public async Task A_Disabled_host_mount_request_while_suspended_throws_the_suspended_reason()
    {
        var h = new SuspendBudgetHarness(Config(HostFixtures.Persistent("prod", probeIntervalSeconds: 0, drive: "P:")));
        h.Probe.SetDeep("prod", DeepProbeOutcome.Failed, times: 1);
        await h.StartAsync();
        Assert.Equal(MountState.Disabled, h.State("prod"));
        await h.SuspendAsync();
        var probesBefore = h.Probe.ShallowCount(Hostname("prod"));

        var ex = await RefusedAsync(h, "prod");

        Assert.Equal(Reason, ex.Reason);
        Assert.Equal(MountState.Disabled, h.State("prod"));
        Assert.Equal(probesBefore, h.Probe.ShallowCount(Hostname("prod")));
        Assert.Equal(0, h.Rclone.TotalMountCalls);
    }

    /// <summary>
    /// <b>Protects:</b> that a parked persistent host is refused like any other while suspended.
    /// <b>Catches:</b> a check that special-cases hosts the supervisor would not auto-mount anyway.
    /// </summary>
    [Fact]
    public async Task A_parked_host_mount_request_while_suspended_throws_the_suspended_reason()
    {
        var h = await ParkedPersistentAsync();
        await h.SuspendAsync();

        var ex = await RefusedAsync(h, "prod");

        Assert.Equal(Reason, ex.Reason);
        Assert.Equal(MountState.Ready, h.State("prod"));
        Assert.Equal(1, h.Rclone.MountCount("P:")); // the original mount only
    }

    /// <summary>
    /// <b>Protects:</b> the second half of the decision. A refused request must NOT clear
    /// UserParked: the user's earlier explicit unmount still stands, so resume must not remount the
    /// host just because someone clicked Mount while the machine was suspending.
    /// <b>Catches:</b> the old ordering, where <c>host.UserParked = false</c> ran before the
    /// refusal -- the host would then auto-mount at resume against the user's wish.
    /// </summary>
    [Fact]
    public async Task A_refused_request_leaves_UserParked_unchanged_and_resume_does_not_remount()
    {
        var h = await ParkedPersistentAsync();
        Assert.True(h.Snapshot("prod").UserParked);
        await h.SuspendAsync();

        await RefusedAsync(h, "prod");

        Assert.True(h.Snapshot("prod").UserParked, "a refused request must not un-park the host");

        await h.ResumeAsync();
        await h.AdvanceAsync(TimeSpan.FromMinutes(10));

        Assert.Equal(MountState.Ready, h.State("prod"));
        Assert.True(h.Snapshot("prod").UserParked);
        Assert.Equal(1, h.Rclone.MountCount("P:"));
    }

    /// <summary>
    /// <b>Protects:</b> the refusal is a property of being suspended, not a stuck state. After
    /// resume the same request is honoured and, as before, un-parks the host.
    /// <b>Catches:</b> a suspended flag that is not cleared by resume, or a refusal that is
    /// remembered per host.
    /// </summary>
    [Fact]
    public async Task After_resume_the_same_request_is_honoured()
    {
        var h = await ParkedPersistentAsync();
        await h.SuspendAsync();
        await RefusedAsync(h, "prod");
        await h.ResumeAsync();
        Assert.Equal(MountState.Ready, h.State("prod")); // parked: resume does not mount it
        Assert.Equal(1, h.Rclone.MountCount("P:"));

        await h.RunAsync(() => h.Supervisor.RequestMountAsync("prod"));

        Assert.Equal(MountState.Mounted, h.State("prod"));
        Assert.False(h.Snapshot("prod").UserParked);
        Assert.Equal(2, h.Rclone.MountCount("P:"));
    }

    /// <summary>The Ready on-demand host also works again after resume (the unparked case).</summary>
    [Fact]
    public async Task After_resume_a_Ready_on_demand_host_request_mounts_it()
    {
        var h = new SuspendBudgetHarness(Config(HostFixtures.OnDemand("archive", drive: "Q:")));
        await h.StartAsync();
        await h.SuspendAsync();
        await RefusedAsync(h, "archive");
        await h.ResumeAsync();

        await h.RunAsync(() => h.Supervisor.RequestMountAsync("archive"));

        Assert.Equal(MountState.Mounted, h.State("archive"));
        Assert.Equal(1, h.Rclone.MountCount("Q:"));
    }

    private static async Task<SuspendBudgetHarness> ParkedPersistentAsync()
    {
        var h = new SuspendBudgetHarness(Config(HostFixtures.Persistent("prod", probeIntervalSeconds: 30, drive: "P:")));
        await h.StartAsync();
        Assert.Equal(MountState.Mounted, h.State("prod"));
        await h.RunAsync(() => h.Supervisor.RequestUnmountAsync("prod"));
        await h.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(MountState.Ready, h.State("prod"));
        Assert.True(h.Snapshot("prod").UserParked);
        return h;
    }
}
