using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor.Independent;

/// <summary>
/// bs-7ck's second "must not happen": "A config change must not reset
/// <c>ConsecutiveMountedFailures</c> / <c>ConsecutiveDeepProbeFailures</c> / the backoff ladder for
/// a host whose own definition did not change. Editing host A must not silently forgive host B's
/// failing probes." And its global row: "adopt for FUTURE scheduling. Do not retroactively re-arm
/// every host or reset any failure counter; a config save must never look like a successful probe."
/// </summary>
/// <remarks>
/// <para>
/// Failure counters are the mechanism by which Invariant I2 actually happens -- ADR-005's "unmount
/// on failure, do not retry through failure" is implemented as counting. Anything that silently
/// zeroes them does not look like a bug; it looks like nothing. The drive stays up, the state
/// machine reports <c>Mounted</c>, and the unmount that should have happened simply does not. With
/// twenty hosts being imported one save at a time (bs-ww9.9), a reset-on-every-save turns
/// "unmount after 3 failures" into "unmount never" for as long as the user is editing.
/// </para>
/// <para>
/// Fakes only: an in-memory rclone double, a probe double, a fake clock, an in-memory config store.
/// </para>
/// </remarks>
public sealed class ConfigReloadFailureAccountingTests
{
    /// <summary>
    /// <b>Protects:</b> "editing host A must not silently forgive host B's failing probes", for the
    /// counter that drives the unmount decision (docs/ARCHITECTURE.md §4 rule 3 / ADR-005).
    /// <b>Catches:</b> a reload that rebuilds every host's runtime record from the new config
    /// rather than diffing -- the shortest implementation that adopts new values correctly, and the
    /// one that starts every counter at zero as a side effect. The visible consequence is not an
    /// error: it is a drive letter pointing at a dead host surviving three more probe intervals for
    /// each save, and Explorer hanging on it in the meantime. The final assertion is the one that
    /// matters -- the counter surviving is only interesting because the unmount it was counting
    /// toward still happens on schedule.
    /// </summary>
    [Fact]
    public async Task Editing_one_host_does_not_forgive_another_hosts_failing_mounted_probes()
    {
        var alpha = HostFixtures.Persistent("alpha", probeIntervalSeconds: 0, drive: "P:");
        var beta = HostFixtures.Persistent("beta", probeIntervalSeconds: 0, drive: "R:");
        var global = HostFixtures.Global(failuresBeforeUnmount: 3, mountedProbeIntervalSeconds: 60);
        var harness = new IndependentHarness(HostFixtures.Build(global, alpha, beta));

        await harness.StartAsync();
        Assert.Equal(MountState.Mounted, harness.State("beta"));

        harness.Probe.SetShallow(IndependentHarness.HostnameOf("beta"), ShallowProbeOutcome.Timeout);
        await harness.AdvanceSecondsAsync(60);
        await harness.AdvanceSecondsAsync(60);
        Assert.Equal(2, harness.Snapshot("beta").ConsecutiveMountedFailures);

        // A save that touches alpha only. beta's definition is byte-identical across the reload.
        await harness.ReloadAsync(HostFixtures.Build(
            global, alpha with { Session = alpha.Session with { TabColor = "#ff8800" } }, beta));

        Assert.Equal(2, harness.Snapshot("beta").ConsecutiveMountedFailures);

        await harness.AdvanceSecondsAsync(60); // the third consecutive failure
        Assert.Null(harness.Rclone.FsAt("R:"));
        Assert.NotNull(harness.Rclone.FsAt("P:"));
    }

