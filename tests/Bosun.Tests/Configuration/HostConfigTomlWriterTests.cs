using Bosun.Configuration;

namespace Bosun.Tests.Configuration;

/// <summary>
/// Round-trip fidelity for <see cref="HostConfigTomlWriter"/> (bs-ww9.8, ADR-019): every test
/// here proves <c>ConfigParser.Parse(HostConfigTomlWriter.Write(config))</c> binds back to a
/// <see cref="BosunConfig"/> equivalent to the original, field by field. <see
/// cref="HostConfigStore.AdoptSelfWrite"/> depends on this holding for every config
/// <see cref="Bosun.Configuration.HostConfigWriter"/> can ever produce -- if it did not, the
/// config the store adopts in memory and the config a fresh load of the file it just wrote would
/// bind to could silently diverge.
/// </summary>
public sealed class HostConfigTomlWriterTests
{
    private static readonly Func<string, bool> AlwaysExists = _ => true;

    /// <summary>
    /// The backbone test the brief asks for: parse the committed, human-maintained
    /// <c>config/hosts.example.toml</c> -- three hosts covering all three mount modes
    /// (persistent, on-demand, none), an explicit non-default <c>[global]</c>, and a host
    /// (<c>example-jump</c>) whose <c>mount</c> table omits every optional field -- write it back
    /// out, re-parse the result, and assert the two bound configs agree on every field. This is
    /// the single test most likely to catch a serializer bug that a hand-crafted fixture would
    /// not: it exercises whatever the maintainer actually put in the shipped file, not just what
    /// this test file's author thought to cover.
    /// </summary>
    [Fact]
    public void WriteThenParse_OfTheCommittedExampleConfig_RoundTripsToAnEquivalentConfig()
    {
        var original = ConfigParser.Parse(ReadExampleConfig(), "hosts.example.toml");

        var text = HostConfigTomlWriter.Write(original);
        var roundTripped = ConfigParser.Parse(text, "hosts.example.toml (round-tripped)");

        AssertConfigsEquivalent(original, roundTripped);

        // The round-tripped text must also still be a config the loader would actually accept --
        // round-trip fidelity that silently produces something the validator rejects is exactly
        // the "loader rejects it on next start" hazard ADR-019 exists to prevent.
        var validation = ConfigValidator.Validate(roundTripped, AlwaysExists);
        Assert.Empty(validation.Errors);
    }

    /// <summary>
    /// A host built the way <c>NewHostDefaults.Create</c> makes one -- on-demand, every optional
    /// mount/session field populated -- round-trips. Covers the "everything present" shape as a
    /// contrast to the "mode = none, everything optional absent" shape the example file's
    /// <c>example-jump</c> host already covers via the backbone test above.
    /// </summary>
    [Fact]
    public void WriteThenParse_HostWithEveryOptionalFieldPopulated_RoundTrips()
    {
        var config = SingleHostConfig(new HostConfig
        {
            Key = "on-demand-host",
            DisplayName = "On Demand Host",
            Hostname = "on-demand.example.internal",
            Port = 2222,
            User = "someuser",
            IdentityFile = "~/.ssh/id_ed25519",
            Mount = new MountConfig
            {
                Mode = MountMode.OnDemand,
                Drive = "R:",
                RemotePath = "/srv/app",
                VfsCacheMode = "full",
                NetworkMode = true,
                IdleUnmountSeconds = 900,
            },
            Session = new SessionConfig
            {
                Autostart = true,
                Reconnect = false,
                Tmux = true,
                TmuxSession = "work",
                TabColor = "#112233",
                ColorScheme = "One Half Dark",
            },
            Probe = new ProbeConfig
            {
                IntervalSeconds = 45,
                DeepProbe = false,
            },
        });

        var roundTripped = ConfigParser.Parse(HostConfigTomlWriter.Write(config), "fixture");

        AssertConfigsEquivalent(config, roundTripped);
    }

    /// <summary>
    /// <c>mount.mode = "none"</c> with every optional mount field left null (never written by
    /// <see cref="ConfigParser"/> to begin with unless the TOML supplied one) round-trips as
    /// null, not as some placeholder value -- the same shape as <c>example-jump</c> in the
    /// committed example, asserted directly here rather than only implicitly via the backbone
    /// test.
    /// </summary>
    [Fact]
    public void WriteThenParse_ModeNoneHostWithNoOptionalMountFields_RoundTripsNullsAsAbsent()
    {
        var config = SingleHostConfig(new HostConfig
        {
            Key = "shell-only",
            DisplayName = "Shell Only",
            Hostname = "shell.example.internal",
            Port = 22,
            User = "someuser",
            IdentityFile = "~/.ssh/id_ed25519",
            Mount = new MountConfig
            {
                Mode = MountMode.None,
                Drive = null,
                RemotePath = null,
                VfsCacheMode = null,
                NetworkMode = null,
                IdleUnmountSeconds = null,
            },
            Session = new SessionConfig
            {
                Autostart = true,
                Reconnect = true,
                Tmux = false,
                TmuxSession = null,
                TabColor = "#3A4A6B",
                ColorScheme = "Campbell",
            },
            Probe = new ProbeConfig
            {
                IntervalSeconds = 0,
                DeepProbe = false,
            },
        });

        var roundTripped = ConfigParser.Parse(HostConfigTomlWriter.Write(config), "fixture");

        var host = roundTripped.Hosts["shell-only"];
        Assert.Null(host.Mount.Drive);
        Assert.Null(host.Mount.RemotePath);
        Assert.Null(host.Mount.NetworkMode);
        Assert.Null(host.Mount.IdleUnmountSeconds);
        Assert.Null(host.Session.TmuxSession);
    }

