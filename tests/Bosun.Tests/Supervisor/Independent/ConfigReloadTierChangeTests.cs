using Bosun.Configuration;
using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor.Independent;

/// <summary>
/// bs-7ck's tier row -- <c>mount.mode</c>. Four transitions, four different answers, and the DESIGN
/// spells each of them out:
/// <c>persistent → on-demand</c> keeps any live mount and stops auto-remounting ("do NOT drain; the
/// user did not ask for the drive to go away");
/// <c>on-demand → persistent</c> mounts it if it is not mounted and is reachable;
/// <c>anything → none</c> drains and stops supervising entirely;
/// <c>none → anything</c> begins supervising from <c>Disabled</c>.
/// </summary>
/// <remarks>
/// <para>
/// A single boolean "the mode changed, so re-arm" satisfies none of these correctly, and a single
/// "the mode changed, so drain" satisfies only one. This file exists because <c>mount.mode</c> is
/// the one field whose old <i>and</i> new values both have to be consulted.
/// </para>
/// <para>
/// Every fixture below changes <c>mount.mode</c> and nothing else -- the drive letter, remote path,
/// hostname and probe settings are byte-identical across the reload -- so a failure here is about
/// the tier and cannot be a mount-affecting field leaking in.
/// </para>
/// <para>
/// Fakes only: an in-memory rclone double, a probe double, a fake clock, an in-memory config store.
/// </para>
/// </remarks>
public sealed class ConfigReloadTierChangeTests
{
    /// <summary>
    /// <b>Protects:</b> "persistent → on-demand: keep any live mount ... do NOT drain".
    /// <b>Catches:</b> the obvious reading, where demoting a tier is treated as revoking the
    /// mount's authorisation. The user's intent in making this edit is "stop mounting this
    /// automatically", not "unmount it now" -- and he is quite likely to be working on the drive
    /// while he decides that. An unmount here is an unmount nobody asked for, which is the one
    /// thing this tool promises never to do without a reason.
    /// </summary>
    [Fact]
    public async Task Demoting_a_mounted_host_to_on_demand_keeps_the_drive_it_already_has()
    {
        var host = HostFixtures.Persistent("prod", probeIntervalSeconds: 0, drive: "P:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), host));

        await harness.StartAsync();
        Assert.Equal(MountState.Mounted, harness.State("prod"));

        await harness.ReloadAsync(HostFixtures.Build(
            HostFixtures.Global(),
            host with { Mount = host.Mount with { Mode = MountMode.OnDemand } }));

        Assert.Equal(0, harness.Rclone.UnmountCount("P:"));
        Assert.Equal(MountState.Mounted, harness.State("prod"));
        Assert.NotNull(harness.Rclone.FsAt("P:"));
    }

    /// <summary>
    /// <b>Protects:</b> the second half of the same row -- "stop auto-remounting" -- which is the
    /// half that actually changes behaviour and the half a "do nothing" implementation of the first
    /// half silently omits.
    /// <b>Catches:</b> a reload that keeps the mount (correct) but leaves the host's tier at
    /// <c>persistent</c> in the supervisor's own record. Nothing is visible until the mount next
    /// goes away for an unrelated reason -- here, the deep probe finding the SSH channel dead
    /// (ADR-016) -- at which point the host climbs straight back to <c>Mounted</c> by itself. The
    /// user set it to on-demand precisely so that would stop happening, and the drive letter he
    /// asked to stop appearing keeps reappearing.
    /// </summary>
    [Fact]
    public async Task A_host_demoted_to_on_demand_does_not_remount_itself_after_the_mount_dies()
    {
        var host = HostFixtures.Persistent("prod", probeIntervalSeconds: 0, drive: "P:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), host));

        await harness.StartAsync();
        Assert.Equal(MountState.Mounted, harness.State("prod"));

        await harness.ReloadAsync(HostFixtures.Build(
            HostFixtures.Global(),
            host with { Mount = host.Mount with { Mode = MountMode.OnDemand } }));

        // Two consecutive deep-probe failures drain it (§4 rule 3a); the host itself stays
        // reachable at TCP level throughout, so the drain lands back in Ready.
        harness.Probe.SetDeep("prod", DeepProbeOutcome.Failed, times: 2);
        await harness.AdvanceSecondsAsync(300);
        await harness.AdvanceSecondsAsync(300);
        Assert.Null(harness.Rclone.FsAt("P:"));

        await harness.AdvanceSecondsAsync(300);

        Assert.Equal(MountState.Ready, harness.State("prod"));
        Assert.Equal(1, harness.Rclone.MountCount("P:")); // the original mount, never repeated
    }

    /// <summary>
    /// <b>Protects:</b> "on-demand → persistent: if not mounted and reachable, mount it."
    /// <b>Catches:</b> a promotion that is recorded but not acted on until something else happens
    /// to wake the host. For a host resting in <c>Ready</c> with <c>interval_seconds = 0</c> --
    /// ADR-008's on-demand default, so the overwhelmingly likely starting point -- nothing else
    /// ever happens: no timer is armed, so the drive appears at the next app restart and not
    /// before. The user promotes a host to persistent and the one thing persistent means does not
    /// occur.
    /// </summary>
    [Fact]
    public async Task Promoting_a_reachable_on_demand_host_to_persistent_mounts_it()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), host));

        await harness.StartAsync();
        Assert.Equal(MountState.Ready, harness.State("archive"));
        Assert.Equal(0, harness.Rclone.TotalMountCalls);

        await harness.ReloadAsync(HostFixtures.Build(
            HostFixtures.Global(),
            host with { Mount = host.Mount with { Mode = MountMode.Persistent } }));

        Assert.Equal(MountState.Mounted, harness.State("archive"));
        Assert.NotNull(harness.Rclone.FsAt("Q:"));
    }

    /// <summary>
    /// <b>Protects:</b> Invariant I1 against the promotion path -- "if not mounted <b>and
    /// reachable</b>".
    /// <b>Catches:</b> a promotion that mounts unconditionally because persistent hosts are
    /// supposed to be mounted. An on-demand host is very often <c>Unreachable</c> (ADR-014 leaves
    /// it dark on purpose, so its state is stale by design), which makes this the tier transition
    /// most likely to be pointed at a host that is not there. Mounting it hands the OS a drive
    /// letter backed by nothing and hangs Explorer process-wide. The second half of the test is
    /// what stops the first half being satisfiable by doing nothing at all: once the host answers,
    /// the ladder must bring the drive up without the user acting, because that ladder is the only
    /// thing that ever will for a host promoted while it was dark.
    /// </summary>
    [Fact]
    public async Task Promoting_an_unreachable_on_demand_host_probes_before_it_mounts()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), host));
        harness.Probe.DefaultShallow = ShallowProbeOutcome.ConnectionRefused;

        await harness.StartAsync();
        Assert.Equal(MountState.Unreachable, harness.State("archive"));

        await harness.ReloadAsync(HostFixtures.Build(
            HostFixtures.Global(),
            host with { Mount = host.Mount with { Mode = MountMode.Persistent } }));

        Assert.Equal(0, harness.Rclone.MountCount("Q:"));
        Assert.Null(harness.Rclone.FsAt("Q:"));
        Assert.NotEqual(MountState.Mounted, harness.State("archive"));

        // Now the host comes back. A persistent host polls its ladder, so the drive arrives with
        // no user action -- that is what the promotion bought.
        harness.Probe.DefaultShallow = ShallowProbeOutcome.Success;
        await harness.AdvanceSecondsAsync(300);

        Assert.Equal(MountState.Mounted, harness.State("archive"));
        Assert.NotNull(harness.Rclone.FsAt("Q:"));
    }

    /// <summary>
    /// <b>Protects:</b> "anything → none: DRAIN and stop supervising entirely", and with it
    /// Invariant I2 -- a host set to <c>none</c> is a host nothing describes any more, so a drive
    /// letter left behind is a drive letter nothing will ever clean up.
    /// <b>Catches:</b> a reload that flips <c>AdministrativelyEnabled</c> to false and returns,
    /// which is the natural one-line implementation. The mount survives with no state machine
    /// watching it: it is never probed, so it never accumulates failures, so it never drains, and
    /// when the host behind it dies the drive letter wedges Explorer with nothing in Bosun even
    /// aware it exists. The "no probe traffic afterwards" half is ADR-008's promise from the other
    /// direction -- <c>mode = "none"</c> hosts "are not enabled, and are never probed".
    /// </summary>
    [Fact]
    public async Task Setting_a_mounted_host_to_mode_none_drains_it_and_then_leaves_it_alone()
    {
        var host = HostFixtures.Persistent("prod", probeIntervalSeconds: 60, drive: "P:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), host));

        await harness.StartAsync();
        Assert.Equal(MountState.Mounted, harness.State("prod"));

        await harness.ReloadAsync(HostFixtures.Build(
            HostFixtures.Global(),
            host with { Mount = host.Mount with { Mode = MountMode.None } }));

        Assert.Null(harness.Rclone.FsAt("P:"));
        Assert.True(harness.Rclone.UnmountCount("P:") >= 1);
        Assert.False(harness.Snapshot("prod").AdministrativelyEnabled);

        var probes = harness.ShallowProbesFor("prod");
        for (var i = 0; i < 10; i++)
        {
            await harness.AdvanceSecondsAsync(300);
        }

        Assert.Equal(probes, harness.ShallowProbesFor("prod"));
        Assert.Null(harness.Rclone.FsAt("P:"));
    }

    /// <summary>
    /// <b>Protects:</b> "none → anything: begin supervising from <c>Disabled</c>" -- the mirror of
    /// the row above.
    /// <b>Catches:</b> a diff that only looks at hosts it already considers enabled, so a host that
    /// has been sitting at <c>mode = "none"</c> since startup is skipped by every branch of the
    /// classifier. Enabling it in the window then does nothing at all until the next restart, with
    /// the window showing it disabled the whole time -- the same "your change takes effect after
    /// you restart Bosun" this issue exists to delete, except now without the message that at least
    /// said so honestly.
    /// </summary>
    [Fact]
    public async Task Enabling_a_mode_none_host_starts_supervising_it_there_and_then()
    {
        var enabled = HostFixtures.Persistent("jump", probeIntervalSeconds: 60, drive: "J:");
        var disabled = enabled with { Mount = enabled.Mount with { Mode = MountMode.None } };
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), disabled));

        await harness.StartAsync();
        Assert.Equal(MountState.Disabled, harness.State("jump"));
        Assert.Equal(0, harness.ShallowProbesFor("jump"));

        await harness.ReloadAsync(HostFixtures.Build(HostFixtures.Global(), enabled));

        Assert.True(
            harness.ShallowProbesFor("jump") >= 1,
            "A host promoted out of mode = \"none\" was never probed, so supervision never began.");
        Assert.True(harness.Snapshot("jump").AdministrativelyEnabled);
        Assert.Equal(MountState.Mounted, harness.State("jump"));
        Assert.NotNull(harness.Rclone.FsAt("J:"));
    }
}
