using Bosun.Terminal;
using Bosun.UI.Tray;

namespace Bosun.Tests.UI.Tray;

/// <summary>bs-4ic: pins the arguments the tray's "Open terminal" hands to <c>wt.exe</c>. Only the
/// pure argument builder is exercised; nothing here starts a process.</summary>
public sealed class Win32ExternalLauncherArgumentTests
{
    [Fact]
    public void OpenTerminal_selects_the_profile_by_braced_guid_of_the_host_key()
    {
        var args = Win32ExternalLauncher.BuildOpenTerminalArguments("example-nas");

        Assert.Equal(
            ["-w", "0", "new-tab", "-p", "{65762cab-0682-50e7-b722-2a7bbfadc2a1}"],
            args);
    }

    [Fact]
    public void OpenTerminal_never_passes_the_raw_host_key()
    {
        var args = Win32ExternalLauncher.BuildOpenTerminalArguments("example-nas");

        Assert.DoesNotContain("example-nas", args);
        Assert.Equal(TerminalProfileArgument.For("example-nas"), args[^1]);
    }
}
