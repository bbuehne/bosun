using Bosun.Configuration;
using Bosun.Health;
using Bosun.Hosting;
using Bosun.Rclone;
using Bosun.Rclone.Process;
using Bosun.Supervisor;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bosun.Diagnostics;

/// <summary>
/// Registers "Copy diagnostics" (bs-ds3, bs-1kt). Lives apart from <c>BosunHostFactory</c> so a test
/// can register the same wiring over fakes and resolve it the way the UI does.
/// </summary>
public static class DiagnosticsServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IDiagnosticsBundleBuilder"/> and <see cref="CopyDiagnosticsCommand"/>. The
    /// machine-reading seams (<see cref="IProcessLister"/>, <see cref="IDriveLister"/>,
    /// <see cref="IWindowsEventSource"/>, ...), <see cref="IExplorerRevealer"/> and
    /// <see cref="IDiagnosticsErrorPresenter"/> are the caller's to register, so a test supplies fakes
    /// and nothing here can reach the real process table or Event Log.
    /// </summary>
    /// <remarks>
    /// <b>What must stay lazy (bs-1kt).</b> The supervisor, the rclone client and the rc port are read
    /// when a section runs, inside that section's own try block. They are the services the container
    /// cannot build when Bosun is faulted (the supervisor's dependencies throw, or the config is
    /// invalid and the rc client's port cannot be read). Resolving them here, at construction, made
    /// <see cref="CopyDiagnosticsCommand"/> unresolvable in exactly that case, so the tray item only
    /// logged. Everything resolved eagerly below has no dependency on config or the supervisor.
    /// </remarks>
    public static IServiceCollection AddDiagnosticsBundle(
        this IServiceCollection services, string logDirectory, string configPath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);

        services.AddSingleton<IDiagnosticsBundleBuilder>(sp => new DiagnosticsBundleBuilder(
            DiagnosticsBundleOptions.ForLogDirectory(
                logDirectory,
                configPath,
                () => sp.GetRequiredService<IHostConfigStore>().Current.Global.RcloneRcPort),
            sp.GetRequiredService<IAppHealth>(),
            () => sp.GetRequiredService<IMountSupervisor>(),
            () => sp.GetRequiredService<IRcloneClient>(),
            sp.GetRequiredService<IPortOwnerResolver>(),
            sp.GetRequiredService<IProcessInspector>(),
            sp.GetRequiredService<IProcessLister>(),
            sp.GetRequiredService<IDriveLister>(),
            sp.GetRequiredService<IWindowsEventSource>(),
            sp.GetRequiredService<IEnvironmentInfoSource>(),
            // Both fall back rather than fail: an unregistered clock or credential is a fault the
            // diagnostics must survive (the TimeProvider one is what bs-4js was found with). No
            // credential means no by-value redaction, but there is then no credential to leak.
            sp.GetService<RcloneRcCredential>(),
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            // Null when the orchestrator is not registered (a host built for a test); the bundle
            // then says so rather than failing.
            () => sp.GetService<StartupOrchestrator>()?.Current,
            sp.GetRequiredService<ILogger<DiagnosticsBundleBuilder>>()));

        services.AddSingleton(sp => new CopyDiagnosticsCommand(
            sp.GetRequiredService<IDiagnosticsBundleBuilder>(),
            sp.GetRequiredService<IExplorerRevealer>(),
            sp.GetRequiredService<IDiagnosticsErrorPresenter>(),
            sp.GetRequiredService<ILogger<CopyDiagnosticsCommand>>()));

        return services;
    }
}
