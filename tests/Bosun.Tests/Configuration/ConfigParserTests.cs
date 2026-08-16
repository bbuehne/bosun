using Bosun.Configuration;

namespace Bosun.Tests.Configuration;

/// <summary>
/// Covers bs-0na: parse-and-bind only, no validation. Uses only fixture TOML text defined
/// in-line or config/hosts.example.toml (fictional per docs/DECISIONS.md ADR-010 / Invariant
/// I9) — never the maintainer's real, gitignored config/hosts.toml.
/// </summary>
public sealed class ConfigParserTests
{
    [Fact]
    public void Parse_ExampleConfig_BindsGlobalFieldByField()
    {
        var config = ConfigParser.Parse(ReadExampleConfig(), "hosts.example.toml");

        Assert.Equal(5572, config.Global.RcloneRcPort);
        Assert.Equal(@"%APPDATA%\rclone\rclone.conf", config.Global.RcloneConfigPath);
        Assert.Equal(5, config.Global.ProbeTimeoutSeconds);
        Assert.Equal(3, config.Global.FailuresBeforeUnmount);
        Assert.Equal([5, 15, 30, 60, 300], config.Global.BackoffSeconds);
        Assert.Equal(60, config.Global.MountedProbeIntervalSeconds);
        Assert.Equal(8, config.Global.SuspendUnmountTimeoutSeconds);
        Assert.True(config.Global.StartWithWindows);
    }

    [Fact]
    public void Parse_ExampleConfig_BindsExampleNasFieldByField()
    {
        var config = ConfigParser.Parse(ReadExampleConfig(), "hosts.example.toml");

        Assert.True(config.Hosts.TryGetValue("example-nas", out var host));
        Assert.Equal("example-nas", host!.Key);
        Assert.Equal("Example NAS", host.DisplayName);
        Assert.Equal("nas.example.internal", host.Hostname);
        Assert.Equal(22, host.Port);
        Assert.Equal("youruser", host.User);
        Assert.Equal("~/.ssh/id_ed25519", host.IdentityFile);

        Assert.Equal(MountMode.Persistent, host.Mount.Mode);
        Assert.Equal("N:", host.Mount.Drive);
        Assert.Equal("/volume1/share", host.Mount.RemotePath);
        Assert.Equal("writes", host.Mount.VfsCacheMode);
        Assert.True(host.Mount.NetworkMode);
        Assert.Equal(0, host.Mount.IdleUnmountSeconds);

        Assert.False(host.Session.Autostart);
        Assert.True(host.Session.Reconnect);
        Assert.True(host.Session.Tmux);
        Assert.Equal("main", host.Session.TmuxSession);
        Assert.Equal("#2D5F3F", host.Session.TabColor);
        Assert.Equal("Campbell", host.Session.ColorScheme);

        Assert.Equal(60, host.Probe.IntervalSeconds);
        Assert.True(host.Probe.DeepProbe);
    }

    [Fact]
    public void Parse_ExampleConfig_BindsExampleRemoteFieldByField()
    {
        var config = ConfigParser.Parse(ReadExampleConfig(), "hosts.example.toml");

        Assert.True(config.Hosts.TryGetValue("example-remote", out var host));
        Assert.Equal("example-remote", host!.Key);
        Assert.Equal("Example Remote", host.DisplayName);
        Assert.Equal("remote.example.com", host.Hostname);
        Assert.Equal(22, host.Port);
        Assert.Equal("youruser", host.User);
        Assert.Equal("~/.ssh/id_ed25519", host.IdentityFile);

        Assert.Equal(MountMode.OnDemand, host.Mount.Mode);
        Assert.Equal("R:", host.Mount.Drive);
        Assert.Equal("/srv/app", host.Mount.RemotePath);
        Assert.Equal("writes", host.Mount.VfsCacheMode);
        Assert.True(host.Mount.NetworkMode);
        Assert.Equal(900, host.Mount.IdleUnmountSeconds);

        Assert.False(host.Session.Autostart);
        Assert.True(host.Session.Reconnect);
        Assert.True(host.Session.Tmux);
        Assert.Equal("main", host.Session.TmuxSession);
        Assert.Equal("#7A1F1F", host.Session.TabColor);
        Assert.Equal("Campbell", host.Session.ColorScheme);

        // ADR-008 default for on-demand: not polled while idle. Still parses and binds cleanly
        // — rejecting it is explicitly out of scope (ADR-011); it must NOT be treated as absent.
        Assert.Equal(0, host.Probe.IntervalSeconds);
        Assert.True(host.Probe.DeepProbe);
    }

