using Bosun.Configuration;
using Bosun.Status;
using Bosun.Supervisor;
using Bosun.Tests.UI.Fakes;
using Bosun.UI;

namespace Bosun.Tests.UI;

/// <summary>
/// The window's State column and the tray menu's host header, both derived from
/// <see cref="HostStatusRow.Category"/> (bs-ww9.1.2). The bug these pin: the column used to bind the raw
/// <see cref="MountState"/>, so a parked host read "Unreachable" beside "P: is parked".
/// </summary>
public sealed class HostRowPresentationTests
{
    public static TheoryData<StatusCategory, MountState, string, HostRowTone> Table => new()
    {
        { StatusCategory.Healthy, MountState.Mounted, "Mounted", HostRowTone.Healthy },
        { StatusCategory.OnDemandIdle, MountState.Ready, "On demand", HostRowTone.Neutral },
        { StatusCategory.Parked, MountState.Ready, "Parked", HostRowTone.Muted },
        { StatusCategory.MountingUnavailable, MountState.Ready, "Mounting unavailable", HostRowTone.Warning },
        { StatusCategory.MountFailing, MountState.Ready, "Mount failing", HostRowTone.Error },
        { StatusCategory.Unreachable, MountState.Unreachable, "Unreachable", HostRowTone.Warning },
        { StatusCategory.ProvisioningFailed, MountState.Ready, "Setup failed", HostRowTone.Error },
        { StatusCategory.ProvisioningFailed, MountState.Mounted, "Mounted, setup failed", HostRowTone.Warning },
        { StatusCategory.NotConfigured, MountState.Disabled, "No mount", HostRowTone.Neutral },
        { StatusCategory.Pending, MountState.Probing, "Checking", HostRowTone.Neutral },
        { StatusCategory.Pending, MountState.Ready, "Ready", HostRowTone.Neutral },
        { StatusCategory.Pending, MountState.Mounting, "Mounting", HostRowTone.Neutral },
        { StatusCategory.Pending, MountState.Draining, "Unmounting", HostRowTone.Neutral },
        { StatusCategory.Pending, MountState.Disabled, "Starting", HostRowTone.Neutral },
    };

    [Theory]
    [MemberData(nameof(Table))]
    public void The_label_and_tone_follow_the_category(StatusCategory category, MountState state, string label, HostRowTone tone)
    {
        var presentation = HostRowPresentation.Create(HostRowFactory.Row(category, state: state));

        Assert.Equal(label, presentation.StateLabel);
        Assert.Equal(tone, presentation.Tone);
    }

    [Fact]
    public void A_parked_host_never_reads_Unreachable_whatever_its_raw_state()
    {
        // The reported defect: Parked is a category, and the raw state under it is Unreachable.
        var presentation = HostRowPresentation.Create(HostRowFactory.Parked());

        Assert.Equal(MountState.Unreachable, presentation.Row.State);
        Assert.Equal("Parked", presentation.StateLabel);
        Assert.DoesNotContain("Unreachable", presentation.StateDisplay, StringComparison.Ordinal);
        Assert.DoesNotContain("unreachable", presentation.TrayHeader, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_category_has_a_presentation()
    {
        foreach (var category in Enum.GetValues<StatusCategory>())
        {
            var presentation = HostRowPresentation.Create(HostRowFactory.Row(category));

            Assert.False(string.IsNullOrWhiteSpace(presentation.StateLabel), category.ToString());
            Assert.False(string.IsNullOrWhiteSpace(presentation.Glyph), category.ToString());
        }
    }

    [Fact]
    public void Parked_and_Unreachable_and_Mounted_differ_by_glyph_as_well_as_tone()
    {
        // Colour alone is not enough to tell them apart.
        var glyphs = new[]
        {
            HostRowPresentation.Create(HostRowFactory.Mounted()).Glyph,
            HostRowPresentation.Create(HostRowFactory.Parked()).Glyph,
            HostRowPresentation.Create(HostRowFactory.Unreachable()).Glyph,
            HostRowPresentation.Create(HostRowFactory.MountFailing()).Glyph,
        };

        Assert.Equal(glyphs.Length, glyphs.Distinct().Count());
    }

    [Fact]
    public void The_state_cell_is_the_glyph_then_the_label()
    {
        var presentation = HostRowPresentation.Create(HostRowFactory.Parked());

        Assert.Equal($"{presentation.Glyph} Parked", presentation.StateDisplay);
    }

    [Fact]
    public void The_grid_columns_read_through_to_the_row()
    {
        var row = HostRowFactory.Mounted("earth", "S:");

        var presentation = HostRowPresentation.Create(row);

        Assert.Same(row, presentation.Row);
        Assert.Equal("Earth", presentation.DisplayName);
        Assert.Equal("S:", presentation.Drive);
        Assert.Equal("S: is mounted", presentation.StatusText);
    }

    [Theory]
    [InlineData(StatusCategory.Healthy, MountState.Mounted, "Mercury — mounted (P:)")]
    [InlineData(StatusCategory.Parked, MountState.Ready, "Mercury — parked")]
    [InlineData(StatusCategory.Unreachable, MountState.Unreachable, "Mercury — unreachable")]
    [InlineData(StatusCategory.MountFailing, MountState.Ready, "Mercury — mount failing")]
    [InlineData(StatusCategory.OnDemandIdle, MountState.Ready, "Mercury — on demand")]
    [InlineData(StatusCategory.ProvisioningFailed, MountState.Mounted, "Mercury — mounted, setup failed")]
    [InlineData(StatusCategory.Pending, MountState.Probing, "Mercury — checking")]
    public void The_tray_header_is_the_name_and_a_short_status(StatusCategory category, MountState state, string expected)
    {
        var presentation = HostRowPresentation.Create(HostRowFactory.Row(category, state: state));

        Assert.Equal(expected, presentation.TrayHeader);
    }

    [Fact]
    public void The_tray_header_for_a_mounted_host_with_no_drive_does_not_print_empty_brackets()
    {
        var presentation = HostRowPresentation.Create(HostRowFactory.Row(StatusCategory.Healthy, state: MountState.Mounted, drive: null));

        Assert.Equal("Mercury — mounted", presentation.TrayHeader);
    }

    [Fact]
    public void An_unmapped_category_throws_rather_than_rendering_as_fine()
    {
        var row = HostRowFactory.Row((StatusCategory)999);

        Assert.Throws<ArgumentOutOfRangeException>(() => HostRowPresentation.Create(row));
    }

    [Fact]
    public void Create_throws_for_a_null_row()
    {
        Assert.Throws<ArgumentNullException>(() => HostRowPresentation.Create(null!));
    }

    [Fact]
    public void Tones_agree_with_the_icon_roll_up_for_persistent_hosts()
    {
        // The row and the tray icon must tell the same story: whatever turns the icon red or amber
        // must not look Neutral/Healthy in the window.
        foreach (var category in Enum.GetValues<StatusCategory>())
        {
            var row = HostRowFactory.Row(category, mode: MountMode.Persistent, state: MountState.Ready);
            var contribution = StatusDerivation.ContributionOf(row);
            var tone = HostRowPresentation.Create(row).Tone;

            if (contribution == AggregateHealth.Error)
            {
                Assert.True(tone is HostRowTone.Error or HostRowTone.Warning, $"{category}: {tone}");
            }
            else if (contribution == AggregateHealth.Degraded)
            {
                Assert.True(tone is HostRowTone.Warning or HostRowTone.Error, $"{category}: {tone}");
            }
        }
    }
}
