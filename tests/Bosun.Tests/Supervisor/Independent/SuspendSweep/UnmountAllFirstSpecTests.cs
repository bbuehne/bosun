using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Independent.SuspendPreempt;
using static Bosun.Tests.Supervisor.Independent.SuspendSweep.SweepSpec;

namespace Bosun.Tests.Supervisor.Independent.SuspendSweep;

/// <summary>
/// bs-5o4, the request phase: "The suspend moves every such host to Draining first, with no rc call. It then
/// sends every mount/unmount together -- all requests are started before any is awaited -- and only afterwards
/// verifies: one mount/listmounts covers all hosts" (docs/ARCHITECTURE.md §4 rule 5). Written from the spec.
/// </summary>
public sealed class UnmountAllFirstSpecTests
{
    private static readonly string[] Keys = ["alpha", "bravo", "charlie"];
    private static readonly string[] Drives = ["M:", "N:", "O:"];

    private static async Task<PreemptSpecHarness> ThreeMountedAsync()
    {
        var h = New(Persistent("alpha", "M:"), Persistent("bravo", "N:"), Persistent("charlie", "O:"));
        await h.StartAsync();
        foreach (var key in Keys)
        {
            Assert.Equal(MountState.Mounted, h.State(key));
        }

        return h;
    }

    /// <summary>
    /// <b>Protects:</b> the maintainer's decision itself (I8): "all requests are started before any is awaited",
    /// so "the request to the last host leaves at the same instant as the first host's".
    /// <b>Catches:</b> the pre-bs-5o4 serial sweep, or any sweep that awaits one host's <c>mount/unmount</c>
    /// before sending the next: with every call held open, the second host's request never arrives.
    /// </summary>
    [Fact]
    public async Task Every_unmount_request_is_on_the_wire_before_any_of_them_returns()
    {
        var h = await ThreeMountedAsync();
        var holds = Drives.ToDictionary(d => d, d => h.HRclone.HoldNextAt(OnCancel.Ignore, "unmount", d));
        var arrival = h.Now;

        var suspend = SuspendCaller.Call(h.Supervisor);
        var pump = h.StartPump();
        await AllArrivedAsync(holds, "every request must be started before any is awaited");

        Assert.All(holds.Values, hold => Assert.True(hold.IsPending));
        foreach (var hold in holds.Values)
        {
            hold.Release();
        }

        await h.FinishAsync(pump, "the sweep never finished after every request was released", suspend);
        foreach (var drive in Drives)
        {
            Assert.Equal([arrival], h.HRclone.Times("unmount", drive));
        }
    }

    /// <summary>
    /// <b>Protects:</b> "only afterwards verifies" and "a host the table no longer lists completes its drain":
    /// a host is not done until the verification, and the verification follows the whole request phase.
    /// Also the amendment's "Only the rc calls run concurrently; every state change ... stays on the channel".
    /// <b>Catches:</b> a per-host continuation that verifies and completes its own host's drain as soon as its
    /// own call returns -- state written from a concurrent rc completion rather than in one place on the
    /// channel, which is the shape that lets two completions race on the supervisor's state.
    /// </summary>
    [Fact]
    public async Task No_host_completes_its_drain_and_nothing_is_verified_while_another_request_is_outstanding()
    {
        var h = await ThreeMountedAsync();
        var held = h.HRclone.HoldNextAt(OnCancel.Ignore, "unmount", "O:");
        var mark = Mark(h);

        var suspend = SuspendCaller.Call(h.Supervisor);
        var pump = h.StartPump();
        await PreemptSpecHarness.ArrivedAsync(held, "charlie's unmount");

        // alpha's and bravo's calls returned (their drives are gone from rclone's table). They could run
        // because charlie's request is the LAST one sent: rcd's mount lock (bs-6lu) was free until it
        // arrived. Hold alpha's instead and these two would be queued behind it, drives still mounted.
        Assert.False(h.IsMountedInRclone("M:"));
        Assert.False(h.IsMountedInRclone("N:"));

        // ... but nothing has been verified and nobody's drain is complete yet.
        Assert.Equal(0, ListMountsSince(h, mark));
        Assert.Equal(MountState.Draining, h.State("alpha"));
        Assert.Equal(MountState.Draining, h.State("bravo"));

        held.Release();
        await h.FinishAsync(pump, "the sweep never finished", suspend);
        Assert.All(Keys, k => Assert.Equal(MountState.Disabled, h.State(k)));
    }

