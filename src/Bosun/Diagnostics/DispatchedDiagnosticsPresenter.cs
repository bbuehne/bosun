using Bosun.Repair;
using Microsoft.Extensions.Logging;

namespace Bosun.Diagnostics;

/// <summary>
/// Tells the user about "Copy diagnostics" outcomes through the app's one dialog path, the
/// <see cref="DispatchedRepairPrompt"/> (bs-6bc): queued on the dispatcher after the tray menu has
/// closed, and owned by the main window.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not a <c>MessageBox</c>.</b> The command is fired from the tray menu's <c>Click</c>, which WPF
/// raises after the menu has begun to close. A <c>MessageBox</c> shown there with no owner takes the
/// thread's active window, which is the menu's own popup, and goes with it (bs-3hx). It only shows when
/// bundle creation fails, so it had not been seen; the repair confirmations are the same code path and
/// were.
/// </para>
/// <para>
/// <b>Why it is attached late.</b> The command is built by the container, before the main window and
/// the <see cref="DispatchedRepairPrompt"/> that is owned by it exist, so this is registered as a
/// singleton and <c>App.InitializeUserInterface</c> hands it the prompt once it has built one. Until
/// then a message is logged rather than shown, which no click can reach: the tray item is created
/// after the attach.
/// </para>
/// </remarks>
public sealed class DispatchedDiagnosticsPresenter(ILogger<DispatchedDiagnosticsPresenter>? logger = null)
    : IDiagnosticsPresenter
{
    /// <summary>The dialog title, without the "Bosun - " prefix the dialog adds.</summary>
    public const string DialogTitle = "Copy diagnostics";

    private IRepairPrompt? _prompt;

    /// <summary>Gives this presenter the prompt to show dialogs with. Call once the UI exists.</summary>
    public void Attach(IRepairPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        Volatile.Write(ref _prompt, prompt);
    }

    public void ShowError(string message) => Show(message);

    // The repair prompt's "error" dialog is a plain OK box with no icon, so it carries a notice as well
    // as an error; there is no separate notice dialog to build.
    public void ShowNotice(string message) => Show(message);

    private void Show(string message)
    {
        var prompt = Volatile.Read(ref _prompt);
        if (prompt is null)
        {
            logger?.LogError("Diagnostics could not tell the user, because no dialog prompt is attached yet: {Message}", message);
            return;
        }

        prompt.ShowError(DialogTitle, message);
    }
}
