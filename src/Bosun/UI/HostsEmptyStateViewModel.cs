using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Bosun.UI;

/// <summary>
/// What the main window's empty-state panel shows when no hosts are configured (bs-ww9.1.3,
/// ADR-012). Plain data plus <see cref="INotifyPropertyChanged"/> -- no WPF types -- so the rule
/// about when the panel appears is unit-testable. The window owns one instance, calls
/// <see cref="Update"/> from its refresh timer (UI thread), and binds the XAML to it.
/// </summary>
/// <remarks>
/// The panel is for a config that loaded and holds zero hosts: a first run (the template is a valid,
/// empty config) or a valid file whose hosts were all removed. It is not for a config that could not
/// be loaded -- there <c>configuredHostCount</c> is <see langword="null"/>, the panel stays hidden, and
/// the health banner carries the cause. It states a fact and says what to do next; it does not
/// describe the situation as anything the user did.
/// </remarks>
public sealed class HostsEmptyStateViewModel : INotifyPropertyChanged
{
    /// <summary>The panel's heading.</summary>
    public const string TitleText = "No hosts configured yet";

    /// <summary>The line above the path.</summary>
    public const string DetailText =
        "Add a host to get a Terminal profile and, if you want one, a drive letter. " +
        "Hosts are kept in this file. You can also edit it by hand; restart Bosun afterwards:";

    private bool isVisible;
    private bool canAddHost;
    private string hostsFilePath = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title => TitleText;

    public string Detail => DetailText;

    /// <summary>True when the panel is shown in place of the (empty) host table.</summary>
    public bool IsVisible
    {
        get => isVisible;
        private set => Set(ref isVisible, value);
    }

    /// <summary>True when the Add Host button works: the host editor is configured. The panel still
    /// shows without it (the path is still useful), but the button must not be a dead control.</summary>
    public bool CanAddHost
    {
        get => canAddHost;
        private set => Set(ref canAddHost, value);
    }

    /// <summary>The full path of <c>hosts.toml</c> as the running Bosun uses it.</summary>
    public string HostsFilePath
    {
        get => hostsFilePath;
        private set => Set(ref hostsFilePath, value);
    }

    /// <summary>Sets the path shown. Call once at wiring time.</summary>
    public void UseHostsFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        HostsFilePath = path;
    }

    /// <summary>
    /// Recomputes the panel from the current config.
    /// </summary>
    /// <param name="configuredHostCount">Hosts in the loaded config, or <see langword="null"/> when no
    /// config is in service (it did not load): the panel is hidden then.</param>
    /// <param name="canAddHost">Whether the host editor is available.</param>
    public void Update(int? configuredHostCount, bool canAddHost)
    {
        IsVisible = configuredHostCount is 0;
        CanAddHost = canAddHost;
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
