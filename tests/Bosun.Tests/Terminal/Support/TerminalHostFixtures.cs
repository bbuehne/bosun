using Bosun.Configuration;

namespace Bosun.Tests.Terminal.Support;

/// <summary>Small direct-construction builders for the <see cref="HostConfig"/> shapes E7's tests
/// need repeatedly, following the same style as <c>Supervisor.Support.HostFixtures</c> and
/// <c>ConfigValidatorTests.ValidHost</c> rather than a fluent builder.</summary>
internal static class TerminalHostFixtures
{
    public static HostConfig Host(
        string key,
        string? displayName = null,
        bool tmux = false,
        string? tmuxSession = null,
        bool reconnect = false,
        string tabColor = "#2D5F3F",
        string colorScheme = "Campbell",
        MountMode mountMode = MountMode.Persistent) => new()
    {
        Key = key,
        DisplayName = displayName ?? key,
        Hostname = $"{key}.example.internal",
        Port = 22,
        User = "someuser",
        IdentityFile = "~/.ssh/id_ed25519",
        Mount = mountMode == MountMode.None
            ? new MountConfig { Mode = MountMode.None }
            : new MountConfig
            {
                Mode = mountMode,
                Drive = "N:",
                RemotePath = "/srv/share",
                VfsCacheMode = "writes",
                NetworkMode = true,
                IdleUnmountSeconds = 0,
            },
        Session = new SessionConfig
        {
            Autostart = false,
            Reconnect = reconnect,
            Tmux = tmux,
            TmuxSession = tmuxSession,
            TabColor = tabColor,
            ColorScheme = colorScheme,
        },
        Probe = new ProbeConfig { IntervalSeconds = 60, DeepProbe = true },
    };

    public static GlobalConfig Global() => new();

    public static BosunConfig Build(params HostConfig[] hosts) => new()
    {
        Global = Global(),
        Hosts = hosts.ToDictionary(h => h.Key, StringComparer.Ordinal),
    };
}
