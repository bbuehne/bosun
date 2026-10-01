using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Diag = System.Diagnostics;

namespace Bosun.Rclone.Process;

/// <summary>
/// Real <see cref="IRcloneProcessLauncher"/>, wrapping <see cref="Diag.Process"/> (aliased --
/// this namespace is itself called <c>Process</c>, so the unqualified BCL type name is
/// ambiguous here). Never exercised by the default test suite (CLAUDE.md worktree-safety rules);
/// covered by a marked integration test against a harmless real executable
/// (<c>cmd.exe /c exit 0</c>) rather than rclone itself, so the mechanics (start, detect exit,
/// kill) are proven without depending on rclone being installed.
/// </summary>
public sealed class Win32RcloneProcessLauncher : IRcloneProcessLauncher
{
    private readonly IProcessJob? _job;
    private readonly ILogger _logger;

    /// <summary>No Job Object: the child is not tied to Bosun's lifetime. Used by tests that only
    /// exercise start/exit/kill mechanics.</summary>
    public Win32RcloneProcessLauncher()
    {
        _logger = NullLogger.Instance;
    }

    /// <summary>Production constructor: every child is assigned to <paramref name="job"/>
    /// (ADR-020 §1, bs-772).</summary>
    public Win32RcloneProcessLauncher(IProcessJob job, ILogger<Win32RcloneProcessLauncher> logger)
    {
        _job = job;
        _logger = logger;
    }

    /// <summary>Win32 <c>ERROR_FILE_NOT_FOUND</c>. This is the specific
    /// <see cref="Win32Exception.NativeErrorCode"/> <see cref="Diag.Process.Start()"/> throws when
    /// the executable cannot be located (including via PATH search) -- the signal
    /// <see cref="RcloneExecutableNotFoundException"/> is built from.</summary>
    private const int ErrorFileNotFound = 2;

    public IRcloneProcessHandle Start(RcloneProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        var startInfoNative = new Diag.ProcessStartInfo
        {
            FileName = startInfo.ExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in startInfo.Arguments)
        {
            startInfoNative.ArgumentList.Add(argument);
        }

        // Merged into the inherited environment (ProcessStartInfo.Environment starts populated
        // with the current process's environment when UseShellExecute is false), never replacing
        // it wholesale -- the child still needs PATH etc. This is also, deliberately, the ONLY
        // place a secret like RCLONE_RC_PASS reaches the child process: never ArgumentList, which
        // ends up in a command line any same-user process can read (see the remarks on
        // RcloneProcessStartInfo.EnvironmentVariables and RcloneProcessService.BuildStartInfo).
        foreach (var (key, value) in startInfo.EnvironmentVariables)
        {
            startInfoNative.Environment[key] = value;
        }

        var process = new Diag.Process { StartInfo = startInfoNative, EnableRaisingEvents = true };

        try
        {
            process.Start();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorFileNotFound)
        {
            process.Dispose();
            throw new RcloneExecutableNotFoundException(startInfo.ExecutablePath, ex);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            throw new RcloneProcessLaunchException(startInfo.ExecutablePath, ex);
        }

        // Job Object assignment (ADR-020 §1, bs-772). RACE WINDOW, NOT CLOSED: Process.Start
        // returns after CreateProcess has already let the child run, so there is a gap of
        // microseconds to a few milliseconds before the assignment below. If Bosun were killed
        // inside that gap the child would be orphaned. Closing it would need CREATE_SUSPENDED (or
        // PROC_THREAD_ATTRIBUTE_JOB_LIST), which Process.Start cannot express; it would mean
        // replacing Process.Start with a hand-rolled CreateProcess plus redirected pipes and an
        // environment block, a lot more interop for a window that is tiny. The residual case is
        // covered by the OTHER half of ADR-020: the next launch finds the orphan holding the rc
        // port and kills it (RcPortGuard). The assignment is also best-effort for children: rcd
        // could in theory spawn a child before we assign; rclone does not do that at startup.
        if (_job is not null)
        {
            try
            {
                JobAssignment.TryAssignOrWarn(_job, process.SafeHandle, startInfo.ExecutablePath, _logger);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                // No usable process handle (it exited this instant). Same policy as a refused
                // assignment: warn, never fail the start.
                _logger.LogWarning(
                    ex, "Could not obtain a handle for {Executable} to assign it to Bosun's Job Object", startInfo.ExecutablePath);
            }
        }

        return new RealHandle(process);
    }

    private sealed class RealHandle : IRcloneProcessHandle
    {
        private readonly Diag.Process _process;
        private int _disposed;

        public RealHandle(Diag.Process process)
        {
            _process = process;
            _process.Exited += OnProcessExited;

            // Race: the process could have already exited between Start() returning and this
            // constructor subscribing to Exited. Check explicitly rather than relying solely on
            // the event, which -- per Process's documented behaviour -- may not fire at all if
            // the process had already exited before EnableRaisingEvents took effect.
            if (_process.HasExited)
            {
                OnProcessExited(this, EventArgs.Empty);
            }
        }

        public bool HasExited => _process.HasExited;
        public int? ExitCode => _process.HasExited ? _process.ExitCode : null;
        public event EventHandler? Exited;

        public void Kill()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Already exited between the check above and the call -- not an error.
            }
        }

        private void OnProcessExited(object? sender, EventArgs e) => Exited?.Invoke(this, EventArgs.Empty);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _process.Exited -= OnProcessExited;
            _process.Dispose();
        }
    }
}
