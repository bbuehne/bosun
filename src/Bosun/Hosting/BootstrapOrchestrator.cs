using Microsoft.Extensions.Hosting;

namespace Bosun.Hosting;

/// <summary>
/// Owns the startup window between process start and the point <c>ILogger&lt;App&gt;</c> becomes
/// resolvable: builds the host, and -- if that throws -- durably records the failure and presents
/// ADR-012 Decision 2's catastrophic channel. Extracted out of <c>Bosun.App</c> specifically so it
/// can be unit tested without a live WPF <see cref="System.Windows.Application"/> (bs-ipq).
/// </summary>
/// <remarks>
/// <para>
/// Before this existed, <c>App.OnStartup</c> registered the <c>AppDomain</c>/<c>Dispatcher</c>
/// unhandled-exception handlers, called <c>BosunHostFactory.CreateHost()</c>, and only then
/// resolved <c>ILogger&lt;App&gt;</c> -- with both handlers logging via <c>_logger?.</c>, a
/// null-conditional. Any exception in that window, including <c>CreateHost()</c> itself throwing
/// (it calls <c>Directory.CreateDirectory</c> on a path that can genuinely be unwritable --
/// permissions, a redirected profile, a full disk), was swallowed entirely: no file, no debug
/// sink, no dialog, nothing. For a tray app with no main window, that is an application that
/// simply never appears, with no diagnostic anywhere.
/// </para>
/// <para>
/// This type closes that gap for the one case ADR-012 assigns a blocking presentation to:
/// "cannot construct the host at all". It does not generalise to other failure conditions --
/// ADR-012 assigns those to different channels (persistent tray icon, toast, first-run window),
/// which is E12b's job, not this one's.
/// </para>
/// <para>
/// <b>Single-instance primacy is a precondition, asserted, not merely documented (bs-2wa).</b>
/// <see cref="TryCreateHost"/> requires the injected <see cref="ISingleInstanceGuard"/> to report
/// <see cref="ISingleInstanceGuard.IsOwned"/> before it will call <c>hostFactory</c>. A comment
/// telling <c>App.OnStartup</c> to call <c>SingleInstanceOrchestrator.TryBecomePrimary()</c> first
/// is exactly the kind of rule a later reorder can silently violate -- nothing would fail, a
/// second launch would just create the log directory, spawn <c>rclone rcd</c>, and race the first
/// instance for the same drive letters before anyone noticed. Making this an injected dependency
/// this class checks turns that into an immediate, loud <see cref="InvalidOperationException"/>
/// instead -- a startup-ordering bug, not a runtime failure, so it deliberately is NOT caught by
/// the try/catch below and NOT routed through the catastrophic notifier (which would misreport it
/// to the user as an environment problem rather than a code defect).
/// </para>
/// </remarks>
public sealed class BootstrapOrchestrator
{
    private readonly Func<IHost> _hostFactory;
    private readonly ICatastrophicStartupNotifier _notifier;
    private readonly IBootstrapDiagnosticSink _diagnosticSink;
    private readonly ISingleInstanceGuard _singleInstanceGuard;

    public BootstrapOrchestrator(
        Func<IHost> hostFactory,
        ICatastrophicStartupNotifier notifier,
        IBootstrapDiagnosticSink diagnosticSink,
        ISingleInstanceGuard singleInstanceGuard)
    {
        _hostFactory = hostFactory;
        _notifier = notifier;
        _diagnosticSink = diagnosticSink;
        _singleInstanceGuard = singleInstanceGuard;
    }

    /// <summary>
    /// Attempts to build the host. On success, returns it. On failure, durably records the
    /// failure and blocks on the catastrophic notifier, then returns <see langword="null"/> so the
    /// caller can exit without a second, uncaught throw.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The injected <see cref="ISingleInstanceGuard"/> does not report <see cref="ISingleInstanceGuard.IsOwned"/>
    /// -- i.e. this process has not established single-instance primacy (bs-2wa). See the class
    /// remarks: this is a startup-ordering bug and is deliberately not caught here.
    /// </exception>
    public IHost? TryCreateHost()
    {
        if (!_singleInstanceGuard.IsOwned)
        {
            throw new InvalidOperationException(
                "BootstrapOrchestrator.TryCreateHost() was called before this process established " +
                "single-instance primacy (bs-2wa / ADR-018 Decision 6). SingleInstanceOrchestrator." +
                "TryBecomePrimary() must be called, and must return true, before the host is built -- " +
                "otherwise a second launch could create the log directory, spawn rclone rcd, or write " +
                "the Terminal fragment before discovering it should have exited. This is a " +
                "startup-ordering bug in App.OnStartup, not a runtime failure.");
        }

        try
        {
            return _hostFactory();
        }
        catch (Exception ex)
        {
            const string reason = "Bosun could not start: the application host failed to construct.";
            RecordPreLoggerFailure(reason, ex);
            _notifier.NotifyAndAwaitDismissal(reason, ex);
            return null;
        }
    }

    /// <summary>
    /// Durably records a failure that happened before the logger became available -- used both by
    /// <see cref="TryCreateHost"/> and by <c>App</c>'s <c>AppDomain</c>/<c>Dispatcher</c>
    /// unhandled-exception handlers for the (rare) case where one fires in that same window.
    /// Swallows any failure from the diagnostic sink itself: a broken durable channel must never
    /// prevent, or throw back into, the caller -- see <see cref="IBootstrapDiagnosticSink"/>.
    /// </summary>
    public void RecordPreLoggerFailure(string reason, Exception? exception)
    {
        try
        {
            _diagnosticSink.Record(reason, exception);
        }
        catch
        {
            // Deliberately swallowed. See the doc comment above and IBootstrapDiagnosticSink: the
            // diagnostic channel is best-effort by contract, but this is the defensive backstop in
            // case an implementation does not honour that contract.
        }
    }

    /// <summary>
    /// Records a pre-logger failure that is going to kill the process, and additionally presents
    /// the catastrophic channel — because a durable record the user never reads is not, by
    /// ADR-012's standard, communication.
    /// </summary>
    /// <remarks>
    /// The distinction from <see cref="RecordPreLoggerFailure"/> is <em>terminality</em>, not the
    /// kind of exception. A pre-logger exception the process survives can be recorded quietly; one
    /// that terminates it cannot, because there is no tray icon yet, no logger, and — a moment
    /// later — no process. That is precisely the invisible death ADR-012 exists to eliminate, and
    /// it is the same catastrophic class as <see cref="TryCreateHost"/> failing, reached by a
    /// different route.
    /// </remarks>
    public void ReportTerminalPreLoggerFailure(string reason, Exception? exception)
    {
        RecordPreLoggerFailure(reason, exception);

        try
        {
            _notifier.NotifyAndAwaitDismissal(reason, exception);
        }
        catch
        {
            // The process is already terminating; a failure to display must not add a second
            // exception on the way down.
        }
    }
}

