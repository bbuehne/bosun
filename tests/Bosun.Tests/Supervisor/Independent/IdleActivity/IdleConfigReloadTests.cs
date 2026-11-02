using Bosun.Configuration;
using Bosun.Supervisor;

namespace Bosun.Tests.Supervisor.Independent.IdleActivity;

/// <summary>
/// A config save while the on-demand drive is Mounted. bs-7ck's classification (IMountSupervisor
/// <c>ConfigChangedAsync</c> remarks): <c>mount.idle_unmount_seconds</c> is scheduling-affecting
/// ("timers re-arm; nothing drains"); <c>mount.mode</c> on-demand -&gt; persistent keeps the live
/// mount. Rule 7 applies only to on-demand hosts with <c>idle_unmount_seconds &gt; 0</c>, and only
/// those are polled.
/// </summary>
public sealed class IdleConfigReloadTests
{
    private const int Interval = 60;

    /// <summary>
    /// 300 -&gt; 0 ("never") at +100. Bug classes: the old timer is not cancelled and unmounts the
    /// drive at +300 against the new config; the sampler keeps polling rcd for a host that is no
    /// longer subject to rule 7.
    /// </summary>
    [Fact]
    public async Task Setting_idle_to_zero_while_mounted_cancels_the_idle_unmount_and_stops_the_polling()
    {
        var harness = IdleActivityHarness.OnDemand(idleUnmountSeconds: 300, mountedProbeIntervalSeconds: Interval);
        await harness.StartAsync();
        await harness.MountAsync("archive");
        await harness.AdvanceToAsync(100);

        await harness.ReloadAsync(WithIdle(harness, 0));
        Assert.Equal(MountState.Mounted, harness.State("archive"));
        var afterReload = harness.Rclone.VfsCallCount;

        await harness.AdvanceSecondsAsync(2 * 3600);

        Assert.True(
            harness.State("archive") == MountState.Mounted,
            $"a host whose idle timeout was set to 0 was unmounted anyway.{Environment.NewLine}{harness.Dump("archive")}");
        Assert.Equal(afterReload, harness.Rclone.VfsCallCount);
    }

    /// <summary>
    /// 300 -&gt; 60 at +10, with no activity since the mount. The new, shorter period applies: the
    /// drive goes well before the old +300 deadline. Bug: the edit is accepted but the timer armed
    /// at the mount is kept, so the change only takes effect from the next mount.
    /// </summary>
    [Fact]
    public async Task Shortening_the_idle_period_while_mounted_takes_effect_on_this_mount()
    {
        var harness = IdleActivityHarness.OnDemand(idleUnmountSeconds: 300, mountedProbeIntervalSeconds: Interval);
        await harness.StartAsync();
        await harness.MountAsync("archive");
        await harness.AdvanceToAsync(10);

        await harness.ReloadAsync(WithIdle(harness, 60));
        Assert.Equal(MountState.Mounted, harness.State("archive"));

        await harness.AdvanceToAsync(140);

        var at = harness.DrainTime("archive", DrainCause.IdleTimeout);
        Assert.True(at is not null && at <= 140, $"the shortened idle period was not applied to the live mount.{Environment.NewLine}{harness.Dump("archive")}");
    }

    /// <summary>
    /// 300 -&gt; 900 at +200. The drive must not go at the old +300 deadline. Bug: the reload arms a
    /// new timer but leaves the old one running (two timers, the earlier wins). Where exactly the
    /// longer period counts from (the last activity at +0, or the reload) is not pinned by the spec
    /// -- see the report -- so only the bounds both readings agree on are asserted.
    /// </summary>
    [Fact]
    public async Task Lengthening_the_idle_period_while_mounted_cancels_the_old_deadline()
    {
        var harness = IdleActivityHarness.OnDemand(idleUnmountSeconds: 300, mountedProbeIntervalSeconds: Interval);
        await harness.StartAsync();
        await harness.MountAsync("archive");
        await harness.AdvanceToAsync(200);

        await harness.ReloadAsync(WithIdle(harness, 900));

        await harness.AdvanceToAsync(890);
        Assert.True(
            harness.State("archive") == MountState.Mounted,
            $"the drive went before the new, longer idle period could have run out.{Environment.NewLine}{harness.Dump("archive")}");

        await harness.AdvanceToAsync(200 + 900 + Interval + 5);
        Assert.NotNull(harness.DrainTime("archive", DrainCause.IdleTimeout));
    }

