using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor.Independent;

/// <summary>
/// bs-7ck's scheduling-affecting row: <c>probe.interval_seconds</c>, <c>probe.deep_probe</c>,
/// <c>mount.idle_unmount_seconds</c> -- "re-arm timers, never drain".
/// </summary>
/// <remarks>
/// <para>
/// This row has two independent halves and they fail independently. "Never drain" fails toward the
/// user losing a drive because he shortened a probe interval. "Re-arm" fails toward a save that
/// appears to do nothing: the user watches a host he set to probe every ten seconds keep probing
/// every ten minutes, concludes the setting does not work, and goes back to editing the file and
/// restarting -- which is the workflow this issue exists to remove.
/// </para>
/// <para>
/// Fakes only: an in-memory rclone double, a probe double, a fake clock, an in-memory config store.
/// </para>
/// </remarks>
public sealed class ConfigReloadSchedulingTests
{
    /// <summary>
    /// <b>Protects:</b> "re-arm timers" for <c>probe.interval_seconds</c> on an idle host.
    /// <b>Catches:</b> a classifier that treats scheduling fields as cosmetic -- a perfectly
    /// defensible-looking simplification, since neither field can invalidate a live mount -- so the
    /// new interval is stored and never armed. The host keeps polling on the old cadence until the
    /// next restart, and nothing anywhere says so.
    /// </summary>
    [Fact]
    public async Task Shortening_the_probe_interval_takes_effect_without_a_restart()
    {
        var host = HostFixtures.OnDemand("archive", probeIntervalSeconds: 60, drive: "Q:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), host));

        await harness.StartAsync();
        Assert.Equal(MountState.Ready, harness.State("archive"));

        await harness.ReloadAsync(HostFixtures.Build(
            HostFixtures.Global(),
            host with { Probe = host.Probe with { IntervalSeconds = 30 } }));

        var probes = harness.ShallowProbesFor("archive");

        await harness.AdvanceSecondsAsync(29);
        Assert.Equal(probes, harness.ShallowProbesFor("archive"));

        await harness.AdvanceSecondsAsync(1);
        Assert.Equal(
            probes + 1,
            harness.ShallowProbesFor("archive"));
    }

    /// <summary>
    /// <b>Protects:</b> "never drain" for <c>probe.interval_seconds</c>, on the host where draining
    /// costs something -- a mounted one.
    /// <b>Catches:</b> a diff that lumps everything under <c>[hosts.x.probe]</c> and
    /// <c>[hosts.x.mount]</c> in with the mount-affecting fields because they share a TOML table
    /// with <c>drive</c> and <c>remote_path</c>. Symptom: tightening a probe interval -- something
    /// the user does precisely <i>because</i> he is worried about a mount going stale -- unmounts
    /// the drive he was watching.
    /// </summary>
    [Fact]
    public async Task Changing_the_probe_interval_of_a_mounted_host_never_drains_it()
    {
        var host = HostFixtures.Persistent("prod", probeIntervalSeconds: 30, drive: "P:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), host));

        await harness.StartAsync();
        Assert.Equal(MountState.Mounted, harness.State("prod"));

        await harness.ReloadAsync(HostFixtures.Build(
            HostFixtures.Global(),
            host with { Probe = host.Probe with { IntervalSeconds = 10 } }));

        Assert.Equal(0, harness.Rclone.UnmountCount("P:"));
        Assert.Equal(MountState.Mounted, harness.State("prod"));
        Assert.NotNull(harness.Rclone.FsAt("P:"));
    }

    /// <summary>
    /// <b>Protects:</b> "re-arm timers" for a <c>Mounted</c> host, where the effective cadence is
    /// <c>min(interval_seconds, global.mounted_probe_interval_seconds)</c> (docs/ARCHITECTURE.md §4
    /// rule 3 / ADR-011) rather than the configured value itself.
    /// <b>Catches:</b> a re-arm that only covers the idle timer. The idle timer is the one
    /// <c>probe.interval_seconds</c> obviously names, so it is the one an implementation covers;
    /// the mounted cadence is derived and lives elsewhere in the code. The consequence is the one
    /// ADR-011 is about: the interval that governs how fast a dead mount is noticed is the interval
    /// that silently ignores the user's edit.
    /// </summary>
    [Fact]
    public async Task A_shortened_probe_interval_re_arms_the_mounted_cadence_too()
    {
        var host = HostFixtures.Persistent("prod", probeIntervalSeconds: 30, drive: "P:");
        var global = HostFixtures.Global(mountedProbeIntervalSeconds: 60);
        var harness = new IndependentHarness(HostFixtures.Build(global, host));

        await harness.StartAsync();
        Assert.Equal(MountState.Mounted, harness.State("prod"));

        await harness.ReloadAsync(HostFixtures.Build(
            global, host with { Probe = host.Probe with { IntervalSeconds = 10 } }));

        var probes = harness.ShallowProbesFor("prod");

        await harness.AdvanceSecondsAsync(9);
        Assert.Equal(probes, harness.ShallowProbesFor("prod"));

        await harness.AdvanceSecondsAsync(1);
        Assert.Equal(probes + 1, harness.ShallowProbesFor("prod"));
    }

    /// <summary>
    /// <b>Protects:</b> "re-arm timers, never drain" for <c>mount.idle_unmount_seconds</c> --
    /// docs/ARCHITECTURE.md §4 rule 7.
    /// <b>Catches:</b> two bugs at once, and they are opposites. Not re-arming means the user
    /// shortens the idle timeout and the drive keeps sitting there on the old one. Draining on the
    /// edit means the drive goes away <i>at the moment of the save</i> rather than after the idle
    /// period -- an unmount the user did not ask for, delivered by an edit that was about when to
    /// unmount.
    /// </summary>
    /// <remarks>
    /// No clock advance between the mount and the reload, so "re-armed from now" and "re-armed from
    /// the last activity" give the same answer -- the test does not depend on which reading of
    /// "re-arm" the implementation takes.
    /// </remarks>
    [Fact]
    public async Task Shortening_the_idle_unmount_timeout_re_arms_it_rather_than_firing_it()
    {
        var host = HostFixtures.OnDemand("archive", idleUnmountSeconds: 600, drive: "Q:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), host));

        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));
        Assert.Equal(MountState.Mounted, harness.State("archive"));

        await harness.ReloadAsync(HostFixtures.Build(
            HostFixtures.Global(),
            host with { Mount = host.Mount with { IdleUnmountSeconds = 60 } }));

        Assert.NotNull(harness.Rclone.FsAt("Q:")); // the edit itself is not an idle timeout

        await harness.AdvanceSecondsAsync(59);
        Assert.NotNull(harness.Rclone.FsAt("Q:"));

        await harness.AdvanceSecondsAsync(1);
        Assert.Null(harness.Rclone.FsAt("Q:"));
    }

    /// <summary>
    /// <b>Protects:</b> the "never drain" half of the scheduling row for <c>probe.deep_probe</c>.
    /// <b>Catches:</b> a classifier that files <c>probe.deep_probe</c> with the mount-affecting
    /// fields on the reasoning that the deep probe is what authorises a mount (§4 rule 2). It is
    /// not: turning the setting off changes what happens on the next tick, not whether the mount
    /// that already exists is valid. Symptom: toggling a checkbox unmounts a drive.
    /// </summary>
    /// <remarks>
    /// Only the "never drain" half is asserted. <c>ProbeConfig.DeepProbe</c> is not read anywhere
    /// in <c>MountSupervisor</c> today -- the periodic deep probe runs off
    /// <c>global.mounted_deep_probe_interval_seconds</c> regardless -- so there is no observable
    /// re-arm to assert against. That gap is reported rather than papered over with a test that
    /// asserts today's behaviour and would have to be deleted when the setting is honoured.
    /// </remarks>
    [Fact]
    public async Task Turning_the_deep_probe_off_never_drains_a_mounted_host()
    {
        var host = HostFixtures.Persistent("prod", probeIntervalSeconds: 0, drive: "P:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), host));

        await harness.StartAsync();
        Assert.Equal(MountState.Mounted, harness.State("prod"));

        await harness.ReloadAsync(HostFixtures.Build(
            HostFixtures.Global(),
            host with { Probe = host.Probe with { DeepProbe = false } }));

        Assert.Equal(0, harness.Rclone.UnmountCount("P:"));
        Assert.Equal(MountState.Mounted, harness.State("prod"));
    }
}
