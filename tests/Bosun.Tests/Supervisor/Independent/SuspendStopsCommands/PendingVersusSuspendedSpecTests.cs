using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Independent.SuspendPreempt;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor.Independent.SuspendStopsCommands;

/// <summary>
/// Where the stop applies and where it does not: a command DEQUEUED while a suspend is pending, a command
/// that runs while the system is already suspended, a single-host command, and suspend/resume pairs queued
/// back to back.
/// </summary>
/// <remarks>
/// Spec (ADR-023 amendment, bs-5ga.1): "It reads SuspendPending and not MountingSuspended: commands that
/// legitimately run while the system is suspended (a network change) keep probing." "A single-host command
/// (RequestRetryNowAsync, a mount or unmount click) has no next host and is unchanged." Decision 1: the
/// counter exists "because suspend, resume, suspend can be pending together".
/// </remarks>
public sealed class PendingVersusSuspendedSpecTests
{
    /// <summary>
    /// <b>Protects:</b> the check before <c>core/version</c> -- the very first unit of the re-derivation -- for
    /// a network change that is dequeued with a suspend already pending (it was queued ahead of the suspend,
    /// behind a single-host command that was still running).
    /// <b>Catches:</b> a check placed only between hosts, so a command that starts while a suspend is pending
    /// still does <c>core/version</c>, <c>mount/listmounts</c> and its first host before noticing.
    /// </summary>
    [Fact]
    public async Task A_network_change_dequeued_while_a_suspend_is_pending_does_no_work()
    {
        var h = StopSpec.NewHarness(
            ["u1", "u2"],
            HostFixtures.Persistent("p1", drive: "P:"),
            HostFixtures.Persistent("u1", drive: "U:"),
            HostFixtures.Persistent("u2", drive: "V:"),
            HostFixtures.OnDemand("r", drive: "Q:"));
        await h.StartAsync();
        Assert.Equal(MountState.Mounted, h.State("p1"));
        Assert.Equal(MountState.Unreachable, h.State("u1"));

        var retryProbe = h.HProbe.HoldNextShallow(StopSpec.Hostname("u1"));
        var retry = h.Supervisor.RequestRetryNowAsync("u1");
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(retryProbe, "retry-now's probe of u1");

        var others = new[] { "p1", "u2", "r" };
        var shallow0 = StopSpec.Counts(h, others, StopSpec.ShallowCalls);
        var deep0 = StopSpec.Counts(h, others, StopSpec.DeepCalls);
        var versions0 = h.HRclone.Count("version");

        var nc = h.Supervisor.NetworkChangedAsync();
        var suspend = SuspendCaller.Call(h.Supervisor);
        retryProbe.Release(ShallowProbeOutcome.Timeout);
        await h.FinishAsync(pump, "retry-now, the network change or the suspend never finished", retry, nc, suspend);

        Assert.True(h.HRclone.Count("version") == versions0,
            "a network change dequeued with a suspend pending still called core/version");
        foreach (var key in others)
        {
            Assert.True(StopSpec.ShallowCalls(h, key) == shallow0[key], $"{key} was shallow-probed by a network change dequeued with a suspend pending");
            Assert.True(StopSpec.DeepCalls(h, key) == deep0[key], $"{key} was deep-probed by a network change dequeued with a suspend pending");
        }

        Assert.Equal([DrainCause.Suspend], h.DrainCauses("p1"));
    }

