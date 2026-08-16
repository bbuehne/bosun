using Bosun.Configuration;

namespace Bosun.Tests.Configuration;

/// <summary>
/// Covers bs-c0g: one test per docs/CONFIG-SCHEMA.md validation rule, each failing for exactly
/// one describable reason, plus the "reports both, not just the first" and "valid config
/// produces no errors" acceptance criteria. The identity-file existence check is always
/// injected here — never real I/O against the developer's <c>~/.ssh</c>.
/// </summary>
public sealed class ConfigValidatorTests
{
    private static readonly Func<string, bool> AlwaysExists = _ => true;
    private static readonly Func<string, bool> NeverExists = _ => false;

    [Fact]
    public void Validate_ValidConfig_ProducesNoErrors()
    {
        var config = Build(host => host);

        var result = ConfigValidator.Validate(config, AlwaysExists);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_DuplicateDisplayName_IsRejected()
    {
        var config = new BosunConfig
        {
            Global = new GlobalConfig(),
            Hosts = new Dictionary<string, HostConfig>
            {
                ["host-a"] = ValidHost("host-a") with { DisplayName = "Same Name", Mount = ValidMount() with { Drive = "N:" } },
                ["host-b"] = ValidHost("host-b") with { DisplayName = "Same Name", Mount = ValidMount() with { Drive = "O:" } },
            },
        };

        var result = ConfigValidator.Validate(config, AlwaysExists);

        var error = Assert.Single(result.Errors);
        Assert.Equal("duplicate-display-name", error.Rule);
        Assert.Contains("Same Name", error.Message);
    }

    [Fact]
    public void Validate_TwoHostsClaimingSameDrive_IsRejected()
    {
        var config = new BosunConfig
        {
            Global = new GlobalConfig(),
            Hosts = new Dictionary<string, HostConfig>
            {
                ["host-a"] = ValidHost("host-a") with { Mount = ValidMount() with { Drive = "N:" } },
                ["host-b"] = ValidHost("host-b") with { Mount = ValidMount() with { Drive = "N:" } },
            },
        };

        var result = ConfigValidator.Validate(config, AlwaysExists);

        var error = Assert.Single(result.Errors);
        Assert.Equal("drive-collision", error.Rule);
        Assert.Contains("N:", error.Message);
    }

    [Fact]
    public void Validate_ModeNotNoneWithoutDrive_IsRejected()
    {
        var config = Build(h => h with { Mount = h.Mount with { Drive = null } });

        var result = ConfigValidator.Validate(config, AlwaysExists);

        var error = Assert.Single(result.Errors);
        Assert.Equal("mount-missing-drive-or-remote-path", error.Rule);
    }

    [Fact]
    public void Validate_ModeNotNoneWithoutRemotePath_IsRejected()
    {
        var config = Build(h => h with { Mount = h.Mount with { RemotePath = null } });

        var result = ConfigValidator.Validate(config, AlwaysExists);

        var error = Assert.Single(result.Errors);
        Assert.Equal("mount-missing-drive-or-remote-path", error.Rule);
    }

    [Theory]
    [InlineData("C:")]
    [InlineData("Z1:")]
    [InlineData("N")]
    [InlineData("a:")]
    public void Validate_DriveNotMatchingPattern_IsRejected(string badDrive)
    {
        var config = Build(h => h with { Mount = h.Mount with { Drive = badDrive } });

        var result = ConfigValidator.Validate(config, AlwaysExists);

        var error = Assert.Single(result.Errors);
        Assert.Equal("invalid-drive-letter", error.Rule);
    }

    [Theory]
    [InlineData("D:")]
    [InlineData("N:")]
    [InlineData("Z:")]
    public void Validate_DriveMatchingPattern_IsAccepted(string goodDrive)
    {
        var config = Build(h => h with { Mount = h.Mount with { Drive = goodDrive } });

        var result = ConfigValidator.Validate(config, AlwaysExists);

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("minimal")]
    public void Validate_RejectedVfsCacheMode_IsRejected(string mode)
    {
        var config = Build(h => h with { Mount = h.Mount with { VfsCacheMode = mode } });

        var result = ConfigValidator.Validate(config, AlwaysExists);

        var error = Assert.Single(result.Errors);
        Assert.Equal("invalid-vfs-cache-mode", error.Rule);
    }

    [Theory]
    [InlineData("writes")]
    [InlineData("full")]
    public void Validate_AcceptedVfsCacheMode_IsAccepted(string mode)
    {
        var config = Build(h => h with { Mount = h.Mount with { VfsCacheMode = mode } });

        var result = ConfigValidator.Validate(config, AlwaysExists);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_IdentityFileDoesNotExist_IsRejected()
    {
        var config = Build(h => h);

        var result = ConfigValidator.Validate(config, NeverExists);

        var error = Assert.Single(result.Errors);
        Assert.Equal("identity-file-not-found", error.Rule);
    }

    [Fact]
    public void Validate_IdentityFileExistenceCheck_ReceivesHomeExpandedPath()
    {
        string? received = null;
        var config = Build(h => h with { IdentityFile = "~/.ssh/id_ed25519" });

        ConfigValidator.Validate(config, path =>
        {
            received = path;
            return true;
        });

        Assert.NotNull(received);
        Assert.DoesNotContain('~', received);
        var expectedHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.StartsWith(expectedHome, received, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_BackoffSecondsEmpty_IsRejected()
    {
        var config = Build(h => h, global => global with { BackoffSeconds = [] });

        var result = ConfigValidator.Validate(config, AlwaysExists);

        var error = Assert.Single(result.Errors);
        Assert.Equal("invalid-backoff-seconds", error.Rule);
    }

    [Fact]
    public void Validate_BackoffSecondsContainingNonPositiveValue_IsRejected()
    {
        var config = Build(h => h, global => global with { BackoffSeconds = [5, 15, 0, 60] });

        var result = ConfigValidator.Validate(config, AlwaysExists);

        var error = Assert.Single(result.Errors);
        Assert.Equal("invalid-backoff-seconds", error.Rule);
    }

    [Fact]
    public void Validate_ProbeIntervalSecondsNegative_IsRejected()
    {
        var config = Build(h => h with { Probe = h.Probe with { IntervalSeconds = -1 } });

        var result = ConfigValidator.Validate(config, AlwaysExists);

        var error = Assert.Single(result.Errors);
        Assert.Equal("negative-probe-interval", error.Rule);
    }

    // -- bs-ze7: session.tmux = true requires session.tmux_session ------------------------------

    [Fact]
    public void Validate_TmuxTrueWithNullSession_IsRejected()
    {
        var config = Build(h => h with { Session = h.Session with { Tmux = true, TmuxSession = null } });

        var result = ConfigValidator.Validate(config, AlwaysExists);

        var error = Assert.Single(result.Errors);
        Assert.Equal("tmux-requires-session", error.Rule);
    }

    [Fact]
    public void Validate_TmuxTrueWithEmptySession_IsRejected()
    {
        var config = Build(h => h with { Session = h.Session with { Tmux = true, TmuxSession = "" } });

        var result = ConfigValidator.Validate(config, AlwaysExists);

        var error = Assert.Single(result.Errors);
        Assert.Equal("tmux-requires-session", error.Rule);
    }

    [Fact]
    public void Validate_TmuxTrueWithSessionName_IsAccepted()
    {
        var config = Build(h => h with { Session = h.Session with { Tmux = true, TmuxSession = "main" } });

        var result = ConfigValidator.Validate(config, AlwaysExists);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_TmuxFalseWithNoSession_IsAccepted()
    {
        // tmux_session is only meaningful when tmux = true -- absent is fine when tmux is off.
        var config = Build(h => h with { Session = h.Session with { Tmux = false, TmuxSession = null } });

        var result = ConfigValidator.Validate(config, AlwaysExists);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_ProbeIntervalSecondsZero_IsAccepted()
    {
        // ADR-008: 0 is the documented on-demand default and must parse and be accepted.
        var config = Build(h => h with { Probe = h.Probe with { IntervalSeconds = 0 } });

        var result = ConfigValidator.Validate(config, AlwaysExists);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_MountedProbeIntervalSecondsZero_IsRejected()
    {
        var config = Build(h => h, global => global with { MountedProbeIntervalSeconds = 0 });

        var result = ConfigValidator.Validate(config, AlwaysExists);

        var error = Assert.Single(result.Errors);
        Assert.Equal("invalid-mounted-probe-interval", error.Rule);
    }

    [Fact]
    public void Validate_MountedProbeIntervalSecondsNegative_IsRejected()
    {
        var config = Build(h => h, global => global with { MountedProbeIntervalSeconds = -60 });

        var result = ConfigValidator.Validate(config, AlwaysExists);

        var error = Assert.Single(result.Errors);
        Assert.Equal("invalid-mounted-probe-interval", error.Rule);
    }

    [Fact]
    public void Validate_MountedProbeIntervalSecondsAtDefault_IsAccepted()
    {
        // ADR-011: absent-at-parse-time already became 60 (bs-0na); the validator must not
        // re-reject the default it was just handed.
        var config = Build(h => h, global => global with { MountedProbeIntervalSeconds = GlobalConfig.DefaultMountedProbeIntervalSeconds });

        var result = ConfigValidator.Validate(config, AlwaysExists);

        Assert.True(result.IsValid);
    }

    // -- bs-z3y: global.failures_before_unmount ----------------------------------------------

    [Fact]
    public void Validate_FailuresBeforeUnmountZero_IsRejected()
    {
        var config = Build(h => h, global => global with { FailuresBeforeUnmount = 0 });

        var result = ConfigValidator.Validate(config, AlwaysExists);

        var error = Assert.Single(result.Errors);
        Assert.Equal("invalid-failures-before-unmount", error.Rule);
    }

    [Fact]
    public void Validate_FailuresBeforeUnmountNegative_IsRejected()
    {
        var config = Build(h => h, global => global with { FailuresBeforeUnmount = -1 });

        var result = ConfigValidator.Validate(config, AlwaysExists);

        var error = Assert.Single(result.Errors);
        Assert.Equal("invalid-failures-before-unmount", error.Rule);
    }

    [Fact]
    public void Validate_FailuresBeforeUnmountOne_IsAccepted()
    {
        // The boundary itself: 1 is the smallest value that still lets a mounted host be
        // unmounted (rather than never), so it must be accepted, not just "> 1".
        var config = Build(h => h, global => global with { FailuresBeforeUnmount = 1 });

        var result = ConfigValidator.Validate(config, AlwaysExists);

        Assert.True(result.IsValid);
    }

    // -- bs-z3y: global.probe_timeout_seconds -------------------------------------------------

    [Fact]
    public void Validate_ProbeTimeoutSecondsZero_IsRejected()
    {
        var config = Build(h => h, global => global with { ProbeTimeoutSeconds = 0 });

        var result = ConfigValidator.Validate(config, AlwaysExists);

        var error = Assert.Single(result.Errors);
        Assert.Equal("invalid-probe-timeout", error.Rule);
    }

    [Fact]
    public void Validate_ProbeTimeoutSecondsNegative_IsRejected()
    {
        var config = Build(h => h, global => global with { ProbeTimeoutSeconds = -5 });

        var result = ConfigValidator.Validate(config, AlwaysExists);

        var error = Assert.Single(result.Errors);
        Assert.Equal("invalid-probe-timeout", error.Rule);
    }

    [Fact]
    public void Validate_ProbeTimeoutSecondsOne_IsAccepted()
    {
        var config = Build(h => h, global => global with { ProbeTimeoutSeconds = 1 });

        var result = ConfigValidator.Validate(config, AlwaysExists);

        Assert.True(result.IsValid);
    }

    // -- bs-z3y: global.rclone_rc_port --------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(100000)]
    public void Validate_RcloneRcPortOutsideValidRange_IsRejected(int port)
    {
        var config = Build(h => h, global => global with { RcloneRcPort = port });

        var result = ConfigValidator.Validate(config, AlwaysExists);

        var error = Assert.Single(result.Errors);
        Assert.Equal("invalid-rclone-rc-port", error.Rule);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5572)]
    [InlineData(65535)]
    public void Validate_RcloneRcPortWithinValidRange_IsAccepted(int port)
    {
        var config = Build(h => h, global => global with { RcloneRcPort = port });

        var result = ConfigValidator.Validate(config, AlwaysExists);

        Assert.True(result.IsValid);
    }

    // -- bs-z3y: global.suspend_unmount_timeout_seconds ---------------------------------------

    [Fact]
    public void Validate_SuspendUnmountTimeoutSecondsZero_IsRejected()
    {
        var config = Build(h => h, global => global with { SuspendUnmountTimeoutSeconds = 0 });

        var result = ConfigValidator.Validate(config, AlwaysExists);

        var error = Assert.Single(result.Errors);
        Assert.Equal("invalid-suspend-unmount-timeout", error.Rule);
    }

    [Fact]
    public void Validate_SuspendUnmountTimeoutSecondsNegative_IsRejected()
    {
        var config = Build(h => h, global => global with { SuspendUnmountTimeoutSeconds = -8 });

        var result = ConfigValidator.Validate(config, AlwaysExists);

        var error = Assert.Single(result.Errors);
        Assert.Equal("invalid-suspend-unmount-timeout", error.Rule);
    }

    [Fact]
    public void Validate_SuspendUnmountTimeoutSecondsOne_IsAccepted()
    {
        var config = Build(h => h, global => global with { SuspendUnmountTimeoutSeconds = 1 });

        var result = ConfigValidator.Validate(config, AlwaysExists);

        Assert.True(result.IsValid);
    }

    // -- bs-z3y: mount.idle_unmount_seconds ---------------------------------------------------

    [Fact]
    public void Validate_IdleUnmountSecondsNegative_IsRejected()
    {
        var config = Build(h => h with { Mount = h.Mount with { IdleUnmountSeconds = -1 } });

        var result = ConfigValidator.Validate(config, AlwaysExists);

        var error = Assert.Single(result.Errors);
        Assert.Equal("negative-idle-unmount-seconds", error.Rule);
    }

    [Fact]
    public void Validate_IdleUnmountSecondsZero_IsAccepted()
    {
        // docs/CONFIG-SCHEMA.md: 0 = never. Must not be conflated with "absent".
        var config = Build(h => h with { Mount = h.Mount with { IdleUnmountSeconds = 0 } });

        var result = ConfigValidator.Validate(config, AlwaysExists);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_IdleUnmountSecondsAbsent_IsAccepted()
    {
        var config = Build(h => h with { Mount = h.Mount with { IdleUnmountSeconds = null } });

        var result = ConfigValidator.Validate(config, AlwaysExists);

        Assert.True(result.IsValid);
    }

    // -- bs-b8t: mount.network_mode -------------------------------------------------------------

    [Fact]
    public void Validate_NetworkModeFalse_IsRejected()
    {
        var config = Build(h => h with { Mount = h.Mount with { NetworkMode = false } });

        var result = ConfigValidator.Validate(config, AlwaysExists);

        var error = Assert.Single(result.Errors);
        Assert.Equal("invalid-network-mode", error.Rule);
    }

    [Fact]
    public void Validate_NetworkModeTrue_IsAccepted()
    {
        var config = Build(h => h with { Mount = h.Mount with { NetworkMode = true } });

        var result = ConfigValidator.Validate(config, AlwaysExists);

        Assert.True(result.IsValid);
    }

    // -- bs-5bk: duplicate display_name is case-insensitive -----------------------------------

    [Fact]
    public void Validate_DuplicateDisplayNameDifferingOnlyByCase_IsRejected()
    {
        var config = new BosunConfig
        {
            Global = new GlobalConfig(),
            Hosts = new Dictionary<string, HostConfig>
            {
                ["host-a"] = ValidHost("host-a") with { DisplayName = "Prod", Mount = ValidMount() with { Drive = "N:" } },
                ["host-b"] = ValidHost("host-b") with { DisplayName = "prod", Mount = ValidMount() with { Drive = "O:" } },
            },
        };

        var result = ConfigValidator.Validate(config, AlwaysExists);

        var error = Assert.Single(result.Errors);
        Assert.Equal("duplicate-display-name", error.Rule);
    }

    [Fact]
    public void Validate_ConfigViolatingTwoRules_ReportsBothNotJustTheFirst()
    {
        var config = Build(
            h => h with
            {
                Mount = h.Mount with { Drive = "A:" }, // invalid-drive-letter
                Probe = h.Probe with { IntervalSeconds = -5 }, // negative-probe-interval
            });

        var result = ConfigValidator.Validate(config, AlwaysExists);

        Assert.Equal(2, result.Errors.Count);
        Assert.Contains(result.Errors, e => e.Rule == "invalid-drive-letter");
        Assert.Contains(result.Errors, e => e.Rule == "negative-probe-interval");
    }

    // -- fixtures -----------------------------------------------------------------------------

    private static BosunConfig Build(
        Func<HostConfig, HostConfig> mutateHost,
        Func<GlobalConfig, GlobalConfig>? mutateGlobal = null)
    {
        var global = mutateGlobal is null ? new GlobalConfig() : mutateGlobal(new GlobalConfig());
        var host = mutateHost(ValidHost("host-a"));

        return new BosunConfig
        {
            Global = global,
            Hosts = new Dictionary<string, HostConfig> { [host.Key] = host },
        };
    }

    private static HostConfig ValidHost(string key) => new()
    {
        Key = key,
        DisplayName = $"Display {key}",
        Hostname = $"{key}.example.internal",
        Port = 22,
        User = "someuser",
        IdentityFile = "~/.ssh/id_ed25519",
        Mount = ValidMount(),
        Session = ValidSession(),
        Probe = ValidProbe(),
    };

    private static MountConfig ValidMount() => new()
    {
        Mode = MountMode.Persistent,
        Drive = "N:",
        RemotePath = "/srv/share",
        VfsCacheMode = "writes",
        NetworkMode = true,
        IdleUnmountSeconds = 0,
    };

    private static SessionConfig ValidSession() => new()
    {
        Autostart = false,
        Reconnect = true,
        Tmux = true,
        TmuxSession = "main",
        TabColor = "#2D5F3F",
        ColorScheme = "Campbell",
    };

    private static ProbeConfig ValidProbe() => new()
    {
        IntervalSeconds = 60,
        DeepProbe = true,
    };
}
