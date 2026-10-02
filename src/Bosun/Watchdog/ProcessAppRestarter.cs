using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Bosun.Watchdog;

/// <summary>Starts a Bosun process. Behind an interface so tests never start one.</summary>
public interface IBosunProcessLauncher
{
    /// <summary>Starts <paramref name="exePath"/> with <paramref name="arguments"/> and returns
    /// without waiting for it. Throws if it cannot be started.</summary>
    void Start(string exePath, IReadOnlyList<string> arguments);
}

/// <summary><see cref="IBosunProcessLauncher"/> over <see cref="Process.Start(ProcessStartInfo)"/>.
/// Arguments go through <see cref="ProcessStartInfo.ArgumentList"/>, so none needs manual quoting.</summary>
public sealed class SystemBosunProcessLauncher : IBosunProcessLauncher
{
    public void Start(string exePath, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            WorkingDirectory = Environment.CurrentDirectory,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Process.Start returned no process for '{exePath}'.");
    }
}

/// <summary>Asks the application to exit through its normal path. In production this posts
/// <c>Application.Shutdown()</c> to the UI thread.</summary>
public interface IApplicationShutdown
{
    void RequestShutdown();
}

/// <summary>What the restarter needs to know about the running process.</summary>
/// <param name="ExecutablePath">This process's exe (<see cref="Environment.ProcessPath"/>).</param>
/// <param name="Arguments">This process's own command-line arguments, without the exe.</param>
/// <param name="ProcessId">This process's id, which the replacement waits on.</param>
public sealed record RestartContext(string ExecutablePath, IReadOnlyList<string> Arguments, int ProcessId)
{
    /// <summary>The running process. Not used by any default-suite test.</summary>
    public static RestartContext ForCurrentProcess() => new(
        Environment.ProcessPath ?? throw new InvalidOperationException("The process path is not available."),
        [.. Environment.GetCommandLineArgs().Skip(1)],
        Environment.ProcessId);
}

/// <summary>
/// The real <see cref="IAppRestarter"/> (bs-6to). Launches a new Bosun with the same arguments plus
/// <c>--restarted-by-watchdog &lt;thisPid&gt;</c>, then shuts this process down under the bounded
/// <see cref="ShutdownGuard"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Order.</b> Launch first, then shut down. If the launch fails the process carries on (a
/// stalled Bosun is better than none) and the watchdog tries again; if the order were reversed, a
/// failed launch would leave the user with no Bosun at all.
/// </para>
/// <para>
/// <b>The handoff.</b> The replacement starts while this process still exists and holds the
/// single-instance mutex, so it waits for this pid to exit before trying to take it
/// (<see cref="OldInstanceWaiter"/>; the wait is in <c>App.OnStartup</c>). This process exits through
/// the normal path -- <see cref="IApplicationShutdown"/> -- with <see cref="ShutdownGuard"/>'s 15 s
/// deadline armed first, so a wedged supervisor cannot keep it alive. When it dies, the Job Object
/// ends its <c>rclone rcd</c>, and the replacement's startup crash recovery adopts or clears
/// whatever mounts remain and probes every host before it mounts anything (I1).
/// </para>
/// <para>
/// This type never mounts or unmounts anything and never touches rclone.
/// </para>
/// </remarks>
public sealed class ProcessAppRestarter(
    RestartContext context,
    IBosunProcessLauncher launcher,
    IApplicationShutdown applicationShutdown,
    ShutdownGuard shutdownGuard,
    ILogger<ProcessAppRestarter> logger) : IAppRestarter
{
    public Task<bool> RestartAsync(string reason, CancellationToken cancellationToken)
    {
        if (shutdownGuard.IsShuttingDown)
        {
            logger.LogWarning("Restart requested ({Reason}) but Bosun is already shutting down; not launching a replacement", reason);
            return Task.FromResult(false);
        }

        var arguments = RestartHandoffArguments.ForRestart(context.Arguments, context.ProcessId);

        try
        {
            launcher.Start(context.ExecutablePath, arguments);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Restart ({Reason}): the replacement Bosun could not be launched; this instance keeps running", reason);
            return Task.FromResult(false);
        }

        logger.LogError(
            "Restarting Bosun ({Reason}): launched a replacement ({Executable} {Arguments}); shutting this instance down",
            reason,
            context.ExecutablePath,
            string.Join(' ', arguments));

        // Armed BEFORE the request: if the UI thread is the thing that is wedged, the request never
        // reaches OnExit, and only a deadline that is already running can end this process.
        shutdownGuard.BeginShutdown($"watchdog restart: {reason}");
        applicationShutdown.RequestShutdown();
        return Task.FromResult(true);
    }
}
