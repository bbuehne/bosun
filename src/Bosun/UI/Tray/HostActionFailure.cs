using Bosun.Supervisor;

namespace Bosun.UI.Tray;

/// <summary>What a failed Mount or Unmount click should tell the user (bs-ngo).</summary>
/// <param name="Title">The dialog title, without the "Bosun - " prefix the dialog adds.</param>
/// <param name="Message">The text shown. States the condition; never addresses or blames the user.</param>
/// <param name="IsExpectedRefusal">True when the supervisor refused on purpose and said why
/// (<see cref="MountRequestRefusedException"/>, <see cref="MountingUnavailableException"/>); false for
/// anything else. The dispatcher logs the first quietly and the second with its exception.</param>
public sealed record HostActionFailure(string Title, string Message, bool IsExpectedRefusal)
{
    /// <summary>The dialog title for a failed mount.</summary>
    public const string MountTitle = "Mount";

    /// <summary>The dialog title for a failed unmount.</summary>
    public const string UnmountTitle = "Unmount";

    /// <summary>
    /// Turns the exception a mount or unmount request threw into the words to show (bs-ngo).
    /// </summary>
    /// <remarks>
    /// The two refusals carry a reason written to be shown -- "the system is suspended", "WinFsp is not
    /// installed" -- and it is passed on as it is. Any other exception is a fault inside Bosun, and its
    /// text is not written for a reader, so the user gets a generic sentence and the log gets the
    /// exception. Every sentence names the host and the condition. None says what the user did or
    /// should have done differently.
    /// </remarks>
    public static HostActionFailure Describe(Exception exception, string displayName, bool isMount)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrEmpty(displayName);

        var title = isMount ? MountTitle : UnmountTitle;
        var past = isMount ? "mounted" : "unmounted";

        var reason = exception switch
        {
            MountRequestRefusedException refused => refused.Reason,
            MountingUnavailableException unavailable => unavailable.Reason,
            _ => null,
        };

        if (exception is MountRequestRefusedException or MountingUnavailableException)
        {
            var trimmed = reason?.Trim().TrimEnd('.');
            var message = string.IsNullOrEmpty(trimmed)
                ? $"{displayName} was not {past}."
                : $"{displayName} was not {past}: {trimmed}.";
            return new HostActionFailure(title, message, IsExpectedRefusal: true);
        }

        return new HostActionFailure(
            title,
            $"{displayName} was not {past}, because Bosun hit an unexpected problem. The details are in the Bosun log.",
            IsExpectedRefusal: false);
    }
}
