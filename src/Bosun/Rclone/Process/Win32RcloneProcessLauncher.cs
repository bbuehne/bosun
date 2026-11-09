using System.ComponentModel;
using System.IO;
using Bosun.Diagnostics;
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
/// <remarks>
/// <b>Output is always drained (bs-0lq).</b> Both streams are redirected, so both are read for the
/// life of the child by a <see cref="RcloneOutputPump"/>; a redirected pipe nobody reads fills, and
/// the child then blocks inside its own logger while still alive.
/// </remarks>
public sealed class Win32RcloneProcessLauncher : IRcloneProcessLauncher
{
    private readonly IProcessJob? _job;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>No Job Object: the child is not tied to Bosun's lifetime. Used by tests that only
    /// exercise start/exit/kill mechanics. The child's output is still drained, and discarded.</summary>
    public Win32RcloneProcessLauncher()
    {
        _logger = NullLogger.Instance;
        _timeProvider = TimeProvider.System;
    }

    /// <summary>Every child is assigned to <paramref name="job"/> (ADR-020 §1, bs-772); wall-clock
    /// time for the output limiter.</summary>
    public Win32RcloneProcessLauncher(IProcessJob job, ILogger<Win32RcloneProcessLauncher> logger)
        : this(job, logger, TimeProvider.System)
    {
    }

    /// <summary>Production constructor (the one the container picks: it has the most parameters it
    /// can satisfy). <paramref name="timeProvider"/> times the child-output rate limiter.</summary>
    public Win32RcloneProcessLauncher(
        IProcessJob job, ILogger<Win32RcloneProcessLauncher> logger, TimeProvider timeProvider)
    {
        _job = job;
        _logger = logger;
        _timeProvider = timeProvider;
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

        // Start draining immediately (bs-0lq): before the Job Object assignment, before anything that
        // could be slow, so a child that writes at once can never fill a pipe nobody is reading.
        var pumpTask = StartOutputPump(process, startInfo);

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

        return new RealHandle(process, pumpTask);
    }

    /// <summary>
    /// Begins reading both redirected streams. The returned task completes when both reach EOF (the
    /// child exited, or its pipes were closed) and never faults. It references only the stream
    /// readers, not the <see cref="Diag.Process"/>, so it cannot keep the process handle alive.
    /// </summary>
    private Task StartOutputPump(Diag.Process process, RcloneProcessStartInfo startInfo)
    {
        var pump = new RcloneOutputPump(
            _logger,
            _timeProvider,
            BuildRedactor(startInfo.EnvironmentVariables),
            Path.GetFileName(startInfo.ExecutablePath));

        var stdout = process.StandardOutput;
        var stderr = process.StandardError;

        // Task.Run so a first read that happens to run synchronously cannot hold up Start.
        return Task.Run(() => pump.RunAsync(stdout, stderr));
    }

    /// <summary>
    /// The rc credential reaches the child only through these environment variables, so that is
    /// where the redactor learns its value. rclone should not print it, but a line that does must not
    /// reach the log: <see cref="DiagnosticsRedactor"/> masks the value in every spelling it knows
    /// and the usual secret-carrying shapes. With no credential in the environment only the shape
    /// layer applies.
    /// </summary>
    private static Func<string, string> BuildRedactor(IReadOnlyDictionary<string, string> environment)
    {
        RcloneRcCredential? credential = null;
        if (environment.TryGetValue("RCLONE_RC_USER", out var user)
            && environment.TryGetValue("RCLONE_RC_PASS", out var password)
            && !string.IsNullOrEmpty(user)
            && !string.IsNullOrEmpty(password))
        {
            credential = new RcloneRcCredential(user, password);
        }

        return new DiagnosticsRedactor(credential).Redact;
    }

    private sealed class RealHandle : IRcloneProcessHandle
    {
        // After the process has exited, how long Dispose waits for the output pump to finish reading
        // what the child wrote last (typically the reason it died) before closing the pipes under it.
        private static readonly TimeSpan PumpDrainGrace = TimeSpan.FromMilliseconds(500);

        private readonly Diag.Process _process;
        private readonly Task _pumpTask;
        private int _disposed;

        public RealHandle(Diag.Process process, Task pumpTask)
        {
            _process = process;
            _pumpTask = pumpTask;
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

        private bool HasExitedSafe()
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
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

            // Exited can fire before the pipes are fully read. Closing them now would discard the
            // child's final lines. Only wait for a child that has really exited (its pipes are
            // then at EOF, or about to be); a still-running one is being abandoned, so closing is
            // what ends the pump. The pump task never faults, so Wait cannot throw.
            if (HasExitedSafe())
            {
                _pumpTask.Wait(PumpDrainGrace);
            }

            _process.Dispose();
        }
    }
}
