using System.Globalization;
using Bosun.Health;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bosun.Watchdog;

/// <summary>How this process was started, as far as restarts are concerned (bs-aoz).</summary>
/// <param name="Kind">Which restart flag was on the command line, or <see langword="null"/> for an
/// ordinary launch.</param>
public sealed record RestartLaunchInfo(RestartKind? Kind)
{
    public static readonly RestartLaunchInfo Ordinary = new((RestartKind?)null);

    public bool RestartedByWatchdog => Kind == RestartKind.Watchdog;

    public static RestartLaunchInfo FromArguments(IReadOnlyList<string> args) =>
        RestartHandoffArguments.TryGetHandoff(args, out var kind, out _) ? new RestartLaunchInfo(kind) : Ordinary;

    /// <summary>The running process. Not used by any default-suite test.</summary>
    public static RestartLaunchInfo ForCurrentProcess() =>
        FromArguments([.. Environment.GetCommandLineArgs().Skip(1)]);
}

/// <summary>The user dismissing the "Bosun restarted itself" notice.</summary>
public interface IRestartNotice
{
    /// <summary>Removes the notice from the health banner. A no-op if it is not showing.</summary>
    void Dismiss();
}

/// <summary>
/// After a watchdog restart, tells the user it happened (bs-aoz, ADR-020 Decision 5 follow-up). The
/// restart is silent by design -- the window stays hidden, because it usually happens when nobody is
/// there -- so without this a user who looks at Bosun later has no way to know it restarted itself, or
/// that something had stalled.
/// </summary>
/// <remarks>
/// <para>
/// Reports <see cref="HealthIssueCodes.WatchdogRestarted"/> as <see cref="HealthLevel.Degraded"/> when,
/// and only when, this process was launched with <c>--restarted-by-watchdog</c>. It clears when the user
/// dismisses it (<see cref="Dismiss"/>) or <see cref="Lifetime"/> (24 hours) after it was reported,
/// whichever is first. The clock is the injected <see cref="TimeProvider"/>.
/// </para>
/// <para>
/// <b>Where the reason comes from.</b> The old instance wrote it into the restart history file in the
/// same write that counted the restart (<see cref="IRestartHistoryStore.Save"/>). This reads it back. The
/// record is used only if it is recent (<see cref="RecordFreshness"/>): an older one belongs to an
/// earlier restart, and describing the wrong cause is worse than saying the cause was not recorded. If
/// there is no usable record the notice still appears, and says so.
/// </para>
/// </remarks>
public sealed class WatchdogRestartNotice(
    RestartLaunchInfo launch,
    IRestartHistoryStore history,
    IAppHealthReporter health,
    TimeProvider timeProvider,
    ILogger<WatchdogRestartNotice> logger,
    TimeZoneInfo? timeZone = null) : IHostedService, IRestartNotice, IDisposable
{
    /// <summary>How long the notice stays if nobody dismisses it. 24 hours.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    /// <summary>
    /// How old the restart history's reason may be and still be taken as the cause of THIS launch. The old
    /// instance writes it, launches this one, and exits within its 15 s bound; this one then waits up to
    /// 30 s for it. Ten minutes covers a slow machine many times over and still rejects the record of a
    /// restart from earlier in the day.
    /// </summary>
    public static readonly TimeSpan RecordFreshness = TimeSpan.FromMinutes(10);

    private readonly object gate = new();
    private ITimer? expiry;
    private bool reported;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!launch.RestartedByWatchdog)
        {
            return Task.CompletedTask;
        }

        var now = timeProvider.GetUtcNow();
        LastRestart? record = null;
        try
        {
            record = history.LoadLast();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The restart history could not be read for the restart notice; describing the restart without a reason");
        }

        var usable = record is not null && now - record.At >= TimeSpan.FromMinutes(-1) && now - record.At <= RecordFreshness
            ? record
            : null;
        var when = usable?.At ?? now;
        var clock = TimeZoneInfo.ConvertTime(when, timeZone ?? timeProvider.LocalTimeZone).ToString("HH:mm", CultureInfo.InvariantCulture);

        var detail = usable is null
            ? $"Bosun restarted itself at {clock} because its watchdog found mount supervision stalled. " +
              "The specific reason was not recorded. See the logs, or use Copy diagnostics."
            : $"Bosun restarted itself at {clock} because mount supervision stalled ({usable.Reason}). " +
              "See the logs, or use Copy diagnostics.";

        lock (gate)
        {
            health.ReportIssue(HealthIssueCodes.WatchdogRestarted, HealthLevel.Degraded, "Bosun restarted itself", detail);
            reported = true;
            expiry = timeProvider.CreateTimer(_ => Expire(), null, Lifetime, Timeout.InfiniteTimeSpan);
        }

        logger.LogWarning(
            "This Bosun was started by the watchdog to replace a stalled instance (restart at {Clock}, reason: {Reason}); " +
            "showing a notice for up to {Lifetime}",
            clock,
            usable?.Reason ?? "not recorded",
            Lifetime);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dismiss()
    {
        if (Clear("dismissed by the user"))
        {
            logger.LogInformation("The 'Bosun restarted itself' notice was dismissed by the user");
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            expiry?.Dispose();
            expiry = null;
        }
    }

    private void Expire()
    {
        if (Clear("expired"))
        {
            logger.LogInformation("The 'Bosun restarted itself' notice expired after {Lifetime}", Lifetime);
        }
    }

    private bool Clear(string how)
    {
        lock (gate)
        {
            if (!reported)
            {
                return false;
            }

            reported = false;
            expiry?.Dispose();
            expiry = null;
            health.ClearIssue(HealthIssueCodes.WatchdogRestarted);
            logger.LogDebug("Cleared {Code}: {How}", HealthIssueCodes.WatchdogRestarted, how);
            return true;
        }
    }
}
