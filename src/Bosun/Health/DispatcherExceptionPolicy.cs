using Microsoft.Extensions.Logging;

namespace Bosun.Health;

/// <summary>What <c>App.OnDispatcherUnhandledException</c> does with an exception (bs-twz).</summary>
public enum DispatcherExceptionDecision
{
    /// <summary>Set <c>e.Handled = true</c>; the process keeps running.</summary>
    Handle,

    /// <summary>Leave <c>e.Handled = false</c>; the process exits.</summary>
    Terminate,
}

/// <summary>
/// Decides whether Bosun survives an unhandled dispatcher (UI-thread) exception once the logger
/// exists (bs-twz, maintainer decision 2026-11-01: "survive and mark red"). Pure decision logic with
/// an injected clock, so the part of <c>App</c> that cannot be unit-tested stays a thin call into it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why survive.</b> Bosun exists to react to hosts going away. A transient fault in the status
/// window that killed the process would silently stop probing and unmounting, which is the worst
/// outcome for this tool. Terminating is not automatically safe either: mounts outlive the process
/// until the next start reconciles them (ARCHITECTURE §6).
/// </para>
/// <para>
/// <b>Why it is marked red, and why the mark stays.</b> Surviving means the UI is in a state nobody
/// has inspected. The Faulted <see cref="HealthIssueCodes.UiUnhandledException"/> issue is reported
/// through <see cref="IAppHealthReporter.ReportIssue"/> and nothing in Bosun ever clears it, not even
/// a later healthy report from rclone or the supervisor: those describe the supervision layer, and a
/// recovery there says nothing about whether the window or tray is now sound. It is cleared by
/// restarting Bosun, which is also what the health banner offers first for an unrecognised issue.
/// Each further exception updates the same issue (count and latest message) rather than adding one.
/// </para>
/// <para>
/// <b>Runaway guard.</b> More than <see cref="MaxExceptionsPerWindow"/> exceptions within
/// <see cref="Window"/> (a repeating timer throwing on every tick looks exactly like this) stop being
/// handled: <see cref="DispatcherExceptionDecision.Terminate"/> is returned, and stays the answer
/// afterwards, so the process exits instead of limping on indefinitely. A gap of exactly
/// <see cref="Window"/> still counts as inside the window.
/// </para>
/// <para>
/// <b>Never throws, never recurses.</b> A failure to log or to report is swallowed (the exception being
/// decided on is the one that matters, and a handler that throws inside the dispatcher's own
/// unhandled-exception event is how an error handler becomes a second crash). If reporting itself
/// raises dispatcher exceptions on this stack, that nested call is counted but does not report again.
/// </para>
/// </remarks>
public sealed class DispatcherExceptionPolicy
{
    /// <summary>More than this many exceptions inside <see cref="Window"/> terminates the process.</summary>
    public const int MaxExceptionsPerWindow = 5;

    /// <summary>The sliding window the guard counts over.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    internal const string IssueTitle = "A UI error occurred; see the log";

    private const int MaxMessageLength = 300;

    private readonly TimeProvider timeProvider;
    private readonly IAppHealthReporter? health;
    private readonly ILogger logger;
    private readonly object gate = new();
    private readonly Queue<DateTimeOffset> recent = new();
    private int totalHandled;
    private bool terminating;
    private int reporting;

    /// <param name="health">Where the Faulted issue goes. Null when the container could not provide
    /// one (the failure bs-4js exists for): exceptions are then still handled and logged, and the
    /// failure to mark Bosun red is logged too.</param>
    public DispatcherExceptionPolicy(TimeProvider timeProvider, IAppHealthReporter? health, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        this.timeProvider = timeProvider;
        this.health = health;
        this.logger = logger;
    }

    public DispatcherExceptionDecision Decide(Exception exception)
    {
        try
        {
            return DecideCore(exception);
        }
        catch
        {
            // Unreachable by construction (every step below is guarded); kept because the caller is
            // an unhandled-exception event, where a throw ends the process with no explanation.
            return DispatcherExceptionDecision.Terminate;
        }
    }

    private DispatcherExceptionDecision DecideCore(Exception exception)
    {
        int countInWindow;
        int handledSoFar;
        bool alreadyTerminating;

        lock (gate)
        {
            var now = timeProvider.GetUtcNow();
            while (recent.Count > 0 && now - recent.Peek() > Window)
            {
                recent.Dequeue();
            }

            recent.Enqueue(now);
            countInWindow = recent.Count;
            alreadyTerminating = terminating;

            if (alreadyTerminating || countInWindow > MaxExceptionsPerWindow)
            {
                terminating = true;
                handledSoFar = totalHandled;
            }
            else
            {
                handledSoFar = ++totalHandled;
            }
        }

        if (alreadyTerminating || countInWindow > MaxExceptionsPerWindow)
        {
            Guarded(() => logger.LogCritical(
                exception,
                "Unhandled dispatcher exception, and this makes {Count} within {WindowSeconds:0} s (limit {Limit}); " +
                "no longer handling them, so Bosun will exit rather than keep running in an unknown UI state.",
                countInWindow,
                Window.TotalSeconds,
                MaxExceptionsPerWindow));
            return DispatcherExceptionDecision.Terminate;
        }

        Guarded(() => logger.LogCritical(
            exception,
            "Unhandled dispatcher exception (handled; {Count} in the last {WindowSeconds:0} s, {Total} since start). " +
            "Supervision continues; the tray icon is marked faulted.",
            countInWindow,
            Window.TotalSeconds,
            handledSoFar));

        Report(exception, handledSoFar);
        return DispatcherExceptionDecision.Handle;
    }

    private void Report(Exception exception, int handledSoFar)
    {
        if (health is null)
        {
            Guarded(() => logger.LogError(
                "No application-health reporter is available, so Bosun could not be marked faulted for the UI error above."));
            return;
        }

        // Re-entry: reporting raised another dispatcher exception on this stack. It is already counted
        // above; the report in progress will show the issue, so do not start a second one inside it.
        if (Interlocked.CompareExchange(ref reporting, 1, 0) != 0)
        {
            return;
        }

        try
        {
            health.ReportIssue(
                HealthIssueCodes.UiUnhandledException,
                HealthLevel.Faulted,
                IssueTitle,
                BuildDetail(exception, handledSoFar));
        }
        catch (Exception reportFailure)
        {
            Guarded(() => logger.LogError(
                reportFailure,
                "Could not report the UI error to the application-health model; Bosun is not marked faulted for it."));
        }
        finally
        {
            Volatile.Write(ref reporting, 0);
        }
    }

    private static string BuildDetail(Exception exception, int handledSoFar)
    {
        var message = exception.Message.Trim();
        if (message.Length > MaxMessageLength)
        {
            message = string.Concat(message.AsSpan(0, MaxMessageLength), "...");
        }

        var count = handledSoFar == 1
            ? "This is the first one since Bosun started."
            : $"{handledSoFar} such errors have occurred since Bosun started; this is the latest.";

        return $"{exception.GetType().FullName}: {message} {count} Mount supervision is still running, but the " +
            "window or tray may not be working properly. The full error is in the log. This stays until Bosun is restarted.";
    }

    private static void Guarded(Action action)
    {
        try
        {
            action();
        }
        catch
        {
            // A failing logger must not turn error handling into a second error.
        }
    }
}
