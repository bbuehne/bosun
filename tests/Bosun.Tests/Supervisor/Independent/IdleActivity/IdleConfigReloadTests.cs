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
    /// longer period counts from (the last activity at +0, or the reload) was not pinned when this
    /// was written, so only bounds are asserted here. bs-4hc has since recorded "from the reload";
    /// <see cref="Changing_the_idle_period_while_mounted_restarts_the_clock_at_the_reload"/> pins it.
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
    /// idle, eventually unmounted. The reload itself must not drain it ("nothing drains"). Only a
    /// bound (two periods plus an interval) is asserted; the exact reload + N / reload + 2N timings
    /// bs-4hc has since recorded are pinned by the two <c>Turning_the_idle_unmount_on_with_a_period_*</c>
    /// tests below. Bug: the sampler and idle timer are started only at mount, so the new setting
    /// does nothing until the user remounts.
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
    /// <c>idle_unmount_seconds &gt; 0</c> that is Mounted. Written before bs-4hc stated this edit's
    /// behaviour, so only a bound is asserted; the <c>Changing_to_on_demand_*</c> tests below pin the
    /// timings. Bug: the sampler starts only on the mount path, so this drive is never
    /// idle-unmounted until it is remounted by hand.
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

    // ---------------------------------------------------------------------------------------------
    // bs-05b: the exact timings docs/ARCHITECTURE.md §4 rule 7, "A reload while the drive is
    // mounted" (bs-4hc), now records. The tests above predate that text and assert only bounds.
    //
    // Every expected second below is off every mounted-probe tick under both readings of whether
    // the reload re-arms the mounted-probe timer (ticks at +60k from the mount, or +60k from the
    // reload), so no expectation depends on which of two same-second timers runs first.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Rule 7, reload bullet 1: "<c>idle_unmount_seconds</c> changed (N to M, both above zero). The
    /// clock restarts at the reload and runs M seconds from then. It counts from the reload, not from
    /// the last activity or from the deadline the old value had set." No activity at all, mount at
    /// +0, reload at +200, so the three readings land on three different seconds:
    /// from the reload = 200 + M; from the last activity (the mount) = M; the old deadline = N.
    /// Covers a shortening (600 -&gt; 290: 490, not 290 or 600) and a lengthening (300 -&gt; 250:
    /// 450, not 250 or 300). Bugs: the new period measured from the mount or the last activity
    /// (the drive goes early, at once if that point is already past); the old timer kept.
    /// </summary>
    [Theory]
    [InlineData(600, 290)]
    [InlineData(300, 250)]
    public async Task Changing_the_idle_period_while_mounted_restarts_the_clock_at_the_reload(int before, int after)
    {
        const int reloadAt = 200;
        var expected = reloadAt + after;
        var harness = IdleActivityHarness.OnDemand(idleUnmountSeconds: before, mountedProbeIntervalSeconds: Interval);
        await harness.StartAsync();
        await harness.MountAsync("archive");
        await harness.AdvanceToAsync(reloadAt);

        await harness.ReloadAsync(WithIdle(harness, after));

        await harness.AdvanceToAsync(expected - 5);
        Assert.True(
            harness.State("archive") == MountState.Mounted,
            $"{before} -> {after}: the drive left Mounted before reload + {after} = +{expected}s.{Environment.NewLine}{harness.Dump("archive")}");

        await harness.AdvanceToAsync(expected + 5);
        var at = harness.DrainTime("archive", DrainCause.IdleTimeout);
        Assert.True(
            at is not null && at >= expected && at <= expected + 1,
            $"{before} -> {after}: expected the idle drain at reload + {after} = +{expected}s.{Environment.NewLine}{harness.Dump("archive")}");
    }

    /// <summary>
    /// Same bullet, "not from the last activity": the VFS moves at +100 (sampled at +120, so the
    /// clock restarts there), then 600 -&gt; 290 at +200. From the reload: +490. From the last
    /// activity: +120 + 290 = +410. Bug: the reload re-computes the deadline as last-activity + M,
    /// which is what "idle for M seconds" naively means and what the spec rules out.
    /// </summary>
    [Fact]
    public async Task Changing_the_idle_period_counts_from_the_reload_not_from_the_last_activity()
    {
        var harness = IdleActivityHarness.OnDemand(idleUnmountSeconds: 600, mountedProbeIntervalSeconds: Interval);
        await harness.StartAsync();
        await harness.MountAsync("archive");
        await harness.AdvanceToAsync(100);
        harness.Rclone.SetStats("Q:", Vfs.Bump(harness.Rclone.StatsFor("Q:"), "diskCache.bytesUsed", 4096));
        await harness.AdvanceToAsync(200);

        await harness.ReloadAsync(WithIdle(harness, 290));

        await harness.AdvanceToAsync(485);
        Assert.True(
            harness.State("archive") == MountState.Mounted,
            $"the drive left Mounted before reload + 290 = +490s (last activity + 290 is +410).{Environment.NewLine}{harness.Dump("archive")}");

        await harness.AdvanceToAsync(495);
        var at = harness.DrainTime("archive", DrainCause.IdleTimeout);
        Assert.True(at is >= 490 and <= 491, $"expected the idle drain at +490s.{Environment.NewLine}{harness.Dump("archive")}");
    }

    /// <summary>
    /// Same bullet: "The activity baseline is kept." The VFS moves at +190, after the +180 sample
    /// and before the reload at +200 (600 -&gt; 290). With the baseline kept, the first sample after
    /// the reload sees the move as activity and restarts the clock, so the drain is one period after
    /// that sample: +530 (sample at +240) or +550 (sample at +260, if the reload re-armed the
    /// mounted-probe timer; the bullet says it does for a probe change but does not say so for an
    /// idle-only change, so both are accepted). With the baseline discarded, the first sample after
    /// the reload merely records a new one, nothing counts as activity, and the drain is at +490.
    /// Bug: the reload resets the sampler along with the clock, losing activity that happened in
    /// the last interval before the save.
    /// </summary>
    [Fact]
    public async Task Changing_the_idle_period_keeps_the_activity_baseline()
    {
        var harness = IdleActivityHarness.OnDemand(idleUnmountSeconds: 600, mountedProbeIntervalSeconds: Interval);
        await harness.StartAsync();
        await harness.MountAsync("archive");
        await harness.AdvanceToAsync(190);
        harness.Rclone.SetStats("Q:", Vfs.Bump(harness.Rclone.StatsFor("Q:"), "metadataCache.dirs", 2));
        await harness.AdvanceToAsync(200);

        await harness.ReloadAsync(WithIdle(harness, 290));

        await harness.AdvanceToAsync(495);
        Assert.True(
            harness.State("archive") == MountState.Mounted,
            $"the drive went at reload + 290 = +490s: the change made at +190 was not compared against the kept baseline.{Environment.NewLine}{harness.Dump("archive")}");

        await harness.AdvanceToAsync(560);
        var at = harness.DrainTime("archive", DrainCause.IdleTimeout);
        Assert.True(
            at is (>= 530 and <= 531) or (>= 550 and <= 551),
            $"expected the idle drain one period after the first post-reload sample (+530 or +550).{Environment.NewLine}{harness.Dump("archive")}");
    }

    public static TheoryData<string> ProbeEdits => new() { "probe.interval_seconds", "probe.deep_probe" };

    /// <summary>
    /// Same bullet: "The same restart happens when the host's <c>probe.interval_seconds</c> or
    /// <c>probe.deep_probe</c> changes, because that reload re-arms the mounted-probe timer and the
    /// idle clock together." Idle 290, mount at +0, probe edit at +200: the drain is at +490, not at
    /// the +290 the clock was armed for. Bug: a probe-only edit re-arms the probe timer but not the
    /// idle clock -- the opposite of the recorded behaviour.
    /// </summary>
    [Theory]
    [MemberData(nameof(ProbeEdits))]
    public async Task A_probe_edit_on_the_mounted_host_restarts_the_idle_clock_at_the_reload(string edit)
    {
        var harness = IdleActivityHarness.OnDemand(idleUnmountSeconds: 290, mountedProbeIntervalSeconds: Interval);
        await harness.StartAsync();
        await harness.MountAsync("archive");
        await harness.AdvanceToAsync(200);

        var current = harness.Store.Current;
        var host = current.Hosts["archive"];
        var edited = edit == "probe.interval_seconds"
            ? host with { Probe = host.Probe with { IntervalSeconds = 45 } }
            : host with { Probe = host.Probe with { DeepProbe = !host.Probe.DeepProbe } };
        await harness.ReloadAsync(Replace(current, edited));

        await harness.AdvanceToAsync(485);
        Assert.True(
            harness.State("archive") == MountState.Mounted,
            $"{edit}: the drive left Mounted before reload + 290 = +490s.{Environment.NewLine}{harness.Dump("archive")}");

        await harness.AdvanceToAsync(495);
        var at = harness.DrainTime("archive", DrainCause.IdleTimeout);
        Assert.True(at is >= 490 and <= 491, $"{edit}: expected the idle drain at +490s.{Environment.NewLine}{harness.Dump("archive")}");
    }

    public static TheoryData<string> UnrelatedEdits => new()
    {
        "another host's idle_unmount_seconds",
        "global.suspend_unmount_timeout_seconds",
        "global.mounted_probe_interval_seconds",
    };

    /// <summary>
    /// Rule 7, reload paragraph: "A config reload re-evaluates the idle clock only for a host whose
    /// own entry changed. A change to <c>[global]</c> or to another host leaves it alone." Idle 290,
    /// mount at +0, unrelated edit at +200: the drain stays at +290. Bug: every reload restarts
    /// every mounted host's clock, so a config saved often enough keeps idle drives up forever.
    /// <c>global.mounted_probe_interval_seconds</c> is included on purpose: it changes this host's
    /// effective mounted cadence, but it is a <c>[global]</c> change, which the sentence covers
    /// without exception.
    /// </summary>
    [Theory]
    [MemberData(nameof(UnrelatedEdits))]
    public async Task A_reload_that_does_not_change_this_hosts_entry_leaves_its_idle_clock_alone(string edit)
    {
        var archive = Support.HostFixtures.OnDemand("archive", probeIntervalSeconds: 0, idleUnmountSeconds: 290, drive: "Q:");
        var other = Support.HostFixtures.OnDemand("other", probeIntervalSeconds: 0, idleUnmountSeconds: 0, drive: "R:");
        var harness = new IdleActivityHarness(Support.HostFixtures.Build(
            Support.HostFixtures.Global(mountedProbeIntervalSeconds: Interval), archive, other));
        await harness.StartAsync();
        await harness.MountAsync("archive");
        await harness.AdvanceToAsync(200);

        var current = harness.Store.Current;
        var next = edit switch
        {
            "another host's idle_unmount_seconds" => Replace(current, other with { Mount = other.Mount with { IdleUnmountSeconds = 100 } }),
            "global.suspend_unmount_timeout_seconds" => current with { Global = current.Global with { SuspendUnmountTimeoutSeconds = current.Global.SuspendUnmountTimeoutSeconds + 1 } },
            _ => current with { Global = current.Global with { MountedProbeIntervalSeconds = 45 } },
        };
        await harness.ReloadAsync(next);

        await harness.AdvanceToAsync(295);
        var at = harness.DrainTime("archive", DrainCause.IdleTimeout);
        Assert.True(
            at is >= 290 and <= 291,
            $"{edit}: expected the idle drain at the original +290s; the reload restarted a clock it should have left alone.{Environment.NewLine}{harness.Dump("archive")}");
    }

    /// <summary>
    /// Rule 7, reload bullet 2, N longer than the mounted-probe interval: idle 0 -&gt; 150 at +1000,
    /// interval 60. "The reload also re-arms the mounted-probe timer, and the first passing mounted
    /// probe after it takes the baseline ... If N is longer than that interval the baseline exists
    /// when the clock fires, and an idle drive unmounts at reload + N" = +1150. Bugs: the no-baseline
    /// path taken although a baseline exists (+1300); the clock counted from the mount (drains at
    /// once, at the reload).
    /// </summary>
    [Fact]
    public async Task Turning_the_idle_unmount_on_with_a_period_longer_than_the_probe_interval_unmounts_at_reload_plus_N()
    {
        var harness = IdleActivityHarness.OnDemand(idleUnmountSeconds: 0, mountedProbeIntervalSeconds: Interval);
        await harness.StartAsync();
        await harness.MountAsync("archive");
        await harness.AdvanceToAsync(1000);

        await harness.ReloadAsync(WithIdle(harness, 150));

        await harness.AdvanceToAsync(1145);
        Assert.True(
            harness.State("archive") == MountState.Mounted,
            $"the drive left Mounted before reload + 150 = +1150s.{Environment.NewLine}{harness.Dump("archive")}");

        await harness.AdvanceToAsync(1155);
        var at = harness.DrainTime("archive", DrainCause.IdleTimeout);
        Assert.True(at is >= 1150 and <= 1151, $"expected the idle drain at reload + N = +1150s.{Environment.NewLine}{harness.Dump("archive")}");
    }

    /// <summary>
    /// Rule 7, reload bullet 2, N no longer than the mounted-probe interval: idle 0 -&gt; 45 at +1000,
    /// interval 60. The clock fires at +1045, before the first post-reload probe (+1060) has taken a
    /// baseline, so it "find[s] nothing to compare with, record[s] a baseline and wait[s] one more
    /// period ... the drive unmounts at reload + 2N" = +1090. Bug: a fire with no baseline unmounts
    /// blind at +1045 -- the exact case "Safety when the clock fires" forbids.
    /// </summary>
    [Fact]
    public async Task Turning_the_idle_unmount_on_with_a_period_no_longer_than_the_probe_interval_unmounts_at_reload_plus_2N()
    {
        var harness = IdleActivityHarness.OnDemand(idleUnmountSeconds: 0, mountedProbeIntervalSeconds: Interval);
        await harness.StartAsync();
        await harness.MountAsync("archive");
        await harness.AdvanceToAsync(1000);

        await harness.ReloadAsync(WithIdle(harness, 45));

        await harness.AdvanceToAsync(1085);
        Assert.True(
            harness.State("archive") == MountState.Mounted,
            $"the drive left Mounted before reload + 2N = +1090s: a fire with no baseline unmounted blind.{Environment.NewLine}{harness.Dump("archive")}");

        await harness.AdvanceToAsync(1095);
        var at = harness.DrainTime("archive", DrainCause.IdleTimeout);
        Assert.True(at is >= 1090 and <= 1091, $"expected the idle drain at reload + 2N = +1090s.{Environment.NewLine}{harness.Dump("archive")}");
    }

    /// <summary>
    /// Rule 7, reload bullet 3: persistent -&gt; on-demand. "The mount is kept, not drained, and the
    /// idle clock is armed for the new tier at once, from the reload ... A persistent mount was never
    /// sampled, so the same no-baseline reading applies: the first passing mounted probe takes the
    /// baseline, and an idle drive unmounts at reload + N". Persistent mount at +0, edit at +500
    /// setting the mode and the idle value together, N = 150, interval 60: +650. Bugs: the reload
    /// drains the mount; the clock counts from the mount (the drive goes at the reload, 500 s having
    /// passed); the clock is never armed until a remount.
    /// </summary>
    [Fact]
    public Task Changing_to_on_demand_while_mounted_arms_the_idle_clock_from_the_reload() =>
        AssertTierChangeToOnDemandDrainsAt(reloadAt: 500, idle: 150, expected: 650);

    /// <summary>
    /// Rule 7, reload bullet 3, short period, probe first (bs-2t1): "this reload does not re-arm
    /// the mounted-probe timer ... If that probe comes before the clock fires, an idle drive
    /// unmounts at reload + N." N = 45 &lt;= 60, edit at +500: the persistent mount's own probe at
    /// +540 takes the baseline before the clock fires at +545, so the drain is at +545. Bugs: the
    /// tier change re-arms the probe timer (first probe +560, drain +590); the fire ignores the
    /// baseline the +540 probe took.
    /// </summary>
    [Fact]
    public Task Changing_to_on_demand_with_a_period_no_longer_than_the_probe_interval_unmounts_at_reload_plus_N_when_a_probe_comes_first() =>
        AssertTierChangeToOnDemandDrainsAt(reloadAt: 500, idle: 45, expected: 545);

    /// <summary>
    /// Rule 7, reload bullet 3, short period, clock first: "If the clock fires first, it finds
    /// nothing to compare with, records a baseline and waits one more period, and the drive
    /// unmounts at reload + 2N." N = 45, edit at +541: the clock fires at +586, before the persistent
    /// mount's next probe at +600, so the fire has no baseline and the drain is at +631. Bug: the
    /// persistent mount brings no baseline into the new tier and the first fire unmounts it blind.
    /// </summary>
    [Fact]
    public Task Changing_to_on_demand_does_not_unmount_blind_when_the_clock_fires_before_any_sample() =>
        AssertTierChangeToOnDemandDrainsAt(reloadAt: 541, idle: 45, expected: 631);

    private static async Task AssertTierChangeToOnDemandDrainsAt(int reloadAt, int idle, int expected)
    {
        var persistent = Support.HostFixtures.Persistent("vault", probeIntervalSeconds: 0, drive: "P:");
        var harness = new IdleActivityHarness(Support.HostFixtures.Build(Support.HostFixtures.Global(mountedProbeIntervalSeconds: Interval), persistent));
        await harness.StartAsync();
        Assert.Equal(MountState.Mounted, harness.State("vault"));
        await harness.AdvanceToAsync(reloadAt);

        var current = harness.Store.Current;
        await harness.ReloadAsync(Replace(current, persistent with
        {
            Mount = persistent.Mount with { Mode = MountMode.OnDemand, IdleUnmountSeconds = idle },
        }));
        Assert.Equal(MountState.Mounted, harness.State("vault"));
        Assert.Equal(0, harness.Rclone.UnmountCount("P:"));

        await harness.AdvanceToAsync(expected - 5);
        Assert.True(
            harness.State("vault") == MountState.Mounted,
            $"N = {idle}, edit at +{reloadAt}: the drive left Mounted before +{expected}s.{Environment.NewLine}{harness.Dump("vault")}");

        await harness.AdvanceToAsync(expected + 5);
        var at = harness.DrainTime("vault", DrainCause.IdleTimeout);
        Assert.True(
            at is not null && at >= expected && at <= expected + 1,
            $"N = {idle}, edit at +{reloadAt}: expected the idle drain at +{expected}s.{Environment.NewLine}{harness.Dump("vault")}");
    }

    /// <summary>
    /// Rule 7, reload bullet 3, last sentence: "The reverse, on-demand to persistent, disarms the
    /// clock." The existing disarm test reloads while the clock is armed for its first period; this
    /// one reloads while the clock is armed by a SKIPPED fire ("skips the unmount ... and restarts
    /// the clock for a full period"). <c>erroredFiles</c> = 2 from the mount (not an activity
    /// counter) blocks the fire at +120 and re-arms for +240; the edit to persistent comes at +150;
    /// the failed files clear at +160, so a timer that survived the edit would find nothing to stop
    /// it at +240. Bug: the skip path re-arms through a timer the tier change does not cancel, and a
    /// persistent drive is idle-unmounted (and then remounted by the tier rule).
    /// </summary>
    [Fact]
    public async Task Changing_to_persistent_disarms_a_clock_re_armed_by_a_skipped_fire()
    {
        var harness = IdleActivityHarness.OnDemand(idleUnmountSeconds: 120, mountedProbeIntervalSeconds: Interval);
        var clean = harness.Rclone.DefaultStats;
        harness.Rclone.SetStats("Q:", clean with { DiskCache = clean.DiskCache! with { ErroredFiles = 2 } });
        await harness.StartAsync();
        await harness.MountAsync("archive");
        await harness.AdvanceToAsync(150);
        Assert.Equal(MountState.Mounted, harness.State("archive")); // the +120 fire was skipped

        var current = harness.Store.Current;
        var host = current.Hosts["archive"];
        await harness.ReloadAsync(Replace(current, host with { Mount = host.Mount with { Mode = MountMode.Persistent } }));
        await harness.AdvanceToAsync(160);
        harness.Rclone.SetStats("Q:", clean);
        var afterReload = harness.Rclone.VfsCallCount;

        await harness.AdvanceSecondsAsync(2 * 3600);

        Assert.Empty(harness.DrainsWith("archive", DrainCause.IdleTimeout));
        Assert.Equal(0, harness.Rclone.UnmountCount("Q:"));
        Assert.Equal(MountState.Mounted, harness.State("archive"));
        Assert.Equal(afterReload, harness.Rclone.VfsCallCount);
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
