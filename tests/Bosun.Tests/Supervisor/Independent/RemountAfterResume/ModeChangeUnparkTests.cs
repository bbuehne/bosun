using Bosun.Configuration;
using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;
using Microsoft.Extensions.Logging;
using static Bosun.Tests.Supervisor.Independent.RemountAfterResume.RemountSpec;

namespace Bosun.Tests.Supervisor.Independent.RemountAfterResume;

/// <summary>
/// bs-uzy (maintainer decision B, 2026-11-07), ADR-015's amendment: changing a host's mode from
/// on-demand to persistent is an explicit "use this drive again", so it clears a user park and the
/// host then behaves as an unparked persistent host. Every other edit, and a change to on-demand,
/// leaves a park alone. The pre-fix bug was a host left <c>Mounted</c> while parked, which the
/// ADR-022 remount record then brought back after a suspend.
/// </summary>
/// <remarks>
/// Fakes only (see <see cref="RemountHarness"/>): no real rclone, drive letter, fragment path or
/// <c>wt.exe</c>. Lives beside the ADR-022 tests because it reuses their harness (reload, suspend,
/// resume, call log).
/// </remarks>
public sealed class ModeChangeUnparkTests
{
    private const string Key = "od";

    private static HostConfig OnDemandHost(int probeIntervalSeconds = 0) =>
        HostFixtures.OnDemand(Key, probeIntervalSeconds: probeIntervalSeconds, drive: OnDemandDrive);

    private static HostConfig WithMode(HostConfig host, MountMode mode) =>
        host with { Mount = host.Mount with { Mode = mode } };

    private static BosunConfig Config(HostConfig host) => HostFixtures.Build(HostFixtures.Global(), host);

    /// <summary>A user-mounted then user-unmounted on-demand host: Ready and parked.</summary>
    private static async Task<RemountHarness> ParkedOnDemandAsync(int probeIntervalSeconds = 0)
    {
        var h = new RemountHarness(Config(OnDemandHost(probeIntervalSeconds)));
        await h.StartAsync();
        await h.MountAsync(Key);
        Assert.Equal(MountState.Mounted, h.State(Key));
        await h.UnmountAsync(Key);
        Assert.Equal(MountState.Ready, h.State(OnDemandKey));
        Assert.True(h.Snapshot(Key).UserParked);
        Assert.Null(h.Rclone.FsAt(OnDemandDrive));
        return h;
    }

    private static void AssertNotMountedWhileParked(RemountHarness h, string step)
    {
        var s = h.Snapshot(Key);
        Assert.False(
            s.State == MountState.Mounted && s.UserParked,
            $"inconsistent state after '{step}': Mounted while UserParked.{Environment.NewLine}{h.TransitionDump()}");
    }

    /// <summary>
    /// <b>Protects:</b> decision B, Ready branch: a parked on-demand host that is Ready, edited to
    /// persistent, loses the park and mounts once, through the deep probe (I1).
    /// <b>Catches:</b> a park left set (the host would mount via the unguarded tier-change branch and
    /// stay "Mounted while parked"); no mount at all; a mount without a passing deep probe.
    /// </summary>
    [Fact]
    public async Task A_parked_Ready_on_demand_host_edited_to_persistent_is_unparked_and_mounts_once_via_the_probe()
    {
        var h = await ParkedOnDemandAsync();
        var mountsBefore = h.Rclone.MountCount(OnDemandDrive);
        var mark = h.Log.Entries.Count;

        await h.ReloadAsync(Config(WithMode(OnDemandHost(), MountMode.Persistent)));

        var s = h.Snapshot(Key);
        Assert.Equal(MountState.Mounted, s.State);
        Assert.False(s.UserParked);
        Assert.Equal(mountsBefore + 1, h.Rclone.MountCount(OnDemandDrive));
        AssertEveryMountPrecededByPassingDeepProbe(h, Key, OnDemandDrive, mark - 1);
        Assert.Equal(
            1,
            h.Logger.Entries.Count(e => e.Level == LogLevel.Information && e.Message.Contains("un-parked")));
    }

