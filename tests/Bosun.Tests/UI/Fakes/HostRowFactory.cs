using Bosun.Configuration;
using Bosun.Status;
using Bosun.Supervisor;

namespace Bosun.Tests.UI.Fakes;

/// <summary>Builds <see cref="HostStatusRow"/>s for UI tests, with the fields a row of each category
/// would really carry (the status text is the one <see cref="StatusDerivation"/> writes, so the tests
/// read like the screen).</summary>
internal static class HostRowFactory
{
    public static HostStatusRow Row(
        StatusCategory category,
        string key = "mercury",
        string displayName = "Mercury",
        string? drive = "P:",
        MountState state = MountState.Ready,
        MountMode mode = MountMode.Persistent,
        string statusText = "P: is something",
        bool userParked = false,
        int mountFailures = 0) => new()
        {
            HostKey = key,
            DisplayName = displayName,
            State = state,
            Mode = mode,
            Drive = drive,
            UserParked = userParked,
            SessionCount = 0,
            Category = category,
            StatusText = statusText,
            ConsecutiveMountFailures = mountFailures,
        };

    /// <summary>A persistent host whose mount has failed <paramref name="times"/> times (an Error).</summary>
    public static HostStatusRow MountFailing(string key = "mercury", string drive = "P:", int times = 3, string lastError = "boom") =>
        Row(
            StatusCategory.MountFailing,
            key,
            displayName: char.ToUpperInvariant(key[0]) + key[1..],
            drive,
            statusText: $"{drive} is not mounted -- mount failed {times} times; last error: {lastError}",
            mountFailures: times);

    public static HostStatusRow Unreachable(string key = "venus", string drive = "Q:") =>
        Row(
            StatusCategory.Unreachable,
            key,
            displayName: char.ToUpperInvariant(key[0]) + key[1..],
            drive,
            state: MountState.Unreachable,
            statusText: $"{drive} is not mounted -- host unreachable after 3 consecutive failed probes");

    public static HostStatusRow Parked(string key = "mars", string drive = "R:") =>
        Row(
            StatusCategory.Parked,
            key,
            displayName: char.ToUpperInvariant(key[0]) + key[1..],
            drive,
            state: MountState.Unreachable,
            statusText: $"{drive} is parked -- you unmounted it; mount it again from the tray when you're ready",
            userParked: true);

    public static HostStatusRow Mounted(string key = "earth", string drive = "S:") =>
        Row(
            StatusCategory.Healthy,
            key,
            displayName: char.ToUpperInvariant(key[0]) + key[1..],
            drive,
            state: MountState.Mounted,
            statusText: $"{drive} is mounted");
}
