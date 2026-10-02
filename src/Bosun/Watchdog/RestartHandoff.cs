using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Bosun.Watchdog;

/// <summary>
/// The command-line contract between a Bosun that is restarting itself and the Bosun it launches
/// (bs-6to, ADR-020 Consequences: "the new instance waits for the old one to exit").
/// </summary>
/// <remarks>
/// The old instance starts the new one with <c>--restarted-by-watchdog &lt;oldPid&gt;</c> appended to
/// its own arguments. Starting the replacement BEFORE this process has gone means the restart cannot
/// be lost to a crash between "exit" and "launch"; the cost is that the replacement must not touch
/// anything until the old process has really exited. It therefore waits (<see cref="OldInstanceWaiter"/>)
/// before it tries to take the single-instance mutex, which the old instance holds until it dies.
/// </remarks>
public static class RestartHandoffArguments
{
    public const string Flag = "--restarted-by-watchdog";

    /// <summary>
    /// The flag for a restart the user asked for (bs-aoz). A distinct flag, not a value on the
    /// watchdog's, so the two cannot be confused: the new instance reads this one as "show the window"
    /// and the other as "stay hidden and say what happened".
    /// </summary>
    public const string ManualFlag = "--restarted-by-user";

    /// <summary>Finds the flag in <paramref name="args"/>, as <c>--restarted-by-watchdog 123</c> or
    /// <c>--restarted-by-watchdog=123</c>. False if it is absent or its value is not a positive
    /// integer. Either restart flag counts: both name the process the new instance must wait for.</summary>
    public static bool TryGetOldProcessId(IReadOnlyList<string> args, out int oldProcessId) =>
        TryGetHandoff(args, out _, out oldProcessId);

    /// <summary>As <see cref="TryGetOldProcessId"/>, and also says which flag it was. If both appear (which
    /// <see cref="ForRestart"/> never produces) the user's wins: it is the one that shows the window.</summary>
    public static bool TryGetHandoff(IReadOnlyList<string> args, out RestartKind kind, out int oldProcessId)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (TryGetFlagValue(args, ManualFlag, out oldProcessId))
        {
            kind = RestartKind.Manual;
            return true;
        }

        kind = RestartKind.Watchdog;
        return TryGetFlagValue(args, Flag, out oldProcessId);
    }

    private static bool TryGetFlagValue(IReadOnlyList<string> args, string flag, out int processId)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string? value = null;

            if (string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase))
            {
                value = i + 1 < args.Count ? args[i + 1] : null;
            }
            else if (arg.StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase))
            {
                value = arg[(flag.Length + 1)..];
            }
            else
            {
                continue;
            }

            if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid > 0)
            {
                processId = pid;
                return true;
            }

            break;
        }

        processId = 0;
        return false;
    }

    /// <summary>The arguments for the replacement: <paramref name="currentArgs"/> with any earlier
    /// handoff flag removed (so they do not pile up across restarts) and this process's id appended
    /// under the flag for <paramref name="kind"/>.</summary>
    /// <remarks>
    /// A <see cref="RestartKind.Manual"/> restart also drops <c>--autostart</c>. That flag is how the
    /// window stays hidden at login; carried into a restart the user just asked for, it would make
    /// Bosun restart and then not show itself, which reads as the restart having failed.
    /// </remarks>
    public static IReadOnlyList<string> ForRestart(
        IReadOnlyList<string> currentArgs, int ownProcessId, RestartKind kind = RestartKind.Watchdog)
    {
        ArgumentNullException.ThrowIfNull(currentArgs);

        var result = new List<string>();
        for (var i = 0; i < currentArgs.Count; i++)
        {
            var arg = currentArgs[i];
            if (IsFlag(arg, Flag) || IsFlag(arg, ManualFlag))
            {
                if (!arg.Contains('='))
                {
                    i++; // its value
                }

                continue;
            }

            if (kind == RestartKind.Manual
                && string.Equals(arg, UI.LaunchContextDetector.AutostartArgument, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            result.Add(arg);
        }

        result.Add(kind == RestartKind.Manual ? ManualFlag : Flag);
        result.Add(ownProcessId.ToString(CultureInfo.InvariantCulture));
        return result;
    }

    private static bool IsFlag(string arg, string flag) =>
        string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase)
        || arg.StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Answers "is that process still running?" -- behind an interface so tests never look at
/// real processes.</summary>
public interface IProcessExitProbe
{
    bool IsRunning(int processId);
}

/// <summary><see cref="IProcessExitProbe"/> over <see cref="Process.GetProcessById(int)"/>.</summary>
/// <remarks>
/// A process id can in principle be reused by an unrelated process. Within the 30 s the wait lasts,
/// that would need the old Bosun to exit and Windows to hand its id to a new process in the same
/// window; the consequence is a wait that runs to its bound, not a wrong action. Not guarded
/// further.
/// </remarks>
public sealed class SystemProcessExitProbe : IProcessExitProbe
{
    public bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // No process with that id.
            return false;
        }
        catch (InvalidOperationException)
        {
            // Exited between the lookup and the check.
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Exists but cannot be inspected. Assume alive: the wait is bounded either way.
            return true;
        }
    }
}

public enum HandoffWaitResult
{
    /// <summary>The old process is gone (or never existed). Safe to proceed.</summary>
    OldInstanceExited,

    /// <summary>The old process was still running when the wait ran out. The caller proceeds as the
    /// single-instance mutex allows, and reports it; it does not kill the old process.</summary>
    TimedOut,
}

/// <summary>
/// The new instance's half of the handoff: waits, boundedly, for the old instance's process to exit
/// before the new one tries to become the primary instance.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the bound is 30 s.</b> The old instance forces its own exit 15 s after it begins shutting
/// down (<c>ShutdownGuard</c>), so it is gone within about that long whatever it is wedged on; 30 s
/// is twice that, enough for a slow disk or a busy machine, short enough that a wedged old process
/// is reported while the user is still looking at the screen.
/// </para>
/// <para>
/// Polling, not a process-handle wait, so that time is the injected <see cref="TimeProvider"/> and
/// the wait is testable without a real process. 250 ms keeps the handoff quick without spinning.
/// </para>
/// </remarks>
public sealed class OldInstanceWaiter(IProcessExitProbe probe, TimeProvider timeProvider, ILogger? logger = null)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    public async Task<HandoffWaitResult> WaitForExitAsync(
        int oldProcessId, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = timeProvider.GetUtcNow() + timeout;

        while (probe.IsRunning(oldProcessId))
        {
            var remaining = deadline - timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                logger?.LogWarning(
                    "Restart handoff: the previous Bosun instance (pid {OldPid}) is still running after {Timeout}; " +
                    "proceeding as the single-instance lock allows, and not killing it",
                    oldProcessId,
                    timeout);
                return HandoffWaitResult.TimedOut;
            }

            await Task.Delay(remaining < PollInterval ? remaining : PollInterval, timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }

        logger?.LogInformation("Restart handoff: the previous Bosun instance (pid {OldPid}) has exited", oldProcessId);
        return HandoffWaitResult.OldInstanceExited;
    }
}
