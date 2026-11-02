namespace Bosun.Rclone.Process;

/// <summary>
/// The process-spawning seam behind <see cref="RcloneProcessService"/> (bs-5mt). Deliberately
/// minimal -- "start this executable with these arguments, tell me when it exits, let me kill
/// it" is everything the service needs. Real production wiring is
/// <see cref="Win32RcloneProcessLauncher"/>; the default test suite always fakes this (CLAUDE.md
/// worktree-safety rules -- "no test in the default suite may... start a real process").
/// </summary>
public interface IRcloneProcessLauncher
{
    /// <summary>
    /// Starts the process described by <paramref name="startInfo"/>.
    /// </summary>
    /// <exception cref="RcloneExecutableNotFoundException">
    /// The executable could not be found at all (e.g. not on PATH) -- distinct from a launch
    /// failure for an executable that DOES exist, per the E3 brief.
    /// </exception>
    /// <exception cref="RcloneProcessLaunchException">
    /// The executable was found but the process could not be started for some other reason.
    /// </exception>
    IRcloneProcessHandle Start(RcloneProcessStartInfo startInfo);
}

/// <summary>A running (or just-exited) child process, as seen by <see cref="RcloneProcessService"/>.</summary>
public interface IRcloneProcessHandle : IDisposable
{
    bool HasExited { get; }

    /// <summary><see langword="null"/> while the process is still running.</summary>
    int? ExitCode { get; }

    /// <summary>
    /// Raised exactly once, when the process exits -- whether that is because
    /// <see cref="Kill"/> was called or because the process died on its own.
    /// <see cref="RcloneProcessService"/> is the one place that decides whether an exit was
    /// expected (it called <see cref="Kill"/> itself, e.g. during shutdown) or unexpected (the
    /// process died on its own, which triggers restart-and-reconcile).
    /// </summary>
    event EventHandler? Exited;

    /// <summary>Forcibly terminates the process (and, in the real implementation, its
    /// descendants) if it has not already exited. Never throws for an already-exited process.</summary>
    void Kill();
}

/// <summary>What to launch. <see cref="RcloneProcessService"/> passes a full path when it found
/// rclone (bs-64k); a bare filename is resolved the way any Win32 <c>CreateProcess</c> call
/// resolves one -- via PATH -- which is what makes "not found" observable as a distinct,
/// catchable failure rather than a silent no-op.</summary>
public sealed record RcloneProcessStartInfo
{
    public required string ExecutablePath { get; init; }
    public required IReadOnlyList<string> Arguments { get; init; }

    /// <summary>
    /// Extra environment variables to set on the child process, merged into (never replacing)
    /// the inherited process environment -- e.g. <c>RCLONE_RC_USER</c>/<c>RCLONE_RC_PASS</c>
    /// (bs-ard). Deliberately environment, not a command-line argument: a command line is
    /// readable by any process running as the same user via <c>Win32_Process.CommandLine</c>
    /// (see <see cref="Process.RcloneProcessService.BuildStartInfo"/>'s remarks), so a secret
    /// belongs here, never in <see cref="Arguments"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; } =
        new Dictionary<string, string>();
}

/// <summary>
/// The executable named by <see cref="ExecutablePath"/> could not be found (e.g. rclone is not
/// installed, or was installed after Bosun started). Distinct from
/// <see cref="RcloneProcessLaunchException"/> ("present but failed to start") per the E3 brief.
/// </summary>
/// <remarks>
/// bs-64k: when <see cref="RcloneProcessService"/> resolves the executable itself (PATH, then the
/// usual install locations) and finds nothing, it throws this with
/// <see cref="SearchedLocations"/> filled in, and the message says where it looked. The launcher
/// throws it without a list when the OS itself reports file-not-found (for example an explicit
/// path that does not exist).
/// </remarks>
public sealed class RcloneExecutableNotFoundException : Exception
{
    public string ExecutablePath { get; }

    /// <summary>Everywhere that was looked at, in order. Empty when the OS reported the failure
    /// and no search was made.</summary>
    public IReadOnlyList<string> SearchedLocations { get; }

    public RcloneExecutableNotFoundException(string executablePath, Exception innerException)
        : base(BuildMessage(executablePath, []), innerException)
    {
        ExecutablePath = executablePath;
        SearchedLocations = [];
    }

    public RcloneExecutableNotFoundException(string executablePath, IReadOnlyList<string> searchedLocations)
        : base(BuildMessage(executablePath, searchedLocations))
    {
        ExecutablePath = executablePath;
        SearchedLocations = searchedLocations;
    }

    /// <summary>One paragraph, plain words: what was not found, where Bosun looked, what to do,
    /// and what still works. It becomes the health banner's detail text, so it must not read as
    /// a reproach: a running program genuinely cannot see a PATH change made after it started.</summary>
    internal static string BuildMessage(string executablePath, IReadOnlyList<string> searchedLocations)
    {
        var where = searchedLocations.Count == 0
            ? $"'{executablePath}' could not be found."
            : $"'{executablePath}' was not found. Bosun looked in: {string.Join("; ", searchedLocations)}.";

        return where +
            " If rclone is not installed, install it (https://rclone.org/downloads/, or " +
            "'winget install Rclone.Rclone'). If it was installed after Bosun started, restart Bosun " +
            "(if that is not enough, sign out and back in): a running program does not see PATH changes " +
            "made after it began. " +
            "Drive mounting is unavailable until rclone is found; " +
            "terminal profiles and SSH sessions are unaffected.";
    }
}

/// <summary>
/// The executable named by <see cref="ExecutablePath"/> exists but the process could not be
/// started (or exited immediately with a failure the OS surfaced as a launch error). Distinct
/// from <see cref="RcloneExecutableNotFoundException"/> per the E3 brief.
/// </summary>
public sealed class RcloneProcessLaunchException : Exception
{
    public string ExecutablePath { get; }

    public RcloneProcessLaunchException(string executablePath, Exception innerException)
        : base($"'{executablePath}' was found but failed to start: {innerException.Message}", innerException)
    {
        ExecutablePath = executablePath;
    }
}
