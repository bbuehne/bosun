using System.IO;
using System.Net.Http;
using Bosun.Configuration;
using Bosun.Probe;
using Bosun.Rclone;
using Bosun.Rclone.Process;
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

        // E4 (bs-pk4/bs-k8p/bs-fhp): the shallow-probe transport has no unresolved dependencies,
        // so it is safe to register now -- constructing it does nothing (no socket touched until
        // ConnectAsync is called), matching the worktree-safety rule above.
        builder.Services.AddSingleton<ITcpProbeTransport, TcpProbeTransport>();

        // E3 (bs-tg9/bs-5mt/bs-e26): IRcloneClient is a thin HttpClient wrapper -- constructing
        // it, and the HttpClient it wraps, does no I/O (no socket touched until a request is
        // actually sent), matching the worktree-safety rule above. The base address is bound
        // to `global.rclone_rc_port`, resolved lazily from IHostConfigStore the first time this
        // is resolved -- never a non-loopback address (the rc API is unauthenticated).
        builder.Services.AddSingleton<IRcloneClient>(sp =>
        {
            var global = sp.GetRequiredService<IHostConfigStore>().Current.Global;
            var httpClient = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{global.RcloneRcPort}/"),
            };
            return new RcloneClient(httpClient);
        });

        builder.Services.AddSingleton<IRcloneRemoteProvisioner, RcloneRemoteProvisioner>();
        builder.Services.AddSingleton<IWinFspDetector, WinFspDetector>();

        // IRemoteRootLister is the seam E4 defined and deliberately left unimplemented (its own
        // doc comment: "E3 owns IRcloneClient and will provide the real implementation once it
        // lands"). HostProbe/IProbe can now be registered too -- this is the exact line E4 left
        // as a comment ("Wire up builder.Services.AddSingleton<IProbe, HostProbe>() once E3
        // provides it").
        builder.Services.AddSingleton<IRemoteRootLister, RemoteRootLister>();
        builder.Services.AddSingleton<IProbe, HostProbe>();

        // RcloneProcessService owns the one long-lived `rclone rcd` child (Invariant I3/I4;
        // docs/ARCHITECTURE.md §2). Registered as a resolvable singleton, but deliberately NOT
        // via AddHostedService yet: doing so would make host.StartAsync() resolve
        // IHostConfigStore unconditionally (RcloneProcessServiceOptions needs
        // global.rclone_rc_port/rclone_config_path), which breaks the deliberately-lazy pattern
        // established for IHostConfigStore above and asserted on by
        // BosunHostFactoryTests.Host_StartsAndStopsCleanlyWithoutAWindow (that test starts a host
        // whose ConfigPath does not point at a real file, specifically to prove nothing forces a
        // config load just from starting the host). Whether RcloneProcessService should start
        // before, after, or interleaved with config validation and IRcloneRemoteProvisioner
        // (bs-e26) is a startup-ordering decision for whichever epic wires the whole app
        // together (E5/E7) -- not something to guess at here. %APPDATA%-style tokens in
        // global.rclone_config_path are expanded at the point of use in this factory lambda, the
        // same way ConfigValidator.ExpandHome expands identity_file's leading `~` at its point
        // of use rather than at bind time.
        builder.Services.AddSingleton<IRcloneProcessLauncher, Win32RcloneProcessLauncher>();
        builder.Services.AddSingleton(sp =>
        {
            var global = sp.GetRequiredService<IHostConfigStore>().Current.Global;
            var rcloneProcessOptions = new RcloneProcessServiceOptions
            {
                RcloneRcPort = global.RcloneRcPort,
                RcloneConfigPath = Environment.ExpandEnvironmentVariables(global.RcloneConfigPath),
            };

            return new RcloneProcessService(
                sp.GetRequiredService<IRcloneProcessLauncher>(),
                sp.GetRequiredService<IRcloneClient>(),
                rcloneProcessOptions,
                TimeProvider.System,
                sp.GetRequiredService<ILogger<RcloneProcessService>>());
        });

        return builder.Build();
    }
}
