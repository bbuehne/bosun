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

    /// <summary>
    /// bs-65r: presence is decided by <c>HKLM\SOFTWARE\WinFsp\InstallDir</c> (32-bit registry view)
    /// and by NOTHING else. Do not "improve" the detector by scanning Add/Remove Programs.
    /// </summary>
    /// <remarks>
    /// Real-world reason, found on the maintainer's own machine: Add/Remove Programs lists
    /// "WinFsp installed by Bitvise SSH Client (remove only)" -- an orphaned uninstall stub left by
    /// the Bitvise install -- while WinFsp is NOT actually present (no install directory, no
    /// <c>HKLM\SOFTWARE\WinFsp</c> or <c>WOW6432Node\WinFsp</c> key, no driver service). An
    /// uninstall-registry scan would report WinFsp present, and every mount would then fail with a
    /// confusing rclone error instead of Bosun's actionable "WinFsp is not installed" message.
    ///
    /// The detector's only seam is the <c>InstallDir</c> reader (<c>Func&lt;string?&gt;</c>), so a
    /// fake cannot present an uninstall entry for the detector to ignore. What this test pins is the
    /// contract at that seam: the reader returning no <c>InstallDir</c> means absent, with exactly
    /// one source consulted. If the detector ever gains a second input, its constructor must
    /// change and this test is the prompt to reconsider why that is safe.
    /// </remarks>
    [Fact]
    public void Detect_with_no_InstallDir_is_absent_regardless_of_any_orphaned_uninstall_entry()
    {
        var installDirReads = 0;
        var detector = new WinFspDetector(() =>
        {
            installDirReads++;
            return null; // InstallDir absent; the Bitvise "remove only" stub is invisible to the detector.
        });

        var result = detector.Detect();

        Assert.False(result.IsInstalled);
        Assert.Null(result.InstallDirectory);
        Assert.Equal(1, installDirReads);
    }

    [Fact]
    public void Detect_treats_an_empty_string_the_same_as_absent()
    {
        var detector = new WinFspDetector(() => string.Empty);

        var result = detector.Detect();

        Assert.False(result.IsInstalled);
    }
}
