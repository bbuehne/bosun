using System.IO;
using System.Net.Http;
using Bosun.Configuration;
using Bosun.Probe;
using Bosun.Rclone;
using Bosun.Rclone.Process;
using Bosun.SessionMonitor;
using Bosun.SessionMonitor.Interop;
using Bosun.Supervisor;
using Bosun.Terminal;
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

    /// <param name="options">See <see cref="BosunHostOptions"/>.</param>
    /// <param name="registerStartupOrchestrator">
    /// Production (the default) registers <see cref="StartupOrchestrator"/> as the host's one
    /// <see cref="IHostedService"/>, which -- on <c>host.StartAsync()</c> -- loads config, starts
    /// the real <c>rclone rcd</c> child process, and runs the mount supervisor (ADR-012 Decision
    /// 1, bs-6f9). Tests that merely build or start/stop a host without exercising that sequence
    /// must pass <see langword="false"/>: starting it for real spawns a real process and can touch
    /// a real drive letter, which CLAUDE.md's worktree-safety rules forbid in the default test
    /// suite. This is the fix bs-127/ADR-012 prescribe for the test that used to rely on
    /// <c>IHostConfigStore</c> staying unresolved through <c>host.StartAsync()</c> — fix the test
    /// (build without the orchestrator), not the architecture.
    /// </param>
    public static IHost CreateHost(BosunHostOptions? options = null, bool registerStartupOrchestrator = true)
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

        // Lazily constructed: nothing here touches config/hosts.toml merely from calling
        // CreateHost() -- only from something actually resolving IHostConfigStore. That keeps
        // BUILDING a host (this method returning) safe to run from a worktree unconditionally.
        // STARTING a host is a different story as of bs-6f9/ADR-012: with the default
        // registerStartupOrchestrator: true, host.StartAsync() now deliberately resolves this
        // (via StartupOrchestrator, the one place allowed to) as its very first step. Tests that
        // start a host without exercising that must pass registerStartupOrchestrator: false --
        // see that parameter's doc above and BosunHostFactoryTests.
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

        // bs-ard: one random rc credential for the lifetime of this Bosun process, shared by
        // IRcloneClient (Basic auth header, set once below) and RcloneProcessService (passed to
        // every `rclone rcd` launch/restart attempt as RCLONE_RC_USER/RCLONE_RC_PASS env vars) --
        // see the class remarks on RcloneRcCredential for why ONE secret across every restart,
        // not one per restart.
        builder.Services.AddSingleton(RcloneRcCredential.CreateRandom());

        // E3 (bs-tg9/bs-5mt/bs-e26): IRcloneClient is a thin HttpClient wrapper -- constructing
        // it, and the HttpClient it wraps, does no I/O (no socket touched until a request is
        // actually sent), matching the worktree-safety rule above. The base address is bound
        // to `global.rclone_rc_port`, resolved lazily from IHostConfigStore the first time this
        // is resolved -- never a non-loopback address. The rc API requires Basic auth as of
        // bs-ard; RcloneClient's constructor sets that header from the shared RcloneRcCredential.
        builder.Services.AddSingleton<IRcloneClient>(sp =>
        {
            var global = sp.GetRequiredService<IHostConfigStore>().Current.Global;
            var httpClient = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{global.RcloneRcPort}/"),
            };
            return new RcloneClient(httpClient, sp.GetRequiredService<RcloneRcCredential>());
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

        // E7c (bs-k41): constructing IFragmentWriter does no I/O -- no directory is created and no
        // file is touched until something actually calls WriteAsync -- matching the worktree-safety
        // rule every other registration here follows. FragmentWriterOptions.CreateDefault() resolves
        // the verified real path (bs-3ir): %LOCALAPPDATA%\Microsoft\Windows Terminal\Fragments\Bosun\
        // bosun.json.
        builder.Services.AddSingleton<IFragmentFileSystem, RealFragmentFileSystem>();
        builder.Services.AddSingleton(FragmentWriterOptions.CreateDefault());
        builder.Services.AddSingleton<IFragmentWriter, FragmentWriter>();

        // bs-3lt: registered as a resolvable singleton, NOT constructor-injected anywhere and NOT
        // an IHostedService -- same reasoning as RcloneProcessService below. Its constructor takes
        // IHostConfigStore, which transitively means this factory lambda is only safe to run AFTER
        // StartupOrchestrator has confirmed config loaded successfully; merely registering it here
        // does no I/O and resolves nothing eagerly (DI registration is lazy), so it stays safe to
        // build a host from a worktree. StartupOrchestrator is the one and only place that ever
        // calls GetRequiredService<FragmentRewriteCoordinator>(), inside its own try/catch -- see
        // its class remarks.
        builder.Services.AddSingleton(sp => new FragmentRewriteCoordinator(
            sp.GetRequiredService<IFragmentWriter>(),
            sp.GetRequiredService<IHostConfigStore>(),
            sp.GetRequiredService<ILogger<FragmentRewriteCoordinator>>()));

        // RcloneProcessService owns the one long-lived `rclone rcd` child (Invariant I3/I4;
        // docs/ARCHITECTURE.md §2). Registered as a resolvable singleton -- NOT via
        // AddHostedService (see StartupOrchestrator's class remarks on why: ADR-012 wants ONE
        // hosted service owning ordering, not several independent registrations racing each
        // other). StartupOrchestrator resolves this singleton and calls its
        // StartAsync/StopAsync directly, in the sequence ADR-012 Decision 1 specifies. Resolving
        // it still ultimately reads global.rclone_rc_port/rclone_config_path from
        // IHostConfigStore, same as before -- what changed (bs-127) is WHO is allowed to trigger
        // that resolution and when: only StartupOrchestrator, and only after it has confirmed
        // config loaded successfully. %APPDATA%-style tokens in global.rclone_config_path are
        // expanded at the point of use in this factory lambda, the same way
        // ConfigValidator.ExpandHome expands identity_file's leading `~` at its point of use
        // rather than at bind time.
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
                sp.GetRequiredService<ILogger<RcloneProcessService>>(),
                sp.GetRequiredService<RcloneRcCredential>());
        });

        // MountSupervisor (bs-psq; docs/ARCHITECTURE.md §4) -- registered as both its concrete
        // type (StartupOrchestrator needs RunAsync, which is deliberately not on IMountSupervisor
        // -- see its class remarks) and the interface (for future consumers, e.g. the tray UI,
        // that only need commands/snapshots and should not see RunAsync at all).
        builder.Services.AddSingleton(sp => new MountSupervisor(
            sp.GetRequiredService<IHostConfigStore>(),
            sp.GetRequiredService<IRcloneClient>(),
            sp.GetRequiredService<IProbe>(),
            TimeProvider.System,
            sp.GetRequiredService<ILogger<MountSupervisor>>()));
        builder.Services.AddSingleton<IMountSupervisor>(sp => sp.GetRequiredService<MountSupervisor>());

        // ADR-012 Decision 1/bs-6f9: the one hosted service that owns the ordered startup
        // sequence. Gated by registerStartupOrchestrator so tests can build/start a host without
        // it -- see the parameter doc above.
        if (registerStartupOrchestrator)
        {
            builder.Services.AddSingleton<IFirstRunConfigBootstrapper, FirstRunConfigBootstrapper>();
            builder.Services.AddSingleton(sp => new StartupOrchestrator(
                sp,
                options,
                sp.GetRequiredService<IFirstRunConfigBootstrapper>(),
                sp.GetRequiredService<IWinFspDetector>(),
                sp.GetRequiredService<ILogger<StartupOrchestrator>>()));
            builder.Services.AddHostedService(sp => sp.GetRequiredService<StartupOrchestrator>());
        }

        return builder.Build();
    }
}
