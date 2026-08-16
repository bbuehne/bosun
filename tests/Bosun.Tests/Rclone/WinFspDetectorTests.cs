using Bosun.Rclone;

namespace Bosun.Tests.Rclone;

/// <summary>
/// Covers bs-5mt's WinFsp detection (ADR-002). Always injects a fake registry reader -- WinFsp is
/// NOT installed on the maintainer's machine as of this epic, so a test that read the real
/// registry would currently pass for the wrong reason (see <see cref="WinFspDetector"/>'s
/// remarks).
/// </summary>
public sealed class WinFspDetectorTests
{
    [Fact]
    public void Detect_reports_not_installed_with_an_actionable_message_when_the_registry_key_is_absent()
    {
        var detector = new WinFspDetector(() => null);

        var result = detector.Detect();

        Assert.False(result.IsInstalled);
        Assert.Null(result.InstallDirectory);
        Assert.Contains("WinFsp is not installed", result.Message);
        Assert.Contains("Drive mounting is disabled", result.Message);
        Assert.Contains("terminal profiles", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Detect_says_terminal_features_are_unaffected_so_the_degrade_is_visible_not_silent()
    {
        var detector = new WinFspDetector(() => null);

        var result = detector.Detect();

        Assert.Contains("unaffected", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Detect_reports_installed_with_the_install_directory_when_present()
    {
        var detector = new WinFspDetector(() => @"C:\Program Files (x86)\WinFsp");

        var result = detector.Detect();

        Assert.True(result.IsInstalled);
        Assert.Equal(@"C:\Program Files (x86)\WinFsp", result.InstallDirectory);
        Assert.Contains(@"C:\Program Files (x86)\WinFsp", result.Message);
    }

    [Fact]
    public void Detect_treats_an_empty_string_the_same_as_absent()
    {
        var detector = new WinFspDetector(() => string.Empty);

        var result = detector.Detect();

        Assert.False(result.IsInstalled);
    }
}
