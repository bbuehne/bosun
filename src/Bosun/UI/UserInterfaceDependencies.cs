using Bosun.Configuration;
using Bosun.Diagnostics;
using Bosun.Health;
using Bosun.SessionMonitor;
using Bosun.Supervisor;
using Bosun.Watchdog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bosun.UI;

/// <summary>
/// Everything the tray icon and status window are built from (bs-4js), resolved so that a service
/// the container cannot build costs the UI that one capability and nothing else.
/// </summary>
/// <remarks>
/// A property that is not nullable is always usable: when the real service could not be resolved it
/// holds an inert stand-in (<see cref="UnavailableMountSupervisor"/>, <see cref="EmptyHostConfigStore"/>,
/// <see cref="NoSessionMonitor"/>). The nullable ones are optional features the UI already knows how to
/// run without (the host editor, "Copy diagnostics").
/// </remarks>
public sealed record UserInterfaceDependencies
{
    public required IMountSupervisor Supervisor { get; init; }

    public required IHostConfigStore ConfigStore { get; init; }

    public required ISessionMonitor SessionMonitor { get; init; }

    public required TimeProvider TimeProvider { get; init; }

    public required IAppHealth AppHealth { get; init; }

    /// <summary>Null when the diagnostics bundle builder could not be built. It needs the real
    /// supervisor, so a failed supervisor takes "Copy diagnostics" with it (reported, not hidden).</summary>
    public CopyDiagnosticsCommand? Diagnostics { get; init; }

    /// <summary>Null when the host config could not be loaded or the writer could not be built; the
    /// window then omits the host editor rather than offering an editor that cannot save.</summary>
    public IHostConfigWriter? ConfigWriter { get; init; }

    /// <summary>Null when the watchdog-restart notice could not be built; the banner then simply has
    /// nothing to dismiss.</summary>
    public IRestartNotice? RestartNotice { get; init; }

    /// <summary>True when the real <see cref="IMountSupervisor"/> could not be resolved and
    /// <see cref="Supervisor"/> is the inert stand-in.</summary>
    public bool SupervisorIsStandIn { get; init; }

    /// <summary>True when the real <see cref="IHostConfigStore"/> could not be resolved (an invalid
    /// first load) and <see cref="ConfigStore"/> is the empty stand-in.</summary>
    public bool ConfigStoreIsStandIn { get; init; }
}

/// <summary>
/// Resolves <see cref="UserInterfaceDependencies"/> from the started host (bs-4js). Extracted from
/// <c>App.InitializeUserInterface</c> so the rule it enforces is unit-testable without WPF:
/// <b>no service that can fail to build may stop the UI from being built.</b>
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this matters.</b> The condition that most needs reporting -- the supervisor failed to
/// start, the config is invalid -- is the same condition that makes the container unable to build the
/// things the UI used to resolve unconditionally, so a UI that required them was destroyed by
/// exactly the faults it exists to report. That is what bs-4js was: a Bosun that ran with no tray icon
/// and no window, supervising nothing, looking like nothing was wrong.
/// </para>
/// <para>
/// <b>What is NOT stood in for.</b> <see cref="IAppHealth"/> is resolved strictly. It depends on
/// neither config nor the supervisor, and it is the channel every fault is reported through -- a UI
/// with no health model cannot report anything, so a failure there is left to <c>App</c>'s outer
/// catch (which logs it) rather than papered over. (The clock falls back to the system clock: that is
/// what the container registers anyway.)
/// </para>
/// <para>
/// Each stand-in is logged once, at Error, with the exception that forced it. The user-facing
/// explanation does not come from here: it comes from the app-health model, which the orchestrator
/// fills from the same underlying fault.
/// </para>
/// </remarks>
public static class UserInterfaceDependencyResolver
{
    public static UserInterfaceDependencies Resolve(IServiceProvider services, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);

        var configStore = TryResolve<IHostConfigStore>(services, logger, "the host config store", out var configStoreOk);
        var supervisor = TryResolve<IMountSupervisor>(services, logger, "the mount supervisor", out var supervisorOk);
        var sessionMonitor = TryResolve<ISessionMonitor>(services, logger, "the session monitor", out _, LogLevel.Warning);

        return new UserInterfaceDependencies
        {
            Supervisor = supervisor ?? new UnavailableMountSupervisor(),
            ConfigStore = configStore ?? new EmptyHostConfigStore(),
            SessionMonitor = sessionMonitor ?? new NoSessionMonitor(),
            // The system clock is what the container would have handed out (BosunHostFactory registers
            // TimeProvider.System), and the unregistered TimeProvider is the very fault bs-4js was
            // found with -- so a missing registration must not be what stops the UI.
            TimeProvider = TryResolve<TimeProvider>(services, logger, "the time provider", out _, LogLevel.Warning)
                ?? TimeProvider.System,
            AppHealth = services.GetRequiredService<IAppHealth>(),
            Diagnostics = TryResolve<CopyDiagnosticsCommand>(services, logger, "the diagnostics command", out _, LogLevel.Warning),
            ConfigWriter = TryResolve<IHostConfigWriter>(services, logger, "the host config writer", out _, LogLevel.Warning),
            RestartNotice = TryResolve<IRestartNotice>(services, logger, "the restart notice", out _, LogLevel.Warning),
            SupervisorIsStandIn = !supervisorOk,
            ConfigStoreIsStandIn = !configStoreOk,
        };
    }

    private static T? TryResolve<T>(IServiceProvider services, ILogger logger, string what, out bool resolved, LogLevel level = LogLevel.Error)
        where T : class
    {
        try
        {
            var service = services.GetService<T>();
            resolved = service is not null;
            if (service is null)
            {
                logger.Log(level, "The UI could not resolve {What}: it is not registered. The tray icon and window are built without it.", what);
            }

            return service;
        }
        catch (Exception ex)
        {
            resolved = false;
            logger.Log(level, ex, "The UI could not resolve {What}. The tray icon and window are built without it.", what);
            return null;
        }
    }
}
