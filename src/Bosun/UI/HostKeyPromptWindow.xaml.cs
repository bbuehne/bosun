using System.Windows;
using System.Windows.Controls;
using Bosun.UI.HostEditor;

namespace Bosun.UI;

/// <summary>Prompts for a new host's key before <see cref="HostEditorWindow"/> opens -- see the
/// XAML file's remarks for why the key must come first. Deliberately thin/untested, same as
/// <see cref="HostEditorWindow"/>.</summary>
public partial class HostKeyPromptWindow : Window
{
    private readonly HostEditorController _controller;

    /// <param name="controller">Used to reject a key that already names another host.</param>
    /// <param name="initialKey">Pre-fills the key field -- used by the Bitvise import flow
    /// (bs-ww9.9, ADR-019), which derives a suggested short name from the imported profile's file
    /// name (<see cref="Bosun.Import.BitviseShortNameDeriver"/>). <see langword="null"/> or blank
    /// for the ordinary "Add Host" flow, which starts empty as before.</param>
    public HostKeyPromptWindow(HostEditorController controller, string? initialKey = null)
    {
        ArgumentNullException.ThrowIfNull(controller);
        InitializeComponent();
        _controller = controller;
        ContinueButton.IsEnabled = false;

        if (!string.IsNullOrWhiteSpace(initialKey))
        {
            // Setting Text fires OnKeyTextChanged, which runs the same uniqueness check a typed
            // key gets -- a suggested key that collides with an existing host is caught here, not
            // silently accepted.
            KeyTextBox.Text = initialKey;
            KeyTextBox.SelectAll();
        }
    }

    /// <summary>The confirmed, trimmed key. Only meaningful when <see cref="Window.DialogResult"/>
    /// is <see langword="true"/>.</summary>
    public string HostKey { get; private set; } = string.Empty;

    private void OnKeyTextChanged(object sender, TextChangedEventArgs e)
    {
        var key = KeyTextBox.Text.Trim();

        if (string.IsNullOrEmpty(key))
        {
            SetError(null);
            return;
        }

        if (_controller.KeyAlreadyExists(key))
        {
            SetError($"'{key}' is already used by another host.");
            return;
        }

        SetError(null);
    }

    private void SetError(string? message)
    {
        ContinueButton.IsEnabled = message is null && KeyTextBox.Text.Trim().Length > 0;
        ErrorTextBlock.Text = message ?? string.Empty;
        ErrorTextBlock.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnContinueClick(object sender, RoutedEventArgs e)
    {
        HostKey = KeyTextBox.Text.Trim();
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
