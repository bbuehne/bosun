namespace Bosun.Remote;

/// <summary>Why <see cref="DevelopmentAreaGuard"/> refused a path.</summary>
public enum PathRejection
{
    /// <summary>Null, empty, or only whitespace.</summary>
    Empty,

    /// <summary>The path starts with a separator or is otherwise rooted (absolute).</summary>
    Rooted,

    /// <summary>A drive specifier such as <c>D:</c> or <c>D:foo</c>, which can name another drive.</summary>
    DriveSpecifier,

    /// <summary>A UNC path or a <c>\\?\</c> / <c>\\.\</c> device path.</summary>
    UncOrDevicePath,

    /// <summary>A <c>:</c> inside a segment: an NTFS alternate data stream.</summary>
    AlternateDataStream,

    /// <summary>A control character, wildcard, or other character Windows does not allow in a file name.</summary>
    InvalidCharacter,

    /// <summary>An empty segment, as in <c>a\\b</c> or a trailing separator.</summary>
    EmptySegment,

    /// <summary>A <c>.</c> or <c>..</c> segment.</summary>
    TraversalSegment,

    /// <summary>A segment ending in a dot or space, which Windows silently strips.</summary>
    TrailingDotOrSpace,

    /// <summary>A reserved device name (CON, NUL, COM1 ...), with or without an extension.</summary>
    ReservedDeviceName,

    /// <summary>A segment longer than the 255 characters a file system component can hold.</summary>
    SegmentTooLong,

    /// <summary>A single project name was required but the input had several segments.</summary>
    NotASingleSegment,

    /// <summary>The canonical path is not strictly inside the area. A backstop; the checks above should catch it first.</summary>
    EscapesArea,

    /// <summary>A junction, symlink, or other reparse point exists on the path below the area root.</summary>
    ReparsePoint,

    /// <summary>The file system could not be inspected (for example access denied). The guard fails closed.</summary>
    Unverifiable,

    /// <summary>
    /// The development area root is not an existing directory at the time of the call (missing, a
    /// file, or unreachable). The guard fails closed.
    /// </summary>
    AreaUnavailable,

    /// <summary>
    /// A project name that is a single, otherwise valid segment but breaks the project-name rule
    /// (ASCII letters, digits, space, <c>.</c>, <c>_</c>, <c>-</c>; starts with a letter or digit;
    /// 1 to 64 characters). ADR-024 addendum, bs-4xw.17.
    /// </summary>
    NameNotAllowed,
}

/// <summary>
/// The outcome of resolving a caller-supplied path against the development area: either a
/// canonical full path inside the area, or a typed rejection. A rejection never contains the area's
/// own path, so it is safe to relay to the remote caller.
/// </summary>
public sealed record PathResolution
{
    private PathResolution(string? fullPath, PathRejection? rejection, string? detail)
    {
        FullPath = fullPath;
        Rejection = rejection;
        Detail = detail;
    }

    /// <summary>True when <see cref="FullPath"/> is set.</summary>
    public bool IsAllowed => FullPath is not null;

    /// <summary>The canonical absolute path, strictly inside the area. Null when rejected.</summary>
    public string? FullPath { get; }

    /// <summary>Why the path was refused. Null when allowed.</summary>
    public PathRejection? Rejection { get; }

    /// <summary>A short explanation for the caller. Null when allowed.</summary>
    public string? Detail { get; }

    internal static PathResolution Allowed(string fullPath) => new(fullPath, null, null);

    internal static PathResolution Rejected(PathRejection reason, string detail) => new(null, reason, detail);
}
