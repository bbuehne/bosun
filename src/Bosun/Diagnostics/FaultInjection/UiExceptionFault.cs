#if DEBUG
using System.Windows.Threading;
using Microsoft.Extensions.Logging;

namespace Bosun.Diagnostics.FaultInjection;

/// <summary>
/// Debug-only, for <c>BOSUN_FAULT=ui-exception</c> (bs-uzk; docs/OPERATIONS.md T13): throws one
/// unhandled exception from a <see cref="DispatcherTimer"/> tick on the UI dispatcher, so
/// <c>App.OnDispatcherUnhandledException</c> and <c>DispatcherExceptionPolicy</c> (bs-twz) handle it
/// exactly as they would one from a real event handler. Fires once.
/// </summary>
internal sealed class UiExceptionFault
{
    /// <summary>Long enough for the tray icon to be up, so the change to red can be watched.</summary>
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(5);

    private readonly ILogger _logger;
    private readonly DispatcherTimer _timer;

    public UiExceptionFault(Dispatcher dispatcher, ILogger logger, TimeSpan? delay = null)
    {
        _logger = logger;
        _timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = delay ?? Delay };
        _timer.Tick += OnTick;
    }

    public void Arm()
    {
        _logger.LogWarning(
            "FAULT INJECTION ACTIVE ({Variable}={Fault}): an unhandled exception will be thrown on the UI dispatcher in " +
            "{Seconds:0.##} s, once, on purpose.",
            DebugFaultSettings.VariableName, DebugFaultSettings.UiExceptionName, _timer.Interval.TotalSeconds);
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        // Stopped before throwing: an exception out of a Tick handler must not leave a timer that
        // fires again, or T13's "1 in the last 60 s, 1 since start" would not hold.
        _timer.Stop();
        _timer.Tick -= OnTick;
        throw CreateException();
    }

    internal static InvalidOperationException CreateException() =>
        new($"{DebugFaultSettings.VariableName}={DebugFaultSettings.UiExceptionName}: injected unhandled exception on the UI dispatcher (not a real fault)");
}
#endif
