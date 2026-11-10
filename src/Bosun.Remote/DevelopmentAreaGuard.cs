using System.Diagnostics.CodeAnalysis;

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
/// <para><b>Missing area (fail closed).</b> The area root itself must be an existing directory at
/// the moment of each call. If it is missing, is a file, or cannot be reached, every resolution is
/// <see cref="PathRejection.AreaUnavailable"/> (after the input-shape checks, which still win).
/// Nothing is cached, so the area disappearing or returning while Bosun runs is noticed on the
/// next call. A guard can be created for a root that does not exist yet.</para>
///
/// <para><b>Not covered.</b> A time-of-check to time-of-use gap remains: something running as the
/// same user could create a junction after the check and before the caller's use. Closing it would
/// need handle-based opens, which is not available to callers that hand a path to another program
/// (Claude Code). It requires write access to the area, which the caller of the tools does not have
/// by any other route. Hard links to files outside the area are likewise not detected. Windows 8.3
/// short names are not special-cased: they can only alias entries that are already inside the
/// area.</para>
///
/// <para><b>Project names (ADR-024 addendum, bs-4xw.17).</b> <see cref="ResolveProject"/> applies a
/// fixed alphabet on top of the segment checks. A name is accepted only when every character is an
/// ASCII letter, an ASCII digit, a space (U+0020 only), <c>.</c>, <c>_</c> or <c>-</c>, the first
/// character is an ASCII letter or digit, the last is not a space or a dot, and it is 1 to 64
/// characters long. The input is never trimmed, normalised or case-folded. The characters allowed in
/// a multi-segment path (<see cref="Resolve"/>) are not covered by that rule.</para>
///
/// <para>Comparisons are case-insensitive, as on Windows. Instances are immutable and thread-safe.
/// The type exposes no member that executes anything.</para>
/// </summary>
public sealed class DevelopmentAreaGuard
{
    private const int MaxSegmentLength = 255;

    private const int MaxProjectNameLength = 64;

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
    /// <c>.</c>/<c>..</c> in the root are normalised. The root need not exist when the guard is
    /// created: existence is checked on every <see cref="Resolve"/> call (a missing area is
    /// <see cref="PathRejection.AreaUnavailable"/>) and by <see cref="AreaExists"/>. Host code that
    /// must not throw on a bad root uses <see cref="TryCreate"/>, which applies the same rules.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The root is empty, relative, UNC or a device path, has a <c>:</c> beyond the drive letter, or
    /// is a bare drive root (an area that large defeats the guard).
    /// </exception>
    public DevelopmentAreaGuard(string developmentAreaRoot)
    {
        var problem = CheckRoot(developmentAreaRoot, out var canonicalRoot, out var inner);
        if (problem is not null)
        {
            throw inner is null
                ? new ArgumentException(problem, nameof(developmentAreaRoot))
                : new ArgumentException(problem, nameof(developmentAreaRoot), inner);
        }

        Root = canonicalRoot!;
    }

    // Only TryCreate uses this, with a root that CheckRoot has already canonicalised.
    private DevelopmentAreaGuard(string canonicalRoot, bool alreadyChecked)
    {
        _ = alreadyChecked;
        Root = canonicalRoot;
    }

    /// <summary>
    /// Creates a guard without throwing for a bad root. Applies exactly the same rules as the
    /// constructor (both call one shared check). A root that does not exist is accepted; existence
    /// is a per-call check.
    /// </summary>
    /// <param name="developmentAreaRoot">The already-expanded absolute path, or null.</param>
    /// <param name="guard">The guard when the root is usable; otherwise null.</param>
    /// <param name="problem">
    /// When the root is refused, a short reason. It never contains the root text. It is for Bosun's
    /// own log and health display, not for the remote caller. Null when the guard was created.
    /// </param>
    /// <returns>True when <paramref name="guard"/> was created.</returns>
    public static bool TryCreate(
        string? developmentAreaRoot,
        [NotNullWhen(true)] out DevelopmentAreaGuard? guard,
        [NotNullWhen(false)] out string? problem)
    {
        problem = CheckRoot(developmentAreaRoot, out var canonicalRoot, out _);
        if (problem is not null)
        {
            guard = null;
            return false;
        }

        guard = new DevelopmentAreaGuard(canonicalRoot!, alreadyChecked: true);
        return true;
    }

