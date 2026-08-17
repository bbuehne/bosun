namespace Bosun.UI.HostEditor;

/// <summary>One entry in the drive-letter picker: the letter, whether it can be chosen, and why
/// not when it cannot.</summary>
public sealed record DriveLetterOption
{
    /// <summary>The value written to config, e.g. <c>"T:"</c>.</summary>
    public required string Letter { get; init; }

    public required bool IsAvailable { get; init; }

    /// <summary>Shown in the dropdown. Says why a letter is unavailable rather than silently
    /// hiding it — a letter vanishing with no explanation reads as a bug.</summary>
    public required string Label { get; init; }
}

/// <summary>
/// Reports which drive letters are in use on this machine right now. Behind an interface for the
/// same reason as <see cref="IIdentityFilePicker"/> and <see cref="IColourPicker"/>: it reads real
/// machine state, so the default suite substitutes a fake and the logic stays testable.
/// </summary>
public interface IDriveLetterInspector
{
    /// <summary>Bare letters currently in use, uppercase and without a colon — e.g. <c>C</c>.</summary>
    IReadOnlySet<string> InUseLetters();
}
