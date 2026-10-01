using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Bosun.Health;
using Bosun.Status;

namespace Bosun.UI.Banner;

/// <summary>
/// A button in the banner's action panel. Nothing creates these yet: the repair actions
/// (Restart rclone, Unmount all and re-probe, Restart Bosun) are bs-aoz, and they will add items
/// to <see cref="HealthBannerViewModel.Actions"/>. The panel is already bound, so adding one needs
/// no XAML change.
/// </summary>
public sealed record HealthBannerAction(string Label, ICommand Command);

/// <summary>One issue as the banner lists it, for the "N more" expansion.</summary>
public sealed record HealthBannerIssueItem(string Code, HealthLevel Level, string Title, string Detail, string SinceText);

/// <summary>
/// What the window's health banner and header show (bs-yyg, ADR-020 Decision 4). Plain data plus
/// <see cref="INotifyPropertyChanged"/> -- no WPF controls or dispatcher -- so every rule about what
/// the banner says is unit-testable. The window owns one instance, calls <see cref="Update"/> from
/// its refresh timer (on the UI thread, like the rest of the window), and binds the XAML to it.
/// </summary>
/// <remarks>
/// The banner is visible exactly when <see cref="AppHealth.Level"/> is not OK. It shows the most
/// severe issue (<see cref="AppHealth.TopIssue"/>: the list is already ordered) in full, and the rest
/// behind an "N more" toggle. Host-level problems (<see cref="AggregateHealth"/>) never produce a
/// banner: they live on the host rows. They only influence <see cref="HeaderText"/>.
/// </remarks>
public sealed class HealthBannerViewModel : INotifyPropertyChanged
{
    private readonly TimeZoneInfo timeZone;

    private bool isVisible;
    private HealthLevel level;
    private string title = string.Empty;
    private string detail = string.Empty;
    private string sinceText = string.Empty;
    private string moreText = string.Empty;
    private string headerText = HeaderFor(AppHealth.Healthy, AggregateHealth.Healthy);
    private bool isExpanded;
    private IReadOnlyList<HealthBannerIssueItem> otherIssues = [];

    /// <param name="timeZone">Used to render "since" times; the local zone when omitted. Injected so
    /// tests are not machine-dependent.</param>
    public HealthBannerViewModel(TimeZoneInfo? timeZone = null)
    {
        this.timeZone = timeZone ?? TimeZoneInfo.Local;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The banner is shown only when the app's own health is not OK. Collapsed it takes no
    /// layout space.</summary>
    public bool IsVisible
    {
        get => isVisible;
        private set => Set(ref isVisible, value);
    }

    /// <summary>Drives the banner's colours: Degraded is amber, Faulted is red.</summary>
    public HealthLevel Level
    {
        get => level;
        private set => Set(ref level, value);
    }

    /// <summary>The most severe issue's one-line title; empty when hidden.</summary>
    public string Title
    {
        get => title;
        private set => Set(ref title, value);
    }

    /// <summary>The most severe issue's detail (e.g. rclone's real fault message); empty when hidden.</summary>
    public string Detail
    {
        get => detail;
        private set => Set(ref detail, value);
    }

    /// <summary>"Since 2026-10-01 09:14:03" -- how long ago it started is what separates a blip from
    /// the 2.5-day silence this model exists to prevent.</summary>
    public string SinceText
    {
        get => sinceText;
        private set => Set(ref sinceText, value);
    }

    public bool HasMore => OtherIssues.Count > 0;

    /// <summary>The number of issues besides the top one.</summary>
    public int MoreCount => OtherIssues.Count;

    /// <summary>The toggle's label: "2 more" while collapsed, "Hide" while expanded; empty when there
    /// is nothing more to show.</summary>
    public string MoreText
    {
        get => moreText;
        private set => Set(ref moreText, value);
    }

    /// <summary>Whether the other issues are listed. Two-way bound to the toggle. Forced back to
    /// collapsed when there is nothing more to show.</summary>
    public bool IsExpanded
    {
        get => isExpanded;
        set
        {
            if (Set(ref isExpanded, value && HasMore))
            {
                MoreText = BuildMoreText();
            }
        }
    }

    /// <summary>Every issue after the top one, in the model's order.</summary>
    public IReadOnlyList<HealthBannerIssueItem> OtherIssues
    {
        get => otherIssues;
        private set
        {
            otherIssues = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasMore));
            OnPropertyChanged(nameof(MoreCount));
        }
    }

    /// <summary>Action buttons. Empty until the repair actions land (bs-aoz); the banner's action
    /// panel is already bound to it.</summary>
    public ObservableCollection<HealthBannerAction> Actions { get; } = [];

    /// <summary>The window header: reflects the level, not just "Error" -- "Bosun — Faulted",
    /// "Bosun — Degraded", "Bosun — Starting…", "Bosun — Hosts need attention", "Bosun — Healthy".</summary>
    public string HeaderText
    {
        get => headerText;
        private set => Set(ref headerText, value);
    }

    /// <summary>Recomputes everything from the latest app health and host roll-up. Cheap and
    /// idempotent: properties whose value did not change raise nothing.</summary>
    public void Update(AppHealth appHealth, AggregateHealth hostHealth)
    {
        ArgumentNullException.ThrowIfNull(appHealth);

        var top = appHealth.TopIssue;
        IsVisible = top is not null;
        Level = appHealth.Level;
        Title = top?.Title ?? string.Empty;
        Detail = top?.Detail ?? string.Empty;
        SinceText = top is null ? string.Empty : BuildSince(top);

        var others = appHealth.Issues
            .Skip(1)
            .Select(i => new HealthBannerIssueItem(i.Code, i.Severity, i.Title, i.Detail, BuildSince(i)))
            .ToList();
        if (!others.SequenceEqual(otherIssues))
        {
            OtherIssues = others;
        }

        // Re-evaluates the collapse rule and the label against the new count.
        var wasExpanded = isExpanded;
        isExpanded = wasExpanded && HasMore;
        if (isExpanded != wasExpanded)
        {
            OnPropertyChanged(nameof(IsExpanded));
        }

        MoreText = BuildMoreText();
        HeaderText = HeaderFor(appHealth, hostHealth);
    }

    private string BuildMoreText() =>
        !HasMore ? string.Empty : isExpanded ? "Hide" : string.Create(CultureInfo.InvariantCulture, $"{MoreCount} more");

    private string BuildSince(HealthIssue issue) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"Since {TimeZoneInfo.ConvertTime(issue.FirstSeenUtc, timeZone):yyyy-MM-dd HH:mm:ss}");

    /// <summary>
    /// The header's one-word-or-so status. The app's own health outranks the hosts': a Faulted Bosun
    /// says so before it says anything about hosts. A host-level Error (a persistent host that keeps
    /// failing to mount) is not "Faulted" -- Bosun is working, a host is not -- so it says what to
    /// do instead.
    /// </summary>
    internal static string HeaderFor(AppHealth appHealth, AggregateHealth hostHealth) =>
        appHealth.Level == HealthLevel.Faulted ? "Bosun — Faulted"

        // Starting outranks the host roll-up: while rclone is still coming up every mountable host
        // reads "unavailable", which is the expected state of starting, not a host needing attention.
        : appHealth.IsStarting ? "Bosun — Starting…"
        : hostHealth == AggregateHealth.Error ? "Bosun — Hosts need attention"
        : appHealth.Level == HealthLevel.Degraded || hostHealth == AggregateHealth.Degraded ? "Bosun — Degraded"
        : "Bosun — Healthy";

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
