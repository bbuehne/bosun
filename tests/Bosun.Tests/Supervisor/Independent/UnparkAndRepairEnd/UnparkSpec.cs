using Bosun.Configuration;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Independent.Fakes;
using Bosun.Tests.Supervisor.Independent.RemountAfterResume;
using Bosun.Tests.Supervisor.Support;
using Microsoft.Extensions.Logging;

namespace Bosun.Tests.Supervisor.Independent.UnparkAndRepairEnd;

/// <summary>
/// Shared fixtures and assertions for the bs-uzy tests in this folder, written from ADR-015's
/// bs-uzy amendment, docs/ARCHITECTURE.md §4 rules 9 and 9a, ADR-022 decision 5 and
/// <see cref="IMountSupervisor.ConfigChangedAsync"/>'s contract, not from the implementation.
/// </summary>
/// <remarks>
/// Fakes and a fake clock only: no real rclone, drive letter, host, fragment path or wt.exe.
/// </remarks>
internal static class UnparkSpec
{
    public const string Key = "od";
    public const string Drive = "Q:";

    public static string Hostname => RemountHarness.HostnameOf(Key);

    /// <summary>The host as on-demand. <paramref name="probeIntervalSeconds"/> 0 is the fixture default
    /// (an on-demand host is not idle-polled, ADR-008).</summary>
    public static HostConfig OnDemandHost(int probeIntervalSeconds = 0) =>
        HostFixtures.OnDemand(Key, probeIntervalSeconds: probeIntervalSeconds, drive: Drive);

    /// <summary>The same host with only <c>mount.mode</c> changed to <paramref name="mode"/>.</summary>
    public static HostConfig WithMode(HostConfig host, MountMode mode) =>
        mode == MountMode.None
            ? host with { Mount = new MountConfig { Mode = MountMode.None } }
            : host with { Mount = (host.Mount.Mode == MountMode.None ? OnDemandHost().Mount : host.Mount) with { Mode = mode } };

    public static BosunConfig Config(params HostConfig[] hosts) => HostFixtures.Build(HostFixtures.Global(), hosts);

    /// <summary>
    /// Started, mounted by the user's click, then unmounted by the user's click: the parked on-demand
    /// host of ADR-015, resting <c>Ready</c>. Asserts the setup really is that.
    /// </summary>
    public static async Task<RemountHarness> ParkedOnDemandAsync(int probeIntervalSeconds = 0, params HostConfig[] others)
    {
        var h = new RemountHarness(Config([OnDemandHost(probeIntervalSeconds), .. others]));
        await h.StartAsync();
        await h.MountAsync(Key);
        Assert.Equal(MountState.Mounted, h.State(Key));
        await h.UnmountAsync(Key);
        Assert.Equal(MountState.Ready, h.State(Key));
        Assert.True(h.Snapshot(Key).UserParked, "setup: a user unmount must park the host (ADR-015)");
        Assert.Null(h.Rclone.FsAt(Drive));
        return h;
    }

    /// <summary>
    /// The one combination bs-uzy exists to make unreachable (ADR-015's bs-uzy amendment: "Mounted and
    /// parked no longer coexist on any path found"). Checked at every step a test samples.
    /// </summary>
    public static void AssertNotMountedWhileParked(HostMountSnapshot s, string when)
    {
        Assert.False(
            s.UserParked && s.State is MountState.Mounted or MountState.Mounting,
            $"{s.HostKey} is {s.State} while UserParked ({when}): a user's unmount is being undone, or the tray shows a mounted drive as parked");
    }

    /// <summary>
    /// Invariant I1 / §4 rules 1-2 on a shared call log: after <paramref name="fromIndex"/>, every
    /// <c>mount/mount</c> at <paramref name="drive"/> is preceded by a passing deep probe of
    /// <paramref name="hostKey"/> that no earlier mount consumed. Works on any harness whose doubles
    /// write <c>deep|key|Outcome</c> and <c>mount|drive</c> lines (unheld calls only).
    /// </summary>
    public static void AssertEveryMountPrecededByPassingDeepProbe(
        SupervisorCallLog log, string hostKey, string drive, int fromIndex)
    {
        string? lastDeep = null;
        foreach (var entry in log.Entries.Skip(fromIndex + 1))
        {
            if (entry.StartsWith($"deep|{hostKey}|", StringComparison.Ordinal))
            {
                lastDeep = entry;
            }
            else if (entry.StartsWith($"mount|{drive}", StringComparison.Ordinal))
            {
                Assert.True(
                    lastDeep == $"deep|{hostKey}|Success",
                    $"{entry} was not preceded by a passing deep probe of {hostKey} (last deep: {lastDeep ?? "none"}).{Environment.NewLine}{log.Dump()}");
                lastDeep = null;
            }
        }
    }

    /// <summary><c>mount/mount</c> calls (attempted, succeeded or failed) at <paramref name="drive"/>
    /// in the log after <paramref name="fromIndex"/>.</summary>
    public static int MountCallsAfter(SupervisorCallLog log, string drive, int fromIndex) =>
        log.Entries.Skip(fromIndex + 1).Count(e => e.StartsWith($"mount|{drive}", StringComparison.Ordinal));

    /// <summary>Index of the last log line, so a test can say "after this point".</summary>
    public static int Here(SupervisorCallLog log) => log.Entries.Count - 1;

    public static IReadOnlyList<string> Errors(Bosun.Tests.Supervisor.Independent.SuspendBudget.TimedLogger logger) =>
        logger.Entries.Where(e => e.Level >= LogLevel.Error).Select(e => e.Message).ToList();

    /// <summary>Every drain cause <paramref name="hostKey"/> has begun since <paramref name="since"/>
    /// transitions ago, oldest first.</summary>
    public static IReadOnlyList<DrainCause> DrainCausesAfter(IReadOnlyList<MountTransitionEntry> transitions, int since) =>
        transitions.Skip(since).Where(t => t.To == MountState.Draining).Select(t => t.DrainCause!.Value).ToList();
}