    /// <summary>
    /// <b>Protects:</b> the same sentence for the backoff ladder (docs/ARCHITECTURE.md §4
    /// "Backoff"), whose reset triggers are an explicit, closed list: a network address change, a
    /// power resume, an explicit user "retry now". A config save is not on it.
    /// <b>Catches:</b> a reload that calls the same "re-enable this host" path resume and network
    /// change use, because it is right there and does the job. Unlike the counter reset above this
    /// one is not silent -- it is a probe storm: every save drops every unreachable host back to
    /// the 5-second rung, so editing twenty hosts in a row keeps every dark host hammering its
    /// dead address at the fastest rung for the whole session. That is precisely the noise ADR-014
    /// and the ladder exist to avoid, and on a machine with an unreachable host it is what the
    /// maintainer's auth logs will show.
    /// </summary>
    [Fact]
    public async Task Editing_one_host_does_not_reset_another_hosts_backoff_ladder()
    {
        var alpha = HostFixtures.Persistent("alpha", probeIntervalSeconds: 0, drive: "P:");
        var beta = HostFixtures.Persistent("beta", probeIntervalSeconds: 60, drive: "R:");
        var global = HostFixtures.Global(backoffSeconds: [5, 15, 30, 60, 300]);
        var harness = new IndependentHarness(HostFixtures.Build(global, alpha, beta));
        harness.Probe.SetShallow(IndependentHarness.HostnameOf("beta"), ShallowProbeOutcome.ConnectionRefused);

        await harness.StartAsync();
        await harness.AdvanceSecondsAsync(5);
        await harness.AdvanceSecondsAsync(15);

        Assert.Equal(MountState.Unreachable, harness.State("beta"));
        Assert.Equal(3, harness.Snapshot("beta").ConsecutiveIdleFailures); // next rung is 30s

        await harness.ReloadAsync(HostFixtures.Build(
            global, alpha with { Session = alpha.Session with { TabColor = "#ff8800" } }, beta));

        Assert.Equal(3, harness.Snapshot("beta").ConsecutiveIdleFailures);

        var probes = harness.ShallowProbesFor("beta");
        await harness.AdvanceSecondsAsync(29);
        Assert.Equal(probes, harness.ShallowProbesFor("beta"));

        await harness.AdvanceSecondsAsync(1);
        Assert.Equal(probes + 1, harness.ShallowProbesFor("beta"));
    }

    /// <summary>
    /// <b>Protects:</b> the global row -- a change to <c>[global]</c> alone touches no host's
    /// failure state.
    /// <b>Catches:</b> a classifier that short-circuits on "the global block changed" and re-arms
    /// or re-enables everything, on the reasoning that global settings apply to every host so every
    /// host must be revisited. They do apply to every host -- for future scheduling. Applying them
    /// by resetting state is the same forgiveness bug as above with a single edit reaching every
    /// host at once, which is strictly worse: one adjustment to a probe timeout wipes the failure
    /// history of every mount on the machine.
    /// </summary>
    [Fact]
    public async Task A_global_only_edit_resets_no_hosts_failure_state()
    {
        var alpha = HostFixtures.Persistent("alpha", probeIntervalSeconds: 0, drive: "P:");
        var beta = HostFixtures.Persistent("beta", probeIntervalSeconds: 60, drive: "R:");
        var before = HostFixtures.Global(probeTimeoutSeconds: 5, mountedProbeIntervalSeconds: 60);
        var harness = new IndependentHarness(HostFixtures.Build(before, alpha, beta));
        harness.Probe.SetShallow(IndependentHarness.HostnameOf("beta"), ShallowProbeOutcome.ConnectionRefused);

        await harness.StartAsync();
        harness.Probe.SetShallow(IndependentHarness.HostnameOf("alpha"), ShallowProbeOutcome.Timeout);
        await harness.AdvanceSecondsAsync(60); // alpha's first mounted-probe failure; beta ladder rung
        await harness.AdvanceSecondsAsync(60); // alpha's second

        var alphaFailures = harness.Snapshot("alpha").ConsecutiveMountedFailures;
        var betaFailures = harness.Snapshot("beta").ConsecutiveIdleFailures;
        Assert.Equal(2, alphaFailures);
        Assert.True(betaFailures >= 2);

        var after = HostFixtures.Global(probeTimeoutSeconds: 9, mountedProbeIntervalSeconds: 60);
        await harness.ReloadAsync(HostFixtures.Build(after, alpha, beta));

        Assert.Equal(alphaFailures, harness.Snapshot("alpha").ConsecutiveMountedFailures);
        Assert.Equal(betaFailures, harness.Snapshot("beta").ConsecutiveIdleFailures);
    }

