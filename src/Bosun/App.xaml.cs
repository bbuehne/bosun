using System.Windows;
using System.Windows.Threading;
using Bosun.Configuration;
using Bosun.Hosting;
using Bosun.SessionMonitor;
using Bosun.Status;
using Bosun.Supervisor;
using Bosun.UI;
using Bosun.UI.Autostart;
using Bosun.UI.HostEditor;
using Bosun.UI.Tray;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bosun;

/// <summary>
/// Interaction logic for App.xaml. Owns the <see cref="IHost"/> lifetime: built and started in
/// <see cref="OnStartup"/>, stopped with a bounded timeout in <see cref="OnExit"/>. Also the
/// composition root for the UI layer (bs-ww9.3 / bs-ww9.5, ADR-018) -- see
/// <see cref="InitializeUserInterface"/> -- constructed once the host has started successfully,
/// never declared via <c>StartupUri</c>; see App.xaml.
/// </summary>
public partial class App : Application
{
    private static readonly TimeSpan HostStopTimeout = TimeSpan.FromSeconds(5);

    // bs-2wa / ADR-018 Decision 6: shared with both SingleInstance and _bootstrap (constructed
    // below, in the App() constructor) -- BootstrapOrchestrator.TryCreateHost() asserts IsOwned on
    // THIS SAME instance before it will build a host, which is what makes the ordering in
    // OnStartup structurally enforced rather than comment-enforced: reordering the two calls
    // there would make TryCreateHost() throw instead of silently touching the log directory /
    // rclone / fragment before a second launch discovers it should exit. See
    // BootstrapOrchestrator's class remarks. (A field initializer can't reference another
    // instance field -- CS0236 -- so this wiring lives in an explicit constructor instead of the
    // field-initializer style the rest of this class otherwise uses.)
    private readonly MutexSingleInstanceGuard _singleInstanceGuard;

    // bs-2wa / ADR-018 Decision 6: TryBecomePrimary() must be called as the very first side effect
    // of OnStartup, before _bootstrap.TryCreateHost() below -- BosunHostFactory.CreateHost's first
    // line is Directory.CreateDirectory(options.LogDirectory), so this MUST run first or a second
    // launch would have already touched the log directory by the time it discovers it should
    // exit. Exposed so InitializeUserInterface can subscribe to ActivationRequested without
    // SingleInstanceOrchestrator itself needing to know anything about windows.
    internal readonly SingleInstanceOrchestrator SingleInstance;

    // Owns the window between process start and the point ILogger<App> becomes resolvable --
    // see BootstrapOrchestrator's doc comment (bs-ipq / ADR-012 Decision 2). Constructed with the
    // real, production seams; tests exercise BootstrapOrchestrator directly with fakes instead of
    // constructing an App.
    private readonly BootstrapOrchestrator _bootstrap;

    private IHost? _host;
    private ILogger<App>? _logger;

    // Polls the supervisor for the window and tray (bs-ww9.6). Held so OnExit can stop its
    // timer; null when the UI never got as far as being constructed.
    private StatusReadModel? _statusReadModel;

    // bs-ww9.3 / bs-ww9.5 / ADR-018: constructed in InitializeUserInterface, once the host has
    // started successfully. Null if the host failed to start (nothing to show), or if UI
    // construction itself failed -- see InitializeUserInterface's own try/catch. Bosun keeps
    // running headless (mounting and probing unaffected) in either case; a UI failure must never
    // take the supervisor down with it.
    private MainWindow? _mainWindow;
    private MainWindowController? _mainWindowController;
    private TrayIconController? _trayIconController;

