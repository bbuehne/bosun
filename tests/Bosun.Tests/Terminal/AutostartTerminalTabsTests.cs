using Bosun.Configuration;
using Bosun.Terminal;
using Bosun.Tests.Terminal.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bosun.Tests.Terminal;

/// <summary>bs-e28: which hosts get a tab, and that a launch failure never escapes.</summary>
public sealed class AutostartTerminalTabsTests
{
    [Fact]
    public void OnlyHostsWithAutostart_GetATab_ByDisplayName_InKeyOrder()
    {
        var launcher = new FakeTerminalTabLauncher();
        var config = Config(
            Host("zeta", "Zeta Box", autostart: true),
            Host("alpha", "Alpha Box", autostart: true),
            Host("quiet", "Quiet Box", autostart: false));

        var opened = new AutostartTerminalTabs(launcher, NullLogger.Instance).Open(config);

        Assert.Equal(2, opened);
        var call = Assert.Single(launcher.Calls);
        Assert.Equal(
            ["-w", "0", "new-tab", "-p", "Alpha Box", ";", "new-tab", "-p", "Zeta Box"], call);
    }

    [Fact]
    public void AMountModeNoneHost_StillGetsItsTab()
    {
        // ADR-008: autostart is the terminal-session axis, independent of mounting.
        var launcher = new FakeTerminalTabLauncher();
        var config = Config(Host("jump", "Example Jump", autostart: true, mode: MountMode.None));

        new AutostartTerminalTabs(launcher, NullLogger.Instance).Open(config);

        Assert.Equal(["-w", "0", "new-tab", "-p", "Example Jump"], Assert.Single(launcher.Calls));
    }

    [Fact]
    public void NoHostWithAutostart_LaunchesNothing()
    {
        var launcher = new FakeTerminalTabLauncher();
        var config = Config(Host("a", "A", autostart: false), Host("b", "B", autostart: false));

        var opened = new AutostartTerminalTabs(launcher, NullLogger.Instance).Open(config);

        Assert.Equal(0, opened);
        Assert.Empty(launcher.Calls);
    }

    [Fact]
    public void NoHosts_LaunchesNothing()
    {
        var launcher = new FakeTerminalTabLauncher();

        new AutostartTerminalTabs(launcher, NullLogger.Instance).Open(Config());

        Assert.Empty(launcher.Calls);
    }

    [Fact]
    public void ADisplayNameWithASemicolon_IsEscapedInTheLaunchedArguments()
    {
        var launcher = new FakeTerminalTabLauncher();
        var config = Config(Host("k", "Prod; Web", autostart: true));

        new AutostartTerminalTabs(launcher, NullLogger.Instance).Open(config);

        Assert.Equal(@"Prod\; Web", Assert.Single(launcher.Calls)[^1]);
    }

    [Fact]
    public void ALauncherThatThrows_IsLoggedNotPropagated()
    {
        var launcher = new FakeTerminalTabLauncher { ThrowOnOpen = new InvalidOperationException("wt.exe missing") };
        var config = Config(Host("a", "A", autostart: true));

        var opened = new AutostartTerminalTabs(launcher, NullLogger.Instance).Open(config);

        Assert.Equal(0, opened);
        Assert.Single(launcher.Calls);
    }

    [Fact]
    public void ABlankDisplayName_IsLoggedNotPropagated_AndLaunchesNothing()
    {
        var launcher = new FakeTerminalTabLauncher();
        var config = Config(Host("a", " ", autostart: true));

        var opened = new AutostartTerminalTabs(launcher, NullLogger.Instance).Open(config);

        Assert.Equal(0, opened);
        Assert.Empty(launcher.Calls);
    }

    private static BosunConfig Config(params HostConfig[] hosts) => new()
    {
        Global = new GlobalConfig(),
        Hosts = hosts.ToDictionary(h => h.Key, StringComparer.Ordinal),
    };

    private static HostConfig Host(string key, string displayName, bool autostart, MountMode mode = MountMode.OnDemand) => new()
    {
        Key = key,
        DisplayName = displayName,
        Hostname = $"{key}.example.internal",
        Port = 22,
        User = "someuser",
        IdentityFile = "/fake/identity",
        Mount = new MountConfig { Mode = mode },
        Session = new SessionConfig { Autostart = autostart },
        Probe = new ProbeConfig { IntervalSeconds = 0, DeepProbe = false },
    };
}