    /// <summary>
    /// 0 -&gt; 120 while mounted. The host becomes subject to rule 7 and must be polled and, being
    /// idle, eventually unmounted. The reload itself must not drain it ("nothing drains"). How soon
    /// is not pinned (no baseline was taken at the mount because the host was not polled then;
    /// rule 7 says a fire with no earlier sample takes one and waits one more period), so the bound
    /// is two periods plus an interval. Bug: the sampler and idle timer are started only at mount,
    /// so the new setting does nothing until the user remounts.
    /// </summary>
    [Fact]
    public async Task Turning_the_idle_unmount_on_while_mounted_starts_polling_and_eventually_unmounts()
    {
        var harness = IdleActivityHarness.OnDemand(idleUnmountSeconds: 0, mountedProbeIntervalSeconds: Interval);
        await harness.StartAsync();
        await harness.MountAsync("archive");
        await harness.AdvanceToAsync(1000);
        Assert.Equal(0, harness.Rclone.VfsCallCount);

        await harness.ReloadAsync(WithIdle(harness, 120));
        Assert.True(
            harness.State("archive") == MountState.Mounted,
            $"the config reload itself drained the host.{Environment.NewLine}{harness.Dump("archive")}");

        await harness.AdvanceToAsync(1000 + (2 * 120) + Interval + 5);

        Assert.True(harness.Rclone.VfsCallCount > 0, "the host was never polled after its idle timeout was turned on");
        Assert.NotNull(harness.DrainTime("archive", DrainCause.IdleTimeout, notBefore: 1000));
    }

    /// <summary>
    /// on-demand -&gt; persistent while Mounted: bs-7ck keeps the live mount, and rule 7 no longer
    /// applies (persistent drives are not idle-unmounted). Bug: the idle timer armed under the old
    /// tier fires and unmounts a persistent drive, which the tier rule then remounts -- a needless
    /// unmount/remount cycle in front of the user, and polling that never stops.
    /// </summary>
    [Fact]
    public async Task Changing_to_persistent_while_mounted_cancels_the_idle_unmount_and_stops_the_polling()
    {
        var harness = IdleActivityHarness.OnDemand(idleUnmountSeconds: 300, mountedProbeIntervalSeconds: Interval);
        await harness.StartAsync();
        await harness.MountAsync("archive");
        await harness.AdvanceToAsync(100);

        var current = harness.Store.Current;
        var host = current.Hosts["archive"];
        await harness.ReloadAsync(Replace(current, host with { Mount = host.Mount with { Mode = MountMode.Persistent } }));
        Assert.Equal(MountState.Mounted, harness.State("archive"));
        var afterReload = harness.Rclone.VfsCallCount;

        await harness.AdvanceSecondsAsync(2 * 3600);

        Assert.Empty(harness.DrainsWith("archive", DrainCause.IdleTimeout));
        Assert.Equal(0, harness.Rclone.UnmountCount("Q:"));
        Assert.Equal(MountState.Mounted, harness.State("archive"));
        Assert.Equal(afterReload, harness.Rclone.VfsCallCount);
    }

    /// <summary>
    /// persistent (with a leftover idle value) -&gt; on-demand while Mounted: bs-7ck keeps the live
    /// mount, and the host is now exactly what rule 7 describes, an on-demand host with
    /// <c>idle_unmount_seconds &gt; 0</c> that is Mounted. Inferred from rule 7 rather than stated for
    /// this edit (see the report). Bug: the sampler starts only on the mount path, so this drive is
    /// never idle-unmounted until it is remounted by hand.
    /// </summary>
    [Fact]
    public async Task Changing_to_on_demand_while_mounted_makes_the_idle_unmount_apply()
    {
        var persistent = Support.HostFixtures.Persistent("vault", probeIntervalSeconds: 0, drive: "P:", idleUnmountSeconds: 120);
        var harness = new IdleActivityHarness(Support.HostFixtures.Build(Support.HostFixtures.Global(mountedProbeIntervalSeconds: Interval), persistent));
        await harness.StartAsync();
        Assert.Equal(MountState.Mounted, harness.State("vault"));
        await harness.AdvanceToAsync(500);

        var current = harness.Store.Current;
        await harness.ReloadAsync(Replace(current, persistent with { Mount = persistent.Mount with { Mode = MountMode.OnDemand } }));
        Assert.Equal(MountState.Mounted, harness.State("vault"));

        await harness.AdvanceToAsync(500 + (2 * 120) + Interval + 5);

        Assert.NotNull(harness.DrainTime("vault", DrainCause.IdleTimeout, notBefore: 500));
    }

    private static BosunConfig WithIdle(IdleActivityHarness harness, int idle)
    {
        var current = harness.Store.Current;
        var host = current.Hosts["archive"];
        return Replace(current, host with { Mount = host.Mount with { IdleUnmountSeconds = idle } });
    }

    private static BosunConfig Replace(BosunConfig current, HostConfig host)
    {
        var hosts = current.Hosts.ToDictionary(h => h.Key, h => h.Value, StringComparer.Ordinal);
        hosts[host.Key] = host;
        return current with { Hosts = hosts };
    }
}