    /// <summary>
    /// <b>Protects:</b> decision B, Unreachable branch: the cleared park lets the host mount when it
    /// reaches Ready. <b>Catches:</b> a park left set (the host would reach Ready and stay parked
    /// forever), or a mount that skips the probe that put it in Ready.
    /// </summary>
    [Fact]
    public async Task A_parked_Unreachable_on_demand_host_edited_to_persistent_mounts_when_it_becomes_reachable()
    {
        // A probe interval so the parked Ready host is probed while idle and can go dark.
        var h = await ParkedOnDemandAsync(probeIntervalSeconds: 60);
        h.Probe.SetShallow(HostnameOf(), ShallowProbeOutcome.ConnectionRefused);
        await h.AdvanceSecondsAsync(61);
        Assert.Equal(MountState.Unreachable, h.State(Key));
        Assert.True(h.Snapshot(Key).UserParked);
        var mountsBefore = h.Rclone.MountCount(OnDemandDrive);

        await h.ReloadAsync(Config(WithMode(OnDemandHost(60), MountMode.Persistent)));

        // Still dark: nothing mounts around the probe (I1), and the park is already cleared.
        Assert.Equal(MountState.Unreachable, h.State(Key));
        Assert.False(h.Snapshot(Key).UserParked);
        Assert.Equal(mountsBefore, h.Rclone.MountCount(OnDemandDrive));

        var mark = h.Log.Entries.Count;
        h.Probe.SetShallow(HostnameOf(), ShallowProbeOutcome.Success);
        await h.AdvanceSecondsAsync(600);

        Assert.Equal(MountState.Mounted, h.State(Key));
        Assert.False(h.Snapshot(Key).UserParked);
        Assert.Equal(mountsBefore + 1, h.Rclone.MountCount(OnDemandDrive));
        AssertEveryMountPrecededByPassingDeepProbe(h, Key, OnDemandDrive, mark - 1);
    }

    /// <summary>
    /// <b>Protects:</b> the park is cleared even when the same edit also changes a mount-affecting
    /// field (the reload then takes the "mount-affecting" path, not the tier-change branch).
    /// <b>Catches:</b> a fix placed only in the tier-change branch.
    /// </summary>
    [Fact]
    public async Task A_mode_change_to_persistent_together_with_a_mount_affecting_edit_still_unparks()
    {
        var h = await ParkedOnDemandAsync();
        var edited = WithMode(OnDemandHost(), MountMode.Persistent);
        edited = edited with { Mount = edited.Mount with { Drive = "R:" } };

        await h.ReloadAsync(Config(edited));

        Assert.False(h.Snapshot(Key).UserParked);
        Assert.Equal(MountState.Mounted, h.State(Key));
        Assert.NotNull(h.Rclone.FsAt("R:"));
    }

    /// <summary>
    /// <b>Protects:</b> the amendment this builds on: an unrelated edit does not un-park, for a parked
    /// persistent host, including across a suspend and resume. <b>Catches:</b> a fix that clears the
    /// park on every reload, or one that lets resume mount a parked persistent host.
    /// </summary>
    [Fact]
    public async Task A_parked_persistent_host_stays_parked_across_an_unrelated_edit_and_a_suspend_resume()
    {
        var persistent = WithMode(OnDemandHost(), MountMode.Persistent);
        var h = new RemountHarness(Config(persistent));
        await h.StartAsync();
        Assert.Equal(MountState.Mounted, h.State(Key));
        await h.UnmountAsync(Key);
        Assert.True(h.Snapshot(Key).UserParked);
        var mountsBefore = h.Rclone.MountCount(OnDemandDrive);

        await h.ReloadAsync(Config(persistent with { Session = persistent.Session with { TabColor = "#ff8800" } }));
        Assert.True(h.Snapshot(Key).UserParked, "a tab-colour edit un-parked the host");

        await h.ReloadAsync(Config(persistent with { Mount = persistent.Mount with { IdleUnmountSeconds = 900 } }));
        Assert.True(h.Snapshot(Key).UserParked, "an idle-seconds edit un-parked the host");

        await h.SuspendAsync();
        await h.ResumeAsync();
        await h.AdvanceSecondsAsync(600);

        Assert.True(h.Snapshot(Key).UserParked);
        Assert.NotEqual(MountState.Mounted, h.State(Key));
        Assert.Null(h.Rclone.FsAt(OnDemandDrive));
        Assert.Equal(mountsBefore, h.Rclone.MountCount(OnDemandDrive));
    }