    public App()
    {
        _singleInstanceGuard = new MutexSingleInstanceGuard();
        SingleInstance = new SingleInstanceOrchestrator(_singleInstanceGuard, new EventWaitHandleActivationChannel());
        _bootstrap = new BootstrapOrchestrator(
            () => BosunHostFactory.CreateHost(),
            new MessageBoxCatastrophicStartupNotifier(),
            new BootstrapDiagnosticSink(),
            _singleInstanceGuard);
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Must be the first thing this method does with side effects (see the field's doc
        // comment). A second launch has, by the time TryBecomePrimary returns false, already
        // signalled the running instance to activate its window (SingleInstanceOrchestrator's own
        // contract) -- so this launch's job is simply to exit as if it had succeeded: no error
        // dialog, no scary log line, no host built, nothing else touched. The user double-clicked
        // Bosun because they wanted to see it; the running instance coming to the foreground *is*
        // that outcome (ADR-018 "Why one instance becomes load-bearing now").
        if (!SingleInstance.TryBecomePrimary())
        {
            SingleInstance.Dispose();
            Shutdown(0);
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // TryCreateHost covers CreateHost() throwing (bs-ipq): it durably records the failure and
        // blocks on the catastrophic notifier before returning null, so this is the one place in
        // the whole startup contract where a null host means "already reported, just exit".
        _host = _bootstrap.TryCreateHost();
        if (_host is null)
        {
            Shutdown(-1);
            return;
        }

        _logger = _host.Services.GetRequiredService<ILogger<App>>();

        try
        {
            await _host.StartAsync();
            _logger.LogInformation("Bosun host started.");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Bosun host failed to start; shutting down.");
            Shutdown(-1);
            return;
        }

        try
        {
            InitializeUserInterface(e.Args);
        }
        catch (Exception ex)
        {
            // ADR-012's fail-soft principle, applied to the UI layer itself: a failure to
            // construct the tray icon or main window must never take the supervisor down with it
            // -- mounting and probing continue headless. There is no catastrophic-failure channel
            // for this (the logger, and therefore normal logging, is already available by this
            // point), so a logged error is the whole response.
            _logger.LogError(ex, "Failed to initialize the tray icon and main window; Bosun continues running headless this session");
        }
    }

    /// <summary>
    /// Constructs the UI layer this epic adds (bs-ww9.3 / bs-ww9.5, ADR-018) and wires it to the
    /// already-running host. Deliberately NOT part of <see cref="Hosting.BosunHostFactory"/> --
    /// see <see cref="IStatusReadModel"/>'s remarks on why UI composition stays out of that file.
    /// </summary>
    /// <remarks>
    /// The read-model (<see cref="StatusReadModel"/>, bs-ww9.6) is started here and stopped in
    /// <see cref="OnExit"/>. It polls rather than subscribing, because the supervisor exposes no
    /// change event -- see its own remarks. Both the window and the tray read from the SAME
    /// instance, which is what guarantees the tray icon and the window can never disagree about
    /// aggregate health (ADR-012 Decision 3).
    /// </remarks>
    private void InitializeUserInterface(string[] args)
    {
        var services = _host!.Services;
        var supervisor = services.GetRequiredService<IMountSupervisor>();
        var configStore = services.GetRequiredService<IHostConfigStore>();

        var readModel = new StatusReadModel(
            supervisor,
            configStore,
            services.GetRequiredService<ISessionMonitor>(),
            services.GetRequiredService<TimeProvider>(),
            services.GetRequiredService<ILogger<StatusReadModel>>());
        readModel.Start();
        _statusReadModel = readModel;
        IStatusReadModel statusReadModel = readModel;
        var launcher = new Win32ExternalLauncher(services.GetRequiredService<ILogger<Win32ExternalLauncher>>());
        var actionDispatcher = new HostActionDispatcher(
            supervisor, launcher, services.GetRequiredService<ILogger<HostActionDispatcher>>());

        _mainWindow = new MainWindow { Logger = services.GetRequiredService<ILogger<MainWindow>>() };
        _mainWindow.Configure(statusReadModel, actionDispatcher);

        // bs-ww9.8 / ADR-019: host create/edit/delete. IHostConfigWriter is registered by the
        // config-writer delivery (bs-ww9.8's other half); resolved here, not constructed, so this
        // class never owns (or reinvents) that seam.
        // The supervisor is passed so DeleteAsync can unmount a host before its definition is
        // removed (Invariant I2). Without it, deleting a mounted host leaves a live drive letter
        // with nothing in hosts.toml describing it -- which is what shipped when the writer and the
        // form each assumed the other did the drain.
        var hostConfigWriter = services.GetRequiredService<IHostConfigWriter>();
        var hostEditorController = new HostEditorController(
            hostConfigWriter,
            configStore,
            supervisor,
            services.GetRequiredService<ILogger<HostEditorController>>());
        _mainWindow.ConfigureHostEditor(
            hostEditorController,
            configStore,
            new Win32IdentityFilePicker(),
            new Win32DriveLetterInspector());

        // ADR-018 rule 3: closing the window hides it to tray, never exits. The only way to exit
        // is the tray's "Exit" item (TrayIconController) or a future explicit Exit command, both
        // of which call Application.Shutdown() directly rather than Window.Close() -- Closing is
        // therefore never raised on the real exit path, only on the user clicking the window's own
        // X button.
        _mainWindow.Closing += (_, closingArgs) =>
        {
            closingArgs.Cancel = true;
            _mainWindowController?.HideToTray();
        };

        var placementStore = new JsonWindowPlacementStore(
            JsonWindowPlacementStore.GetDefaultFilePath(),
            services.GetRequiredService<ILogger<JsonWindowPlacementStore>>());

        _mainWindowController = new MainWindowController(
            _mainWindow,
            placementStore,
            new WpfVirtualScreenProvider(),
            services.GetRequiredService<ILogger<MainWindowController>>());

        var autostart = new AutostartRegistration(
            new RegistryAutostartStore(),
            logger: services.GetRequiredService<ILogger<AutostartRegistration>>());

        _trayIconController = new TrayIconController(
            statusReadModel,
            supervisor,
            actionDispatcher,
            _mainWindowController,
            autostart,
            services.GetRequiredService<ILogger<TrayIconController>>());

        // bs-2wa / ADR-018 Decision 6: a second launch is a user asking to see Bosun. Raised on
        // EventWaitHandleActivationChannel's dedicated listener thread, never the UI thread -- see
        // its class remarks -- so this must marshal back onto the dispatcher before touching any
        // WPF object.
        SingleInstance.ActivationRequested += (_, _) =>
            Dispatcher.BeginInvoke(() => _mainWindowController?.ShowAndActivate());

        _mainWindowController.Initialize(LaunchContextDetector.Detect(args));
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        // Persists whatever geometry the window currently has, whether or not it is visible right
        // now -- harmless when HideToTray already persisted it (the common path), and the only
        // thing that captures a final geometry for a user who exits via the tray's "Exit" item
        // while the window is open (that path never goes through HideToTray).
        _mainWindowController?.PersistCurrentPlacement();
        _trayIconController?.Dispose();

        // Stop polling before the host goes away: the read-model reaches into IMountSupervisor and
        // ISessionMonitor on a timer, and a tick that fires mid-teardown would be reading from
        // services that are already shutting down (bs-ww9.6).
        _statusReadModel?.Stop();

        if (_host is not null)
        {
            using var cts = new CancellationTokenSource(HostStopTimeout);
            try
            {
                await _host.StopAsync(cts.Token);
                _logger?.LogInformation("Bosun host stopped.");
            }
            catch (OperationCanceledException)
            {
                _logger?.LogWarning(
                    "Bosun host did not stop within {Timeout}; proceeding with shutdown anyway.",
                    HostStopTimeout);
            }
            finally
            {
                _host.Dispose();
            }
        }

        // Releases the named Mutex (so a restart or the next launch acquires cleanly rather than
        // relying on AbandonedMutexException handling) and stops the activation-channel listener
        // thread. Only meaningful when this was the primary instance -- the secondary-launch path
        // above already disposed it before reaching Shutdown().
        SingleInstance.Dispose();

        base.OnExit(e);
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception;
        if (_logger is { } logger)
        {
            logger.LogCritical(
                exception,
                "Unhandled AppDomain exception (terminating: {IsTerminating}).",
                e.IsTerminating);
        }
        else if (e.IsTerminating)
        {
            // Pre-logger AND fatal: no logger, no tray icon, and in a moment no process either.
            // Recording it durably is necessary but not sufficient -- a record nobody reads is not
            // communication (ADR-012). This is the same catastrophic class as CreateHost() throwing,
            // reached by a different route, so it gets the same channel.
            _bootstrap.ReportTerminalPreLoggerFailure(
                "Bosun could not start: an unrecoverable error occurred during startup.",
                exception);
        }
        else
        {
            // Pre-logger but survivable -- record quietly; the process continues and the normal
            // channels take over once the logger resolves.
            _bootstrap.RecordPreLoggerFailure(
                "Unhandled AppDomain exception before the logger was available (non-terminating).",
                exception);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (_logger is { } logger)
        {
            logger.LogCritical(e.Exception, "Unhandled dispatcher exception.");
        }
        else
        {
            // e.Handled is deliberately left false (see bs-twz), so this terminates the process.
            // Same reasoning as the AppDomain handler above: pre-logger plus fatal must be seen,
            // not merely recorded.
            _bootstrap.ReportTerminalPreLoggerFailure(
                "Bosun could not start: an unrecoverable UI error occurred during startup.",
                e.Exception);
        }
    }
}
