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
        DateTimeOffset? timestamp = null,
        DrainCause? cause = null) => new()
    {
        TimestampUtc = timestamp ?? DateTimeOffset.UnixEpoch,
        HostKey = "example",
        From = from,
        To = to,
        Trigger = trigger,
        DrainCause = cause,
    };

    [Fact]
    public void IsUnexpectedUnmount_IsFalse_ForAUserRequestedUnmount()
    {
        var entry = Entry(MountState.Mounted, MountState.Draining, "user unmount", cause: DrainCause.UserUnmount);

        Assert.False(UnexpectedUnmountDetector.IsUnexpectedUnmount(entry));
    }

    [Fact]
    public void IsUnexpectedUnmount_IsFalse_ForASystemSuspend()
    {
        var entry = Entry(MountState.Mounted, MountState.Draining, "system suspend", cause: DrainCause.Suspend);

        Assert.False(UnexpectedUnmountDetector.IsUnexpectedUnmount(entry));
    }

    [Theory]
    [InlineData(DrainCause.Automatic, "3 consecutive mounted-probe failures (threshold 3)")]
    [InlineData(DrainCause.Automatic, "2 consecutive deep-probe failures (threshold 2)")]
    [InlineData(DrainCause.Automatic, "reconciliation: mount missing from listmounts")]
    [InlineData(DrainCause.Automatic, "idle timeout (120s without activity)")]
    [InlineData(DrainCause.MountFailure, "mount/mount failed: connection refused")]
    [InlineData(DrainCause.ConfigRemoved, "config change: host removed")]
    [InlineData(DrainCause.Repair, "repair: unmount all and re-probe")]
    public void IsUnexpectedUnmount_IsTrue_ForAnyDrainCauseOtherThanUserOrSuspend(DrainCause cause, string trigger)
    {
        var entry = Entry(MountState.Mounted, MountState.Draining, trigger, cause: cause);

        Assert.True(UnexpectedUnmountDetector.IsUnexpectedUnmount(entry));
    }

    [Fact]
    public void IsUnexpectedUnmount_IsTrue_WhenNoCauseWasRecorded()
    {
        // The supervisor always records one on a drain start; if one is ever missing, failing
        // toward a balloon is the safe direction for a rule that must not be silent (ADR-005).
        var entry = Entry(MountState.Mounted, MountState.Draining, "user unmount", cause: null);

        Assert.True(UnexpectedUnmountDetector.IsUnexpectedUnmount(entry));
    }

    /// <summary>
    /// bs-ynb: the classification follows the cause and ignores the wording. Both directions: a
    /// deliberate cause stays deliberate whatever its trigger says (including text that used to
    /// look unexpected), and an unexpected cause stays unexpected even if its trigger is reworded
    /// to the old deliberate prefixes.
    /// </summary>
    [Theory]
    [InlineData(DrainCause.UserUnmount, "the tray unmount button was pressed", false)]
    [InlineData(DrainCause.UserUnmount, "mount/mount failed: connection refused", false)]
    [InlineData(DrainCause.Suspend, "going to sleep", false)]
    [InlineData(DrainCause.Suspend, "2 consecutive deep-probe failures (threshold 2)", false)]
    [InlineData(DrainCause.Automatic, "user unmount", true)]
    [InlineData(DrainCause.Automatic, "USER UNMOUNT (from tray)", true)]
    [InlineData(DrainCause.MountFailure, "system suspend", true)]
    [InlineData(DrainCause.Repair, "", true)]
    public void IsUnexpectedUnmount_IgnoresTheTriggerWording(DrainCause cause, string trigger, bool expected)
    {
        var entry = Entry(MountState.Mounted, MountState.Draining, trigger, cause: cause);

        Assert.Equal(expected, UnexpectedUnmountDetector.IsUnexpectedUnmount(entry));
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
            Entry(MountState.Mounted, MountState.Draining, "probe failure", since.AddMinutes(2), DrainCause.Automatic), // new
            Entry(MountState.Mounted, MountState.Draining, "probe failure", since, DrainCause.Automatic), // not newer than `since`
            Entry(MountState.Mounted, MountState.Draining, "probe failure", since.AddMinutes(-1), DrainCause.Automatic), // old
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
            Entry(MountState.Mounted, MountState.Draining, "probe failure", since.AddMinutes(3), DrainCause.Automatic),
            Entry(MountState.Mounted, MountState.Draining, "probe failure", since.AddMinutes(2), DrainCause.Automatic),
            Entry(MountState.Mounted, MountState.Draining, "probe failure", since.AddMinutes(1), DrainCause.Automatic),
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
            Entry(MountState.Mounted, MountState.Draining, "user unmount", since.AddMinutes(1), DrainCause.UserUnmount),
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
