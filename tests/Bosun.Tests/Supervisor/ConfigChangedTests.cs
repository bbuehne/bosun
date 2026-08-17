using Bosun.Configuration;
using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor;

/// <summary>
/// <see cref="IMountSupervisor.ConfigChangedAsync"/> (bs-7ck): the per-host field classification
/// the issue's DESIGN field specifies. See that interface method's remarks for the four categories
/// these tests exercise -- mount-affecting, tier change, scheduling-affecting, and cosmetic -- plus
/// host add/remove.
/// </summary>
public sealed class ConfigChangedTests
{
    /// <summary>The first of the two things the brief calls out explicitly: a save that changes
    /// only a cosmetic field must not touch a live mount. If everything drained on any change,
    /// editing a host in the GUI would be an unmount/remount cycle -- worse than the restart it
    /// replaces.</summary>
    [Fact]
    public async Task Cosmetic_only_change_does_not_disturb_a_mounted_host()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        await harness.StartAsync();
        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);
        var mountCallsBefore = harness.Rclone.MountCalls.Count;
        var unmountCallsBefore = harness.Rclone.UnmountCalls.Count;

        var changed = host with
        {
            DisplayName = "Production (renamed)",
            Session = host.Session with { TabColor = "#ff00ff", ColorScheme = "One Half Dark" },
        };
        var newConfig = HostFixtures.Build(HostFixtures.Global(), changed);

        await harness.RunAsync(() => harness.Supervisor.ConfigChangedAsync(newConfig));

        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);
        Assert.Equal(mountCallsBefore, harness.Rclone.MountCalls.Count);
        Assert.Equal(unmountCallsBefore, harness.Rclone.UnmountCalls.Count);
    }

    /// <summary>The second of the two things the brief calls out explicitly: editing host A must
    /// never forgive host B's failing probes. A config save must never look like a successful probe
    /// for a host whose own definition did not change.</summary>
    [Fact]
    public async Task Editing_one_host_does_not_reset_another_hosts_failure_counters_or_state()
    {
        var stable = HostFixtures.Persistent("stable", drive: "P:");
        var struggling = HostFixtures.Persistent("struggling", drive: "Q:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), stable, struggling));
        harness.Probe.EnqueueShallow(struggling.Hostname, ShallowProbeOutcome.ConnectionRefused);
        await harness.StartAsync();

        Assert.Equal(MountState.Mounted, harness.Snapshot("stable").State);
        Assert.Equal(MountState.Unreachable, harness.Snapshot("struggling").State);
        Assert.Equal(1, harness.Snapshot("struggling").ConsecutiveIdleFailures);
        var strugglingProbesBefore = harness.Probe.ShallowProbeCalls.Count(h => h == struggling.Hostname);

        var changedStable = stable with { Session = stable.Session with { TabColor = "#123456" } };
        var newConfig = HostFixtures.Build(HostFixtures.Global(), changedStable, struggling);

        await harness.RunAsync(() => harness.Supervisor.ConfigChangedAsync(newConfig));

        Assert.Equal(MountState.Unreachable, harness.Snapshot("struggling").State);
        Assert.Equal(1, harness.Snapshot("struggling").ConsecutiveIdleFailures);
        Assert.Equal(strugglingProbesBefore, harness.Probe.ShallowProbeCalls.Count(h => h == struggling.Hostname));
    }

    /// <summary>GLOBAL CHANGES adopt for future scheduling only -- never a retroactive re-arm or
    /// reset for any host.</summary>
    [Fact]
    public async Task Global_only_change_does_not_reprobe_or_reset_any_hosts_backoff()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        harness.Probe.SetDefaultShallow(ShallowProbeOutcome.ConnectionRefused);
        await harness.StartAsync();
        Assert.Equal(MountState.Unreachable, harness.Snapshot("prod").State);
        Assert.Equal(1, harness.Snapshot("prod").ConsecutiveIdleFailures);
        var probesBefore = harness.Probe.ShallowProbeCalls.Count;

        var newConfig = HostFixtures.Build(HostFixtures.Global(failuresBeforeUnmount: 5), host);

        await harness.RunAsync(() => harness.Supervisor.ConfigChangedAsync(newConfig));

        Assert.Equal(MountState.Unreachable, harness.Snapshot("prod").State);
        Assert.Equal(1, harness.Snapshot("prod").ConsecutiveIdleFailures);
        Assert.Equal(probesBefore, harness.Probe.ShallowProbeCalls.Count);
    }

    [Fact]
    public async Task Mount_affecting_drive_change_drains_the_old_mount_point_and_remounts_at_the_new_one()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        await harness.StartAsync();
        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);

        var changed = host with { Mount = host.Mount with { Drive = "Q:" } };
        var newConfig = HostFixtures.Build(HostFixtures.Global(), changed);

        await harness.RunAsync(() => harness.Supervisor.ConfigChangedAsync(newConfig));

        // The OLD mount point is what is actually live -- draining must target it, never the new
        // (not-yet-mounted) one. See HostRuntime.PendingConfig's remarks.
        Assert.Contains("P:", harness.Rclone.UnmountCalls);
        Assert.DoesNotContain("Q:", harness.Rclone.UnmountCalls);

        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);
        Assert.Equal("Q:", harness.Snapshot("prod").Drive);
        Assert.Contains(harness.Rclone.MountCalls, m => m.MountPoint == "Q:");
    }

    [Fact]
    public async Task Mount_affecting_hostname_change_forces_an_immediate_reprobe_of_an_Unreachable_host()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        harness.Probe.SetDefaultShallow(ShallowProbeOutcome.ConnectionRefused);
        await harness.StartAsync();
        Assert.Equal(MountState.Unreachable, harness.Snapshot("prod").State);
        var probesBefore = harness.Probe.ShallowProbeCalls.Count;

        harness.Probe.SetDefaultShallow(ShallowProbeOutcome.Success);
        var changed = host with { Hostname = "corrected.example.internal" };
        var newConfig = HostFixtures.Build(HostFixtures.Global(), changed);

        await harness.RunAsync(() => harness.Supervisor.ConfigChangedAsync(newConfig));

        Assert.Equal(probesBefore + 1, harness.Probe.ShallowProbeCalls.Count);
        Assert.Equal("corrected.example.internal", harness.Probe.ShallowProbeCalls[^1]);
        // Persistent tier auto-mounts once the forced probe lands it back on Ready.
        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);
    }

    [Fact]
    public async Task Tier_change_persistent_to_on_demand_keeps_a_live_mount_without_draining()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        await harness.StartAsync();
        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);
        var unmountCallsBefore = harness.Rclone.UnmountCalls.Count;

        var changed = host with { Mount = host.Mount with { Mode = MountMode.OnDemand } };
        var newConfig = HostFixtures.Build(HostFixtures.Global(), changed);

        await harness.RunAsync(() => harness.Supervisor.ConfigChangedAsync(newConfig));

        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);
        Assert.Equal(unmountCallsBefore, harness.Rclone.UnmountCalls.Count);
    }

    /// <summary>The idle-unmount timer never existed while this host was persistent (rule 7 is
    /// on-demand-only): without arming it at the moment of the tier flip, it would stay mounted
    /// forever despite now being on-demand with a configured idle timeout.</summary>
    [Fact]
    public async Task Tier_change_persistent_to_on_demand_while_mounted_arms_the_idle_unmount_timer()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:", idleUnmountSeconds: 100);
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        await harness.StartAsync();
        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);

        var changed = host with { Mount = host.Mount with { Mode = MountMode.OnDemand } };
        var newConfig = HostFixtures.Build(HostFixtures.Global(), changed);
        await harness.RunAsync(() => harness.Supervisor.ConfigChangedAsync(newConfig));
        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);

        await harness.AdvanceAsync(TimeSpan.FromSeconds(100));

        // Drains on the idle timeout, then -- still administratively enabled -- auto re-enables and
        // re-probes, resting at Ready (the on-demand tier's resting state, rule 6: on-demand hosts
        // rest in Ready, they do not auto-mount). The point of this assertion is simply that it is
        // no longer Mounted -- proving the idle-unmount timer actually fired.
        Assert.Equal(MountState.Ready, harness.Snapshot("prod").State);
    }

    /// <summary>The reverse of the previous test: a persistent host must never auto-unmount on an
    /// idle timeout, so a timer armed while still on-demand must be disarmed by the flip.</summary>
    [Fact]
    public async Task Tier_change_on_demand_to_persistent_while_mounted_disarms_the_idle_unmount_timer()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:", idleUnmountSeconds: 100);
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestMountAsync("archive"));
        Assert.Equal(MountState.Mounted, harness.Snapshot("archive").State);

        var changed = host with { Mount = host.Mount with { Mode = MountMode.Persistent } };
        var newConfig = HostFixtures.Build(HostFixtures.Global(), changed);
        await harness.RunAsync(() => harness.Supervisor.ConfigChangedAsync(newConfig));
        Assert.Equal(MountState.Mounted, harness.Snapshot("archive").State);

        await harness.AdvanceAsync(TimeSpan.FromSeconds(200));

        // Asserting the end state alone is not enough here: a persistent host auto-remounts after
        // an idle-triggered drain (rule 6), which would mask a leftover on-demand timer by simply
        // ending up Mounted again anyway. UnmountCalls staying empty is what actually proves the
        // stale on-demand idle-unmount timer never fired at all.
        Assert.Empty(harness.Rclone.UnmountCalls);
        Assert.Equal(MountState.Mounted, harness.Snapshot("archive").State);
    }

    [Fact]
    public async Task Tier_change_on_demand_to_persistent_mounts_immediately_if_ready_and_reachable()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        await harness.StartAsync();
        Assert.Equal(MountState.Ready, harness.Snapshot("archive").State);

        var changed = host with { Mount = host.Mount with { Mode = MountMode.Persistent } };
        var newConfig = HostFixtures.Build(HostFixtures.Global(), changed);

        await harness.RunAsync(() => harness.Supervisor.ConfigChangedAsync(newConfig));

        Assert.Equal(MountState.Mounted, harness.Snapshot("archive").State);
    }

    [Fact]
    public async Task Tier_change_to_none_drains_a_mounted_host_and_parks_it_administratively_disabled()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        await harness.StartAsync();
        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);

        var changed = host with { Mount = new MountConfig { Mode = MountMode.None } };
        var newConfig = HostFixtures.Build(HostFixtures.Global(), changed);

        await harness.RunAsync(() => harness.Supervisor.ConfigChangedAsync(newConfig));

        Assert.Contains("P:", harness.Rclone.UnmountCalls);
        var snapshot = harness.Snapshot("prod");
        Assert.Equal(MountState.Disabled, snapshot.State);
        Assert.False(snapshot.AdministrativelyEnabled);
        // Still present in the snapshot -- the key still exists in config, unlike a removed host.
        Assert.Single(harness.Supervisor.GetSnapshot());
    }

    [Fact]
    public async Task Tier_change_from_none_begins_supervision_from_Disabled()
    {
        var host = HostFixtures.None("jump");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        await harness.StartAsync();
        Assert.Equal(MountState.Disabled, harness.Snapshot("jump").State);
        Assert.False(harness.Snapshot("jump").AdministrativelyEnabled);

        var changed = HostFixtures.Persistent("jump", drive: "P:");
        var newConfig = HostFixtures.Build(HostFixtures.Global(), changed);

        await harness.RunAsync(() => harness.Supervisor.ConfigChangedAsync(newConfig));

        var snapshot = harness.Snapshot("jump");
        Assert.True(snapshot.AdministrativelyEnabled);
        Assert.Equal(MountState.Mounted, snapshot.State);
    }

    [Fact]
    public async Task Scheduling_change_rearms_the_idle_probe_timer_with_the_new_interval()
    {
        var host = HostFixtures.OnDemand("archive", probeIntervalSeconds: 3600, drive: "Q:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        await harness.StartAsync();
        Assert.Equal(MountState.Ready, harness.Snapshot("archive").State);
        var probesBefore = harness.Probe.ShallowProbeCalls.Count;

        var changed = host with { Probe = host.Probe with { IntervalSeconds = 5 } };
        var newConfig = HostFixtures.Build(HostFixtures.Global(), changed);
        await harness.RunAsync(() => harness.Supervisor.ConfigChangedAsync(newConfig));

        // The stale 3600s timer must be gone -- the new 5s one fires well before it would have.
        await harness.AdvanceAsync(TimeSpan.FromSeconds(5));

        Assert.True(harness.Probe.ShallowProbeCalls.Count > probesBefore);
    }

    [Fact]
    public async Task Host_added_begins_supervision_exactly_like_startup()
    {
        var existing = HostFixtures.Persistent("prod", drive: "P:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), existing));
        await harness.StartAsync();
        Assert.Single(harness.Supervisor.GetSnapshot());

        var added = HostFixtures.OnDemand("archive", drive: "Q:");
        var newConfig = HostFixtures.Build(HostFixtures.Global(), existing, added);

        await harness.RunAsync(() => harness.Supervisor.ConfigChangedAsync(newConfig));

        Assert.Equal(2, harness.Supervisor.GetSnapshot().Count);
        Assert.Equal(MountState.Ready, harness.Snapshot("archive").State);
    }

    [Fact]
    public async Task Host_removed_while_mounted_drains_before_disappearing_from_the_snapshot()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        await harness.StartAsync();
        Assert.Equal(MountState.Mounted, harness.Snapshot("prod").State);

        var newConfig = HostFixtures.Build(HostFixtures.Global());

        await harness.RunAsync(() => harness.Supervisor.ConfigChangedAsync(newConfig));

        Assert.Contains("P:", harness.Rclone.UnmountCalls);
        Assert.Empty(harness.Supervisor.GetSnapshot());
    }

    [Fact]
    public async Task Host_removed_without_a_live_mount_disappears_immediately_without_unmounting()
    {
        var host = HostFixtures.OnDemand("archive", drive: "Q:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        await harness.StartAsync();
        Assert.Equal(MountState.Ready, harness.Snapshot("archive").State);

        var newConfig = HostFixtures.Build(HostFixtures.Global());
        await harness.RunAsync(() => harness.Supervisor.ConfigChangedAsync(newConfig));

        Assert.Empty(harness.Rclone.UnmountCalls);
        Assert.Empty(harness.Supervisor.GetSnapshot());
    }

    [Fact]
    public async Task Config_change_before_start_is_ignored_without_throwing()
    {
        var host = HostFixtures.Persistent("prod", drive: "P:");
        var harness = new SupervisorHarness(HostFixtures.Build(HostFixtures.Global(), host));
        var newConfig = HostFixtures.Build(HostFixtures.Global(), host);

        await harness.RunAsync(() => harness.Supervisor.ConfigChangedAsync(newConfig));

        Assert.Empty(harness.Supervisor.GetSnapshot());
    }
}