    [Fact]
    public void Parse_ExampleConfig_BindsExampleJumpFieldByField_WithMountModeNone()
    {
        var config = ConfigParser.Parse(ReadExampleConfig(), "hosts.example.toml");

        Assert.True(config.Hosts.TryGetValue("example-jump", out var host));
        Assert.Equal("example-jump", host!.Key);
        Assert.Equal("Example Jump", host.DisplayName);
        Assert.Equal("jump.example.com", host.Hostname);
        Assert.Equal(22, host.Port);
        Assert.Equal("youruser", host.User);
        Assert.Equal("~/.ssh/id_ed25519", host.IdentityFile);

        // mode = "none" -- every other mount field is absent from the TOML entirely and must
        // bind to null rather than throw. Requiring them is ConfigValidator's job (bs-c0g), not
        // the binder's. vfs_cache_mode is the one exception: bs-lrd / bs-mb2 default it to
        // "writes" at bind time unconditionally of mode, so nothing downstream ever sees a null
        // for it -- see the remarks on MountConfig.VfsCacheMode.
        Assert.Equal(MountMode.None, host.Mount.Mode);
        Assert.Null(host.Mount.Drive);
        Assert.Null(host.Mount.RemotePath);
        Assert.Equal(MountConfig.DefaultVfsCacheMode, host.Mount.VfsCacheMode);
        Assert.Null(host.Mount.NetworkMode);
        Assert.Null(host.Mount.IdleUnmountSeconds);

        Assert.True(host.Session.Autostart);
        Assert.True(host.Session.Reconnect);
        Assert.False(host.Session.Tmux);
        Assert.Null(host.Session.TmuxSession); // tmux = false; tmux_session absent in the fixture
        Assert.Equal("#3A4A6B", host.Session.TabColor);
        Assert.Equal("Campbell", host.Session.ColorScheme);

        Assert.Equal(0, host.Probe.IntervalSeconds);
        Assert.False(host.Probe.DeepProbe);
    }

    [Fact]
    public void Parse_ExampleConfig_HasExactlyTheThreeDocumentedHosts()
    {
        var config = ConfigParser.Parse(ReadExampleConfig(), "hosts.example.toml");

        Assert.Equal(["example-jump", "example-nas", "example-remote"], config.Hosts.Keys.Order());
    }

    [Fact]
    public void Parse_HostKeyIsStableIdentity_DistinctFromDisplayName()
    {
        const string toml = """
            [hosts.prod-web]
            display_name  = "Something Entirely Different"
            hostname      = "prod-web.example.internal"
            port          = 22
            user          = "barry"
            identity_file = "~/.ssh/id_ed25519"

              [hosts.prod-web.mount]
              mode = "none"

              [hosts.prod-web.session]
              autostart = false
              reconnect = true
              tmux      = false
              tab_color    = "#7A1F1F"
              color_scheme = "Campbell"

              [hosts.prod-web.probe]
              interval_seconds = 0
              deep_probe       = false
            """;

        var config = ConfigParser.Parse(toml);

        var host = Assert.Single(config.Hosts).Value;
        Assert.Equal("prod-web", host.Key);
        Assert.Equal("Something Entirely Different", host.DisplayName);
        Assert.NotEqual(host.Key, host.DisplayName);
    }

    [Fact]
    public void Parse_GlobalSectionEntirelyAbsent_AppliesEveryDocumentedDefault()
    {
        const string toml = """
            [hosts.jump]
            display_name  = "Jump"
            hostname      = "jump.example.com"
            port          = 22
            user          = "barry"
            identity_file = "~/.ssh/id_ed25519"

              [hosts.jump.mount]
              mode = "none"

              [hosts.jump.session]
              autostart = true
              reconnect = true
              tmux      = false
              tab_color    = "#3A4A6B"
              color_scheme = "Campbell"

              [hosts.jump.probe]
              interval_seconds = 0
              deep_probe       = false
            """;

        var config = ConfigParser.Parse(toml);

        Assert.Equal(GlobalConfig.DefaultRcloneRcPort, config.Global.RcloneRcPort);
        Assert.Equal(GlobalConfig.DefaultRcloneConfigPath, config.Global.RcloneConfigPath);
        Assert.Equal(GlobalConfig.DefaultProbeTimeoutSeconds, config.Global.ProbeTimeoutSeconds);
        Assert.Equal(GlobalConfig.DefaultFailuresBeforeUnmount, config.Global.FailuresBeforeUnmount);
        Assert.Equal(GlobalConfig.DefaultBackoffSeconds, config.Global.BackoffSeconds);
        Assert.Equal(GlobalConfig.DefaultMountedProbeIntervalSeconds, config.Global.MountedProbeIntervalSeconds);
        Assert.Equal(GlobalConfig.DefaultSuspendUnmountTimeoutSeconds, config.Global.SuspendUnmountTimeoutSeconds);
        Assert.Equal(GlobalConfig.DefaultStartWithWindows, config.Global.StartWithWindows);
    }

