using Bosun.Configuration;
using Bosun.Probe;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Independent.SuspendPreempt;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor.Independent.SuspendStopsCommands;

/// <summary>
/// Shared setup for the bs-5ga.1 tests in this folder, written from ADR-023's amendment of 2026-11-06
/// ("multi-host commands stop between hosts while a suspend is pending") and docs/ARCHITECTURE.md §4
/// rule 5, not from the implementation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why every host is held, not one.</b> The spec fixes the ORDER of a command's phases
/// (<c>core/version</c>, <c>mount/listmounts</c>, then per <c>Mounted</c> host shallow then deep, then
/// the <c>Ready</c>/<c>Unreachable</c> loop) but never the order of hosts inside a phase. So a test
/// cannot say "hold host A's probe" and know A is first. <see cref="HoldSet"/> arms a hold on every
/// host of the phase and treats whichever arrives as "the call in flight"; the assertions are then
/// quantified over "the host that was in flight" and "every other host", which is order-free.
/// </para>
/// <para>
/// <b>Skipped holds are released before the in-flight one.</b> Once the suspend is pending, every
/// hold that has not been reached is released in advance with a chosen outcome. If the command
/// wrongly carries on, those calls then complete at once (and are counted) instead of hanging the
/// run, so a regression shows up as a readable call-count assertion, not a guard timeout.
/// </para>
/// <para>
/// Fakes and a fake clock only: no real rclone, drive letter, host, fragment path or wt.exe.
/// </para>
/// </remarks>
internal static class StopSpec
{
    /// <summary>bs-5ga.2 (maintainer decision A): a repair a pending suspend stopped must tell its caller,
    /// not complete as if it had finished. Awaits the already-finished repair task and checks it faulted with
    /// the stop report, whose reason is the one the user is shown.</summary>
    public static async Task AssertRepairReportedStopAsync(Task repair)
    {
        var stopped = await Assert.ThrowsAsync<RepairStoppedException>(() => repair);
        Assert.Equal("the computer is going to sleep", stopped.Reason);
    }

    public static PreemptSpecHarness NewHarness(params HostConfig[] hosts) => NewHarness([], hosts);

    /// <summary>A harness whose <paramref name="unreachable"/> hosts' shallow probes always fail (until a
    /// test re-scripts them), so they rest <c>Unreachable</c> after start.</summary>
    public static PreemptSpecHarness NewHarness(string[] unreachable, params HostConfig[] hosts)
    {
        var h = new PreemptSpecHarness(HostFixtures.Build(
            HostFixtures.Global(failuresBeforeUnmount: 3, suspendUnmountTimeoutSeconds: 3), hosts));
        foreach (var key in unreachable)
        {
            h.Probe.SetShallow(Hostname(key), ShallowProbeOutcome.Timeout);
        }

        return h;
    }

    public static string Hostname(string key) => PreemptSpecHarness.HostnameOf(key);

    public static int ShallowCalls(PreemptSpecHarness h, string key) => h.HProbe.ShallowCalls(Hostname(key));

    public static int DeepCalls(PreemptSpecHarness h, string key) => h.HProbe.DeepCalls(key);

    /// <summary>Every host's <c>X -&gt; Mounting</c> entry so far. A test reads it before the suspend is
    /// called and after it has run: nothing may enter <c>Mounting</c> in between (I1, I8, ADR-023
    /// decision 1).</summary>
    public static int MountingEntries(PreemptSpecHarness h) =>
        h.Supervisor.GetTransitionHistory().Count(t => t.To == MountState.Mounting);

    public static Dictionary<string, int> Counts(PreemptSpecHarness h, IEnumerable<string> keys, Func<PreemptSpecHarness, string, int> count) =>
        keys.ToDictionary(k => k, k => count(h, k));

