using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Bosun.UI;

namespace Bosun.Repair;

// The WPF half of the repair prompt (bs-3hx). Pure window plumbing, like TrayIconController: the
// decisions it carries out (queue it, own it, name the outcome) are in DispatchedRepairPrompt and are
// what the unit tests cover. Nothing here is reachable without a running dispatcher.

/// <summary>Runs work on the application's dispatcher at Background priority: below Render, where a menu
/// item's <c>Click</c> is raised, and below Input, so the closing menu has finished first.</summary>
public sealed class WpfUiDispatcher : IUiDispatcher
{
    private readonly Dispatcher dispatcher;

    public WpfUiDispatcher(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        this.dispatcher = dispatcher;
    }

    public Task<T> InvokeAsync<T>(Func<T> work) =>
        dispatcher.InvokeAsync(work, DispatcherPriority.Background).Task;
}

/// <summary>The main window as the owner of repair dialogs. It is shown and activated first if it is hidden,
/// because a dialog owned by a hidden window is itself hidden.</summary>
public sealed class MainWindowRepairDialogOwner : IRepairDialogOwner
{
    private readonly Func<Window?> window;
    private readonly Func<MainWindowController?> controller;

    /// <summary>Both are functions because the window is built after the repair commands that need this.</summary>
    public MainWindowRepairDialogOwner(Func<Window?> window, Func<MainWindowController?> controller)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(controller);
        this.window = window;
        this.controller = controller;
    }

    public nint Prepare()
    {
        var main = window();
        var windowController = controller();
        if (main is null || windowController is null)
        {
            return 0;
        }

        windowController.ShowAndActivate();
        if (main.WindowState == WindowState.Minimized)
        {
            main.WindowState = WindowState.Normal;
        }

        return new WindowInteropHelper(main).EnsureHandle();
    }
}

/// <summary>Shows a <see cref="RepairDialogWindow"/>, owned by the handle it is given.</summary>
public sealed class WpfRepairDialogPresenter : IRepairDialogPresenter
{
    public RepairConfirmation Show(nint owner, RepairDialogRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var dialog = new RepairDialogWindow(request);
        if (owner != 0)
        {
            new WindowInteropHelper(dialog).Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        dialog.ShowDialog();
        return dialog.Answer;
    }
}

/// <summary>
/// A small modal dialog built in code. It exists instead of <see cref="MessageBox"/> because
/// <see cref="MessageBox"/> reports a dialog that was destroyed without an answer as "No", so a user's
/// decision and a dialog that vanished could not be told apart in the log (bs-3hx). Here the answer is
/// recorded by the buttons and nothing else: closing the window any other way leaves
/// <see cref="RepairConfirmation.NoAnswer"/>.
/// </summary>
internal sealed class RepairDialogWindow : Window
{
    public RepairDialogWindow(RepairDialogRequest request)
    {
        Title = $"Bosun - {request.Title}";
        SizeToContent = SizeToContent.Height;
        Width = 480;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var text = new TextBlock
        {
            Text = request.Message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16),
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        Button defaultButton;
        if (request.IsConfirmation)
        {
            var yes = MakeButton("_Yes", RepairConfirmation.Confirmed);
            var no = MakeButton("_No", RepairConfirmation.Declined);
            no.IsDefault = true;
            buttons.Children.Add(yes);
            buttons.Children.Add(no);
            defaultButton = no;
        }
        else
        {
            var ok = MakeButton("OK", RepairConfirmation.Confirmed);
            ok.IsDefault = true;
            buttons.Children.Add(ok);
            defaultButton = ok;
        }

        var layout = new StackPanel { Margin = new Thickness(16) };
        layout.Children.Add(text);
        layout.Children.Add(buttons);
        Content = layout;

        // Esc is a No. Done here rather than with IsCancel, which would also run the window's own
        // cancel command on top of the Close below.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Answer = request.IsConfirmation ? RepairConfirmation.Declined : RepairConfirmation.Confirmed;
                Close();
            }
        };

        // The default button takes focus, so Enter answers No and Yes needs a deliberate click or Alt+Y.
        Loaded += (_, _) =>
        {
            Activate();
            defaultButton.Focus();
        };
    }

    /// <summary>How the dialog ended. <see cref="RepairConfirmation.NoAnswer"/> until a button is used.</summary>
    public RepairConfirmation Answer { get; private set; } = RepairConfirmation.NoAnswer;

    private Button MakeButton(string label, RepairConfirmation answer)
    {
        var button = new Button
        {
            Content = label,
            MinWidth = 80,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(12, 4, 12, 4),
        };
        button.Click += (_, _) =>
        {
            Answer = answer;
            Close();
        };
        return button;
    }
}
