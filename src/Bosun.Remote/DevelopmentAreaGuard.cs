namespace Bosun.Remote;

/// <summary>
/// The security boundary for ADR-024's remote tools: every path a tool takes from the caller is
/// resolved here, and a result is either a canonical path strictly inside the development area or a
/// typed rejection.
///
/// <para><b>Approach.</b> The caller's input is a relative path made of plain names. It is split
/// into segments and each segment is validated on its own (no <c>..</c>, no <c>:</c>, no device
/// names, no trailing dot or space, ...) before anything is combined, so an escape has to survive
/// segment validation, not only a final prefix comparison. The combined path is then canonicalised and
/// checked against the root as a defence in depth, and the file system is walked to refuse reparse
/// points.</para>
///
/// <para><b>Reparse-point policy (conservative).</b> Every existing component below the area root,
/// including the target itself, is refused if it is a reparse point of any kind: junction, directory
/// or file symlink, or anything else. The guard does not try to work out where a link leads. That is
/// simpler and cannot be fooled by link chains or relative link targets. The cost is that a
/// project folder that is deliberately a junction or symlink is refused, and so is a OneDrive
/// placeholder (cloud files are reparse points) if the area sits inside a synced folder. The area root
/// itself, and anything above it, is trusted configuration and is not inspected: the maintainer may
/// legitimately have made the area a junction. Components that do not exist yet (create-project)
/// cannot be links and are accepted.</para>
///
/// <para><b>Not covered.</b> A time-of-check to time-of-use gap remains: something running as the
/// same user could create a junction after the check and before the caller's use. Closing it would
/// need handle-based opens, which is not available to callers that hand a path to another program
/// (Claude Code). It requires write access to the area, which the caller of the tools does not have
/// by any other route. Hard links to files outside the area are likewise not detected. Windows 8.3
/// short names are not special-cased: they can only alias entries that are already inside the
/// area.</para>
///
/// <para>Comparisons are case-insensitive, as on Windows. Instances are immutable and thread-safe.
/// The type exposes no member that executes anything.</para>
/// </summary>
public sealed class DevelopmentAreaGuard
{
    private const int MaxSegmentLength = 255;

    private static readonly char[] Separators = ['\\', '/'];