    /// <summary>
    /// <b>Protects:</b> "one mount/listmounts covers all hosts".
    /// <b>Catches:</b> verification done per host (N listmounts calls, each of which can hang for its own
    /// timeout against a stuck rcd, re-creating the serial delay the amendment removed).
    /// </summary>
    [Fact]
    public async Task One_listmounts_verifies_every_host_of_the_sweep()
    {
        var h = await ThreeMountedAsync();
        var mark = Mark(h);

        await h.SuspendAsync();

        Assert.Equal(1, ListMountsSince(h, mark));
        Assert.All(Keys, k => Assert.Equal(MountState.Disabled, h.State(k)));
    }

    /// <summary>
    /// <b>Protects:</b> "no unmount request lost or duplicated": each swept host gets exactly one
    /// <c>mount/unmount</c> at the arrival instant, and a drain that completed leaves nothing behind -- no
    /// per-host retry timer still re-issuing, and no escalation.
    /// <b>Catches:</b> a host sent twice (once by the sweep, once by a per-host drain step run afterwards, or
    /// by a retry timer the sweep armed and never disarmed); a forced unmount sent for a host that confirmed.
    /// </summary>
    [Fact]
    public async Task Each_host_gets_exactly_one_unmount_and_a_confirmed_sweep_leaves_no_retry_or_escalation()
    {
        var h = await ThreeMountedAsync();

        await h.SuspendAsync();
        await h.AdvanceAsync(TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(100));

        foreach (var (key, drive) in Keys.Zip(Drives))
        {
            Assert.Equal(1, h.HRclone.Count("unmount", drive));
            Assert.Empty(h.EscalationTimes(key));
            Assert.Equal(MountState.Disabled, h.State(key));
        }
    }

    /// <summary>
    /// <b>Protects:</b> the transition log (the primary triage artifact, rule 10): exactly one
    /// <c>Mounted -> Draining</c> with the suspend cause per host, then <c>Draining -> Disabled</c>.
    /// <b>Catches:</b> a sweep that logs a second Draining entry (once when it moves every host to Draining,
    /// again when the per-host step runs); a missing or wrong drain cause (which would raise the
    /// "unexpectedly unmounted" toast for an Automatic cause or park the host for UserUnmount).
    /// </summary>
    [Fact]
    public async Task The_transition_log_shows_one_suspend_drain_per_host_then_Disabled()
    {
        var h = await ThreeMountedAsync();
        var before = Keys.ToDictionary(k => k, k => h.Transitions(k).Count);

        await h.SuspendAsync();

        foreach (var key in Keys)
        {
            AssertOneSuspendDrainThenDisabled(h, key, before[key]);
            Assert.Equal(before[key] + 2, h.Transitions(key).Count);
        }
    }

    /// <summary>
    /// <b>Protects:</b> rule 5's scope: "every host in Mounted or Mounting goes to Draining". A host that is
    /// <c>Ready</c> or <c>Unreachable</c> has no drive, so the sweep sends it nothing and logs nothing for it.
    /// <b>Catches:</b> a sweep that iterates every supervised host instead of the mounted ones: spurious
    /// <c>mount/unmount</c> calls (each of which can hang 30 s against a stuck rcd and, with a WhenAll, holds
    /// the verification of every real mount) and spurious Draining entries in the log.
    /// </summary>
    [Fact]
    public async Task Hosts_with_no_mount_get_no_unmount_request_and_no_drain()
    {
        var h = New(
            Persistent("alpha", "M:"),
            OnDemand("resting", "R:"),
            Persistent("gone", "G:"));
        h.Probe.SetShallow(PreemptSpecHarness.HostnameOf("gone"), ShallowProbeOutcome.ConnectionRefused);
        await h.StartAsync();
        Assert.Equal(MountState.Ready, h.State("resting"));
        Assert.Equal(MountState.Unreachable, h.State("gone"));

        await h.SuspendAsync();

        Assert.Equal(1, h.HRclone.Count("unmount", "M:"));
        Assert.Equal(0, h.HRclone.Count("unmount", "R:"));
        Assert.Equal(0, h.HRclone.Count("unmount", "G:"));
        Assert.Empty(h.DrainCauses("resting"));
        Assert.Empty(h.DrainCauses("gone"));
    }
}