    [Fact]
    public void Parse_MountedProbeIntervalSecondsAbsentFromPresentGlobalTable_DefaultsTo60()
    {
        // ADR-011: absent is not an error, even when [global] itself is present with other keys
        // set -- it is specifically THIS key's absence that must default, not "no [global] at
        // all" as a special case.
        const string toml = """
            [global]
            rclone_rc_port = 6000

            [hosts.jump]
            display_name  = "Jump"
            hostname      = "jump.example.com"
            port          = 22
            user          = "barry"
            identity_file = "~/.ssh/id_ed25519"

              [hosts.jump.mount]
              mode = "none"

              [hosts.jump.session]
              autostart = true
              reconnect = true
              tmux      = false
              tab_color    = "#3A4A6B"
              color_scheme = "Campbell"

              [hosts.jump.probe]
              interval_seconds = 0
              deep_probe       = false
            """;

        var config = ConfigParser.Parse(toml);

        Assert.Equal(6000, config.Global.RcloneRcPort);
        Assert.Equal(60, config.Global.MountedProbeIntervalSeconds);
    }

    [Fact]
    public void Parse_ExplicitEmptyBackoffList_IsPreservedRatherThanDefaulted()
    {
        // ConfigValidator (bs-c0g) is the one that rejects an empty ladder. The parser must not
        // silently paper over an explicit `backoff_seconds = []` by treating it as "absent".
        const string toml = """
            [global]
            backoff_seconds = []

            [hosts.jump]
            display_name  = "Jump"
            hostname      = "jump.example.com"
            port          = 22
            user          = "barry"
            identity_file = "~/.ssh/id_ed25519"

              [hosts.jump.mount]
              mode = "none"

              [hosts.jump.session]
              autostart = true
              reconnect = true
              tmux      = false
              tab_color    = "#3A4A6B"
              color_scheme = "Campbell"

              [hosts.jump.probe]
              interval_seconds = 0
              deep_probe       = false
            """;

        var config = ConfigParser.Parse(toml);

        Assert.Empty(config.Global.BackoffSeconds);
    }

    [Fact]
    public void Parse_MalformedToml_ThrowsWithLineAndColumn()
    {
        const string toml = """
            [global
            rclone_rc_port = 5572
            """;

        var ex = Assert.Throws<ConfigParseException>(() => ConfigParser.Parse(toml, "hosts.toml"));

        Assert.NotNull(ex.Line);
        Assert.NotNull(ex.Column);
        Assert.NotEmpty(ex.Diagnostics);
    }

    [Fact]
    public void Parse_MissingRequiredHostField_ThrowsNamingTheField()
    {
        const string toml = """
            [hosts.jump]
            display_name  = "Jump"
            port          = 22
            user          = "barry"
            identity_file = "~/.ssh/id_ed25519"

              [hosts.jump.mount]
              mode = "none"

              [hosts.jump.session]
              autostart = true
              reconnect = true
              tmux      = false
              tab_color    = "#3A4A6B"
              color_scheme = "Campbell"

              [hosts.jump.probe]
              interval_seconds = 0
              deep_probe       = false
            """;

        var ex = Assert.Throws<ConfigParseException>(() => ConfigParser.Parse(toml));

        Assert.Contains("hosts.jump.hostname", ex.Message);
    }

    [Fact]
    public void Parse_UnrecognizedMountMode_Throws()
    {
        const string toml = """
            [hosts.jump]
            display_name  = "Jump"
            hostname      = "jump.example.com"
            port          = 22
            user          = "barry"
            identity_file = "~/.ssh/id_ed25519"

              [hosts.jump.mount]
              mode = "always"

              [hosts.jump.session]
              autostart = true
              reconnect = true
              tmux      = false
              tab_color    = "#3A4A6B"
              color_scheme = "Campbell"

              [hosts.jump.probe]
              interval_seconds = 0
              deep_probe       = false
            """;

        var ex = Assert.Throws<ConfigParseException>(() => ConfigParser.Parse(toml));

        Assert.Contains("hosts.jump.mount.mode", ex.Message);
        Assert.Contains("always", ex.Message);
    }

    private static string ReadExampleConfig()
    {
        var repoRoot = FindRepoRoot(AppContext.BaseDirectory);
        var path = Path.Combine(repoRoot, "config", "hosts.example.toml");
        return File.ReadAllText(path);
    }

    private static string FindRepoRoot(string startDirectory)
    {
        var dir = new DirectoryInfo(startDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Bosun.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException($"Could not find Bosun.sln above {startDirectory}");
    }
}
