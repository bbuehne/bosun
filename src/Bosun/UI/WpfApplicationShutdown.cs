using System.Windows;
using Bosun.Watchdog;

namespace Bosun.UI;

/// <summary>
/// <see cref="IApplicationShutdown"/> over WPF: posts <see cref="Application.Shutdown()"/> to the
/// UI thread -- the same call the tray's Exit item makes, so a watchdog restart exits through the
/// ordinary <c>App.OnExit</c> path. Posted rather than called, because the watchdog calls from a
/// timer thread and <c>Shutdown</c> must run on the dispatcher's.
/// </summary>
public sealed class WpfApplicationShutdown : IApplicationShutdown
{
    public void RequestShutdown()
    {
        var application = Application.Current;
        application?.Dispatcher.BeginInvoke(() => application.Shutdown());
    }
}