    /// <summary>
    /// <b>Protects:</b> "adopt for FUTURE scheduling. Do not retroactively re-arm every host" --
    /// both halves, on the one global value whose effect is directly observable in a probe call.
    /// <b>Catches:</b> a global change that is stored but never consulted again (the supervisor
    /// caches <c>global</c> once at start, so this is the default outcome of doing nothing), and
    /// the opposite bug where adopting it means forcing an immediate probe of every host. The
    /// second is the more damaging: a forced probe of every host on every save is a burst of
    /// connection attempts against every configured machine each time the user presses Save, and
    /// during a twenty-host import that is twenty bursts.
    /// </summary>
    [Fact]
    public async Task A_global_probe_timeout_edit_is_adopted_by_the_next_scheduled_probe_and_forces_none()
    {
        var host = HostFixtures.Persistent("prod", probeIntervalSeconds: 60, drive: "P:");
        var before = HostFixtures.Global(probeTimeoutSeconds: 5, backoffSeconds: [5, 15, 30, 60, 300]);
        var harness = new IndependentHarness(HostFixtures.Build(before, host));
        harness.Probe.DefaultShallow = ShallowProbeOutcome.ConnectionRefused;

        await harness.StartAsync();
        Assert.Equal(MountState.Unreachable, harness.State("prod")); // next rung: 5s
        Assert.Equal(TimeSpan.FromSeconds(5), harness.Probe.LastShallowTimeout);

        await harness.AdvanceSecondsAsync(3);
        var probes = harness.ShallowProbesFor("prod");

        var after = HostFixtures.Global(probeTimeoutSeconds: 9, backoffSeconds: [5, 15, 30, 60, 300]);
        await harness.ReloadAsync(HostFixtures.Build(after, host));

        Assert.Equal(probes, harness.ShallowProbesFor("prod")); // no probe forced by the save

        await harness.AdvanceSecondsAsync(2); // t = 5s: the rung that was already armed

        Assert.Equal(probes + 1, harness.ShallowProbesFor("prod"));
        Assert.Equal(TimeSpan.FromSeconds(9), harness.Probe.LastShallowTimeout);
    }

    /// <summary>
    /// <b>Protects:</b> "do not retroactively re-arm" for <c>global.mounted_probe_interval_seconds</c>,
    /// where the armed timer and the new value genuinely disagree, followed by the adoption the
    /// same sentence requires.
    /// <b>Catches:</b> both halves of that sentence with one timeline. A retroactive re-arm fires
    /// the first probe early (harmless in itself, but it is the same code path that re-arms
    /// <i>every</i> host on <i>every</i> save, and the "never look like a successful probe" rule
    /// lives next to it); never adopting the new value means a user who shortened the mounted probe
    /// interval -- the setting that decides how quickly a dead mount is noticed, ADR-011 -- keeps
    /// the old, slower one until the next restart, while the window shows the new number.
    /// </summary>
    [Fact]
    public async Task A_global_mounted_interval_edit_applies_from_the_next_re_arm_not_the_armed_one()
    {
        var host = HostFixtures.Persistent("prod", probeIntervalSeconds: 0, drive: "P:");
        var before = HostFixtures.Global(mountedProbeIntervalSeconds: 60);
        var harness = new IndependentHarness(HostFixtures.Build(before, host));

        await harness.StartAsync();
        Assert.Equal(MountState.Mounted, harness.State("prod"));
        var probes = harness.ShallowProbesFor("prod");

        var after = HostFixtures.Global(mountedProbeIntervalSeconds: 30);
        await harness.ReloadAsync(HostFixtures.Build(after, host));

        await harness.AdvanceSecondsAsync(59);
        Assert.Equal(probes, harness.ShallowProbesFor("prod")); // the armed 60s tick is left alone

        await harness.AdvanceSecondsAsync(1);
        Assert.Equal(probes + 1, harness.ShallowProbesFor("prod"));

        await harness.AdvanceSecondsAsync(29);
        Assert.Equal(probes + 1, harness.ShallowProbesFor("prod"));

        await harness.AdvanceSecondsAsync(1); // the re-arm after that tick uses the new value
        Assert.Equal(probes + 2, harness.ShallowProbesFor("prod"));
    }
}