    /// <summary>
    /// <b>Protects:</b> the other direction, stated explicitly: persistent to on-demand does NOT clear
    /// a park (an on-demand host never auto-mounts). <b>Catches:</b> a fix that clears the park on any
    /// mode change.
    /// </summary>
    [Fact]
    public async Task A_parked_persistent_host_stays_parked_when_changed_to_on_demand()
    {
        var persistent = WithMode(OnDemandHost(), MountMode.Persistent);
        var h = new RemountHarness(Config(persistent));
        await h.StartAsync();
        await h.UnmountAsync(Key);
        Assert.True(h.Snapshot(Key).UserParked);

        await h.ReloadAsync(Config(OnDemandHost()));

        Assert.True(h.Snapshot(Key).UserParked);
        Assert.NotEqual(MountState.Mounted, h.State(Key));
        Assert.Null(h.Rclone.FsAt(OnDemandDrive));
    }

    /// <summary>
    /// <b>Protects:</b> an unrelated edit made to a parked ON-DEMAND host does not un-park it, so the
    /// park only clears for the persistent promotion. <b>Catches:</b> a fix that keys on "parked" alone.
    /// </summary>
    [Fact]
    public async Task A_parked_on_demand_host_stays_parked_across_an_unrelated_edit()
    {
        var h = await ParkedOnDemandAsync();
        var host = OnDemandHost();

        await h.ReloadAsync(Config(host with { Session = host.Session with { TabColor = "#112233" } }));

        Assert.True(h.Snapshot(Key).UserParked);
        Assert.Equal(MountState.Ready, h.State(Key));
    }

    /// <summary>
    /// <b>Protects:</b> the full bs-uzy sequence. Park, to persistent, back to on-demand, suspend,
    /// resume: ends Mounted and unparked, with no "Mounted while parked" at any step.
    /// <b>Catches:</b> the original bug, where the park survived the promotion, the mount happened
    /// anyway, and the ADR-022 record then remounted a drive the user had taken down.
    /// </summary>
    [Fact]
    public async Task The_bs_uzy_sequence_ends_Mounted_and_unparked_with_no_inconsistent_step()
    {
        var h = await ParkedOnDemandAsync();
        AssertNotMountedWhileParked(h, "park");

        await h.ReloadAsync(Config(WithMode(OnDemandHost(), MountMode.Persistent)));
        AssertNotMountedWhileParked(h, "to persistent");
        Assert.Equal(MountState.Mounted, h.State(Key));
        Assert.False(h.Snapshot(Key).UserParked);

        await h.ReloadAsync(Config(OnDemandHost()));
        AssertNotMountedWhileParked(h, "back to on-demand");
        Assert.Equal(MountState.Mounted, h.State(Key));
        Assert.False(h.Snapshot(Key).UserParked);

        await h.SuspendAsync();
        AssertNotMountedWhileParked(h, "suspend");
        Assert.Null(h.Rclone.FsAt(OnDemandDrive));

        var resumeMark = h.LastMarkIndex("MARK|suspend");
        await h.ResumeAsync();
        AssertNotMountedWhileParked(h, "resume");

        Assert.Equal(MountState.Mounted, h.State(Key));
        Assert.False(h.Snapshot(Key).UserParked);
        AssertEveryMountPrecededByPassingDeepProbe(h, Key, OnDemandDrive, resumeMark);
    }

    private static string HostnameOf() => RemountHarness.HostnameOf(Key);
}
