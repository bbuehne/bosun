using Bosun.Hosting;
using Bosun.Rclone.Process;
using Microsoft.Extensions.Logging;

namespace Bosun.Health;

/// <summary>
/// The one application-health model (bs-yyg, ADR-020 Decision 4). Sources push observations in
/// (<see cref="IAppHealthReporter"/>); every push re-derives the full <see cref="AppHealth"/> from
/// ALL current inputs and publishes it if it changed (<see cref="IAppHealth"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why re-derive from state, not accumulate events.</b> "rclone is faulted" is a fact about the
/// present, so each observation overwrites the previous one and the issue list is recomputed. An
/// issue then cannot be left behind by a missed "cleared" event: rclone reporting Healthy is all it
/// takes for its issue to go.
/// </para>
/// <para>
/// <b>Time.</b> The clock is injected. Two things use it: each issue's
/// <see cref="HealthIssue.FirstSeenUtc"/>, and the startup grace period
/// (<see cref="AppHealthOptions.StartupGracePeriod"/>), whose end is scheduled on the injected
/// <see cref="TimeProvider"/> so that "rclone never came up" turns into an issue without anyone
/// having to report anything.
/// </para>
/// <para>
/// <b>Threading.</b> State changes under one lock; <see cref="IAppHealth.Changed"/> is raised after
/// the lock is released, on the thread that made the change.
/// </para>
/// </remarks>
public sealed class AppHealthService : IAppHealth, IAppHealthReporter, IDisposable
{
    private readonly TimeProvider timeProvider;
    private readonly ILogger<AppHealthService>? logger;
    private readonly object gate = new();
    private readonly DateTimeOffset graceEndsUtc;
    private ITimer? graceTimer;
    private bool graceElapsed;

    // Inputs. Everything in AppHealth is a pure function of these plus the clock.
    private RcloneProcessStatus rcloneStatus = RcloneProcessStatus.Stopped;
    private (RcloneProcessFaultKind Kind, string? Message)? rcloneFault;
    private bool configInvalid;
    private List<Spec> startupSpecs = [];
    private bool loopStarted;
    private bool loopRunning;
    private readonly Dictionary<string, Spec> reported = new(StringComparer.Ordinal);

    private AppHealth current;

    public AppHealthService(
        TimeProvider timeProvider,
        AppHealthOptions? options = null,
        ILogger<AppHealthService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        this.timeProvider = timeProvider;
        this.logger = logger;

        var grace = (options ?? new AppHealthOptions()).StartupGracePeriod;
        graceEndsUtc = timeProvider.GetUtcNow() + (grace > TimeSpan.Zero ? grace : TimeSpan.Zero);

        // Seeded under the lock-free constructor path: nothing can observe the instance yet.
        current = Derive(timeProvider.GetUtcNow(), previous: AppHealth.Healthy);

        if (grace > TimeSpan.Zero)
        {
            // One shot. When it fires, "still not up" stops being "starting" and becomes an issue.
            graceTimer = timeProvider.CreateTimer(_ => OnGraceElapsed(), null, grace, Timeout.InfiniteTimeSpan);
        }
    }

    public AppHealth Current
    {
        get
        {
            lock (gate)
            {
                return current;
            }
        }
    }

    public event EventHandler<AppHealthChangedEventArgs>? Changed;

    public void ReportIssue(string code, HealthLevel severity, string title, string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(detail);
        if (severity == HealthLevel.Ok)
        {
            throw new ArgumentOutOfRangeException(nameof(severity), severity, "An issue's severity must be Degraded or Faulted.");
        }

        Mutate(() => reported[code] = new Spec(code, severity, title, detail));
    }

    public void ClearIssue(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Mutate(() => reported.Remove(code));
    }