    // Devices that Windows resolves by base name regardless of extension ("CON.txt" is CON).
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "COM\u00B9", "COM\u00B2", "COM\u00B3", "LPT\u00B9", "LPT\u00B2", "LPT\u00B3",
    };

    /// <summary>
    /// Creates a guard for <paramref name="developmentAreaRoot"/>, an already-expanded absolute path
    /// (the caller expands <c>%VAR%</c> tokens; ADR-024 "Configuration"). Trailing separators and
    /// <c>.</c>/<c>..</c> in the root are normalised. The root need not exist.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The root is empty, relative, UNC or a device path, has a <c>:</c> beyond the drive letter, or
    /// is a bare drive root (an area that large defeats the guard).
    /// </exception>
    public DevelopmentAreaGuard(string developmentAreaRoot)
    {
        if (string.IsNullOrWhiteSpace(developmentAreaRoot))
        {
            throw new ArgumentException("The development area root is empty.", nameof(developmentAreaRoot));
        }

        // Reject UNC and device roots on the raw text: GetFullPath keeps "\\?\C:\x" as it is.
        if (developmentAreaRoot.StartsWith(@"\\", StringComparison.Ordinal)
            || developmentAreaRoot.StartsWith("//", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The development area root must be a drive-letter path, not a UNC or device path.",
                nameof(developmentAreaRoot));
        }

        if (developmentAreaRoot.Length < 3
            || !char.IsAsciiLetter(developmentAreaRoot[0])
            || developmentAreaRoot[1] != ':'
            || !Array.Exists(Separators, s => s == developmentAreaRoot[2]))
        {
            throw new ArgumentException(
                "The development area root must be a fully qualified drive-letter path such as C:\\Dev.",
                nameof(developmentAreaRoot));
        }

        string full;
        try
        {
            full = Path.GetFullPath(developmentAreaRoot);
        }
        catch (Exception ex) when (ex is PathTooLongException or NotSupportedException)
        {
            throw new ArgumentException("The development area root is not a valid path.", nameof(developmentAreaRoot), ex);
        }

        if (full.IndexOf(':', 2) >= 0)
        {
            throw new ArgumentException(
                "The development area root contains ':' after the drive letter.",
                nameof(developmentAreaRoot));
        }

        var trimmed = Path.TrimEndingDirectorySeparator(full);
        // TrimEndingDirectorySeparator leaves "C:\" alone, so a bare drive is 3 characters here.
        if (trimmed.Length <= 3)
        {
            throw new ArgumentException(
                "The development area root must be a folder, not a whole drive.",
                nameof(developmentAreaRoot));
        }

        Root = trimmed;
    }

    /// <summary>The canonical area root, without a trailing separator.</summary>
    public string Root { get; }

    /// <summary>
    /// Resolves a relative path (one or more names separated by <c>\</c> or <c>/</c>) to a full path
    /// strictly inside the area. The area root itself is not a valid result.
    /// </summary>
    public PathResolution Resolve(string? relativePath) => ResolveCore(relativePath, singleSegment: false);

    /// <summary>
    /// Resolves a project name: exactly one segment, so a project always sits directly in the
    /// area. Everything <see cref="Resolve"/> checks applies.
    /// </summary>
    public PathResolution ResolveProject(string? projectName) => ResolveCore(projectName, singleSegment: true);

    private PathResolution ResolveCore(string? input, bool singleSegment)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return Reject(PathRejection.Empty, "The path is empty.");
        }

        // Rooted forms first, so the caller gets the most specific reason.
        if (input.StartsWith(@"\\", StringComparison.Ordinal) || input.StartsWith("//", StringComparison.Ordinal))
        {
            return Reject(PathRejection.UncOrDevicePath, "UNC and device paths are not allowed.");
        }

        if (input[0] is '\\' or '/')
        {
            return Reject(PathRejection.Rooted, "The path must be relative to the development area.");
        }

        if (input.Length >= 2 && char.IsAsciiLetter(input[0]) && input[1] == ':')
        {
            return Reject(PathRejection.DriveSpecifier, "Drive specifiers are not allowed.");
        }

        var segments = input.Split(Separators);

        if (singleSegment && segments.Length != 1)
        {
            return Reject(PathRejection.NotASingleSegment, "A project name must be a single folder name.");
        }

        foreach (var segment in segments)
        {
            var problem = ValidateSegment(segment);
            if (problem is not null)
            {
                return problem;
            }
        }

        // Defence in depth: segment validation should already make an escape impossible.
        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine([Root, .. segments]));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return Reject(PathRejection.InvalidCharacter, "The path is not valid on this system.");
        }

        if (!IsStrictlyInside(Root, full))
        {
            return Reject(PathRejection.EscapesArea, "The path leaves the development area.");
        }

        return CheckForReparsePoints(segments) ?? PathResolution.Allowed(full);
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is below <paramref name="root"/> (both canonical, no
    /// trailing separator on the root), compared ignoring case. The root itself is not inside, and
    /// a sibling that merely shares the root's text as a prefix (<c>C:\Dev</c> vs <c>C:\Development</c>)
    /// is not inside either, because the comparison includes the separator.
    /// </summary>
    internal static bool IsStrictlyInside(string root, string candidate)
        => candidate.Length > root.Length + 1
           && candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)
           && candidate[root.Length] == Path.DirectorySeparatorChar;

    private static PathResolution? ValidateSegment(string segment)
    {
        if (segment.Length == 0)
        {
            return Reject(PathRejection.EmptySegment, "The path has an empty segment.");
        }

        if (segment is "." or "..")
        {
            return Reject(PathRejection.TraversalSegment, $"'{segment}' is not allowed in a path.");
        }

        if (segment.Contains(':'))
        {
            return Reject(PathRejection.AlternateDataStream, "':' is not allowed in a name (drive specifiers and data streams).");
        }

        if (segment.Length > MaxSegmentLength)
        {
            return Reject(PathRejection.SegmentTooLong, $"A name is longer than {MaxSegmentLength} characters.");
        }

        if (segment.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return Reject(PathRejection.InvalidCharacter, "A name contains a character Windows does not allow.");
        }

        if (string.IsNullOrWhiteSpace(segment))
        {
            return Reject(PathRejection.Empty, "A name is only whitespace.");
        }

        if (segment[^1] is '.' or ' ')
        {
            return Reject(PathRejection.TrailingDotOrSpace, "A name must not end with a dot or a space.");
        }

        // "CON.txt" and "NUL .x" are still the device: Windows looks at the part before the first dot.
        var dot = segment.IndexOf('.');
        var baseName = (dot < 0 ? segment : segment[..dot]).TrimEnd(' ');
        if (ReservedNames.Contains(baseName))
        {
            return Reject(PathRejection.ReservedDeviceName, $"'{baseName}' is a reserved device name.");
        }

        return null;
    }

    /// <summary>
    /// Walks from the root down the requested segments and refuses at the first existing component
    /// that is a reparse point. Stops quietly at the first component that does not exist.
    /// </summary>
    private PathResolution? CheckForReparsePoints(string[] segments)
    {
        var current = Root;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);

            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return null; // Nothing here yet, so nothing deeper exists either.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return Reject(PathRejection.Unverifiable, $"'{segment}' could not be inspected.");
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return Reject(PathRejection.ReparsePoint, $"'{segment}' is a link (junction or symlink); links are not followed.");
            }
        }

        return null;
    }

    private static PathResolution Reject(PathRejection reason, string detail) => PathResolution.Rejected(reason, detail);
}
