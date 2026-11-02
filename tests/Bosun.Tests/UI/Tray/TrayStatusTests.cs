using Bosun.Health;
using Bosun.Status;
using Bosun.Supervisor;
using Bosun.Tests.UI.Fakes;
using Bosun.UI.Tray;

namespace Bosun.Tests.UI.Tray;

/// <summary>
/// The tray's combination of the host roll-up and the app's own health (bs-yyg): which of the
/// existing three icons shows, and what the tooltip says.
/// </summary>
public sealed class TrayStatusTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private static readonly IReadOnlyList<HostStatusRow> NoRows = [];

    private static AppHealth Health(params (string Code, HealthLevel Severity, string Title)[] issues) => new()
    {
        Level = issues.Length == 0 ? HealthLevel.Ok : issues.Max(i => i.Severity),
        Issues = issues.Select(i => new HealthIssue(i.Code, i.Severity, i.Title, "detail", T0)).ToList(),
    };

    [Fact]
    public void A_healthy_app_and_healthy_hosts_keep_the_existing_healthy_tooltip()
    {
        var status = TrayStatus.Compose(AggregateHealth.Healthy, AppHealth.Healthy, NoRows);

        Assert.Equal(AggregateHealth.Healthy, status.IconHealth);
        Assert.Equal(TrayIconAppearanceSelector.Select(AggregateHealth.Healthy).AccessibleName, status.Tooltip);
    }

    [Fact]
    public void A_Faulted_app_shows_the_error_icon_even_when_every_host_looks_healthy()
    {
        // The supervisor loop dying leaves host rows frozen at whatever they last said.
        var status = TrayStatus.Compose(
            AggregateHealth.Healthy, Health(("supervisor.loop-stopped", HealthLevel.Faulted, "Mount supervision has stopped")), NoRows);

        Assert.Equal(AggregateHealth.Error, status.IconHealth);
        Assert.Equal("Bosun — Faulted: Mount supervision has stopped", status.Tooltip);
    }

    [Fact]
    public void A_Degraded_app_shows_at_least_the_degraded_icon_and_never_downgrades_a_host_error()
    {
        var degraded = Health(("startup.terminal-fragment", HealthLevel.Degraded, "Windows Terminal profiles could not be written"));

        Assert.Equal(AggregateHealth.Degraded, TrayStatus.Compose(AggregateHealth.Healthy, degraded, NoRows).IconHealth);
        Assert.Equal(AggregateHealth.Error, TrayStatus.Compose(AggregateHealth.Error, degraded, NoRows).IconHealth);
    }

    [Fact]
    public void The_tooltip_names_the_level_and_the_top_issue_and_counts_the_rest()
    {
        var status = TrayStatus.Compose(
            AggregateHealth.Error,
            Health(
                ("rclone.unauthorized", HealthLevel.Faulted, "Another rclone is answering on Bosun's control port"),
                ("startup.terminal-fragment", HealthLevel.Degraded, "Terminal profiles not written")),
            NoRows);

        Assert.Equal("Bosun — Faulted: Another rclone is answering on Bosun's control port (+1 more)", status.Tooltip);
    }

    [Fact]
    public void The_tooltip_is_truncated_to_the_Win32_limit()
    {
        var status = TrayStatus.Compose(
            AggregateHealth.Healthy, Health(("a.b", HealthLevel.Faulted, new string('x', 400))), NoRows);

        Assert.Equal(TrayStatus.MaxTooltipLength, status.Tooltip.Length);
        Assert.EndsWith("…", status.Tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void Starting_holds_the_host_error_at_degraded_and_says_so()
    {
        // While rclone is coming up every mountable host reads unavailable (Error); a red icon for
        // the first seconds of every launch would teach the user to ignore red.
        var starting = new AppHealth { Level = HealthLevel.Ok, Issues = [], IsStarting = true };

        var status = TrayStatus.Compose(AggregateHealth.Error, starting, [HostRowFactory.MountFailing()]);

        Assert.Equal(AggregateHealth.Degraded, status.IconHealth);
        Assert.Equal("Bosun — starting…", status.Tooltip);
    }

    [Fact]
    public void A_host_level_Error_with_a_healthy_app_names_the_failing_host_not_mounting_unavailable()
    {
        // bs-ww9.1.2: a persistent host failing to mount is a host problem. The old tooltip said
        // "mounting unavailable", which is the process-wide condition and was simply wrong here.
        var status = TrayStatus.Compose(
            AggregateHealth.Error, AppHealth.Healthy, [HostRowFactory.Mounted(), HostRowFactory.MountFailing(times: 3, lastError: "x")]);

        Assert.Equal(AggregateHealth.Error, status.IconHealth);
        Assert.Equal("Bosun — P: is not mounted -- mount failed 3 times; last error: x", status.Tooltip);
        Assert.DoesNotContain("mounting unavailable", status.Tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void A_host_level_Degraded_with_a_healthy_app_names_the_unreachable_host()
    {
        var status = TrayStatus.Compose(AggregateHealth.Degraded, AppHealth.Healthy, [HostRowFactory.Unreachable()]);

        Assert.Equal(AggregateHealth.Degraded, status.IconHealth);
        Assert.Equal("Bosun — Q: is not mounted -- host unreachable after 3 consecutive failed probes", status.Tooltip);
    }

    [Fact]
    public void The_worst_host_is_named_not_the_first_and_the_rest_are_counted()
    {
        // Unreachable (Degraded) comes first in the list, the mount failure (Error) second: the
        // tooltip names the Error, and says one more host is affected.
        var status = TrayStatus.Compose(
            AggregateHealth.Error,
            AppHealth.Healthy,
            [HostRowFactory.Unreachable(), HostRowFactory.MountFailing(times: 5, lastError: "x")]);

        Assert.Equal("Bosun — P: is not mounted -- mount failed 5 times; last error: x (+1 more)", status.Tooltip);
    }

    [Fact]
    public void A_parked_host_is_never_named_because_it_contributes_nothing()
    {
        var status = TrayStatus.Compose(
            AggregateHealth.Degraded, AppHealth.Healthy, [HostRowFactory.Parked(), HostRowFactory.Unreachable()]);

        Assert.StartsWith("Bosun — Q:", status.Tooltip, StringComparison.Ordinal);
        Assert.DoesNotContain("more)", status.Tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_mount_failure_keeps_the_cause_and_drops_the_last_error_clause()
    {
        // 127 characters is all the shell shows. The count is the cause; the rclone message after
        // "; last error:" is the long part and goes first.
        var status = TrayStatus.Compose(
            AggregateHealth.Error, AppHealth.Healthy, [HostRowFactory.MountFailing(times: 3, lastError: new string('e', 300))]);

        Assert.Equal("Bosun — P: is not mounted -- mount failed 3 times", status.Tooltip);
    }

    [Fact]
    public void A_cause_with_no_clause_to_drop_is_cut_with_an_ellipsis_at_the_limit_and_keeps_the_more_suffix()
    {
        var long1 = HostRowFactory.Row(
            StatusCategory.MountFailing, statusText: "P: is not mounted -- " + new string('z', 300), mountFailures: 3);

        var status = TrayStatus.Compose(AggregateHealth.Error, AppHealth.Healthy, [long1, HostRowFactory.Unreachable()]);

        Assert.True(status.Tooltip.Length <= TrayStatus.MaxTooltipLength);
        Assert.EndsWith("… (+1 more)", status.Tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void An_app_issue_still_takes_the_tooltip_over_a_host_cause()
    {
        var status = TrayStatus.Compose(
            AggregateHealth.Error,
            Health(("rclone.unauthorized", HealthLevel.Faulted, "Another rclone is answering")),
            [HostRowFactory.MountFailing()]);

        Assert.Equal("Bosun — Faulted: Another rclone is answering", status.Tooltip);
    }

    [Fact]
    public void A_host_Error_with_no_row_to_name_falls_back_to_the_icon_name()
    {
        var status = TrayStatus.Compose(AggregateHealth.Error, AppHealth.Healthy, NoRows);

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

        Assert.Equal(AggregateHealth.Error, TrayStatus.Compose(AggregateHealth.Error, faultedWhileStarting, NoRows).IconHealth);
    }
}
