using Microsoft.Extensions.Logging;

namespace Bosun.Repair;

/// <summary>How a repair confirmation ended. Three outcomes, not two: "No" is the user's decision, but a
/// dialog that closed with no answer is not, and the log must not say it was (bs-3hx).</summary>
public enum RepairConfirmation
{
    /// <summary>The user clicked Yes.</summary>
    Confirmed,

    /// <summary>The user clicked No (or pressed Esc or Enter on the default No).</summary>
    Declined,

    /// <summary>The dialog closed without an answer: its window was closed, or it could not be shown.</summary>
    NoAnswer,
}

/// <summary>Asks the user to confirm a disruptive repair, and tells them when one failed. Behind an
/// interface so tests never show a dialog.</summary>
public interface IRepairPrompt
{
    /// <summary>Asks the user. Completes when they answer. Never shows the dialog before the caller's
    /// current dispatcher operation (a menu click) has returned.</summary>
    Task<RepairConfirmation> ConfirmAsync(string title, string message);

    /// <summary>Tells the user a repair could not be done. Returns without waiting for them to dismiss it.</summary>
    void ShowError(string title, string message);
}

/// <summary>Runs work on the UI thread, later than the operation that asks. Behind an interface so a test
/// can hold the work back and see that nothing ran inline.</summary>
public interface IUiDispatcher
{
    /// <summary>Queues <paramref name="work"/> at a priority below menu teardown and input, and returns a
    /// task for its result. It does not run <paramref name="work"/> before returning.</summary>
    Task<T> InvokeAsync<T>(Func<T> work);
}

/// <summary>Provides the window a repair dialog belongs to.</summary>
public interface IRepairDialogOwner
{
    /// <summary>
    /// Makes the owner visible, restored and active, and returns its native handle for the dialog to be
    /// owned by. Zero means there is no window to own the dialog. Called on the UI thread, immediately
    /// before the dialog is shown.
    /// </summary>
    nint Prepare();
}

/// <summary>What a repair dialog says and which buttons it has.</summary>
/// <param name="Title">The dialog title, without the "Bosun - " prefix.</param>
/// <param name="Message">The text shown.</param>
/// <param name="IsConfirmation">True for Yes and No buttons with No as the default; false for a single OK.</param>
public sealed record RepairDialogRequest(string Title, string Message, bool IsConfirmation);

/// <summary>Shows one repair dialog, modal to its owner, and reports how it ended.</summary>
public interface IRepairDialogPresenter
{
    /// <summary>Blocks until the dialog closes. For a confirmation the default button is No, so a stray
    /// Enter does not disconnect drives. An error dialog returns <see cref="RepairConfirmation.Confirmed"/>
    /// when dismissed, which callers ignore.</summary>
    RepairConfirmation Show(nint owner, RepairDialogRequest request);
}

/// <summary>
/// The prompt the app uses (bs-3hx). Every dialog is shown from the UI dispatcher, queued rather than run
/// inside the caller, and owned by the main window.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not show it inline.</b> The tray menu's <c>Click</c> is raised after the menu has begun to close
/// (WPF raises <c>PreviewClick</c>, which dismisses the menu, then <c>Click</c> at Render priority). A
/// <c>MessageBox</c> shown there with no owner takes the thread's active window, which is the context
/// menu's own popup, and that window is destroyed as the menu closes. The dialog goes with it, and WPF
/// reports a destroyed message box as "No". That is the 2026-10-07 23:09 failure: a window that flashed
/// and closed with nobody clicking, and the repair skipped.
/// </para>
/// <para>
/// <b>What this does instead.</b> Queue at Background priority so the menu is gone first, make the main
/// window visible and active, and own the dialog by it. The banner buttons take the same path, so the
/// two surfaces cannot differ.
/// </para>
/// </remarks>
public sealed class DispatchedRepairPrompt : IRepairPrompt
{
    private readonly IUiDispatcher ui;
    private readonly IRepairDialogOwner owner;
    private readonly IRepairDialogPresenter presenter;
    private readonly ILogger<DispatchedRepairPrompt>? logger;

    public DispatchedRepairPrompt(
        IUiDispatcher ui,
        IRepairDialogOwner owner,
        IRepairDialogPresenter presenter,
        ILogger<DispatchedRepairPrompt>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(presenter);

        this.ui = ui;
        this.owner = owner;
        this.presenter = presenter;
        this.logger = logger;
    }

    public Task<RepairConfirmation> ConfirmAsync(string title, string message) =>
        ui.InvokeAsync(() => presenter.Show(owner.Prepare(), new RepairDialogRequest(title, message, IsConfirmation: true)));

    public void ShowError(string title, string message) => _ = ShowErrorAsync(title, message);

    private async Task ShowErrorAsync(string title, string message)
    {
        try
        {
            await ui.InvokeAsync(
                () => presenter.Show(owner.Prepare(), new RepairDialogRequest(title, message, IsConfirmation: false))).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Nobody awaits an error dialog, so a failure to show it ends here, in the log.
            logger?.LogError(ex, "Could not show the '{Title}' error dialog to the user", title);
        }
    }
}
