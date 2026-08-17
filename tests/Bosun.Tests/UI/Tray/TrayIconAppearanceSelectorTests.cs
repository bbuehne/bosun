using Bosun.UI.Tray;
using Bosun.Status;

namespace Bosun.Tests.UI.Tray;

/// <summary>
/// bs-ww9.5's explicit testability requirement: "mapping aggregate health -&gt; which icon to
/// display" (ADR-012 Decision 3 -- a degraded Bosun must not look identical to a healthy one).
/// </summary>
public sealed class TrayIconAppearanceSelectorTests
{
    [Theory]
    [InlineData(AggregateHealth.Healthy)]
    [InlineData(AggregateHealth.Degraded)]
    [InlineData(AggregateHealth.Error)]
    public void Select_IsDefinedForEveryAggregateHealthValue(AggregateHealth health)
    {
        var appearance = TrayIconAppearanceSelector.Select(health);

        Assert.NotNull(appearance);
        Assert.False(string.IsNullOrWhiteSpace(appearance.ColorHex));
        Assert.NotNull(appearance.AccessibleName);
    }

    [Fact]
    public void Select_Throws_ForAnUnmappedHealthValue()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TrayIconAppearanceSelector.Select((AggregateHealth)999));
    }

    [Fact]
    public void Select_ProducesADistinctColor_ForEveryHealthValue()
    {
        var colors = Enum.GetValues<AggregateHealth>()
            .Select(TrayIconAppearanceSelector.Select)
            .Select(a => a.ColorHex)
            .ToList();

        Assert.Equal(colors.Count, colors.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Select_ProducesADistinctGlyph_ForEveryHealthValue()
    {
        var glyphs = Enum.GetValues<AggregateHealth>()
            .Select(TrayIconAppearanceSelector.Select)
            .Select(a => a.Glyph)
            .ToList();

        Assert.Equal(glyphs.Count, glyphs.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Select_ForHealthy_UsesNoGlyph()
    {
        // Healthy is represented by the absence of a warning mark, not an extra symbol.
        var appearance = TrayIconAppearanceSelector.Select(AggregateHealth.Healthy);

        Assert.Equal("", appearance.Glyph);
    }

    [Fact]
    public void Select_ForDegradedAndMountingUnavailable_UsesANonEmptyGlyph()
    {
        // Both fault states must be distinguishable by shape, not just color (accessibility).
        Assert.NotEqual("", TrayIconAppearanceSelector.Select(AggregateHealth.Degraded).Glyph);
        Assert.NotEqual("", TrayIconAppearanceSelector.Select(AggregateHealth.Error).Glyph);
    }
}
