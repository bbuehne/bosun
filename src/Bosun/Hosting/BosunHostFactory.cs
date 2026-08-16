using System.IO;
using Bosun.Configuration;
using Bosun.SessionMonitor;
using Bosun.SessionMonitor.Interop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Bosun.Hosting;

/// <summary>
/// Builds the Bosun <see cref="IHost"/>: a Generic Host running in-process inside the WPF app
/// (Invariant I3 — never a Windows Service), wired to Serilog so <c>ILogger&lt;T&gt;</c>
/// injection works throughout.
/// </summary>
/// <remarks>
/// Services from later epics (<c>IHostConfigStore</c>, <c>IRcloneClient</c>, <c>IProbe</c>,
/// <c>IMountSupervisor</c>, <c>IFragmentWriter</c>, <c>ISessionMonitor</c>,
/// <c>ISystemEventSource</c>, and the hosted-service wrappers listed in
/// docs/ARCHITECTURE.md §2) are registered here as they arrive. Nothing is stubbed in E1.
/// </remarks>
public static class BosunHostFactory
{
    /// <summary>
    /// The output template every sink shares, so log lines look the same whether they land in
    /// the rolling file or the debug sink. E5 will log every mount state transition through
    /// this pipeline with host, from-state, to-state, and trigger — see docs/OPERATIONS.md.
    /// </summary>
    private const string LogOutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}{NewLine}{Message:lj}{NewLine}{Exception}";

    public static IHost CreateHost(BosunHostOptions? options = null)
    {
        options ??= BosunHostOptions.CreateDefault();

        Directory.CreateDirectory(options.LogDirectory);

        var serilogLogger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.Debug(outputTemplate: LogOutputTemplate)
            .WriteTo.File(
                Path.Combine(options.LogDirectory, "bosun-.log"),
                rollingInterval: RollingInterval.Day,
                outputTemplate: LogOutputTemplate)
            .CreateLogger();

        var builder = Host.CreateApplicationBuilder();

        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(serilogLogger, dispose: true);

        // Lazily constructed: nothing here touches config/hosts.toml until something actually
        // resolves IHostConfigStore. That keeps this factory (and every test that merely builds
        // or starts a host without resolving it) safe to run from a worktree, per CLAUDE.md's
        // worktree-safety rules -- Load() below does real, synchronous file I/O.
        builder.Services.AddSingleton<IHostConfigStore>(sp => HostConfigStore.Load(
            path: options.ConfigPath,
            reader: new FileConfigReader(),
            watcher: new FileSystemConfigWatcher(options.ConfigPath),
            timeProvider: TimeProvider.System));

        // E8 (bs-gme/bs-8dr/bs-8je): all Windows interop stays behind ISessionMonitor. Every
        // constructor here is inert -- no process enumeration, no CIM, no P/Invoke call happens
        // until something actually calls GetActiveSessions(), which keeps `dotnet build` /
        // `dotnet test` safe to run from a worktree (CLAUDE.md worktree-safety rules).
        builder.Services.AddSingleton<ISshProcessEnumerator, CimSshProcessEnumerator>();
        builder.Services.AddSingleton<ITcpConnectionReader, Win32TcpConnectionReader>();
        builder.Services.AddSingleton<ISessionMonitor, SshSessionMonitor>();

        return builder.Build();
    }
}
