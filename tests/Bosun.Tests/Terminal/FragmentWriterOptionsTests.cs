using Bosun.Terminal;

namespace Bosun.Tests.Terminal;

/// <summary>
/// Covers bs-k41's path-resolution acceptance: the verified real path (bs-3ir) is
/// <c>%LOCALAPPDATA%\Microsoft\Windows Terminal\Fragments\Bosun\bosun.json</c> -- note the
/// <c>Microsoft\</c> segment, which ADR-006 and CLAUDE.md Invariant I5 both abbreviate away in
/// prose but which is part of the real path.
/// </summary>
public sealed class FragmentWriterOptionsTests
{
    [Fact]
    public void CreateDefault_resolves_the_verified_LocalAppData_path_including_the_Microsoft_segment()
    {
        var options = FragmentWriterOptions.CreateDefault();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var expected = Path.Combine(localAppData, "Microsoft", "Windows Terminal", "Fragments", "Bosun", "bosun.json");

        Assert.Equal(expected, options.FragmentPath);
    }

    [Fact]
    public void CreateDefault_does_not_target_settingsJson()
    {
        var options = FragmentWriterOptions.CreateDefault();

        Assert.DoesNotContain("settings.json", options.FragmentPath, StringComparison.OrdinalIgnoreCase);
    }
}
