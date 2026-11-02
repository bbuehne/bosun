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

    /// <summary>bs-p97: the only cause that toasts is a lost drive (probe failure, deep-probe
    /// failure, reconciliation drift), all <see cref="DrainCause.Automatic"/>.</summary>
    [Theory]
    [InlineData("3 consecutive mounted-probe failures (threshold 3)")]
    [InlineData("2 consecutive deep-probe failures (threshold 2)")]
    [InlineData("reconciliation: mount missing from listmounts")]
    [InlineData("deep probe failed immediately after resume: SSH channel confirmed dead")]
    public void IsUnexpectedUnmount_IsTrue_ForAnAutomaticDrainOfAMountedHost(string trigger)
    {
        var entry = Entry(MountState.Mounted, MountState.Draining, trigger, cause: DrainCause.Automatic);

        Assert.True(UnexpectedUnmountDetector.IsUnexpectedUnmount(entry));
    }

    /// <summary>bs-p97 / bs-z9e, one case per silent cause.</summary>
    [Theory]
    [InlineData(DrainCause.IdleTimeout, "idle timeout (120s without activity)")]
    [InlineData(DrainCause.Repair, "repair: unmount all and re-probe")]
    [InlineData(DrainCause.ConfigChanged, "config change: mount-affecting field changed")]
    [InlineData(DrainCause.ConfigChanged, "config change: mount.mode set to none")]
    [InlineData(DrainCause.ConfigRemoved, "config change: host removed")]
    [InlineData(DrainCause.MountFailure, "mount/mount failed: connection refused")]
    public void IsUnexpectedUnmount_IsFalse_ForCausesTheMaintainerDecidedAreSilent(DrainCause cause, string trigger)
    {
        var entry = Entry(MountState.Mounted, MountState.Draining, trigger, cause: cause);

        Assert.False(UnexpectedUnmountDetector.IsUnexpectedUnmount(entry));
    }

    /// <summary>A new <see cref="DrainCause"/> value must be classified here on purpose: this fails
    /// until the new value is added to one of the two lists, so the toast policy is never decided
    /// by accident.</summary>
    [Fact]
    public void Every_DrainCause_Value_Has_An_Explicit_Toast_Decision()
    {
        DrainCause[] toasts = [DrainCause.Automatic];
        DrainCause[] silent =
        [
            DrainCause.UserUnmount, DrainCause.Suspend, DrainCause.IdleTimeout, DrainCause.Repair,
            DrainCause.ConfigChanged, DrainCause.ConfigRemoved, DrainCause.MountFailure,
        ];

        Assert.Equal(
            Enum.GetValues<DrainCause>().OrderBy(c => c),
            toasts.Concat(silent).OrderBy(c => c));
        Assert.All(toasts, c => Assert.True(
            UnexpectedUnmountDetector.IsUnexpectedUnmount(Entry(MountState.Mounted, MountState.Draining, "x", cause: c))));
        Assert.All(silent, c => Assert.False(
            UnexpectedUnmountDetector.IsUnexpectedUnmount(Entry(MountState.Mounted, MountState.Draining, "x", cause: c))));
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
    [InlineData(DrainCause.IdleTimeout, "2 consecutive deep-probe failures (threshold 2)", false)]
    [InlineData(DrainCause.Repair, "reconciliation: mount missing from listmounts", false)]
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