    /// <summary>
    /// The single place the root rules live, shared by the constructor and <see cref="TryCreate"/> so
    /// the two cannot drift. Returns null and the canonical root when the root is usable; otherwise a
    /// short problem text (never containing the root) and, when the runtime rejected the path, the
    /// exception to chain.
    /// </summary>
    private static string? CheckRoot(string? developmentAreaRoot, out string? canonicalRoot, out Exception? inner)
    {
        canonicalRoot = null;
        inner = null;

        if (string.IsNullOrWhiteSpace(developmentAreaRoot))
        {
            return "The development area root is empty.";
        }

        // Reject UNC and device roots on the raw text: GetFullPath keeps "\\?\C:\x" as it is.
        if (developmentAreaRoot.StartsWith(@"\\", StringComparison.Ordinal)
            || developmentAreaRoot.StartsWith("//", StringComparison.Ordinal))
        {
            return "The development area root must be a drive-letter path, not a UNC or device path.";
        }

        if (developmentAreaRoot.Length < 3
            || !char.IsAsciiLetter(developmentAreaRoot[0])
            || developmentAreaRoot[1] != ':'
            || !Array.Exists(Separators, s => s == developmentAreaRoot[2]))
        {
            return "The development area root must be a fully qualified drive-letter path such as C:\\Dev.";
        }

        string full;
        try
        {
            full = Path.GetFullPath(developmentAreaRoot);
        }
        catch (Exception ex) when (ex is PathTooLongException or NotSupportedException)
        {
            inner = ex;
            return "The development area root is not a valid path.";
        }

        if (full.IndexOf(':', 2) >= 0)
        {
            return "The development area root contains ':' after the drive letter.";
        }

        var trimmed = Path.TrimEndingDirectorySeparator(full);
        // TrimEndingDirectorySeparator leaves "C:\" alone, so a bare drive is 3 characters here.
        if (trimmed.Length <= 3)
        {
            return "The development area root must be a folder, not a whole drive.";
        }

        canonicalRoot = trimmed;
        return null;
    }

    /// <summary>The canonical area root, without a trailing separator.</summary>
    public string Root { get; }

    /// <summary>
    /// True when the area root is an existing directory right now. Not cached: the folder can
    /// disappear or come back while Bosun runs. For tools that never call <see cref="Resolve"/>
    /// (list projects, workstation check). An unreachable root counts as not existing.
    /// </summary>
    public bool AreaExists() => RootIsExistingDirectory();

    // Directory.Exists alone says true for a junction whose target is gone (the link entry itself is a
    // directory), so the root is also opened for listing: that follows the link and fails when the
    // target is missing or unreadable. Only the first entry is requested, so a large area costs nothing.
    private bool RootIsExistingDirectory()
    {
        if (!Directory.Exists(Root))
        {
            return false;
        }

        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(Root).GetEnumerator();
            entries.MoveNext();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Resolves a relative path (one or more names separated by <c>\</c> or <c>/</c>) to a full path
    /// strictly inside the area. The area root itself is not a valid result.
    /// </summary>
    public PathResolution Resolve(string? relativePath) => ResolveCore(relativePath, singleSegment: false);

    /// <summary>
    /// Resolves a project name: exactly one segment, so a project always sits directly in the
    /// area. Everything <see cref="Resolve"/> checks applies, and so does the project-name rule
    /// (ADR-024 addendum, bs-4xw.17): every character is an ASCII letter, an ASCII digit, a space
    /// (U+0020 only), <c>.</c>, <c>_</c> or <c>-</c>; the first character is an ASCII letter or digit;
    /// the last is not a space or a dot; and the name is 1 to 64 characters long. A name that passes
    /// the checks shared with <see cref="Resolve"/> but breaks that rule is
    /// <see cref="PathRejection.NameNotAllowed"/>. The rule runs before the area is touched, so a
    /// malformed name is reported as malformed even when the area is unavailable. The input is not
    /// trimmed, normalised or case-folded.
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

        // The project-name alphabet (bs-4xw.17). After the segment checks so their specific reasons
        // win, and before any file-system access so a bad name never reaches the area checks.
        if (singleSegment)
        {
            var nameProblem = ValidateProjectName(segments[0]);
            if (nameProblem is not null)
            {
                return nameProblem;
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

        // Fail closed when the area itself is gone (missing, a file, unreachable): the reparse walk
        // below stops quietly at the first missing component, which would otherwise let a
        // create-project tool build the whole area. Checked on every call, never cached, and after
        // the input-shape checks so a malformed name is still reported as malformed.
        if (!RootIsExistingDirectory())
        {
            return Reject(PathRejection.AreaUnavailable, "The development area is not available on the workstation.");
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

    /// <summary>
    /// The project-name rule (ADR-024 addendum, bs-4xw.17). ASCII only, by explicit comparison: no
    /// culture-sensitive or Unicode-aware character class, no trimming, no normalisation. The detail
    /// states the rule and does not repeat the name, which may carry control or lookalike characters.
    /// </summary>
    private static PathResolution? ValidateProjectName(string name)
    {
        if (name.Length is < 1 or > MaxProjectNameLength
            || !char.IsAsciiLetterOrDigit(name[0])
            || name[^1] is '.' or ' ')
        {
            return RejectName();
        }

        foreach (var c in name)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is ' ' or '.' or '_' or '-'))
            {
                return RejectName();
            }
        }

        return null;

        static PathResolution RejectName() => Reject(
            PathRejection.NameNotAllowed,
            "A project name may use only the letters A to Z (no accents), the digits 0 to 9, spaces, "
            + "'.', '_' and '-'. It must start with a letter or digit, must not end with a space or a "
            + "dot, and must be 1 to 64 characters long.");
    }

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
