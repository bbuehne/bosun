using Bosun.Configuration;
using Bosun.Supervisor;
using Bosun.Tests.Supervisor.Support;

namespace Bosun.Tests.Supervisor.Independent.RemountAfterResume;

/// <summary>Shared fixtures and assertions for the ADR-022 tests in this folder.</summary>
internal static class RemountSpec
{
    public const string OnDemandKey = "od";
    public const string OnDemandDrive = "Q:";

    public static string OnDemandHostname => RemountHarness.HostnameOf(OnDemandKey);

    public static BosunConfig OneOnDemand(int idleUnmountSeconds = 0) => HostFixtures.Build(
        HostFixtures.Global(),
        HostFixtures.OnDemand(OnDemandKey, drive: OnDemandDrive, idleUnmountSeconds: idleUnmountSeconds));

    /// <summary>Started, then mounted by the user's click; asserts the setup really is Mounted.</summary>
    public static async Task<RemountHarness> UserMountedOnDemandAsync(int idleUnmountSeconds = 0)
    {
        var h = new RemountHarness(OneOnDemand(idleUnmountSeconds));
        await h.StartAsync();
        Assert.Equal(MountState.Ready, h.State(OnDemandKey));
        await h.MountAsync(OnDemandKey);
        Assert.Equal(MountState.Mounted, h.State(OnDemandKey));
        Assert.Equal(1, h.Rclone.MountCount(OnDemandDrive));
        return h;
    }

    /// <summary>
    /// Invariant I1 / §4 rules 1-2 on the shared call log: after <paramref name="fromIndex"/>, every
    /// <c>mount/mount</c> at <paramref name="drive"/> is immediately preceded (among this host's
    /// deep probes) by a PASSING deep probe of <paramref name="hostKey"/> that no earlier mount
    /// already consumed.
    /// </summary>
    public static void AssertEveryMountPrecededByPassingDeepProbe(
        RemountHarness h, string hostKey, string drive, int fromIndex)
    {
        string? lastDeep = null;
        foreach (var entry in h.LogAfter(fromIndex))
        {
            if (entry.StartsWith($"deep|{hostKey}|", StringComparison.Ordinal))
            {
                lastDeep = entry;
            }
            else if (entry.StartsWith($"mount|{drive}", StringComparison.Ordinal))
            {
                Assert.True(
                    lastDeep == $"deep|{hostKey}|Success",
                    $"{entry} was not preceded by a passing deep probe of {hostKey} (last deep: {lastDeep ?? "none"}).{Environment.NewLine}{h.Log.Dump()}");
                lastDeep = null;
            }
        }
    }

    /// <summary>Mount calls at <paramref name="drive"/> in the call log after <paramref name="fromIndex"/>.</summary>
    public static int MountsAfter(RemountHarness h, string drive, int fromIndex) =>
        h.LogAfter(fromIndex).Count(e => e.StartsWith($"mount|{drive}", StringComparison.Ordinal));

    public static (MountState From, MountState To)[] Pairs(IEnumerable<MountTransitionEntry> entries) =>
        entries.Select(t => (t.From, t.To)).ToArray();
}
