using Bosun.Configuration;
using Bosun.Terminal;
using Bosun.Tests.Terminal.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bosun.Tests.Terminal;

/// <summary>bs-e28: which hosts get a tab, and that a launch failure never escapes.</summary>
public sealed class AutostartTerminalTabsTests
{
    [Fact]
    public void OnlyHostsWithAutostart_GetATab_ByProfileGuid_InKeyOrder()
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
            [
                "-w", "0", "new-tab", "-p", Guid("alpha"),
                ";", "new-tab", "-p", Guid("zeta"),
            ],
            call);
    }

    [Fact]
    public void AMountModeNoneHost_StillGetsItsTab()
    {
        // ADR-008: autostart is the terminal-session axis, independent of mounting.
        var launcher = new FakeTerminalTabLauncher();
        var config = Config(Host("jump", "Example Jump", autostart: true, mode: MountMode.None));

        new AutostartTerminalTabs(launcher, NullLogger.Instance).Open(config);

        Assert.Equal(["-w", "0", "new-tab", "-p", Guid("jump")], Assert.Single(launcher.Calls));
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
    public void TheDisplayName_NeverReachesTheLaunchedArguments_SoRenamingAHostCannotBreakItsTab()
    {
        // ADR-013 Decision 2: the key is the identity, display_name is only a label. A name that
        // would need escaping, or that is blank, is irrelevant to the tab.
        var launcher = new FakeTerminalTabLauncher();
        var config = Config(
            Host("k1", "Prod; Web", autostart: true),
            Host("k2", " ", autostart: true));

        var opened = new AutostartTerminalTabs(launcher, NullLogger.Instance).Open(config);

        Assert.Equal(2, opened);
        Assert.Equal(
            [
                "-w", "0", "new-tab", "-p", Guid("k1"),
                ";", "new-tab", "-p", Guid("k2"),
            ],
            Assert.Single(launcher.Calls));
    }

    [Fact]
    public void TheLaunchedArgument_IsTheGuidTheFragmentGeneratorEmitsForTheHost()
    {
        // Same derivation as the fragment: if the generator's GUID changes, this must follow it.
        var launcher = new FakeTerminalTabLauncher();
        var host = Host("nas", "Some Display Name", autostart: true);

        new AutostartTerminalTabs(launcher, NullLogger.Instance).Open(Config(host));

        Assert.Equal(
            FragmentProfileGenerator.CreateProfile(host).Guid.ToString("B"),
            Assert.Single(launcher.Calls)[^1]);
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

    private static string Guid(string hostKey) => TerminalProfileArgument.For(hostKey);

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
