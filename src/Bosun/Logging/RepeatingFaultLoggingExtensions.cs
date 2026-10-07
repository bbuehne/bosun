using Microsoft.Extensions.Logging;

namespace Bosun.Logging;

/// <summary>
/// Writes what <see cref="RepeatingFaultLogger"/> decided, so each call site is one line and the
/// wording of reminders and recoveries is the same everywhere.
/// </summary>
public static class RepeatingFaultLoggingExtensions
{
    /// <summary>
    /// Logs <paramref name="message"/> at <paramref name="level"/> on the first sighting of
    /// <paramref name="kind"/> (or when it changes); once per reminder interval, a Warning of the
    /// form "context: still failing: cause (N times since HH:mm)"; otherwise nothing.
    /// </summary>
    public static void LogRepeatingFault(
        this ILogger logger,
        RepeatingFaultLogger faults,
        string source,
        string kind,
        LogLevel level,
        Exception? exception,
        string message,
        string context,
        string cause,
        string noun = "times")
    {
        var seen = faults.Observe(source, kind);
        switch (seen.Action)
        {
            case FaultLogAction.Log:
                logger.Log(level, exception, "{Message}", message);
                break;
            case FaultLogAction.Remind:
                logger.LogWarning(
                    "{Context}: still failing: {Cause} ({Count} {Noun} since {Since:HH:mm})",
                    context, cause, seen.Count, noun, seen.Since);
                break;
        }
    }

    /// <summary>Logs one Information line if <paramref name="source"/> had been failing.</summary>
    public static void LogFaultRecovered(
        this ILogger logger, RepeatingFaultLogger faults, string source, string context, string noun = "times")
    {
        if (faults.Recover(source) is { } ended)
        {
            logger.LogInformation(
                "{Context}: working again after {Count} failed {Noun} since {Since:HH:mm}",
                context, ended.Count, noun, ended.Since);
        }
    }
}
