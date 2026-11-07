using Bosun.Configuration;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Independent.SuspendPreempt;
using Bosun.Tests.Supervisor.Support;
using static Bosun.Tests.Supervisor.Independent.SuspendSweep.SweepSpec;

namespace Bosun.Tests.Supervisor.Independent.SuspendSweepAlreadyDraining;

/// <summary>
/// Shared setup for the bs-mua.1 tests in this folder: hosts that are ALREADY <c>Draining</c> for an ordinary
/// reason when a suspend arrives, and whose forced-unmount deadline the suspend budget shortens, have their
/// unmount re-issued in the suspend sweep's arrival batch (after the swept hosts' requests), keep their own drain
/// cause, and share the sweep's one verification and its forced-unmount phase. Written from ADR-023's
/// "Amendment (2026-11-07, bs-mua.1 ...)" and docs/ARCHITECTURE.md §4 rule 5 ("The re-issue is part of the
/// sweep's batch"), not from the implementation.
/// </summary>
/// <remarks>
/// <para>
/// Built on <see cref="PreemptSpecHarness"/> (fake clock, in-memory mount table, holdable rc calls whose fake
/// models rcd's one mount lock, bs-6lu). Every assertion about "leaves at arrival" is about the request's
/// ARRIVAL at rcd (<see cref="HoldableRclone.Calls"/>), never its execution: rcd runs <c>mount/*</c> calls one at
/// a time, so execution order is not Bosun's to promise.
/// </para>
/// <para>
/// Host keys are distinct words that are not substrings of one another, because escalation is observed through
/// the warning log line naming the host (<see cref="PreemptSpecHarness.EscalationTimes"/>). Nothing here reaches a
/// real drive letter, <c>rclone rcd</c>, WinFsp, <c>SystemEvents</c> or a host.
/// </para>
/// </remarks>
internal static class AlreadyDrainingSpec
{
    /// <summary>The ordinary drain's forced-unmount deadline, counted from the drain's start (rule 4's "the
    /// timeout"; the 10 s figure docs/ARCHITECTURE.md §4 rule 5 quotes as <c>DrainConfirmTimeout</c>).</summary>
    public const int OrdinaryDeadlineSeconds = 10;

    /// <summary>Clock step for windows where an exact instant is asserted.</summary>
    public static readonly TimeSpan Fine = TimeSpan.FromMilliseconds(10);

    public static BosunConfig Config(params HostConfig[] hosts) =>
        HostFixtures.Build(HostFixtures.Global(failuresBeforeUnmount: 3, suspendUnmountTimeoutSeconds: Budget), hosts);

    /// <summary>An on-demand host with <c>mount.idle_unmount_seconds</c> set (rule 7).</summary>
    public static HostConfig OnDemandIdle(string key, string drive, int idleSeconds) =>
        HostFixtures.OnDemand(key, idleUnmountSeconds: idleSeconds, drive: drive);

    public static PreemptSpecHarness NewHarness(params HostConfig[] hosts) => new(Config(hosts));

    /// <summary>An on-demand host the user mounts (rule 6: it does not auto-mount).</summary>
    public static async Task UserMountAsync(PreemptSpecHarness h, string hostKey)
    {
        await h.RunAsync(() => h.Supervisor.RequestMountAsync(hostKey));
        Assert.Equal(MountState.Mounted, h.State(hostKey));
    }

    /// <summary>
    /// The user unmounts <paramref name="hostKey"/> (rule 9) and its first <c>mount/unmount</c> at
    /// <paramref name="drive"/> does NOT take effect; the drive goes on the <paramref name="confirmAtCall"/>-th
    /// call. Returns the drain's start time. The host is left <c>Draining</c> and not yet escalated.
    /// </summary>
    public static async Task<DateTimeOffset> UserUnmountUnconfirmedAsync(
        PreemptSpecHarness h, string hostKey, string drive, int confirmAtCall)
    {
        h.Rclone.ConfirmUnmountAtCall(drive, confirmAtCall);
        await h.RunAsync(() => h.Supervisor.RequestUnmountAsync(hostKey));
        Assert.Equal(MountState.Draining, h.State(hostKey));
        Assert.Empty(h.EscalationTimes(hostKey));
        return DrainStart(h, hostKey);
    }

    /// <summary>A successful config reload: the store's current config becomes <paramref name="next"/>, then the
    /// supervisor's reload command runs to completion.</summary>
    public static Task ReloadAsync(PreemptSpecHarness h, BosunConfig next)
    {
        h.Store.Publish(next);
        return h.RunAsync(() => h.Supervisor.ConfigChangedAsync(next));
    }

    public static bool IsSupervised(PreemptSpecHarness h, string hostKey) =>
        h.Supervisor.GetSnapshot().Any(s => s.HostKey == hostKey);

    /// <summary>Fake-clock time of <paramref name="hostKey"/>'s latest transition into <c>Draining</c>.</summary>
    public static DateTimeOffset DrainStart(PreemptSpecHarness h, string hostKey) =>
        h.Transitions(hostKey).Last(t => t.To == MountState.Draining).TimestampUtc;

    /// <summary>The drive of every <c>mount/unmount</c> recorded since <paramref name="mark"/>, in arrival order.</summary>
    public static IReadOnlyList<string> UnmountTargetsSince(PreemptSpecHarness h, int mark) =>
        h.HRclone.Calls.Skip(mark).Where(c => c.Operation == "unmount").Select(c => c.Target ?? "?").ToList();

    /// <summary><c>mount/unmount</c> calls at <paramref name="drive"/> recorded since <paramref name="mark"/>.</summary>
    public static int UnmountsSince(PreemptSpecHarness h, int mark, string drive) =>
        h.HRclone.Calls.Skip(mark).Count(c => c.Operation == "unmount" && string.Equals(c.Target, drive, StringComparison.OrdinalIgnoreCase));

    /// <summary>Invariant I1 over the whole transition history: <c>Mounting</c> is entered only from <c>Ready</c>.</summary>
    public static void AssertMountingOnlyFromReady(PreemptSpecHarness h)
    {
        var bad = h.Supervisor.GetTransitionHistory().Where(t => t.To == MountState.Mounting && t.From != MountState.Ready).ToList();
        Assert.True(
            bad.Count == 0,
            "I1: Mounting entered from something other than Ready: " +
            string.Join(", ", bad.Select(t => $"{t.HostKey} {t.From}->{t.To}")));
    }

    /// <summary>Exactly one escalation for <paramref name="hostKey"/>, within <paramref name="tolerance"/> of
    /// <paramref name="expected"/>.</summary>
    public static void AssertEscalatedOnceNear(
        PreemptSpecHarness h, string hostKey, DateTimeOffset expected, TimeSpan tolerance, DateTimeOffset origin)
    {
        var escalations = h.EscalationTimes(hostKey);
        Assert.True(
            escalations.Count == 1 && (escalations[0] - expected).Duration() <= tolerance,
            $"{hostKey}: expected exactly one escalation at +{(expected - origin).TotalSeconds:0.###} s; saw " +
            $"[{string.Join(", ", escalations.Select(e => $"+{(e - origin).TotalSeconds:0.###} s"))}]");
    }
}
