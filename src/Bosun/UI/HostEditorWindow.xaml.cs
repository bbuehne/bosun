using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Bosun.Configuration;
using Bosun.UI.HostEditor;

namespace Bosun.UI;

/// <summary>
/// Add/edit-one-host dialog (bs-ww9.8, ADR-019). Deliberately thin: every parsing, defaulting, and
/// validation-mapping decision lives in <see cref="HostEditorController"/>, which is what
/// <c>tests/Bosun.Tests/UI/HostEditor</c> exercises. This class only reads/writes named controls
/// and is therefore left untested, per CLAUDE.md's XAML/layout carve-out and the rule that no
/// default-suite test may construct a WPF <see cref="Window"/>.
/// </summary>
public partial class HostEditorWindow : Window
{
    private readonly HostEditorController _controller;
    private readonly IIdentityFilePicker _filePicker;
    private readonly Dictionary<HostFormFieldId, (Control Control, TextBlock? ErrorBlock)> _fieldControls;

    public HostEditorWindow(HostEditorController controller, IIdentityFilePicker filePicker, HostFormModel model)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(filePicker);
        ArgumentNullException.ThrowIfNull(model);

        InitializeComponent();

        _controller = controller;
        _filePicker = filePicker;

        _fieldControls = new Dictionary<HostFormFieldId, (Control, TextBlock?)>
        {
            [HostFormFieldId.Key] = (KeyTextBox, KeyError),
            [HostFormFieldId.DisplayName] = (DisplayNameTextBox, DisplayNameError),
            [HostFormFieldId.Hostname] = (HostnameTextBox, HostnameError),
            [HostFormFieldId.Port] = (PortTextBox, PortError),
            [HostFormFieldId.User] = (UserTextBox, UserError),
            [HostFormFieldId.IdentityFile] = (IdentityFileTextBox, IdentityFileError),
            [HostFormFieldId.Drive] = (DriveTextBox, DriveError),
            [HostFormFieldId.RemotePath] = (RemotePathTextBox, RemotePathError),
            [HostFormFieldId.VfsCacheMode] = (VfsCacheModeComboBox, VfsCacheModeError),
            [HostFormFieldId.IdleUnmountSeconds] = (IdleUnmountSecondsTextBox, IdleUnmountSecondsError),
            [HostFormFieldId.TmuxSession] = (TmuxSessionTextBox, TmuxSessionError),
            [HostFormFieldId.ProbeIntervalSeconds] = (ProbeIntervalSecondsTextBox, ProbeIntervalSecondsError),
        };

        ModeComboBox.ItemsSource = new[] { MountMode.Persistent, MountMode.OnDemand, MountMode.None };
        VfsCacheModeComboBox.ItemsSource = HostEditorController.AllowedVfsCacheModes;

