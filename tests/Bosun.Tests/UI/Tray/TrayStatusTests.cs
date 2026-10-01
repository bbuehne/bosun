using Bosun.Health;
using Bosun.Status;
using Bosun.UI.Tray;

namespace Bosun.Tests.UI.Tray;

/// <summary>
/// The tray's combination of the host roll-up and the app's own health (bs-yyg): which of the
/// existing three icons shows, and what the tooltip says.
/// </summary>
public sealed class TrayStatusTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private static AppHealth Health(params (string Code, HealthLevel Severity, string Title)[] issues) => new()
    {
        Level = issues.Length == 0 ? HealthLevel.Ok : issues.Max(i => i.Severity),
        Issues = issues.Select(i => new HealthIssue(i.Code, i.Severity, i.Title, "detail", T0)).ToList(),
    };

    [Fact]
    public void A_healthy_app_and_healthy_hosts_keep_the_existing_healthy_tooltip()
    {
        var status = TrayStatus.Compose(AggregateHealth.Healthy, AppHealth.Healthy);

        Assert.Equal(AggregateHealth.Healthy, status.IconHealth);
        Assert.Equal(TrayIconAppearanceSelector.Select(AggregateHealth.Healthy).AccessibleName, status.Tooltip);
    }

    [Fact]
    public void A_Faulted_app_shows_the_error_icon_even_when_every_host_looks_healthy()
    {
        // The supervisor loop dying leaves host rows frozen at whatever they last said.
        var status = TrayStatus.Compose(
            AggregateHealth.Healthy, Health(("supervisor.loop-stopped", HealthLevel.Faulted, "Mount supervision has stopped")));

        Assert.Equal(AggregateHealth.Error, status.IconHealth);
        Assert.Equal("Bosun — Faulted: Mount supervision has stopped", status.Tooltip);
    }

    [Fact]
    public void A_Degraded_app_shows_at_least_the_degraded_icon_and_never_downgrades_a_host_error()
    {
        var degraded = Health(("startup.terminal-fragment", HealthLevel.Degraded, "Windows Terminal profiles could not be written"));

        Assert.Equal(AggregateHealth.Degraded, TrayStatus.Compose(AggregateHealth.Healthy, degraded).IconHealth);
        Assert.Equal(AggregateHealth.Error, TrayStatus.Compose(AggregateHealth.Error, degraded).IconHealth);
    }

    [Fact]
    public void The_tooltip_names_the_level_and_the_top_issue_and_counts_the_rest()
    {
        var status = TrayStatus.Compose(
            AggregateHealth.Error,
            Health(
                ("rclone.unauthorized", HealthLevel.Faulted, "Another rclone is answering on Bosun's control port"),
                ("startup.terminal-fragment", HealthLevel.Degraded, "Terminal profiles not written")));

        Assert.Equal("Bosun — Faulted: Another rclone is answering on Bosun's control port (+1 more)", status.Tooltip);
    }

    [Fact]
    public void The_tooltip_is_truncated_to_the_Win32_limit()
    {
        var status = TrayStatus.Compose(
            AggregateHealth.Healthy, Health(("a.b", HealthLevel.Faulted, new string('x', 400))));

        Assert.Equal(TrayStatus.MaxTooltipLength, status.Tooltip.Length);
        Assert.EndsWith("…", status.Tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void Starting_holds_the_host_error_at_degraded_and_says_so()
    {
        // While rclone is coming up every mountable host reads unavailable (Error); a red icon for
        // the first seconds of every launch would teach the user to ignore red.
        var starting = new AppHealth { Level = HealthLevel.Ok, Issues = [], IsStarting = true };

        var status = TrayStatus.Compose(AggregateHealth.Error, starting);

        Assert.Equal(AggregateHealth.Degraded, status.IconHealth);
        Assert.Equal("Bosun — starting…", status.Tooltip);
    }

    [Fact]
    public void A_host_level_Error_with_a_healthy_app_keeps_the_existing_error_icon_and_tooltip()
    {
        var status = TrayStatus.Compose(AggregateHealth.Error, AppHealth.Healthy);

        Assert.Equal(AggregateHealth.Error, status.IconHealth);
        Assert.Equal(TrayIconAppearanceSelector.Select(AggregateHealth.Error).AccessibleName, status.Tooltip);
    }

    [Fact]
    public void A_real_fault_during_startup_is_not_masked()
    {
        var faultedWhileStarting = new AppHealth
        {
            Level = HealthLevel.Faulted,
            Issues = [new HealthIssue("startup.winfsp-missing", HealthLevel.Faulted, "WinFsp is not installed", "d", T0)],
            IsStarting = true,
        };

        Assert.Equal(AggregateHealth.Error, TrayStatus.Compose(AggregateHealth.Error, faultedWhileStarting).IconHealth);
    }
}
