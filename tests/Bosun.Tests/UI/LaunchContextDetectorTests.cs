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

    [Fact]
    public void Detect_Throws_WhenArgsIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => LaunchContextDetector.Detect(null!));
    }
}