        LoadFromModel(model);
    }

    private void LoadFromModel(HostFormModel model)
    {
        Title = model.IsNewHost ? "Add host" : $"Edit host -- {model.Key}";

        KeyTextBox.Text = model.Key;
        KeyTextBox.IsReadOnly = !model.IsNewHost;
        if (!model.IsNewHost)
        {
            KeyTextBox.Background = SystemColors.ControlLightBrush;
        }

        DisplayNameTextBox.Text = model.DisplayName;
        HostnameTextBox.Text = model.Hostname;
        PortTextBox.Text = model.Port;
        UserTextBox.Text = model.User;
        IdentityFileTextBox.Text = model.IdentityFile;

        ModeComboBox.SelectedItem = model.Mode;
        DriveTextBox.Text = model.Drive;
        RemotePathTextBox.Text = model.RemotePath;
        VfsCacheModeComboBox.SelectedItem = model.VfsCacheMode;
        IdleUnmountSecondsTextBox.Text = model.IdleUnmountSeconds;

        AutostartCheckBox.IsChecked = model.Autostart;
        ReconnectCheckBox.IsChecked = model.Reconnect;
        TmuxCheckBox.IsChecked = model.Tmux;
        TmuxSessionTextBox.Text = model.TmuxSession;
        TabColorTextBox.Text = model.TabColor;
        ColorSchemeTextBox.Text = model.ColorScheme;

        ProbeIntervalSecondsTextBox.Text = model.ProbeIntervalSeconds;
        DeepProbeCheckBox.IsChecked = model.DeepProbe;

        ApplyMountDetailEnablement();
        ApplyTmuxSessionEnablement();
    }

    private HostFormModel ReadModel() => new()
    {
        Key = KeyTextBox.Text,
        IsNewHost = !KeyTextBox.IsReadOnly,
        DisplayName = DisplayNameTextBox.Text,
        Hostname = HostnameTextBox.Text,
        Port = PortTextBox.Text,
        User = UserTextBox.Text,
        IdentityFile = IdentityFileTextBox.Text,
        Mode = ModeComboBox.SelectedItem is MountMode mode ? mode : MountMode.None,
        Drive = DriveTextBox.Text,
        RemotePath = RemotePathTextBox.Text,
        VfsCacheMode = VfsCacheModeComboBox.SelectedItem as string ?? MountConfig.DefaultVfsCacheMode,
        IdleUnmountSeconds = IdleUnmountSecondsTextBox.Text,
        Autostart = AutostartCheckBox.IsChecked == true,
        Reconnect = ReconnectCheckBox.IsChecked == true,
        Tmux = TmuxCheckBox.IsChecked == true,
        TmuxSession = TmuxSessionTextBox.Text,
        TabColor = TabColorTextBox.Text,
        ColorScheme = ColorSchemeTextBox.Text,
        ProbeIntervalSeconds = ProbeIntervalSecondsTextBox.Text,
        DeepProbe = DeepProbeCheckBox.IsChecked == true,
    };

    private void OnModeSelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyMountDetailEnablement();

    private void ApplyMountDetailEnablement()
    {
        var selectedMode = ModeComboBox.SelectedItem is MountMode mode ? mode : MountMode.None;
        MountDetailPanel.IsEnabled = HostEditorController.IsMountDetailEnabled(selectedMode);
    }

    private void OnTmuxCheckedChanged(object sender, RoutedEventArgs e) => ApplyTmuxSessionEnablement();

    private void ApplyTmuxSessionEnablement()
    {
        TmuxSessionPanel.IsEnabled = HostEditorController.IsTmuxSessionEnabled(TmuxCheckBox.IsChecked == true);
    }

    private void OnBrowseIdentityFileClick(object sender, RoutedEventArgs e)
    {
        var current = IdentityFileTextBox.Text;
        var initialDirectory = TryResolveDirectory(current)
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");

        var picked = _filePicker.PickIdentityFile(initialDirectory);
        if (picked is not null)
        {
            IdentityFileTextBox.Text = picked;
        }
    }

    private static string? TryResolveDirectory(string path)
    {
        try
        {
            var expanded = ConfigValidator.ExpandHome(path);
            var directory = Path.GetDirectoryName(expanded);
            return directory is not null && Directory.Exists(directory) ? directory : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = false;
        try
        {
            var result = await _controller.SaveAsync(ReadModel());
            if (result.Succeeded)
            {
                MessageBox.Show(
                    this,
                    "Host saved. Changes to mount mode, drive, or tier take effect only after Bosun restarts -- the mount supervisor builds its host set once at startup.",
                    "Host saved",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                DialogResult = true;
                Close();
                return;
            }

            ShowErrors(result.FieldErrors, result.GeneralError);
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private void ShowErrors(IReadOnlyList<HostFormValidationError> fieldErrors, string? generalError)
    {
        foreach (var (control, errorBlock) in _fieldControls.Values)
        {
            control.ClearValue(Control.BorderBrushProperty);
            control.ClearValue(Control.BorderThicknessProperty);
            if (errorBlock is not null)
            {
                errorBlock.Visibility = Visibility.Collapsed;
                errorBlock.Text = string.Empty;
            }
        }

        foreach (var error in fieldErrors)
        {
            if (!_fieldControls.TryGetValue(error.Field, out var target))
            {
                continue;
            }

            target.Control.BorderBrush = Brushes.Firebrick;
            target.Control.BorderThickness = new Thickness(1.5);
            if (target.ErrorBlock is not null)
            {
                target.ErrorBlock.Text = error.Message;
                target.ErrorBlock.Visibility = Visibility.Visible;
            }
        }

        ErrorSummaryTextBlock.Text = generalError ?? "Save failed.";
        ErrorSummaryTextBlock.Visibility = Visibility.Visible;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
