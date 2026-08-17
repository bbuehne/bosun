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

    public HostEditorWindow(
        HostEditorController controller,
        IIdentityFilePicker filePicker,
        IReadOnlyList<DriveLetterOption> driveLetters,
        HostFormModel model)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(filePicker);
        ArgumentNullException.ThrowIfNull(driveLetters);
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
            [HostFormFieldId.Drive] = (DriveComboBox, DriveError),
            [HostFormFieldId.RemotePath] = (RemotePathTextBox, RemotePathError),
            [HostFormFieldId.VfsCacheMode] = (VfsCacheModeComboBox, VfsCacheModeError),
            [HostFormFieldId.IdleUnmountSeconds] = (IdleUnmountSecondsTextBox, IdleUnmountSecondsError),
            [HostFormFieldId.TmuxSession] = (TmuxSessionTextBox, TmuxSessionError),
            [HostFormFieldId.ProbeIntervalSeconds] = (ProbeIntervalSecondsTextBox, ProbeIntervalSecondsError),
        };

        // Enum names are not UI text: bound directly, this showed "OnDemand" and left all three
        // options to be guessed at. Each choice now says what it DOES.
        ModeComboBox.ItemsSource = new[]
        {
            new ModeChoice(MountMode.Persistent, "Always — mount at login and keep it mounted"),
            new ModeChoice(MountMode.OnDemand, "On demand — only when I ask from the tray"),
            new ModeChoice(MountMode.None, "Never — terminal profile only, no drive"),
        };
        ModeComboBox.DisplayMemberPath = nameof(ModeChoice.Label);

        VfsCacheModeComboBox.ItemsSource = new[]
        {
            new CacheChoice("writes", "Writes — safe for editors and Office (recommended)"),
            new CacheChoice("full", "Full — also caches reads; more disk, faster re-reads"),
        };
        VfsCacheModeComboBox.DisplayMemberPath = nameof(CacheChoice.Label);

        DriveComboBox.ItemsSource = driveLetters;

        // Editable, not a closed list: a scheme can come from the user's own settings.json or
        // another app's fragment, and Invariant I5 forbids reading settings.json to enumerate them.
        ColorSchemeComboBox.ItemsSource = HostEditorController.BuiltInColorSchemes;
        TabColorPaletteItems.ItemsSource = TabColorPalette.Choices;

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

        ModeComboBox.SelectedItem = ModeComboBox.Items.OfType<ModeChoice>().First(c => c.Mode == model.Mode);
        DriveComboBox.SelectedItem = DriveComboBox.Items.OfType<DriveLetterOption>()
            .FirstOrDefault(o => string.Equals(o.Letter, model.Drive, StringComparison.OrdinalIgnoreCase));
        RemotePathTextBox.Text = model.RemotePath;
        VfsCacheModeComboBox.SelectedItem = VfsCacheModeComboBox.Items.OfType<CacheChoice>()
            .FirstOrDefault(c => c.Value == model.VfsCacheMode) ?? VfsCacheModeComboBox.Items.OfType<CacheChoice>().First();
        IdleUnmountSecondsTextBox.Text = model.IdleUnmountSeconds;

        AutostartCheckBox.IsChecked = model.Autostart;
        ReconnectCheckBox.IsChecked = model.Reconnect;
        TmuxCheckBox.IsChecked = model.Tmux;
        TmuxSessionTextBox.Text = model.TmuxSession;
        TabColorTextBox.Text = model.TabColor;
        ColorSchemeComboBox.Text = model.ColorScheme;

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
        Mode = ModeComboBox.SelectedItem is ModeChoice choice ? choice.Mode : MountMode.None,
        Drive = (DriveComboBox.SelectedItem as DriveLetterOption)?.Letter ?? string.Empty,
        RemotePath = RemotePathTextBox.Text,
        VfsCacheMode = (VfsCacheModeComboBox.SelectedItem as CacheChoice)?.Value ?? MountConfig.DefaultVfsCacheMode,
        IdleUnmountSeconds = IdleUnmountSecondsTextBox.Text,
        Autostart = AutostartCheckBox.IsChecked == true,
        Reconnect = ReconnectCheckBox.IsChecked == true,
        Tmux = TmuxCheckBox.IsChecked == true,
        TmuxSession = TmuxSessionTextBox.Text,
        TabColor = TabColorTextBox.Text,
        ColorScheme = ColorSchemeComboBox.Text,
        ProbeIntervalSeconds = ProbeIntervalSecondsTextBox.Text,
        DeepProbe = DeepProbeCheckBox.IsChecked == true,
    };

    private void OnModeSelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyMountDetailEnablement();

    private void ApplyMountDetailEnablement()
    {
        var selectedMode = ModeComboBox.SelectedItem is ModeChoice sel ? sel.Mode : MountMode.None;
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

    /// <summary>Keeps the swatch in step with whatever is typed, so an invalid hex is visible as a
    /// blank swatch rather than only as a validation error after Save.</summary>
    private void OnTabColorTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        UpdateColorSwatch(TabColorTextBox.Text);

    private void OnPaletteSwatchClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string hex })
        {
            TabColorTextBox.Text = hex;
        }
    }

    private void UpdateColorSwatch(string? hex)
    {
        try
        {
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter
                .ConvertFromString((hex ?? string.Empty).Trim());
            TabColorSwatch.Background = new System.Windows.Media.SolidColorBrush(color);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException)
        {
            TabColorSwatch.Background = System.Windows.Media.Brushes.Transparent;
        }
    }
}

/// <summary>A mount mode plus the sentence shown for it. Enum names are not UI text.</summary>
internal sealed record ModeChoice(MountMode Mode, string Label);

/// <summary>An rclone vfs-cache-mode value plus the sentence shown for it.</summary>
internal sealed record CacheChoice(string Value, string Label);