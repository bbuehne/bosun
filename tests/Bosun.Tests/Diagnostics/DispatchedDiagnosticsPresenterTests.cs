using Bosun.Diagnostics;
using Bosun.Repair;
using Bosun.Tests.Diagnostics.Fakes;

namespace Bosun.Tests.Diagnostics;

/// <summary>
/// bs-6bc: the "bundle could not be written" dialog was an unowned <c>MessageBox</c> opened inline from
/// the tray click, which bs-3hx showed is torn down with the closing menu. These pin that it now goes
/// through <see cref="DispatchedRepairPrompt"/>: queued, not inline, and owned by the main window. Fakes
/// only; no window exists. The WPF dialog itself (<c>WpfRepairDialogPresenter</c>) is exercised by hand.
/// </summary>
public sealed class DispatchedDiagnosticsPresenterTests
{
    private const nint MainWindowHandle = 0x4321;

    private sealed class QueuedDispatcher : IUiDispatcher
    {
        private readonly Queue<Action> _pending = [];

        public int Queued => _pending.Count;

        public Task<T> InvokeAsync<T>(Func<T> work)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Enqueue(() => completion.SetResult(work()));
            return completion.Task;
        }

        public void RunQueued()
        {
            while (_pending.Count > 0)
            {
                _pending.Dequeue()();
            }
        }
    }

    private sealed class Owner : IRepairDialogOwner
    {
        public int Prepared { get; private set; }

        public nint Prepare()
        {
            Prepared++;
            return MainWindowHandle;
        }
    }

    private sealed class Presenter : IRepairDialogPresenter
    {
        public List<(nint Owner, RepairDialogRequest Request)> Shown { get; } = [];

        public RepairConfirmation Show(nint owner, RepairDialogRequest request)
        {
            Shown.Add((owner, request));
            return RepairConfirmation.Confirmed;
        }
    }

    private sealed class RecordingPrompt : IRepairPrompt
    {
        public List<(string Title, string Message)> Errors { get; } = [];

        public Task<RepairConfirmation> ConfirmAsync(string title, string message) =>
            throw new InvalidOperationException("diagnostics never asks for a confirmation");

        public void ShowError(string title, string message) => Errors.Add((title, message));
    }

    [Fact]
    public void An_error_is_handed_to_the_attached_prompt_with_the_diagnostics_title()
    {
        var prompt = new RecordingPrompt();
        var presenter = new DispatchedDiagnosticsPresenter();
        presenter.Attach(prompt);

        presenter.ShowError("The disk is full");

        var error = Assert.Single(prompt.Errors);
        Assert.Equal(DispatchedDiagnosticsPresenter.DialogTitle, error.Title);
        Assert.Equal("The disk is full", error.Message);
    }

    [Fact]
    public void An_error_before_a_prompt_is_attached_is_dropped_without_throwing()
    {
        var presenter = new DispatchedDiagnosticsPresenter();

        var thrown = Record.Exception(() => presenter.ShowError("too early"));

        Assert.Null(thrown);
    }

    [Fact]
    public async Task A_failed_bundle_is_not_shown_inline_from_the_click_but_queued_and_then_owned_by_the_main_window()
    {
        var ui = new QueuedDispatcher();
        var owner = new Owner();
        var dialogs = new Presenter();
        var presenter = new DispatchedDiagnosticsPresenter();
        presenter.Attach(new DispatchedRepairPrompt(ui, owner, dialogs));
        var builder = new FakeBundleBuilder { Throws = new IOException("The disk is full") };
        var command = new CopyDiagnosticsCommand(builder, new FakeExplorerRevealer(), presenter);

        await command.RunAsync();

        // Back from the click handler: nothing has been shown and the owner has not been touched. This
        // is what an inline MessageBox got wrong.
        Assert.Empty(dialogs.Shown);
        Assert.Equal(0, owner.Prepared);
        Assert.Equal(1, ui.Queued);

        ui.RunQueued();

        var (shownOwner, request) = Assert.Single(dialogs.Shown);
        Assert.Equal(MainWindowHandle, shownOwner);
        Assert.Equal(1, owner.Prepared);
        Assert.Equal(DispatchedDiagnosticsPresenter.DialogTitle, request.Title);
        Assert.False(request.IsConfirmation);
        Assert.Contains("The disk is full", request.Message);
    }
}
