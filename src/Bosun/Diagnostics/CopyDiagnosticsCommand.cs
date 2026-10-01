using Microsoft.Extensions.Logging;

namespace Bosun.Diagnostics;

/// <summary>
/// "Copy diagnostics" (bs-ds3, ADR-020 §6): build the bundle, reveal it in Explorer, and tell the
/// user if it failed. One instance serves the tray menu and the window button through
/// <c>HostActionDispatcher</c>, so the two cannot behave differently (ADR-018).
/// </summary>
/// <remarks>
/// Never throws, so a click handler can fire it and forget it. Call it on the UI thread: the
/// continuation after the build resumes there, which is what lets the error presenter show a
/// message box. A second click while a build is running is ignored.
/// </remarks>
public sealed class CopyDiagnosticsCommand(
    IDiagnosticsBundleBuilder builder,
    IExplorerRevealer revealer,
    IDiagnosticsErrorPresenter errorPresenter,
    ILogger<CopyDiagnosticsCommand>? logger = null)
{
    private int _running;

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    public async Task RunAsync()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        try
        {
            var result = await builder.BuildAsync().ConfigureAwait(true);
            logger?.LogInformation(
                "Diagnostics bundle written to {Path} ({Problems} section problem(s))", result.Path, result.Problems.Count);

            try
            {
                revealer.Reveal(result.Path);
            }
            catch (Exception ex)
            {
                // The bundle exists; failing to open Explorer is not a failed bundle.
                logger?.LogWarning(ex, "Diagnostics bundle written but Explorer could not be opened at {Path}", result.Path);
            }
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Building the diagnostics bundle failed");
            try
            {
                errorPresenter.ShowError($"Bosun could not write the diagnostics bundle.{Environment.NewLine}{Environment.NewLine}{ex.Message}");
            }
            catch (Exception presenterEx)
            {
                logger?.LogError(presenterEx, "Could not show the diagnostics failure to the user");
            }
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }
}
