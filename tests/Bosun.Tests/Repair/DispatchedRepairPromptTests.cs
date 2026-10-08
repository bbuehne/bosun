using Bosun.Repair;

namespace Bosun.Tests.Repair;

/// <summary>
/// bs-3hx: the repair confirmation flashed up and closed itself because it was shown inline from the
/// tray menu's Click, with no owner. These pin the two things that fix it: the dialog is queued on the
/// dispatcher rather than shown inline, and it is shown only after its owner is prepared and with that
/// owner. Fakes only; no window exists.
/// </summary>
public sealed class DispatchedRepairPromptTests
{
    private const nint MainWindowHandle = 0x1234;

    private sealed class QueuedDispatcher : IUiDispatcher
    {
        private readonly Queue<Action> pending = new();

        public int Queued => pending.Count;

        public Task<T> InvokeAsync<T>(Func<T> work)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending.Enqueue(() =>
            {
                try
                {
                    completion.SetResult(work());
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            });
            return completion.Task;
        }

        /// <summary>The dispatcher getting round to the queued work, after the click handler returned.</summary>
        public void RunQueued()
        {
            while (pending.Count > 0)
            {
                pending.Dequeue()();
            }
        }
    }

    private sealed class Owner : IRepairDialogOwner
    {
        private readonly List<string> events;

        public Owner(List<string> events) => this.events = events;

        public nint Handle { get; set; } = MainWindowHandle;

        public int Prepared { get; private set; }

        public nint Prepare()
        {
            Prepared++;
            events.Add("prepare-owner");
            return Handle;
        }
    }

    private sealed class Presenter : IRepairDialogPresenter
    {
        private readonly List<string> events;

        public Presenter(List<string> events) => this.events = events;

        public RepairConfirmation Result { get; set; } = RepairConfirmation.Confirmed;

        public Exception? Throws { get; set; }

        public List<(nint Owner, RepairDialogRequest Request)> Shown { get; } = [];

        public RepairConfirmation Show(nint owner, RepairDialogRequest request)
        {
            events.Add("show");
            Shown.Add((owner, request));
            return Throws is null ? Result : throw Throws;
        }
    }

    private sealed class Harness
    {
        public List<string> Events { get; } = [];
        public QueuedDispatcher Ui { get; } = new();
        public Owner Owner { get; }
        public Presenter Presenter { get; }
        public DispatchedRepairPrompt Prompt { get; }

        public Harness()
        {
            Owner = new Owner(Events);
            Presenter = new Presenter(Events);
            Prompt = new DispatchedRepairPrompt(Ui, Owner, Presenter);
        }
    }

    [Fact]
    public void Asking_does_not_show_the_dialog_before_returning_to_the_caller()
    {
        // The caller is the menu's Click handler. Showing inline there is the bug.
        var h = new Harness();

        var answer = h.Prompt.ConfirmAsync("Restart rclone", "Sure?");

        Assert.Empty(h.Presenter.Shown);
        Assert.Equal(0, h.Owner.Prepared);
        Assert.Equal(1, h.Ui.Queued);
        Assert.False(answer.IsCompleted);
    }

    [Fact]
    public async Task The_dialog_is_shown_when_the_dispatcher_runs_the_work_and_its_answer_comes_back()
    {
        var h = new Harness { Presenter = { Result = RepairConfirmation.Declined } };
        var answer = h.Prompt.ConfirmAsync("Restart Bosun", "Sure?");

        h.Ui.RunQueued();

        Assert.Equal(RepairConfirmation.Declined, await answer);
        var shown = Assert.Single(h.Presenter.Shown);
        Assert.Equal("Restart Bosun", shown.Request.Title);
        Assert.Equal("Sure?", shown.Request.Message);
        Assert.True(shown.Request.IsConfirmation);
    }

    [Fact]
    public async Task The_dialog_is_owned_by_the_window_that_was_prepared_for_it_and_prepared_first()
    {
        var h = new Harness();
        var answer = h.Prompt.ConfirmAsync("Restart rclone", "Sure?");

        h.Ui.RunQueued();
        await answer;

        Assert.Equal(["prepare-owner", "show"], h.Events);
        Assert.Equal(MainWindowHandle, Assert.Single(h.Presenter.Shown).Owner);
    }

    [Fact]
    public async Task Each_ask_prepares_the_owner_again_because_the_window_may_have_been_hidden_since()
    {
        var h = new Harness();

        var first = h.Prompt.ConfirmAsync("a", "a");
        h.Ui.RunQueued();
        await first;
        var second = h.Prompt.ConfirmAsync("b", "b");
        h.Ui.RunQueued();
        await second;

        Assert.Equal(2, h.Owner.Prepared);
    }

    [Fact]
    public async Task With_no_window_the_dialog_is_still_shown_and_the_presenter_is_told_there_is_no_owner()
    {
        var h = new Harness { Owner = { Handle = 0 } };
        var answer = h.Prompt.ConfirmAsync("Restart rclone", "Sure?");

        h.Ui.RunQueued();
        await answer;

        Assert.Equal(0, Assert.Single(h.Presenter.Shown).Owner);
    }

    [Fact]
    public async Task A_dialog_that_ended_without_an_answer_is_reported_as_such()
    {
        var h = new Harness { Presenter = { Result = RepairConfirmation.NoAnswer } };
        var answer = h.Prompt.ConfirmAsync("Restart rclone", "Sure?");

        h.Ui.RunQueued();

        Assert.Equal(RepairConfirmation.NoAnswer, await answer);
    }

    [Fact]
    public async Task A_dialog_that_cannot_be_shown_faults_the_ask_so_the_repair_reports_it()
    {
        var h = new Harness { Presenter = { Throws = new InvalidOperationException("no dispatcher") } };
        var answer = h.Prompt.ConfirmAsync("Restart rclone", "Sure?");

        h.Ui.RunQueued();

        await Assert.ThrowsAsync<InvalidOperationException>(() => answer);
    }

    [Fact]
    public void An_error_dialog_is_queued_too_and_is_a_single_button_dialog()
    {
        // ShowError is called from RepairCommands synchronously on some paths (rclone never started),
        // which is inline from the same click handler.
        var h = new Harness();

        h.Prompt.ShowError("Restart rclone", "rclone was never started");

        Assert.Empty(h.Presenter.Shown);
        h.Ui.RunQueued();

        var shown = Assert.Single(h.Presenter.Shown);
        Assert.Equal(MainWindowHandle, shown.Owner);
        Assert.False(shown.Request.IsConfirmation);
        Assert.Equal(["prepare-owner", "show"], h.Events);
    }

    [Fact]
    public void An_error_dialog_that_cannot_be_shown_does_not_throw_into_the_caller()
    {
        var h = new Harness { Presenter = { Throws = new InvalidOperationException("no dispatcher") } };

        h.Prompt.ShowError("Restart rclone", "rclone was never started");

        var ex = Record.Exception(h.Ui.RunQueued);
        Assert.Null(ex);
    }
}
