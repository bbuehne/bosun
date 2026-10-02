using Bosun.Health;
using Bosun.Status;
using Bosun.UI.Banner;

namespace Bosun.Tests.UI.Banner;

/// <summary>
/// The window's health banner and header (bs-yyg): visibility, text, the "N more" expansion, and the
/// header wording, all driven through <see cref="HealthBannerViewModel.Update"/> -- no WPF, no UI
/// automation.
/// </summary>
public sealed class HealthBannerViewModelTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 14, 3, TimeSpan.Zero);

    private static HealthBannerViewModel NewViewModel() => new(TimeZoneInfo.Utc);

    private static HealthIssue Issue(
        string code, HealthLevel severity = HealthLevel.Faulted, string? title = null, string detail = "detail", DateTimeOffset? since = null) =>
        new(code, severity, title ?? $"title of {code}", detail, since ?? T0);

    private static AppHealth Health(params HealthIssue[] issues) => new()
    {
        Level = issues.Length == 0 ? HealthLevel.Ok : issues.Max(i => i.Severity),
        Issues = issues,
    };

    // ------------------------------------------------------------------------------------------
    // Visibility and the top issue
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void When_health_is_Ok_the_banner_is_not_visible_and_empty()
    {
        var vm = NewViewModel();

        vm.Update(AppHealth.Healthy, AggregateHealth.Healthy);

        Assert.False(vm.IsVisible);
        Assert.Equal(string.Empty, vm.Title);
        Assert.Equal(string.Empty, vm.Detail);
        Assert.False(vm.HasMore);
    }

    [Fact]
    public void A_single_issue_shows_its_title_detail_level_and_since_time()
    {
        var vm = NewViewModel();

        vm.Update(Health(Issue("rclone.port-held", title: "Another program is using rclone's control port", detail: "PID 4242 (C:\\x\\rclone.exe)")), AggregateHealth.Healthy);

        Assert.True(vm.IsVisible);
        Assert.Equal(HealthLevel.Faulted, vm.Level);
        Assert.Equal("Another program is using rclone's control port", vm.Title);
        Assert.Equal("PID 4242 (C:\\x\\rclone.exe)", vm.Detail);
        Assert.Equal("Since 2026-10-01 09:14:03", vm.SinceText);
        Assert.False(vm.HasMore);
        Assert.Equal(string.Empty, vm.MoreText);
    }

    [Fact]
    public void A_Degraded_only_state_is_visible_with_the_Degraded_level()
    {
        var vm = NewViewModel();

        vm.Update(Health(Issue("startup.terminal-fragment", HealthLevel.Degraded)), AggregateHealth.Healthy);

        Assert.True(vm.IsVisible);
        Assert.Equal(HealthLevel.Degraded, vm.Level);
    }

    [Fact]
    public void Recovery_hides_the_banner_again()
    {
        var vm = NewViewModel();
        vm.Update(Health(Issue("a.b")), AggregateHealth.Healthy);

        vm.Update(AppHealth.Healthy, AggregateHealth.Healthy);

        Assert.False(vm.IsVisible);
        Assert.Equal(string.Empty, vm.Title);
    }

    [Fact]
    public void Since_text_is_rendered_in_the_injected_time_zone()
    {
        var vm = new HealthBannerViewModel(TimeZoneInfo.CreateCustomTimeZone("plus2", TimeSpan.FromHours(2), "plus2", "plus2"));

        vm.Update(Health(Issue("a.b")), AggregateHealth.Healthy);

        Assert.Equal("Since 2026-10-01 11:14:03", vm.SinceText);
    }

    // ------------------------------------------------------------------------------------------
    // "N more"
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Several_issues_show_the_most_severe_in_full_and_count_the_rest()
    {
        var vm = NewViewModel();
        var top = Issue("a.faulted", HealthLevel.Faulted, title: "the worst");
        var second = Issue("b.degraded", HealthLevel.Degraded, title: "second");
        var third = Issue("c.degraded", HealthLevel.Degraded, title: "third");

        // AppHealth orders issues; the banner trusts that order, so it is given here as the service
        // would produce it.
        vm.Update(Health(top, second, third), AggregateHealth.Healthy);

        Assert.Equal("the worst", vm.Title);
        Assert.True(vm.HasMore);
        Assert.Equal(2, vm.MoreCount);
        Assert.Equal("2 more", vm.MoreText);
        Assert.Equal(new[] { "second", "third" }, vm.OtherIssues.Select(i => i.Title).ToArray());
        Assert.False(vm.IsExpanded);
    }

    [Fact]
    public void Expanding_changes_the_toggle_label_and_collapsing_restores_it()
    {
        var vm = NewViewModel();
        vm.Update(Health(Issue("a.b"), Issue("c.d")), AggregateHealth.Healthy);

        vm.IsExpanded = true;
        Assert.True(vm.IsExpanded);
        Assert.Equal("Hide", vm.MoreText);

        vm.IsExpanded = false;
        Assert.Equal("1 more", vm.MoreText);
    }

    [Fact]
    public void Expansion_survives_a_refresh_that_changes_nothing()
    {
        var vm = NewViewModel();
        var health = Health(Issue("a.b"), Issue("c.d"));
        vm.Update(health, AggregateHealth.Healthy);
        vm.IsExpanded = true;

        vm.Update(health, AggregateHealth.Healthy);

        Assert.True(vm.IsExpanded);
    }

    [Fact]
    public void Expansion_collapses_when_there_is_nothing_more_to_show()
    {
        var vm = NewViewModel();
        vm.Update(Health(Issue("a.b"), Issue("c.d")), AggregateHealth.Healthy);
        vm.IsExpanded = true;

        vm.Update(Health(Issue("a.b")), AggregateHealth.Healthy);

        Assert.False(vm.IsExpanded);
        Assert.Equal(string.Empty, vm.MoreText);
    }

    [Fact]
    public void A_toggle_with_nothing_to_expand_stays_collapsed()
    {
        var vm = NewViewModel();
        vm.Update(Health(Issue("a.b")), AggregateHealth.Healthy);

        vm.IsExpanded = true;

        Assert.False(vm.IsExpanded);
    }

    // ------------------------------------------------------------------------------------------
    // Actions (filled from RepairActionPlan once UseActions supplies commands: see RepairActionPlanTests)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void The_actions_collection_exists_and_is_empty_until_a_command_source_is_supplied()
    {
        var vm = NewViewModel();
        vm.Update(Health(Issue("a.b")), AggregateHealth.Healthy);

        Assert.NotNull(vm.Actions);
        Assert.Empty(vm.Actions);
    }

    // ------------------------------------------------------------------------------------------
    // Change notification
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void An_unchanged_update_raises_no_property_changes()
    {
        var vm = NewViewModel();
        var health = Health(Issue("a.b"), Issue("c.d"));
        vm.Update(health, AggregateHealth.Healthy);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.Update(health, AggregateHealth.Healthy);

        Assert.Empty(raised);
    }

    [Fact]
    public void A_changed_title_raises_PropertyChanged_for_it()
    {
        var vm = NewViewModel();
        vm.Update(Health(Issue("a.b", title: "one")), AggregateHealth.Healthy);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.Update(Health(Issue("a.b", title: "two")), AggregateHealth.Healthy);

        Assert.Contains(nameof(HealthBannerViewModel.Title), raised);
    }

    // ------------------------------------------------------------------------------------------
    // Header text: reflects the level, not just "Error"
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Header_says_Healthy_when_nothing_is_wrong()
    {
        var vm = NewViewModel();
        vm.Update(AppHealth.Healthy, AggregateHealth.Healthy);

        Assert.Equal("Bosun — Healthy", vm.HeaderText);
    }

    [Fact]
    public void Header_says_Faulted_for_a_Faulted_app_even_when_every_host_looks_fine()
    {
        var vm = NewViewModel();
        vm.Update(Health(Issue("supervisor.loop-stopped")), AggregateHealth.Healthy);

        Assert.Equal("Bosun — Faulted", vm.HeaderText);
    }

    [Fact]
    public void Header_says_Faulted_rather_than_Error_when_the_host_roll_up_is_Error_too()
    {
        // The 2026-10-01 case: rclone faulted makes every host MountingUnavailable (Error).
        var vm = NewViewModel();
        vm.Update(Health(Issue("rclone.unauthorized")), AggregateHealth.Error);

        Assert.Equal("Bosun — Faulted", vm.HeaderText);
    }

    [Fact]
    public void Header_says_Degraded_for_a_Degraded_app()
    {
        var vm = NewViewModel();
        vm.Update(Health(Issue("startup.terminal-fragment", HealthLevel.Degraded)), AggregateHealth.Healthy);

        Assert.Equal("Bosun — Degraded", vm.HeaderText);
    }

    [Fact]
    public void Header_says_Degraded_for_a_Degraded_host_roll_up_with_a_healthy_app()
    {
        var vm = NewViewModel();
        vm.Update(AppHealth.Healthy, AggregateHealth.Degraded);

        Assert.Equal("Bosun — Degraded", vm.HeaderText);
        Assert.False(vm.IsVisible);
    }

    [Fact]
    public void A_host_level_Error_with_a_healthy_app_says_hosts_need_attention_and_shows_no_banner()
    {
        var vm = NewViewModel();
        vm.Update(AppHealth.Healthy, AggregateHealth.Error);

        Assert.Equal("Bosun — Hosts need attention", vm.HeaderText);
        Assert.False(vm.IsVisible);
    }

    [Fact]
    public void Header_says_Starting_during_startup_even_though_every_host_reads_unavailable()
    {
        var vm = NewViewModel();

        vm.Update(new AppHealth { Level = HealthLevel.Ok, Issues = [], IsStarting = true }, AggregateHealth.Error);

        Assert.Equal("Bosun — Starting…", vm.HeaderText);
        Assert.False(vm.IsVisible);
    }
}
