using System.Windows;
using System.Windows.Threading;
using Bosun.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bosun;

/// <summary>
/// Interaction logic for App.xaml. Owns the <see cref="IHost"/> lifetime: built and started in
/// <see cref="OnStartup"/>, stopped with a bounded timeout in <see cref="OnExit"/>. There is no
/// main window — see App.xaml.
/// </summary>
public partial class App : Application
{
    private static readonly TimeSpan HostStopTimeout = TimeSpan.FromSeconds(5);

    // Owns the window between process start and the point ILogger<App> becomes resolvable --
    // see BootstrapOrchestrator's doc comment (bs-ipq / ADR-012 Decision 2). Constructed with the
    // real, production seams; tests exercise BootstrapOrchestrator directly with fakes instead of
    // constructing an App.
    private readonly BootstrapOrchestrator _bootstrap = new(
        () => BosunHostFactory.CreateHost(),
        new MessageBoxCatastrophicStartupNotifier(),
        new BootstrapDiagnosticSink());

    private IHost? _host;
    private ILogger<App>? _logger;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
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
