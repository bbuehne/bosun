using Microsoft.Extensions.Logging;

namespace Bosun.Diagnostics;

/// <summary>
/// "Copy diagnostics" (bs-ds3, ADR-020 §6): build the bundle, reveal it in Explorer, and tell the
/// user if it failed. One instance serves the tray menu and the window button through
/// <c>HostActionDispatcher</c>, so the two cannot behave differently (ADR-018).
/// </summary>
/// <remarks>
/// Never throws, so a click handler can fire it and forget it. Call it on the UI thread: the
/// continuation after the build resumes there. The presenter does not show anything inline; it queues
/// the dialog so it appears after the tray menu has gone (see
/// <see cref="DispatchedDiagnosticsPresenter"/>). A second click while a build is running is ignored.
/// </remarks>
public sealed class CopyDiagnosticsCommand(
    IDiagnosticsBundleBuilder builder,
    IExplorerRevealer revealer,
    IDiagnosticsPresenter presenter,
    ILogger<CopyDiagnosticsCommand>? logger = null)
{
    private int _running;

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>The most problems listed in the notice; <c>summary.txt</c> has them all.</summary>
    private const int ProblemsShown = 5;

    /// <summary>"Bundle written with N collection problems", then the first few, then where the rest are.</summary>
    internal static string DescribeProblems(DiagnosticsBundleResult result)
    {
        var count = result.Problems.Count;
        var text = new System.Text.StringBuilder();
        text.Append(
            $"Diagnostics bundle written with {count} collection problem{(count == 1 ? string.Empty : "s")}, " +
            "so part of it is missing.");
        text.AppendLine().AppendLine();
        foreach (var problem in result.Problems.Take(ProblemsShown))
        {
            text.Append("- ").AppendLine(problem);
        }

        if (count > ProblemsShown)
        {
            text.AppendLine($"- and {count - ProblemsShown} more");
        }

        text.AppendLine().Append("summary.txt in the bundle lists every problem.");
        return text.ToString();
    }

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

            // bs-egu: the problems are in summary.txt, but nobody opens it to find out whether to.
            if (result.Problems.Count > 0)
            {
                try
                {
                    presenter.ShowNotice(DescribeProblems(result));
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Could not tell the user the diagnostics bundle was incomplete");
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Building the diagnostics bundle failed");
            try
            {
                presenter.ShowError($"Bosun could not write the diagnostics bundle.{Environment.NewLine}{Environment.NewLine}{ex.Message}");
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
