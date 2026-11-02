using Bosun.UI;

namespace Bosun.Tests.UI;

/// <summary>
/// bs-ww9.3 / ADR-018 rule 2: launch context decides window visibility. Covers the flag
/// (<c>--autostart</c>) E10's autostart registration must inherit -- see
/// <see cref="LaunchContextDetector"/>'s remarks.
/// </summary>
public sealed class LaunchContextDetectorTests
{
    [Fact]
    public void Detect_ReturnsManual_ForNoArguments()
    {
        Assert.Equal(LaunchContext.Manual, LaunchContextDetector.Detect([]));
    }

    [Fact]
    public void Detect_ReturnsManual_ForUnrelatedArguments()
    {
        Assert.Equal(LaunchContext.Manual, LaunchContextDetector.Detect(["--some-other-flag", "value"]));
    }

    [Fact]
    public void Detect_ReturnsAutostart_WhenTheAutostartFlagIsPresent()
    {
        Assert.Equal(LaunchContext.Autostart, LaunchContextDetector.Detect(["--autostart"]));
    }

    [Fact]
    public void Detect_ReturnsAutostart_RegardlessOfArgumentPosition()
    {
        Assert.Equal(LaunchContext.Autostart, LaunchContextDetector.Detect(["--some-flag", "--autostart", "--another"]));
    }

    [Fact]
    public void Detect_IsCaseInsensitive()
    {
        Assert.Equal(LaunchContext.Autostart, LaunchContextDetector.Detect(["--AutoStart"]));
    }

    // bs-e28: session.autostart opens Terminal tabs for the Run-key launch and nothing else.
    [Theory]
    [InlineData(LaunchContext.Manual, false)]
    [InlineData(LaunchContext.Autostart, true)]
    [InlineData(LaunchContext.WatchdogRestart, false)]
    [InlineData(LaunchContext.UserRestart, false)]
    public void OpensAutostartTerminalTabs_IsTrueOnlyForAutostart(LaunchContext context, bool expected)
    {
        Assert.Equal(expected, context.OpensAutostartTerminalTabs());
    }

    [Fact]
    public void EveryLaunchContextIsCoveredByTheTabsRule()
    {
        // A new LaunchContext must make a deliberate choice about tabs, not inherit one by accident.
        Assert.Equal(
            [LaunchContext.Autostart],
            Enum.GetValues<LaunchContext>().Where(c => c.OpensAutostartTerminalTabs()));
    }

    [Theory]
    [InlineData(false, new string[0])]
    [InlineData(true, new[] { "--autostart" })]
    [InlineData(false, new[] { "--restarted-by-watchdog", "123" })]
    [InlineData(false, new[] { "--restarted-by-user", "123" })]
    // A restarted Bosun inherits the original's arguments, so the restart flag must win over --autostart.
    [InlineData(false, new[] { "--autostart", "--restarted-by-watchdog", "123" })]
    [InlineData(false, new[] { "--autostart", "--restarted-by-user", "123" })]
    public void FromTheCommandLine_OnlyAPlainAutostartLaunchOpensTabs(bool expected, string[] args)
    {
        Assert.Equal(expected, LaunchContextDetector.Detect(args).OpensAutostartTerminalTabs());
    }

    [Fact]
    public void Detect_Throws_WhenArgsIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => LaunchContextDetector.Detect(null!));
    }
}
