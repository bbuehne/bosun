using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Bosun.Watchdog;

/// <summary>Ends the process. Behind an interface so tests never end it.</summary>
public interface IProcessExiter
{
    /// <summary>Terminates the process now. Does not return in production.</summary>
    void Exit(int exitCode);
}

/// <summary>
/// <see cref="IProcessExiter"/> over <see cref="Environment.Exit(int)"/>, with a backstop.
/// </summary>
/// <remarks>
/// <see cref="Environment.Exit(int)"/> runs <c>ProcessExit</c> handlers and finalizers, and either
/// can block if the process is already wedged on a lock -- which is the situation this is called in.
/// So a dedicated thread is started first that, if the process is still here 5 s later, terminates
/// it outright (<see cref="Process.Kill()"/>: <c>TerminateProcess</c>, no handlers). Either way the
/// OS closes the process's handles, which closes the Job Object and so ends <c>rclone rcd</c>
/// (ADR-020 Decision 1). Not exercised by any default-suite test, for the obvious reason.
/// </remarks>
public sealed class EnvironmentProcessExiter : IProcessExiter
{
    private static readonly TimeSpan BackstopDelay = TimeSpan.FromSeconds(5);

    public void Exit(int exitCode)
    {
        var backstop = new Thread(() =>
        {
            Thread.Sleep(BackstopDelay);
            using var self = Process.GetCurrentProcess();
            self.Kill();
        })
        {
            IsBackground = true,
            Name = "Bosun exit backstop",
        };
        backstop.Start();

        Environment.Exit(exitCode);
    }
}

/// <summary>
/// Puts a hard upper bound on how long Bosun takes to exit (bs-6to, ADR-020 Decision 7).
/// </summary>
/// <remarks>
/// <para>
/// On 2026-10-01 the user exited Bosun, the UI hung, and Windows killed it as not responding. The
/// supervisor was wedged, and exit waited on the supervisor. Exit now arms a deadline timer at the
/// moment it begins (<see cref="BeginShutdown"/>); if shutdown has not called <see cref="Complete"/>
/// when the deadline passes, the guard logs at Error and forces the process to exit. That is the last
/// resort: the orderly stop still runs first and almost always finishes in well under a second.
/// </para>
/// <para>
/// The timer runs on the injected <see cref="TimeProvider"/> and fires on a thread-pool thread, never
/// on the thread that is wedged, so it works whatever shutdown is stuck on -- including the UI
/// thread. <see cref="BeginShutdown"/> is idempotent: the first call arms the deadline and later
/// calls (the restarter, then <c>OnExit</c>) leave it alone, so the bound counts from the earliest
/// moment exit began.
/// </para>
/// <para>
/// 15 s: the orderly path's own bound (the host's stop token, 5 s) sits well inside it, and it
/// is long enough for a slow disk to flush the log. The watchdog's restart handoff waits twice this
/// for the old process to be gone. See <see cref="DefaultBound"/>.
/// </para>
/// </remarks>
public sealed class ShutdownGuard : IShutdownState, IDisposable
{
    /// <summary>The exit code used when the deadline forces the process to exit.</summary>
    public const int ForcedExitCode = 3;

    /// <summary>15 seconds. See the class remarks.</summary>
    public static readonly TimeSpan DefaultBound = TimeSpan.FromSeconds(15);

    private readonly TimeProvider timeProvider;
    private readonly IProcessExiter exiter;
    private readonly Func<ILogger?> logger;
    private readonly Action<string>? recordWithoutLogger;
    private readonly TimeSpan bound;
    private readonly object gate = new();

    private ITimer? deadline;
    private volatile bool shuttingDown;
    private bool completed;

    /// <param name="logger">Resolved when it is needed: the logger does not exist until the host
    /// has been built, and shutdown can begin before that (or after the host failed to build).</param>
    /// <param name="recordWithoutLogger">Used instead of the logger when there is none.</param>
    public ShutdownGuard(
        TimeProvider timeProvider,
        IProcessExiter exiter,
        Func<ILogger?> logger,
        Action<string>? recordWithoutLogger = null,
        TimeSpan? bound = null)
    {
        this.timeProvider = timeProvider;
        this.exiter = exiter;
        this.logger = logger;
        this.recordWithoutLogger = recordWithoutLogger;
        this.bound = bound ?? DefaultBound;
    }

    public bool IsShuttingDown => shuttingDown;

    /// <summary>Marks the application as exiting and arms the deadline, once. Safe to call from any
    /// thread, any number of times.</summary>
    public void BeginShutdown(string reason)
    {
        lock (gate)
        {
            if (shuttingDown || completed)
            {
                return;
            }

            shuttingDown = true;
            deadline = timeProvider.CreateTimer(_ => OnDeadline(reason), null, bound, Timeout.InfiniteTimeSpan);
        }

        logger()?.LogInformation("Shutdown begun ({Reason}); the process will be forced to exit if it has not finished within {Bound}", reason, bound);
    }

    /// <summary>Shutdown finished in time: disarms the deadline.</summary>
    public void Complete()
    {
        lock (gate)
        {
            completed = true;
            deadline?.Dispose();
            deadline = null;
        }
    }

    public void Dispose() => Complete();

    private void OnDeadline(string reason)
    {
        lock (gate)
        {
            if (completed)
            {
                return;
            }
        }

        var message =
            $"Shutdown ({reason}) did not finish within {bound}; something is wedged (most likely the mount " +
            "supervisor). Forcing the process to exit so Windows does not have to kill it as not responding.";

        if (logger() is { } log)
        {
            log.LogError("{Message}", message);
        }
        else
        {
            recordWithoutLogger?.Invoke(message);
        }

        exiter.Exit(ForcedExitCode);
    }
}