    /// <summary>
    /// Windows identity-file paths contain backslashes, and display names or drive-adjacent
    /// fields could in principle contain a quote or a control character (see
    /// <c>ConfigValidatorAdversarialTests</c>, which proves a TOML basic string can already carry
    /// an escaped literal newline through <see cref="ConfigParser"/>). A writer that assumed
    /// "these are just simple names" would emit a backslash or a raw control character straight
    /// into a single-line basic string -- invalid TOML that <see cref="ConfigParser"/> would
    /// refuse to re-parse at all.
    /// </summary>
    [Fact]
    public void WriteThenParse_StringsWithBackslashesQuotesAndControlCharacters_RoundTripExactly()
    {
        const string trickyDisplayName = "Weird \"Name\" \\ with \t a tab and \n a newline";
        const string windowsIdentityPath = @"C:\Users\barry\.ssh\id_ed25519";

        var config = SingleHostConfig(new HostConfig
        {
            Key = "tricky",
            DisplayName = trickyDisplayName,
            Hostname = "tricky.example.internal",
            Port = 22,
            User = "someuser",
            IdentityFile = windowsIdentityPath,
            Mount = new MountConfig { Mode = MountMode.None },
            Session = new SessionConfig
            {
                Autostart = false,
                Reconnect = true,
                Tmux = false,
                TabColor = "#000000",
                ColorScheme = "Campbell",
            },
            Probe = new ProbeConfig { IntervalSeconds = 0, DeepProbe = false },
        });

        var roundTripped = ConfigParser.Parse(HostConfigTomlWriter.Write(config), "fixture");

        var host = roundTripped.Hosts["tricky"];
        Assert.Equal(trickyDisplayName, host.DisplayName);
        Assert.Equal(windowsIdentityPath, host.IdentityFile);
    }

    /// <summary>
    /// Host keys are, in practice, ssh-config-alias-shaped bare identifiers (ADR-013), but
    /// nothing enforces that at the type level. A key containing characters that are not legal in
    /// a bare TOML key (a space, here) must still produce a table header Tomlyn can parse back
    /// into the same key.
    /// </summary>
    [Fact]
    public void WriteThenParse_HostKeyWithNonBareCharacters_QuotesTheKeyAndStillRoundTrips()
    {
        var config = SingleHostConfig(new HostConfig
        {
            Key = "weird key.with.dots",
            DisplayName = "Weird Key Host",
            Hostname = "weird.example.internal",
            Port = 22,
            User = "someuser",
            IdentityFile = "~/.ssh/id_ed25519",
            Mount = new MountConfig { Mode = MountMode.None },
            Session = new SessionConfig
            {
                Autostart = false,
                Reconnect = true,
                Tmux = false,
                TabColor = "#000000",
                ColorScheme = "Campbell",
            },
            Probe = new ProbeConfig { IntervalSeconds = 0, DeepProbe = false },
        });

        var text = HostConfigTomlWriter.Write(config);
        var roundTripped = ConfigParser.Parse(text, "fixture");

        Assert.True(roundTripped.Hosts.ContainsKey("weird key.with.dots"));
    }

    /// <summary>
    /// Two hosts, sorted or not, must both survive -- proves the serializer does not silently
    /// drop or merge entries when there is more than one.
    /// </summary>
    [Fact]
    public void WriteThenParse_MultipleHosts_AllSurvive()
    {
        var hostA = SingleHostConfig(new HostConfig
        {
            Key = "zzz-last",
            DisplayName = "Zzz Last",
            Hostname = "zzz.example.internal",
            Port = 22,
            User = "someuser",
            IdentityFile = "~/.ssh/id_ed25519",
            Mount = new MountConfig { Mode = MountMode.None },
            Session = new SessionConfig { Autostart = false, Reconnect = true, Tmux = false, TabColor = "#000000", ColorScheme = "Campbell" },
            Probe = new ProbeConfig { IntervalSeconds = 0, DeepProbe = false },
        }).Hosts["zzz-last"];

        var hostB = SingleHostConfig(new HostConfig
        {
            Key = "aaa-first",
            DisplayName = "Aaa First",
            Hostname = "aaa.example.internal",
            Port = 22,
            User = "someuser",
            IdentityFile = "~/.ssh/id_ed25519",
            Mount = new MountConfig { Mode = MountMode.None },
            Session = new SessionConfig { Autostart = false, Reconnect = true, Tmux = false, TabColor = "#000000", ColorScheme = "Campbell" },
            Probe = new ProbeConfig { IntervalSeconds = 0, DeepProbe = false },
        }).Hosts["aaa-first"];

        var config = new BosunConfig
        {
            Global = DefaultGlobal(),
            Hosts = new Dictionary<string, HostConfig> { ["zzz-last"] = hostA, ["aaa-first"] = hostB },
        };

        var roundTripped = ConfigParser.Parse(HostConfigTomlWriter.Write(config), "fixture");

        Assert.Equal(["aaa-first", "zzz-last"], roundTripped.Hosts.Keys.Order(StringComparer.Ordinal));
    }