    /// <summary>The fields of a host's snapshot that "left exactly as it was" covers.</summary>
    public static string Fingerprint(HostMountSnapshot s) =>
        $"state={s.State} enabled={s.AdministrativelyEnabled} mountedFailures={s.ConsecutiveMountedFailures} " +
        $"deepFailures={s.ConsecutiveDeepProbeFailures} idleFailures={s.ConsecutiveIdleFailures} " +
        $"mountFailures={s.ConsecutiveMountFailures} parked={s.UserParked} " +
        $"lastTransition={s.LastTransitionUtc:O}/{s.LastTransitionTrigger}";
}

/// <summary>
/// A hold armed on the next call of one kind for each of several hosts. See <see cref="StopSpec"/>'s
/// remarks for why every host of a phase is held.
/// </summary>
internal sealed class HoldSet
{
    private readonly Dictionary<string, Hold> holds;
    private readonly List<string> arrivalOrder = [];

    private HoldSet(Dictionary<string, Hold> holds) => this.holds = holds;

    public Hold this[string key] => holds[key];

    public IReadOnlyList<string> Arrived => arrivalOrder;

    public IEnumerable<string> NotArrived => holds.Keys.Where(k => !holds[k].HasArrived);

    public static HoldSet Shallow(PreemptSpecHarness h, IEnumerable<string> keys, OnCancel onCancel = OnCancel.LikeProduction) =>
        new(keys.ToDictionary(k => k, k => h.HProbe.HoldNextShallow(StopSpec.Hostname(k), onCancel)));

    public static HoldSet Deep(PreemptSpecHarness h, IEnumerable<string> keys, OnCancel onCancel = OnCancel.LikeProduction) =>
        new(keys.ToDictionary(k => k, k => h.HProbe.HoldNextDeep(k, onCancel)));

    /// <summary>Holds the next <c>mount/unmount</c> at each host's drive. <c>OnCancel.Ignore</c>: an
    /// unmount in flight is not cancelled by anything in this folder, and an Ignore hold keeps rcd's
    /// mount lock (bs-6lu) until it is released, which is the faithful case.</summary>
    public static HoldSet Unmount(PreemptSpecHarness h, IReadOnlyDictionary<string, string> driveByKey) =>
        new(driveByKey.ToDictionary(kv => kv.Key, kv => h.HRclone.HoldNextAt(OnCancel.Ignore, "unmount", kv.Value)));

    /// <summary>
    /// Waits for the <paramref name="n"/>-th host (1-based) to reach its held call. Each earlier arrival
    /// is released with <paramref name="releaseEarlier"/>'s value for that host, so the command moves on
    /// to its next host. Returns the host whose call is now in flight (still held).
    /// </summary>
    public async Task<string> InFlightAtArrivalAsync(int n, Func<string, object?> releaseEarlier)
    {
        while (true)
        {
            var waiting = holds.Where(kv => !arrivalOrder.Contains(kv.Key)).ToList();
            Assert.True(waiting.Count > 0, $"setup: only {arrivalOrder.Count} host(s) reached their held call, wanted {n}");
            try
            {
                await Task.WhenAny(waiting.Select(kv => kv.Value.WhenArrived)).WaitAsync(PreemptSpecHarness.Guard);
            }
            catch (TimeoutException)
            {
                Assert.Fail($"setup: the command reached only {arrivalOrder.Count} of the {n} held call(s) it was expected to reach");
            }

            var arrived = waiting.Where(kv => kv.Value.HasArrived).Select(kv => kv.Key).ToList();
            Assert.True(arrived.Count == 1, $"setup: more than one held call in flight at once ({string.Join(", ", arrived)}); the channel is serial");
            arrivalOrder.Add(arrived[0]);

            if (arrivalOrder.Count == n)
            {
                return arrived[0];
            }

            holds[arrived[0]].Release(releaseEarlier(arrived[0]));
        }
    }

    /// <summary>Releases, in advance, every hold no call has reached yet. A call that reaches one
    /// afterwards completes at once with <paramref name="value"/>'s value (and is still counted).</summary>
    public void ReleaseNotArrived(Func<string, object?> value)
    {
        foreach (var key in NotArrived.ToList())
        {
            holds[key].Release(value(key));
        }
    }
}
