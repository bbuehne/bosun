using Bosun.Rclone;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor.Independent;

/// <summary>
/// docs/ARCHITECTURE.md §4 "Reconciliation" and §6's crash-recovery row: compare intended state
/// against <c>mount/listmounts</c> every tick and reconcile toward intent, and on startup adopt or
/// clear whatever is already mounted before doing anything else.
/// </summary>
public sealed class ReconciliationDriftTests
{
    /// <summary>
    /// Both drift directions in one tick. Bug this catches: a reconciliation loop that returns
    /// after the first correction (or whose branches are mutually exclusive per tick), so a second
    /// drifted host waits another full interval -- or forever, if the first correction re-triggers
    /// on every tick.
    /// </summary>
    [Fact]
    public async Task Reconciliation_corrects_a_lost_mount_and_an_orphaned_mount_in_the_same_tick()
    {
        var prod = HostFixtures.Persistent("prod", probeIntervalSeconds: 0, drive: "P:");
        var archive = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), prod, archive));

        await harness.StartAsync();
        Assert.Equal(MountState.Mounted, harness.State("prod"));
        Assert.Equal(MountState.Ready, harness.State("archive"));

        harness.Rclone.DropMountSilently("P:");                     // rclone lost one we believe in
        harness.Rclone.SeedMount("Q:", "bosun-archive:/srv/share"); // and gained one we do not

        await harness.AdvanceSecondsAsync(30);

        Assert.Equal(1, harness.Rclone.UnmountCount("P:"));
        Assert.Equal(2, harness.Rclone.MountCount("P:"));
        Assert.Equal(MountState.Mounted, harness.State("prod"));

        Assert.Equal(1, harness.Rclone.UnmountCount("Q:"));
        Assert.Equal(MountState.Ready, harness.State("archive"));
        Assert.Equal(0, harness.Rclone.MountCount("Q:"));
    }

    /// <summary>
    /// The other half of "never believe a drive is gone when it is not", applied to reconciliation
    /// rather than to draining. Bug this catches: treating a failed <c>mount/listmounts</c> as an
    /// empty result. Every mounted host would look like drift and be drained, so a brief rcd
    /// hiccup would rip every drive letter out from under the user -- and then, because the
    /// persistent tier remounts, put them all back. Drives flapping on an rcd blip.
    /// </summary>
    [Fact]
    public async Task Reconciliation_corrects_nothing_while_listmounts_cannot_be_reached()
    {
        var prod = HostFixtures.Persistent("prod", probeIntervalSeconds: 0, drive: "P:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), prod));

        await harness.StartAsync();
        Assert.Equal(MountState.Mounted, harness.State("prod"));

        harness.Rclone.ListMountsAlwaysFails = true;

        for (var tick = 0; tick < 6; tick++)
        {
            await harness.AdvanceSecondsAsync(30);
            Assert.Equal(MountState.Mounted, harness.State("prod"));
            Assert.Equal(0, harness.Rclone.UnmountCount("P:"));
        }

        harness.Rclone.ListMountsAlwaysFails = false;
        await harness.AdvanceSecondsAsync(30);

        // The mount was there all along, so a working listmounts changes nothing.
        Assert.Equal(MountState.Mounted, harness.State("prod"));
        Assert.Equal(0, harness.Rclone.UnmountCount("P:"));
        Assert.Equal(1, harness.Rclone.MountCount("P:"));
    }

    /// <summary>
    /// Bug this catches: reconciliation force-unmounting a host that is legitimately mid-flight.
    /// A host in <c>Draining</c> already owns its drive letter's fate; a tick that also issues
    /// unmounts for it races the drain's own confirm-and-escalate sequence.
    /// </summary>
    [Fact]
    public async Task Reconciliation_leaves_a_draining_host_to_its_own_drain()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), host));

        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));
        harness.Rclone.ConfirmUnmountAtCall("Q:", int.MaxValue);
        await harness.RunAsync(() => harness.Supervisor.RequestUnmountAsync("archive"));

        // Let the drain run its own retry-and-escalate schedule up to t=29. Its next attempt is
        // then due at t=34, so the reconciliation tick at t=30 lands on its own.
        await harness.AdvanceSecondsAsync(29);
        var beforeReconciliationTick = harness.Rclone.UnmountCount("Q:");
        Assert.True(beforeReconciliationTick > 1, "the drain should have retried and escalated on its own schedule");

        await harness.AdvanceSecondsAsync(1); // t=30: reconciliation only

        Assert.Equal(beforeReconciliationTick, harness.Rclone.UnmountCount("Q:"));
        Assert.Equal(MountState.Draining, harness.State("archive"));
    }

    /// <summary>
    /// docs/ARCHITECTURE.md §6 crash recovery: an existing mount is adopted only if it is genuinely
    /// <b>this host's</b> mount. A drive letter reassigned between one run and the next leaves a
    /// mount at the right letter serving the wrong remote.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This test currently fails.</b> Adoption matches on mount point alone, so the supervisor
    /// reports <c>prod</c> as Mounted at <c>P:</c> while <c>P:</c> is still serving the previous
    /// host's filesystem. That is worse than a wedge: nothing hangs, nothing is logged as wrong,
    /// and the user reads and writes files on a server they did not choose. A stale drive letter is
    /// exactly the state a crash leaves behind, and drive-letter reassignment between runs is
    /// ordinary config editing, so the two coincide easily.
    /// </para>
    /// <para>
    /// The assertion is deliberately the weakest form of the requirement: not "adoption must
    /// compare the fs", just "if the supervisor claims this host is mounted at this letter, that
    /// letter must actually serve this host's remote". Clearing and remounting satisfies it; so
    /// does an fs-aware adopt.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_existing_mount_is_only_adopted_if_it_serves_the_claiming_hosts_own_remote()
    {
        var prod = HostFixtures.Persistent("prod", drive: "P:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), prod));

        // P: was this host's letter in a previous run; the config now points it at "prod", but the
        // surviving mount still serves the remote it was created for.
        harness.Rclone.SeedMount("P:", RcloneRemoteNaming.RemoteFsPath("previous-owner", "/somewhere/else"));

        await harness.StartAsync();

        Assert.Equal(MountState.Mounted, harness.State("prod"));
        Assert.Equal(
            RcloneRemoteNaming.RemoteFsPath("prod", "/srv/share"),
            harness.Rclone.FsAt("P:"));
    }
}
