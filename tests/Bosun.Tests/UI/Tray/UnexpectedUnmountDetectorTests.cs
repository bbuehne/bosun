using Bosun.Supervisor;
using Bosun.UI.Tray;

namespace Bosun.Tests.UI.Tray;

/// <summary>
/// bs-ww9.5: "Balloon notification on UNEXPECTED unmount ... MUST NOT be silent" (ADR-005).
/// </summary>
public sealed class UnexpectedUnmountDetectorTests
{
    private static MountTransitionEntry Entry(
        MountState from,
        MountState to,
        string trigger,
        DateTimeOffset? timestamp = null) => new()
    {
        TimestampUtc = timestamp ?? DateTimeOffset.UnixEpoch,
        HostKey = "example",
        From = from,
        To = to,
        Trigger = trigger,
    };

    [Fact]
    public void IsUnexpectedUnmount_IsFalse_ForAUserRequestedUnmount()
    {
        var entry = Entry(MountState.Mounted, MountState.Draining, "user unmount");

        Assert.False(UnexpectedUnmountDetector.IsUnexpectedUnmount(entry));
    }

    [Fact]
    public void IsUnexpectedUnmount_IsFalse_ForASystemSuspend()
    {
        var entry = Entry(MountState.Mounted, MountState.Draining, "system suspend");

        Assert.False(UnexpectedUnmountDetector.IsUnexpectedUnmount(entry));
    }

    [Theory]
    [InlineData("3 consecutive mounted-probe failures (threshold 3)")]
    [InlineData("2 consecutive deep-probe failures (threshold 2)")]
    [InlineData("reconciliation: mount missing from listmounts")]
    [InlineData("mount/mount failed: connection refused")]
    public void IsUnexpectedUnmount_IsTrue_ForAnAutomaticDrainOfAMountedHost(string trigger)
    {
        var entry = Entry(MountState.Mounted, MountState.Draining, trigger);

        Assert.True(UnexpectedUnmountDetector.IsUnexpectedUnmount(entry));
    }

    [Fact]
    public void IsUnexpectedUnmount_IsFalse_WhenTheFromStateIsNotMounted()
    {
        // A host that never reached Mounted (e.g. a failed Mounting attempt) has nothing
        // established to lose -- not the "drive disappeared" case ADR-005 is about.
        var entry = Entry(MountState.Mounting, MountState.Draining, "mount/mount failed: timeout");

        Assert.False(UnexpectedUnmountDetector.IsUnexpectedUnmount(entry));
    }

    [Fact]
    public void IsUnexpectedUnmount_IsFalse_WhenTheToStateIsNotDraining()
    {
        var entry = Entry(MountState.Mounted, MountState.Mounted, "periodic mounted probe");

        Assert.False(UnexpectedUnmountDetector.IsUnexpectedUnmount(entry));
    }

    [Fact]
    public void IsUnexpectedUnmount_MatchesTheDeliberatePrefix_CaseInsensitively()
    {
        var entry = Entry(MountState.Mounted, MountState.Draining, "USER UNMOUNT (from tray)");

        Assert.False(UnexpectedUnmountDetector.IsUnexpectedUnmount(entry));
    }

    [Fact]
    public void IsUnexpectedUnmount_Throws_WhenEntryIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => UnexpectedUnmountDetector.IsUnexpectedUnmount(null!));
    }

    [Fact]
    public void SelectNewUnexpectedUnmounts_ReturnsOnlyEntriesNewerThanSince()
    {
        var since = DateTimeOffset.UnixEpoch.AddMinutes(5);
        var newestFirst = new[]
        {
            Entry(MountState.Mounted, MountState.Draining, "probe failure", since.AddMinutes(2)), // new
            Entry(MountState.Mounted, MountState.Draining, "probe failure", since), // not newer than `since`
            Entry(MountState.Mounted, MountState.Draining, "probe failure", since.AddMinutes(-1)), // old
        };

        var result = UnexpectedUnmountDetector.SelectNewUnexpectedUnmounts(newestFirst, since);

        var entry = Assert.Single(result);
        Assert.Equal(since.AddMinutes(2), entry.TimestampUtc);
    }

    [Fact]
    public void SelectNewUnexpectedUnmounts_ReturnsOldestFirst()
    {
        var since = DateTimeOffset.UnixEpoch;
        var newestFirst = new[]
        {
            Entry(MountState.Mounted, MountState.Draining, "probe failure", since.AddMinutes(3)),
            Entry(MountState.Mounted, MountState.Draining, "probe failure", since.AddMinutes(2)),
            Entry(MountState.Mounted, MountState.Draining, "probe failure", since.AddMinutes(1)),
        };

        var result = UnexpectedUnmountDetector.SelectNewUnexpectedUnmounts(newestFirst, since);

        Assert.Equal(
            [since.AddMinutes(1), since.AddMinutes(2), since.AddMinutes(3)],
            result.Select(e => e.TimestampUtc));
    }

    [Fact]
    public void SelectNewUnexpectedUnmounts_ExcludesDeliberateUnmounts_EvenIfNewerThanSince()
    {
        var since = DateTimeOffset.UnixEpoch;
        var newestFirst = new[]
        {
            Entry(MountState.Mounted, MountState.Draining, "user unmount", since.AddMinutes(1)),
        };

        var result = UnexpectedUnmountDetector.SelectNewUnexpectedUnmounts(newestFirst, since);

        Assert.Empty(result);
    }

    [Fact]
    public void SelectNewUnexpectedUnmounts_ReturnsEmpty_WhenHistoryIsEmpty()
    {
        var result = UnexpectedUnmountDetector.SelectNewUnexpectedUnmounts([], DateTimeOffset.UnixEpoch);

        Assert.Empty(result);
    }

    [Fact]
    public void SelectNewUnexpectedUnmounts_Throws_WhenHistoryIsNull()
    {
        Assert.Throws<ArgumentNullException>(
            () => UnexpectedUnmountDetector.SelectNewUnexpectedUnmounts(null!, DateTimeOffset.UnixEpoch));
    }
}
