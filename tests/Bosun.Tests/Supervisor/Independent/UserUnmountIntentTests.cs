using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor.Independent;

/// <summary>
/// What an explicit user unmount means, for both tiers.
/// </summary>
/// <remarks>
/// <para>
/// docs/ARCHITECTURE.md §4's diagram lists "user unmount" as one of the four triggers for
/// <c>Mounted -&gt; Draining</c>, and the only edge leaving <c>Draining</c> is
/// <c>Draining -&gt; Disabled</c> on "unmount confirmed". The only edge leaving <c>Disabled</c> is
/// labelled "enable", and the only annotation on <c>Disabled</c> is "user disables / mode =
/// none". Read as a whole, the diagram says a user-requested unmount ends with the host parked
/// and waiting for a user action -- not cycling straight back through <c>Probing</c> to
/// <c>Ready</c> and remounting.
/// </para>
/// <para>
/// Rule 6 ("on-demand hosts rest in <c>Ready</c>, not <c>Mounted</c>. They do not auto-mount") is
/// about the resting state of a tier, not about overriding an explicit command. Nothing in ADR-005
/// reaches this either: that ADR is about drives disappearing on probe failure, and it says
/// nothing about undoing a deliberate instruction.
/// </para>
/// </remarks>
public sealed class UserUnmountIntentTests
{
    /// <summary>
    /// <b>This test currently fails.</b> A persistent host that the user unmounts remounts itself
    /// within the same drain, because the auto-re-enable after a completed drain feeds it back to
    /// <c>Ready</c> and rule 6's persistent-tier auto-mount fires on every arrival there, not only
    /// the first.
    /// </summary>
    /// <remarks>
    /// The user-visible result is that the tray's "Unmount" item does nothing for the persistent
    /// tier -- the drive letter blinks and comes back -- which reads as a broken command rather
    /// than a policy. It also removes the only in-app way to release a drive letter before, say,
    /// running a backup or handing the laptop over, short of editing the config and restarting.
    /// Bug class: a machine that cannot distinguish "this host became Ready" from "this host became
    /// Ready because the user just asked for the opposite".
    /// </remarks>
    [Fact]
    public async Task A_persistent_host_the_user_unmounts_stays_unmounted()
    {
        var host = HostFixtures.Persistent("prod", probeIntervalSeconds: 0, drive: "P:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), host));

        await harness.StartAsync();
        Assert.Equal(MountState.Mounted, harness.State("prod"));
        Assert.Equal(1, harness.Rclone.MountCount("P:"));

        await harness.RunAsync(() => harness.Supervisor.RequestUnmountAsync("prod"));

        Assert.NotEqual(MountState.Mounted, harness.State("prod"));
        Assert.Equal(1, harness.Rclone.MountCount("P:"));

        // ... and it stays down until something asks for it again.
        await harness.AdvanceAsync(TimeSpan.FromMinutes(30));
        Assert.NotEqual(MountState.Mounted, harness.State("prod"));
        Assert.Equal(1, harness.Rclone.MountCount("P:"));
    }

    /// <summary>
    /// The on-demand half of the same requirement, which the implementation already satisfies --
    /// kept so that any future change to the post-drain path has to keep satisfying it. Bug this
    /// catches: making the auto-re-enable tier-blind in the other direction.
    /// </summary>
    [Fact]
    public async Task An_on_demand_host_the_user_unmounts_stays_unmounted_and_rests_in_Ready()
    {
        var host = HostFixtures.OnDemand("archive", probeIntervalSeconds: 0, drive: "Q:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), host));

        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));
        Assert.Equal(1, harness.Rclone.MountCount("Q:"));

        await harness.RunAsync(() => harness.Supervisor.RequestUnmountAsync("archive"));

        Assert.Equal(MountState.Ready, harness.State("archive"));

        await harness.AdvanceAsync(TimeSpan.FromDays(1));

        Assert.Equal(MountState.Ready, harness.State("archive"));
        Assert.Equal(1, harness.Rclone.MountCount("Q:"));
    }

    /// <summary>
    /// docs/OPERATIONS.md T5: an idle-unmounted on-demand host "unmounts cleanly, host returns to
    /// Ready" -- and, critically, does not then re-mount itself and start the idle clock again.
    /// Bug this catches: an idle-unmount that loops.
    /// </summary>
    [Fact]
    public async Task An_idle_unmounted_host_returns_to_Ready_and_does_not_remount_itself()
    {
        var host = HostFixtures.OnDemand("archive", probeIntervalSeconds: 0, idleUnmountSeconds: 120, drive: "Q:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), host));

        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));

        await harness.AdvanceSecondsAsync(120);

        Assert.Equal(MountState.Ready, harness.State("archive"));
        Assert.Equal(1, harness.Rclone.UnmountCount("Q:"));

        await harness.AdvanceAsync(TimeSpan.FromDays(1));

        Assert.Equal(MountState.Ready, harness.State("archive"));
        Assert.Equal(1, harness.Rclone.MountCount("Q:"));
        Assert.Equal(1, harness.Rclone.UnmountCount("Q:"));
    }
}