    // -- helpers -----------------------------------------------------------------------------

    private static BosunConfig SingleHostConfig(HostConfig host) => new()
    {
        Global = DefaultGlobal(),
        Hosts = new Dictionary<string, HostConfig> { [host.Key] = host },
    };

    private static GlobalConfig DefaultGlobal() => new()
    {
        RcloneRcPort = 5599,
        RcloneConfigPath = @"%APPDATA%\rclone\rclone.conf",
        ProbeTimeoutSeconds = 7,
        FailuresBeforeUnmount = 4,
        BackoffSeconds = [3, 9, 27],
        MountedProbeIntervalSeconds = 45,
        MountedDeepProbeIntervalSeconds = 120,
        SuspendUnmountTimeoutSeconds = 6,
        StartWithWindows = false,
    };

    internal static void AssertConfigsEquivalent(BosunConfig expected, BosunConfig actual)
    {
        AssertGlobalEquivalent(expected.Global, actual.Global);

        Assert.Equal(
            expected.Hosts.Keys.OrderBy(k => k, StringComparer.Ordinal),
            actual.Hosts.Keys.OrderBy(k => k, StringComparer.Ordinal));

        foreach (var key in expected.Hosts.Keys)
        {
            AssertHostEquivalent(expected.Hosts[key], actual.Hosts[key]);
        }
    }

    private static void AssertGlobalEquivalent(GlobalConfig expected, GlobalConfig actual)
    {
        Assert.Equal(expected.RcloneRcPort, actual.RcloneRcPort);
        Assert.Equal(expected.RcloneConfigPath, actual.RcloneConfigPath);
        Assert.Equal(expected.ProbeTimeoutSeconds, actual.ProbeTimeoutSeconds);
        Assert.Equal(expected.FailuresBeforeUnmount, actual.FailuresBeforeUnmount);
        Assert.Equal(expected.BackoffSeconds, actual.BackoffSeconds);
        Assert.Equal(expected.MountedProbeIntervalSeconds, actual.MountedProbeIntervalSeconds);
        Assert.Equal(expected.MountedDeepProbeIntervalSeconds, actual.MountedDeepProbeIntervalSeconds);
        Assert.Equal(expected.SuspendUnmountTimeoutSeconds, actual.SuspendUnmountTimeoutSeconds);
        Assert.Equal(expected.StartWithWindows, actual.StartWithWindows);
    }

    private static void AssertHostEquivalent(HostConfig expected, HostConfig actual)
    {
        Assert.Equal(expected.Key, actual.Key);
        Assert.Equal(expected.DisplayName, actual.DisplayName);
        Assert.Equal(expected.Hostname, actual.Hostname);
        Assert.Equal(expected.Port, actual.Port);
        Assert.Equal(expected.User, actual.User);
        Assert.Equal(expected.IdentityFile, actual.IdentityFile);

        Assert.Equal(expected.Mount.Mode, actual.Mount.Mode);
        Assert.Equal(expected.Mount.Drive, actual.Mount.Drive);
        Assert.Equal(expected.Mount.RemotePath, actual.Mount.RemotePath);
        Assert.Equal(expected.Mount.VfsCacheMode, actual.Mount.VfsCacheMode);
        Assert.Equal(expected.Mount.NetworkMode, actual.Mount.NetworkMode);
        Assert.Equal(expected.Mount.IdleUnmountSeconds, actual.Mount.IdleUnmountSeconds);

        Assert.Equal(expected.Session.Autostart, actual.Session.Autostart);
        Assert.Equal(expected.Session.Reconnect, actual.Session.Reconnect);
        Assert.Equal(expected.Session.Tmux, actual.Session.Tmux);
        Assert.Equal(expected.Session.TmuxSession, actual.Session.TmuxSession);
        Assert.Equal(expected.Session.TabColor, actual.Session.TabColor);
        Assert.Equal(expected.Session.ColorScheme, actual.Session.ColorScheme);

        Assert.Equal(expected.Probe.IntervalSeconds, actual.Probe.IntervalSeconds);
        Assert.Equal(expected.Probe.DeepProbe, actual.Probe.DeepProbe);
    }

    private static string ReadExampleConfig()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Bosun.sln")))
        {
            dir = dir.Parent;
        }

        var repoRoot = dir?.FullName
            ?? throw new InvalidOperationException($"Could not find Bosun.sln above {AppContext.BaseDirectory}");

        return File.ReadAllText(Path.Combine(repoRoot, "config", "hosts.example.toml"));
    }
}