    public void ObserveRclone(RcloneProcessStatus status, RcloneProcessFaultKind faultKind, string? message) =>
        Mutate(() =>
        {
            rcloneStatus = status;
            switch (status)
            {
                case RcloneProcessStatus.Faulted:
                    rcloneFault = (faultKind, message);
                    break;
                case RcloneProcessStatus.Healthy:
                    rcloneFault = null;
                    break;

                // Starting/Stopped deliberately leave the last fault in place. rclone cycles
                // Faulted -> Starting -> Faulted while it retries, and dropping the real cause on
                // every Starting would flash the banner back to a generic message in between.
            }
        });

    public void ObserveStartup(StartupReadiness readiness)
    {
        ArgumentNullException.ThrowIfNull(readiness);

        // The placeholder every consumer sees before the first real publication claims "config
        // invalid / not checked yet" -- that is "not started", not a finding.
        if (ReferenceEquals(readiness, StartupReadiness.Initial))
        {
            return;
        }

        var specs = new List<Spec>();
        var invalid = readiness.ConfigState == ConfigReadinessState.Invalid;

        if (invalid)
        {
            var errors = readiness.ConfigErrors.Count > 0
                ? string.Join("\n", readiness.ConfigErrors)
                : "No further detail was reported.";
            specs.Add(new Spec(
                HealthIssueCodes.StartupConfigInvalid,
                HealthLevel.Faulted,
                "hosts.toml is invalid",
                $"Mounting is disabled until it is fixed and Bosun is restarted.\n{errors}"));
        }

        if (!readiness.WinFspInstalled)
        {
            specs.Add(new Spec(
                HealthIssueCodes.StartupWinFspMissing,
                HealthLevel.Faulted,
                "WinFsp is not installed",
                readiness.WinFspMessage));
        }

        if (!readiness.TerminalFragmentWritten && readiness.TerminalFragmentFaultMessage is { Length: > 0 } fragmentFault)
        {
            specs.Add(new Spec(
                HealthIssueCodes.StartupTerminalFragment,
                HealthLevel.Degraded,
                "Windows Terminal profiles could not be written",
                fragmentFault));
        }

        Mutate(() =>
        {
            configInvalid = invalid;
            startupSpecs = specs;
        });
    }

    public void ObserveSupervisorLoop(bool started, bool isRunning) =>
        Mutate(() =>
        {
            loopStarted = started;
            loopRunning = isRunning;
        });

    public void Dispose()
    {
        ITimer? timer;
        lock (gate)
        {
            timer = graceTimer;
            graceTimer = null;
        }

        timer?.Dispose();
    }

    private void OnGraceElapsed() =>
        // A flag as well as the clock comparison: a real timer can fire a hair before the wall clock
        // reads the deadline, and nothing would ever re-evaluate after that.
        Mutate(() => graceElapsed = true);

    private void Mutate(Action change)
    {
        lock (gate)
        {
            change();
        }

        Recompute();
    }

