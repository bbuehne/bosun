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

    private IHost? _host;
    private ILogger<App>? _logger;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        _host = BosunHostFactory.CreateHost();
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
        _logger?.LogCritical(
            exception,
            "Unhandled AppDomain exception (terminating: {IsTerminating}).",
            e.IsTerminating);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogCritical(e.Exception, "Unhandled dispatcher exception.");
    }
}
