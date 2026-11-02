using Bosun.Configuration;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Independent.SuspendBudget;
using Bosun.Tests.Supervisor.Independent.SuspendPreempt;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor.Independent.SuspendSweep;

/// <summary>
/// Shared setup and observations for the bs-5o4 suspend-sweep tests in this folder ("on suspend, send every
/// <c>mount/unmount</c> first, then verify them"), written from ADR-023's amendment of 2026-11-02 and
/// docs/ARCHITECTURE.md §4 rules 4 and 5, not from the implementation.
/// </summary>
/// <remarks>
/// <para>
/// Built on <see cref="PreemptSpecHarness"/>: a fake clock, the in-memory <c>RcloneDouble</c> mount table
/// (what Explorer would show), and holds that keep an rc call open until the test releases or fails it.
/// Nothing here reaches a real drive letter, <c>rclone rcd</c>, WinFsp, <c>SystemEvents</c> or a host.
/// </para>
/// <para>
/// Host keys are distinct words that are not substrings of one another, because escalation is observed
/// through the warning log line that names the host (see <see cref="PreemptSpecHarness.EscalationTimes"/>).
/// </para>
/// </remarks>
internal static class SweepSpec
{
    /// <summary><c>global.suspend_unmount_timeout_seconds</c> in every test here.</summary>
    public const int Budget = 3;

    /// <summary>Clock step for windows where an instant is asserted.</summary>
    public static readonly TimeSpan Step = TimeSpan.FromMilliseconds(10);

    public static PreemptSpecHarness New(params HostConfig[] hosts) =>
        new(HostFixtures.Build(
            HostFixtures.Global(failuresBeforeUnmount: 3, suspendUnmountTimeoutSeconds: Budget), hosts));

    public static HostConfig Persistent(string key, string drive) =>
        HostFixtures.Persistent(key, probeIntervalSeconds: 60, drive: drive);

    public static HostConfig OnDemand(string key, string drive) => HostFixtures.OnDemand(key, drive: drive);

    /// <summary><c>mount/unmount</c> calls at <paramref name="drive"/> made exactly at <paramref name="at"/>.</summary>
    public static int UnmountsAt(PreemptSpecHarness h, string drive, DateTimeOffset at) =>
        h.HRclone.Times("unmount", drive).Count(t => t == at);

    /// <summary><c>mount/unmount</c> calls at <paramref name="drive"/> in [<paramref name="from"/>, <paramref name="to"/>).</summary>
    public static int UnmountsBetween(PreemptSpecHarness h, string drive, DateTimeOffset from, DateTimeOffset to) =>
        h.HRclone.Times("unmount", drive).Count(t => t >= from && t < to);

    /// <summary>A position in the rc call record. Calls are counted from a mark, not by timestamp, because
    /// startup's own <c>mount/listmounts</c> (adoption) happens at the same fake instant as the suspend.</summary>
    public static int Mark(PreemptSpecHarness h) => h.HRclone.Calls.Count;

    /// <summary><c>mount/listmounts</c> calls made since <paramref name="mark"/>.</summary>
    public static int ListMountsSince(PreemptSpecHarness h, int mark) =>
        h.HRclone.Calls.Skip(mark).Count(c => c.Operation == "listmounts");

    /// <summary>Loop faults: an exception that reached the channel's catch-all. (An Error line that the
    /// supervisor writes on purpose, such as "Forced unmount ... STILL not confirmed", is not one.)</summary>
    public static IReadOnlyList<TimedLogEntry> LoopFaults(PreemptSpecHarness h) =>
        h.Errors.Where(e => e.Message.Contains("Unhandled", StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Waits (guarded) for every hold to be reached; names the first one that never was.</summary>
    public static async Task AllArrivedAsync(IReadOnlyDictionary<string, Hold> holds, string why)
    {
        foreach (var (drive, hold) in holds)
        {
            await PreemptSpecHarness.ArrivedAsync(hold, $"mount/unmount at {drive} -- {why}");
        }
    }

    /// <summary>
    /// The transition-log claim for a host the suspend sweep drained: exactly one transition into
    /// <c>Draining</c> from index <paramref name="fromIndex"/> on, it is <c>Mounted -> Draining</c> with
    /// <see cref="DrainCause.Suspend"/>, and the transition right after it is <c>Draining -> Disabled</c>.
    /// </summary>
    public static void AssertOneSuspendDrainThenDisabled(PreemptSpecHarness h, string hostKey, int fromIndex = 0)
    {
        var all = h.Transitions(hostKey);
        var tail = all.Skip(fromIndex).ToList();
        var intoDraining = tail.Where(t => t.To == MountState.Draining).ToList();
        Assert.True(
            intoDraining.Count == 1,
            $"{hostKey}: expected exactly one transition into Draining, saw {intoDraining.Count}: {Describe(tail)}");
        var drain = intoDraining[0];
        Assert.True(
            drain.From == MountState.Mounted && drain.DrainCause == DrainCause.Suspend,
            $"{hostKey}: expected Mounted -> Draining (Suspend), saw {drain.From} -> Draining ({drain.DrainCause})");
        var after = tail.SkipWhile(t => !ReferenceEquals(t, drain)).Skip(1).FirstOrDefault();
        Assert.True(
            after is { From: MountState.Draining, To: MountState.Disabled },
            $"{hostKey}: expected Draining -> Disabled next, saw {(after is null ? "nothing" : $"{after.From} -> {after.To}")}");
    }

    /// <summary>Every host's transitions chain: each one leaves the state the previous one entered.</summary>
    public static void AssertChainsAreConsistent(PreemptSpecHarness h, IEnumerable<string> hostKeys)
    {
        foreach (var key in hostKeys)
        {
            var list = h.Transitions(key);
            for (var i = 1; i < list.Count; i++)
            {
                Assert.True(
                    list[i].From == list[i - 1].To,
                    $"{key}: transition {i} leaves {list[i].From} but the previous one entered {list[i - 1].To}: {Describe(list)}");
            }
        }
    }

    public static string Describe(IEnumerable<MountTransitionEntry> transitions) =>
        string.Join(", ", transitions.Select(t => $"{t.From}->{t.To}{(t.DrainCause is { } c ? $"({c})" : string.Empty)}"));
}