    private void Recompute()
    {
        AppHealth? published = null;

        lock (gate)
        {
            var next = Derive(timeProvider.GetUtcNow(), current);
            if (!next.SameContentAs(current))
            {
                current = next;
                published = next;
            }
        }

        if (published is null)
        {
            return;
        }

        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<AppHealthChangedEventArgs>>())
        {
            try
            {
                handler(this, new AppHealthChangedEventArgs(published));
            }
            catch (Exception ex)
            {
                // A subscriber's bug must not stop other subscribers, and this can run on a timer
                // thread where an escaping exception would take the process down.
                logger?.LogError(ex, "An AppHealth.Changed subscriber threw");
            }
        }
    }

    // ------------------------------------------------------------------------------------------
    // Derivation: inputs + clock -> AppHealth. Callers hold the lock (or are the constructor).
    // ------------------------------------------------------------------------------------------

    private AppHealth Derive(DateTimeOffset now, AppHealth previous)
    {
        var specs = new Dictionary<string, Spec>(StringComparer.Ordinal);

        var inGrace = !graceElapsed && now < graceEndsUtc;
        var isStarting = false;

        // rclone. A config that never loaded means rclone was never started, so "rclone is not
        // running" would only repeat the real cause (startup.config-invalid) under another name.
        if (!configInvalid && rcloneStatus != RcloneProcessStatus.Healthy)
        {
            if (rcloneFault is { } fault)
            {
                var spec = DescribeRcloneFault(fault.Kind, fault.Message);
                specs[spec.Code] = spec;
            }
            else if (inGrace)
            {
                isStarting = true;
            }
            else
            {
                var spec = new Spec(
                    HealthIssueCodes.RcloneNotRunning,
                    HealthLevel.Faulted,
                    "rclone is not running",
                    "rclone has not reported healthy since Bosun started, and no failure was reported. "
                    + "Mounting is unavailable until it does.");
                specs[spec.Code] = spec;
            }
        }

        foreach (var spec in startupSpecs)
        {
            specs[spec.Code] = spec;
        }

        if (loopStarted && !loopRunning)
        {
            var spec = new Spec(
                HealthIssueCodes.SupervisorLoopStopped,
                HealthLevel.Faulted,
                "Mount supervision has stopped",
                "The loop that probes, mounts and unmounts hosts is no longer running. No host is being "
                + "probed or remounted until Bosun is restarted.");
            specs[spec.Code] = spec;
        }

        // Reported last: a source that names a code explicitly overrides a derived issue of that code.
        foreach (var spec in reported.Values)
        {
            specs[spec.Code] = spec;
        }

        var firstSeen = previous.Issues.ToDictionary(i => i.Code, i => i.FirstSeenUtc, StringComparer.Ordinal);

        var issues = specs.Values
            .Select(s => new HealthIssue(
                s.Code, s.Severity, s.Title, s.Detail, firstSeen.TryGetValue(s.Code, out var seen) ? seen : now))
            .OrderByDescending(i => i.Severity)
            .ThenBy(i => i.FirstSeenUtc)
            .ThenBy(i => i.Code, StringComparer.Ordinal)
            .ToList();

        return new AppHealth
        {
            Level = issues.Count == 0 ? HealthLevel.Ok : issues.Max(i => i.Severity),
            Issues = issues,
            IsStarting = isStarting,
        };
    }

    private static Spec DescribeRcloneFault(RcloneProcessFaultKind kind, string? message)
    {
        var detail = string.IsNullOrWhiteSpace(message) ? "rclone reported no further detail." : message;

        var (code, title) = kind switch
        {
            RcloneProcessFaultKind.ExecutableNotFound =>
                (HealthIssueCodes.RcloneExecutableNotFound, "rclone was not found"),
            RcloneProcessFaultKind.LaunchFailed =>
                (HealthIssueCodes.RcloneLaunchFailed, "rclone could not be started"),
            RcloneProcessFaultKind.HealthCheckFailed =>
                (HealthIssueCodes.RcloneHealthCheckFailed, "rclone started but is not responding"),
            RcloneProcessFaultKind.HealthCheckUnauthorized =>
                (HealthIssueCodes.RcloneUnauthorized, "Another rclone is answering on Bosun's control port"),
            RcloneProcessFaultKind.ProcessExitedBeforeHealthy =>
                (HealthIssueCodes.RcloneExitedBeforeHealthy, "rclone exited right after starting"),
            RcloneProcessFaultKind.PortHeldByOtherProcess =>
                (HealthIssueCodes.RclonePortHeld, "Another program is using rclone's control port"),
            RcloneProcessFaultKind.ProcessExitedUnexpectedly =>
                (HealthIssueCodes.RcloneExitedUnexpectedly, "rclone stopped unexpectedly"),

            // FaultKind.None with Status Faulted should not happen; it still must not hide the fault.
            _ => (HealthIssueCodes.RcloneNotRunning, "rclone is not running"),
        };

        return new Spec(code, HealthLevel.Faulted, title, detail);
    }

    private sealed record Spec(string Code, HealthLevel Severity, string Title, string Detail);
}
