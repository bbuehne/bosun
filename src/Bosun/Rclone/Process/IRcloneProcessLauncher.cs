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

/// <summary>What to launch. <see cref="ExecutablePath"/> is resolved the same way any Win32
/// <c>CreateProcess</c> call resolves a bare filename -- via PATH -- which is what makes "not
/// found" observable as a distinct, catchable failure rather than a silent no-op.</summary>
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
/// installed, or is installed but not on PATH). Distinct from
/// <see cref="RcloneProcessLaunchException"/> ("present but failed to start") per the E3 brief --
/// this is the one that will actually fire on the maintainer's first run, since rclone is not
/// yet installed on his machine.
/// </summary>
public sealed class RcloneExecutableNotFoundException : Exception
{
    public string ExecutablePath { get; }

    public RcloneExecutableNotFoundException(string executablePath, Exception innerException)
        : base(
            $"'{executablePath}' could not be found. Install rclone (https://rclone.org/downloads/) " +
            "and ensure it is on PATH, then restart Bosun. Drive mounting is unavailable until then -- " +
            "terminal profiles and SSH sessions are unaffected.",
            innerException)
    {
        ExecutablePath = executablePath;
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
