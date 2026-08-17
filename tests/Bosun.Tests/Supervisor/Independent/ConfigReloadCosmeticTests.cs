using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor.Independent;

/// <summary>
/// bs-7ck's cosmetic / session-only row, and the reason the whole classification exists: "A save
/// that changes only a tab colour must not disturb a mounted drive. That is the whole point of the
/// classification -- if everything drains, editing a host in the GUI becomes an unmount/remount
/// cycle and the feature is worse than the restart it replaces."
/// </summary>
/// <remarks>
/// <para>
/// The failure this file exists to catch is not a crash and not a hang. It is a tool that quietly
/// yanks the maintainer's drive letters away every time he saves a form -- while he has files open
/// on them, because that is when people edit things. It is also the failure most likely to ship,
/// because "re-arm every host on any change" is the shortest correct-looking implementation of the
/// subscription and passes every test that only asks whether the reload was noticed.
/// </para>
/// <para>
/// Fakes only: an in-memory rclone double, a probe double, a fake clock, an in-memory config store.
/// </para>
/// </remarks>
public sealed class ConfigReloadCosmeticTests
{
    /// <summary>
    /// <b>Protects:</b> the cosmetic row of bs-7ck's classification -- "do NOTHING to the mount" --
    /// for every field listed in it.
    /// <b>Catches:</b> the drain-everything implementation, and the narrower version of it where
    /// the diff covers <c>session.*</c> but treats <c>display_name</c> (which sits at the top level
    /// of <c>HostConfig</c>, right next to <c>hostname</c>) as mount-affecting. Symptom: renaming a
    /// host in the window unmounts its drive. The transition-history assertion is the strong one --
    /// it says nothing happened <i>at all</i>, rather than that the mount happened to survive
    /// whatever did happen.
    /// </summary>
    [Theory]
    [InlineData("display_name")]
    [InlineData("session.tab_color")]
    [InlineData("session.color_scheme")]
    [InlineData("session.tmux")]
    [InlineData("session.tmux_session")]
    [InlineData("session.autostart")]
    [InlineData("session.reconnect")]
    public async Task A_cosmetic_edit_leaves_a_mounted_drive_completely_undisturbed(string field)
    {
        var host = HostFixtures.Persistent("prod", probeIntervalSeconds: 0, drive: "P:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), host));

        await harness.StartAsync();
        Assert.Equal(MountState.Mounted, harness.State("prod"));
        var fsBefore = harness.Rclone.FsAt("P:");
        var transitionsBefore = harness.Supervisor.GetTransitionHistory().Count;

        var edited = ConfigReloadFixtures.ApplyCosmetic(host, field);
        await harness.ReloadAsync(HostFixtures.Build(HostFixtures.Global(), edited));

        Assert.Equal(0, harness.Rclone.UnmountCount("P:"));
        Assert.Equal(1, harness.Rclone.MountCount("P:"));
        Assert.Equal(fsBefore, harness.Rclone.FsAt("P:"));
        Assert.Equal(MountState.Mounted, harness.State("prod"));
        Assert.Equal(transitionsBefore, harness.Supervisor.GetTransitionHistory().Count);
    }

    /// <summary>
    /// <b>Protects:</b> "do NOTHING to the mount" read strictly, against the mounted-probe cadence
    /// (docs/ARCHITECTURE.md §4 rule 3 / ADR-011). A cosmetic edit is not a scheduling change, so
    /// the armed timer is not its business.
    /// <b>Catches:</b> a reload handler that re-arms every timer unconditionally and only guards
    /// the drain. Each save then pushes the next mounted probe a full interval into the future, so
    /// the window in which a dead mount goes undetected -- the window in which the user's Explorer
    /// is the thing that discovers it (docs/OPERATIONS.md's first triage row) -- is extended by
    /// every save. Twenty imported hosts (bs-ww9.9) means twenty saves in a row, each one deferring
    /// detection for every host.
    /// </summary>
    /// <remarks>
    /// This is the strictest reading of "do NOTHING" in this file, and the one place where a
    /// reasonable implementer might disagree: re-arming is harmless in isolation. It is asserted
    /// because the harm is cumulative and invisible, not because the DESIGN spells it out.
    /// </remarks>
    [Fact]
    public async Task A_cosmetic_edit_does_not_restart_the_mounted_probe_cadence()
    {
        var host = HostFixtures.Persistent("prod", probeIntervalSeconds: 0, drive: "P:");
        var harness = new IndependentHarness(
            HostFixtures.Build(HostFixtures.Global(mountedProbeIntervalSeconds: 60), host));

        await harness.StartAsync();
        Assert.Equal(MountState.Mounted, harness.State("prod"));

        await harness.AdvanceSecondsAsync(30); // half way to the next mounted probe
        var probes = harness.ShallowProbesFor("prod");

        await harness.ReloadAsync(HostFixtures.Build(
            HostFixtures.Global(mountedProbeIntervalSeconds: 60),
            host with { Session = host.Session with { TabColor = "#ff8800" } }));

        await harness.AdvanceSecondsAsync(29);
        Assert.Equal(probes, harness.ShallowProbesFor("prod"));

        await harness.AdvanceSecondsAsync(1); // t = 60s: the tick that was already armed
        Assert.Equal(probes + 1, harness.ShallowProbesFor("prod"));
    }

    /// <summary>
    /// <b>Protects:</b> bs-7ck's "a config save must never look like a successful probe", applied
    /// to the host that was actually edited.
    /// <b>Catches:</b> a handler that rebuilds the edited host's runtime record from the new
    /// <c>HostConfig</c> -- the natural way to adopt the new values -- and in doing so starts its
    /// failure counters at zero. Only the mount-affecting branch drains; the cosmetic branch does
    /// not, so the host keeps a live drive letter whose failure history has just been erased. A
    /// mount already two failures into a three-failure drain gets a full reprieve because the user
    /// changed its colour, and the drive stays pointed at a dead host for another three probe
    /// intervals -- during which Explorer can hang on it (Invariant I2).
    /// </summary>
    /// <remarks>
    /// The DESIGN's wording is "a host whose own definition did not change", which read literally
    /// exempts the edited host. The sentence it is justifying is not: "a config save must never
    /// look like a successful probe" is about saves, not about which host was touched. That
    /// ambiguity is called out in the delivery report rather than resolved silently -- this test
    /// takes the reading that matches the stated harm.
    /// </remarks>
    [Fact]
    public async Task A_cosmetic_edit_does_not_forgive_the_edited_hosts_own_accumulated_failures()
    {
        var host = HostFixtures.Persistent("prod", probeIntervalSeconds: 0, drive: "P:");
        var global = HostFixtures.Global(failuresBeforeUnmount: 3, mountedProbeIntervalSeconds: 60);
        var harness = new IndependentHarness(HostFixtures.Build(global, host));

        await harness.StartAsync();
        Assert.Equal(MountState.Mounted, harness.State("prod"));

        harness.Probe.SetShallow(IndependentHarness.HostnameOf("prod"), ShallowProbeOutcome.Timeout);
        await harness.AdvanceSecondsAsync(60);
        await harness.AdvanceSecondsAsync(60);
        Assert.Equal(2, harness.Snapshot("prod").ConsecutiveMountedFailures);

        await harness.ReloadAsync(HostFixtures.Build(
            global, host with { Session = host.Session with { TabColor = "#ff8800" } }));

        Assert.Equal(2, harness.Snapshot("prod").ConsecutiveMountedFailures);

        // The third consecutive failure is still the third, so it still drains (ADR-005 / I2).
        await harness.AdvanceSecondsAsync(60);
        Assert.Null(harness.Rclone.FsAt("P:"));
    }

    /// <summary>
    /// <b>Protects:</b> ADR-015 / docs/ARCHITECTURE.md §4 rule 9 -- a user-requested unmount parks
    /// the host -- against bs-7ck's reload path, in the direction nobody tests: a mount appearing
    /// rather than disappearing.
    /// <b>Catches:</b> a reload that unparks every host because §4 rule 9 lists "a config reload"
    /// among the things that clear parking. Written when a reload meant hand-editing
    /// <c>hosts.toml</c>, that clause is reasonable; with the window saving on every edit
    /// (ADR-019), it means the drive the user just explicitly unmounted comes back the next time
    /// they change any host's colour -- and keeps coming back. The tray's Unmount item then looks
    /// broken, which is the precise complaint ADR-015 was written to fix.
    /// </summary>
    /// <remarks>
    /// This is a genuine conflict between two specs, not a misreading: §4 rule 9 says a config
    /// reload unparks, and bs-7ck's DESIGN says a cosmetic edit does nothing to the mount. The test
    /// follows bs-7ck, which is the later and more specific of the two and the one written with GUI
    /// editing in mind, and the conflict is escalated in the delivery report.
    /// </remarks>
    [Fact]
    public async Task A_cosmetic_edit_does_not_unpark_a_host_the_user_deliberately_unmounted()
    {
        var host = HostFixtures.Persistent("prod", probeIntervalSeconds: 60, drive: "P:");
        var harness = new IndependentHarness(HostFixtures.Build(HostFixtures.Global(), host));

        await harness.StartAsync();
        await harness.RunAsync(() => harness.Supervisor.RequestUnmountAsync("prod"));
        Assert.True(harness.Snapshot("prod").UserParked);
        Assert.Null(harness.Rclone.FsAt("P:"));

        await harness.ReloadAsync(HostFixtures.Build(
            HostFixtures.Global(),
            host with { Session = host.Session with { TabColor = "#ff8800" } }));

        Assert.True(
            harness.Snapshot("prod").UserParked,
            "A tab-colour edit unparked a host the user had explicitly unmounted.");

        await harness.AdvanceSecondsAsync(300);
        Assert.Null(harness.Rclone.FsAt("P:"));
    }
}