    /// <summary>
    /// <b>Protects:</b> the stop reads "pending", not "suspended or pending". A network change that arrives
    /// while the machine is already suspended (Windows can report an address change during the suspend
    /// window) still re-derives and re-probes its idle hosts; and the mount gate (I1/I8) still keeps a host
    /// whose probe passes in <c>Ready</c>.
    /// <b>Catches:</b> the check written against <c>MountingSuspended</c> (the decision-1 gate): every command
    /// run while suspended silently does nothing, and the hosts' rows go stale until resume.
    /// </summary>
    [Fact]
    public async Task A_network_change_while_already_suspended_still_probes_but_mounts_nothing()
    {
        var h = StopSpec.NewHarness(
            ["u"],
            HostFixtures.Persistent("p1", drive: "P:"),
            HostFixtures.Persistent("u", drive: "U:"),
            HostFixtures.OnDemand("r", drive: "Q:"));
        await h.StartAsync();
        await h.SuspendAsync();
        Assert.Equal(MountState.Disabled, h.State("p1"));
        Assert.Equal(MountState.Unreachable, h.State("u"));
        Assert.Equal(MountState.Ready, h.State("r"));

        var all = new[] { "p1", "u", "r" };
        var shallow0 = StopSpec.Counts(h, all, StopSpec.ShallowCalls);
        var versions0 = h.HRclone.Count("version");
        var mounts0 = h.TotalMountCalls;
        var mounting0 = StopSpec.MountingEntries(h);

        // u's host is back: its probe passes, so it reaches Ready -- and, being persistent, would mount
        // if anything let it.
        h.Probe.SetShallow(StopSpec.Hostname("u"), ShallowProbeOutcome.Success);
        await h.RunAsync(() => h.Supervisor.NetworkChangedAsync());

        Assert.True(h.HRclone.Count("version") > versions0, "a network change while suspended skipped core/version");
        Assert.True(StopSpec.ShallowCalls(h, "u") > shallow0["u"], "a network change while suspended did not re-probe the Unreachable host");
        Assert.True(StopSpec.ShallowCalls(h, "r") > shallow0["r"], "a network change while suspended did not re-probe the Ready host");
        Assert.Equal(shallow0["p1"], StopSpec.ShallowCalls(h, "p1"));
        Assert.Equal(MountState.Ready, h.State("u"));
        Assert.Equal(MountState.Disabled, h.State("p1"));
        Assert.Equal(mounts0, h.TotalMountCalls);
        Assert.Equal(mounting0, StopSpec.MountingEntries(h));

        await h.ResumeAsync();
        Assert.Equal(MountState.Mounted, h.State("p1"));
        Assert.Equal(MountState.Mounted, h.State("u"));
    }

    /// <summary>
    /// <b>Protects:</b> "a single-host command ... is unchanged": "retry now" dequeued while a suspend is
    /// pending still probes its host; the decision-1 gate alone keeps the host from mounting.
    /// <b>Catches:</b> the between-hosts check copied into single-host commands, so the user's explicit
    /// "retry now" is silently swallowed; and, the other way, a retry that mounts with a suspend pending (I8).
    /// </summary>
    [Fact]
    public async Task Retry_now_dequeued_while_a_suspend_is_pending_still_probes_and_does_not_mount()
    {
        var h = StopSpec.NewHarness(
            ["u1", "u2"],
            HostFixtures.Persistent("u1", drive: "U:"),
            HostFixtures.Persistent("u2", drive: "V:"));
        await h.StartAsync();

        var first = h.HProbe.HoldNextShallow(StopSpec.Hostname("u1"));
        var retry1 = h.Supervisor.RequestRetryNowAsync("u1");
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(first, "retry-now's probe of u1");

        var u2Before = StopSpec.ShallowCalls(h, "u2");
        var mounts0 = h.TotalMountCalls;
        var mounting0 = StopSpec.MountingEntries(h);
        h.Probe.SetShallow(StopSpec.Hostname("u2"), ShallowProbeOutcome.Success);

        var retry2 = h.Supervisor.RequestRetryNowAsync("u2");
        var suspend = SuspendCaller.Call(h.Supervisor);
        first.Release(ShallowProbeOutcome.Timeout);
        await h.FinishAsync(pump, "a retry-now or the suspend never finished", retry1, retry2, suspend);

        Assert.True(StopSpec.ShallowCalls(h, "u2") == u2Before + 1,
            "retry-now (a single-host command) did not probe its host because a suspend was pending");
        Assert.Equal(MountState.Ready, h.State("u2"));
        Assert.Equal(mounts0, h.TotalMountCalls);
        Assert.Equal(mounting0, StopSpec.MountingEntries(h));

        await h.ResumeAsync();
        Assert.Equal(MountState.Mounted, h.State("u2"));
    }

