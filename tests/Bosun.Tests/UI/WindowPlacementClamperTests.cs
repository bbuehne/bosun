using Bosun.UI;

namespace Bosun.Tests.UI;

/// <summary>
/// bs-ww9.3 / ADR-018 rule 1: "restoring a window to coordinates on a monitor that is no longer
/// attached puts it off-screen where the user cannot reach it" -- this is the guard against that.
/// </summary>
public sealed class WindowPlacementClamperTests
{
    private static readonly ScreenBounds SingleMonitor = new(Left: 0, Top: 0, Width: 1920, Height: 1080);

    [Fact]
    public void CreateDefault_ProducesAPlacement_CenteredOnTheScreen_AndNotMaximized()
    {
        var placement = WindowPlacementClamper.CreateDefault(SingleMonitor);

        Assert.False(placement.IsMaximized);
        Assert.Equal(WindowPlacementClamper.DefaultWidth, placement.Width);
        Assert.Equal(WindowPlacementClamper.DefaultHeight, placement.Height);
        Assert.Equal((SingleMonitor.Width - placement.Width) / 2, placement.Left);
        Assert.Equal((SingleMonitor.Height - placement.Height) / 2, placement.Top);
    }

    [Fact]
    public void CreateDefault_NeverProducesAWindowSmallerThanTheMinimum_EvenOnATinyScreen()
    {
        var tinyScreen = new ScreenBounds(0, 0, 200, 150);

        var placement = WindowPlacementClamper.CreateDefault(tinyScreen);

        Assert.True(placement.Width >= WindowPlacementClamper.MinWidth);
        Assert.True(placement.Height >= WindowPlacementClamper.MinHeight);
    }

    [Fact]
    public void Clamp_LeavesAPlacement_FullyWithinTheScreen_Unchanged()
    {
        var placement = new WindowPlacement { Left = 100, Top = 100, Width = 800, Height = 600, IsMaximized = false };

        var clamped = WindowPlacementClamper.Clamp(placement, SingleMonitor);

        Assert.Equal(placement, clamped);
    }

    [Fact]
    public void Clamp_PullsAWindow_ThatWasOnAMonitorNoLongerAttached_BackOntoTheRemainingScreen()
    {
        // The monitor this window last lived on (a second monitor to the right, at x=1920..3840)
        // is unplugged; only the primary 0..1920 monitor remains.
        var placement = new WindowPlacement { Left = 2200, Top = 300, Width = 800, Height = 600, IsMaximized = false };

        var clamped = WindowPlacementClamper.Clamp(placement, SingleMonitor);

        Assert.True(clamped.Left + clamped.Width <= SingleMonitor.Right);
        Assert.True(clamped.Left >= SingleMonitor.Left);
        Assert.True(clamped.Top + clamped.Height <= SingleMonitor.Bottom);
        Assert.True(clamped.Top >= SingleMonitor.Top);
    }

    [Fact]
    public void Clamp_PullsANegativePosition_BackOntoTheScreen()
    {
        var placement = new WindowPlacement { Left = -5000, Top = -5000, Width = 800, Height = 600, IsMaximized = false };

        var clamped = WindowPlacementClamper.Clamp(placement, SingleMonitor);

        Assert.Equal(SingleMonitor.Left, clamped.Left);
        Assert.Equal(SingleMonitor.Top, clamped.Top);
    }

    [Fact]
    public void Clamp_ShrinksAWindow_LargerThanTheCurrentScreen()
    {
        var placement = new WindowPlacement { Left = 0, Top = 0, Width = 4000, Height = 3000, IsMaximized = false };

        var clamped = WindowPlacementClamper.Clamp(placement, SingleMonitor);

        Assert.True(clamped.Width <= SingleMonitor.Width);
        Assert.True(clamped.Height <= SingleMonitor.Height);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-100)]
    [InlineData(0)]
    public void Clamp_FallsBackToTheDefaultSize_ForACorruptWidthOrHeight(double corruptValue)
    {
        var placement = new WindowPlacement { Left = 0, Top = 0, Width = corruptValue, Height = corruptValue, IsMaximized = false };

        var clamped = WindowPlacementClamper.Clamp(placement, SingleMonitor);

        Assert.Equal(WindowPlacementClamper.DefaultWidth, clamped.Width);
        Assert.Equal(WindowPlacementClamper.DefaultHeight, clamped.Height);
    }

    [Fact]
    public void Clamp_FallsBackToTheScreenOrigin_ForANonFiniteLeftOrTop()
    {
        var placement = new WindowPlacement { Left = double.NaN, Top = double.NaN, Width = 800, Height = 600, IsMaximized = false };

        var clamped = WindowPlacementClamper.Clamp(placement, SingleMonitor);

        Assert.Equal(SingleMonitor.Left, clamped.Left);
        Assert.Equal(SingleMonitor.Top, clamped.Top);
    }

    [Fact]
    public void Clamp_PreservesIsMaximized()
    {
        var placement = new WindowPlacement { Left = -5000, Top = -5000, Width = 800, Height = 600, IsMaximized = true };

        var clamped = WindowPlacementClamper.Clamp(placement, SingleMonitor);

        Assert.True(clamped.IsMaximized);
    }

    [Fact]
    public void Clamp_Throws_WhenPlacementIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => WindowPlacementClamper.Clamp(null!, SingleMonitor));
    }

    [Fact]
    public void Clamp_DoesNotThrow_WhenTheScreenIsSmallerThanTheMinimumWindowSize()
    {
        var degenerateScreen = new ScreenBounds(0, 0, 50, 50);
        var placement = new WindowPlacement { Left = 0, Top = 0, Width = 800, Height = 600, IsMaximized = false };

        var clamped = WindowPlacementClamper.Clamp(placement, degenerateScreen);

        Assert.True(clamped.Width >= WindowPlacementClamper.MinWidth);
        Assert.True(clamped.Height >= WindowPlacementClamper.MinHeight);
    }
}