    /// <summary>
    /// <b>Protects:</b> the stop against a counter with more than one suspend in it. Resume #1 is in flight;
    /// suspend #2, resume #2 and suspend #3 are queued behind it. Resume #1 stops after its host in flight;
    /// resume #2 runs while suspend #3 is pending and so does nothing at all (not even <c>core/version</c>);
    /// resume #4 picks up every host.
    /// <b>Catches:</b> a stop that reads a flag cleared by suspend #2's dequeue (resume #2 then probes and,
    /// through the persistent auto-mount path, a drive is created just before the second sleep, which is what
    /// I8 forbids); hosts skipped twice that the final resume then misses.
    /// </summary>
    [Fact]
    public async Task Resume_suspend_resume_suspend_queued_behind_a_resume_in_flight_does_nothing_until_the_last_resume()
    {
        var keys = new[] { "u1", "u2", "u3", "r" };
        var h = StopSpec.NewHarness(
            ["u1", "u2", "u3"],
            HostFixtures.Persistent("u1", drive: "U:"),
            HostFixtures.Persistent("u2", drive: "V:"),
            HostFixtures.Persistent("u3", drive: "W:"),
            HostFixtures.OnDemand("r", drive: "Q:"));
        await h.StartAsync();
        await h.SuspendAsync();

        // Every host is reachable again from here on (each held probe is released with Success).
        foreach (var key in new[] { "u1", "u2", "u3" })
        {
            h.Probe.SetShallow(StopSpec.Hostname(key), ShallowProbeOutcome.Success);
        }

        var shallow0 = StopSpec.Counts(h, keys, StopSpec.ShallowCalls);
        var versions0 = h.HRclone.Count("version");
        var mounts0 = h.TotalMountCalls;
        var mounting0 = StopSpec.MountingEntries(h);

        var holds = HoldSet.Shallow(h, keys);
        var r1 = h.Supervisor.ResumeAsync();
        var pump = h.StartPump();
        var inFlight = await holds.InFlightAtArrivalAsync(1, _ => null);

        var s2 = SuspendCaller.Call(h.Supervisor);
        var r2 = h.Supervisor.ResumeAsync();
        var s3 = SuspendCaller.Call(h.Supervisor);
        Assert.False(holds[inFlight].CancellationRequested, "the suspend cancelled resume #1's probe");
        holds.ReleaseNotArrived(_ => ShallowProbeOutcome.Success);
        holds[inFlight].Release(ShallowProbeOutcome.Success);
        await h.FinishAsync(pump, "the resume/suspend sequence never finished", r1, s2, r2, s3);

        Assert.True(h.HRclone.Count("version") == versions0 + 1,
            $"expected only resume #1 to call core/version; resume #2 ran with suspend #3 pending (calls: {h.HRclone.Count("version") - versions0})");
        Assert.Equal(shallow0[inFlight] + 1, StopSpec.ShallowCalls(h, inFlight));
        foreach (var key in keys.Where(k => k != inFlight))
        {
            Assert.True(StopSpec.ShallowCalls(h, key) == shallow0[key],
                $"{key} was probed by a resume that ran with a suspend pending");
        }

        Assert.Equal(mounts0, h.TotalMountCalls);
        Assert.Equal(mounting0, StopSpec.MountingEntries(h));

        await h.ResumeAsync();
        foreach (var key in keys)
        {
            Assert.True(StopSpec.ShallowCalls(h, key) > shallow0[key] + (key == inFlight ? 1 : 0),
                $"the last resume did not re-probe {key}");
        }

        Assert.Equal(MountState.Mounted, h.State("u1"));
        Assert.Equal(MountState.Mounted, h.State("u2"));
        Assert.Equal(MountState.Mounted, h.State("u3"));
        Assert.Equal(MountState.Ready, h.State("r"));
    }
}
