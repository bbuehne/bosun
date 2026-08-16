using Bosun.Configuration;

namespace Bosun.Tests.Configuration;

/// <summary>
/// Independently authored from docs/CONFIG-SCHEMA.md ("Validation rules"), ADR-008, ADR-011 and
/// Invariant I6 — deliberately not from <see cref="ConfigValidator"/>'s implementation
/// (CLAUDE.md §6: config validation is one of the areas where self-testing cannot be trusted to
/// catch a spec misreading). Focus is on the things a single-rule-per-test suite structurally
/// cannot cover: rule <em>interaction</em>, error accumulation across scopes, and boundary
/// values where two adjacent rules treat the same literal (<c>0</c>) in opposite directions.
///
/// The identity-file existence check is injected in every test — nothing here reads the
/// maintainer's real <c>~/.ssh</c> (CLAUDE.md worktree-safety rules, Invariant I9).
/// </summary>
public sealed class ConfigValidatorAdversarialTests
{
    private static readonly Func<string, bool> AlwaysExists = _ => true;

    // -- rule interaction and error accumulation ----------------------------------------------

    /// <summary>
    /// A cross-host rule firing must not suppress the per-host rules for the same hosts.
    /// Catches: a validator that returns as soon as a cross-host rule fires, or that skips
    /// per-host validation for hosts already named in a collision — which would hide the reason
    /// the config is unfixable (both letters are reserved) behind the collision, costing the
    /// user a second edit-save-reject cycle.
    /// </summary>
    [Fact]
    public void Validate_TwoHostsCollidingOnAReservedDriveLetter_ReportsCollisionAndBothLetterViolations()
    {
        var config = ConfigOf(
            ValidHost("host-a") with { Mount = ValidMount() with { Drive = "C:" } },
            ValidHost("host-b") with { Mount = ValidMount() with { Drive = "C:" } });

        var result = ConfigValidator.Validate(config, AlwaysExists);

        Assert.Equal(
            ["drive-collision", "invalid-drive-letter", "invalid-drive-letter"],
            result.Errors.Select(e => e.Rule).Order());
    }

    /// <summary>
    /// docs/CONFIG-SCHEMA.md rejects "the whole config" — every violation in it, wherever it
    /// lives. Catches: an early return after the <c>[global]</c> block, a <c>break</c> in the
    /// per-host loop, or an error list that is replaced rather than appended to per scope. Any
    /// of those turns one save into N saves for a user fixing a hand-edited file.
    /// </summary>
    [Fact]
    public void Validate_ViolationsSpanningGlobalAndThreeHosts_ReportsEveryOne()
    {
        var config = new BosunConfig
        {
            Global = new GlobalConfig
            {
                BackoffSeconds = [],                 // invalid-backoff-seconds
                MountedProbeIntervalSeconds = 0,     // invalid-mounted-probe-interval (ADR-011)
            },
            Hosts = new Dictionary<string, HostConfig>
            {
                ["host-a"] = ValidHost("host-a") with
                {
                    Mount = ValidMount() with { Drive = "N:" },
                    Probe = new ProbeConfig { IntervalSeconds = -30, DeepProbe = true }, // negative-probe-interval
                },
                ["host-b"] = ValidHost("host-b") with
                {
                    Mount = ValidMount() with { Drive = "R:", VfsCacheMode = "off" },    // invalid-vfs-cache-mode (I6)
                },
                ["host-c"] = ValidHost("host-c") with
                {
                    Mount = ValidMount() with { Drive = "S:" },
                    IdentityFile = "/keys/missing",                                      // identity-file-not-found
                },
            },
        };

        var result = ConfigValidator.Validate(config, path => path != "/keys/missing");

        Assert.Equal(
            [
                "identity-file-not-found",
                "invalid-backoff-seconds",
                "invalid-mounted-probe-interval",
                "invalid-vfs-cache-mode",
                "negative-probe-interval",
            ],
            result.Errors.Select(e => e.Rule).Order());
    }

    /// <summary>
    /// Every host-scoped error must name the host it belongs to. Catches: a message that says
    /// only "probe.interval_seconds must not be negative" in a twenty-host file, which is an
    /// error the user cannot locate.
    /// </summary>
    [Fact]
    public void Validate_HostScopedViolationsInAMultiHostConfig_EachNameTheOffendingHostKey()
    {
        var config = ConfigOf(
            ValidHost("alpha") with { Mount = ValidMount() with { Drive = "N:", VfsCacheMode = "minimal" } },
            ValidHost("bravo") with
            {
                Mount = ValidMount() with { Drive = "R:" },
                Probe = new ProbeConfig { IntervalSeconds = -1, DeepProbe = false },
            });

        var result = ConfigValidator.Validate(config, AlwaysExists);

        var vfsError = Assert.Single(result.Errors, e => e.Rule == "invalid-vfs-cache-mode");
        Assert.Contains("alpha", vfsError.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("bravo", vfsError.Message, StringComparison.Ordinal);

        var probeError = Assert.Single(result.Errors, e => e.Rule == "negative-probe-interval");
        Assert.Contains("bravo", probeError.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("alpha", probeError.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Catches an identity-file check whose result is computed once and applied to every host
    /// (or applied to the wrong host), which would either reject a whole valid config or — far
    /// worse — pass a host whose key is missing and let it reach <c>Mounting</c>.
    /// </summary>
    [Fact]
    public void Validate_IdentityFileMissingForOneOfTwoHosts_BlamesOnlyThatHost()
    {
        var config = ConfigOf(
            ValidHost("present") with { Mount = ValidMount() with { Drive = "N:" }, IdentityFile = "/keys/present" },
            ValidHost("absent") with { Mount = ValidMount() with { Drive = "R:" }, IdentityFile = "/keys/absent" });

        var result = ConfigValidator.Validate(config, path => path == "/keys/present");

        var error = Assert.Single(result.Errors);
        Assert.Equal("identity-file-not-found", error.Rule);
        Assert.Contains("absent", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// docs/ARCHITECTURE.md §6: "Drive letter already in use … surface the conflict … do not
    /// silently pick another letter." Catches a collision check that reports only the first
    /// colliding pair, leaving the third host's claim invisible.
    /// </summary>
    [Fact]
    public void Validate_ThreeHostsClaimingOneDrive_SurfacesEveryClaimant()
    {
        var config = ConfigOf(
            ValidHost("host-a") with { Mount = ValidMount() with { Drive = "N:" } },
            ValidHost("host-b") with { Mount = ValidMount() with { Drive = "N:" } },
            ValidHost("host-c") with { Mount = ValidMount() with { Drive = "N:" } });

        var result = ConfigValidator.Validate(config, AlwaysExists);

        Assert.NotEmpty(result.Errors);
        Assert.All(result.Errors, e => Assert.Equal("drive-collision", e.Rule));

        var text = string.Join(" ", result.Errors.Select(e => e.Message));
        Assert.Contains("host-a", text, StringComparison.Ordinal);
        Assert.Contains("host-b", text, StringComparison.Ordinal);
        Assert.Contains("host-c", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Catches a collision check that stops after finding one duplicated drive — a four-host
    /// config with two independent collisions must report both, or the user fixes one and is
    /// rejected again for the other.
    /// </summary>
    [Fact]
    public void Validate_TwoIndependentDriveCollisions_ReportsBoth()
    {
        var config = ConfigOf(
            ValidHost("host-a") with { Mount = ValidMount() with { Drive = "N:" } },
            ValidHost("host-b") with { Mount = ValidMount() with { Drive = "N:" } },
            ValidHost("host-c") with { Mount = ValidMount() with { Drive = "R:" } },
            ValidHost("host-d") with { Mount = ValidMount() with { Drive = "R:" } });

        var result = ConfigValidator.Validate(config, AlwaysExists);

        Assert.Equal(2, result.Errors.Count);
        Assert.All(result.Errors, e => Assert.Equal("drive-collision", e.Rule));
        Assert.Single(result.Errors, e => e.Message.Contains("N:", StringComparison.Ordinal));
        Assert.Single(result.Errors, e => e.Message.Contains("R:", StringComparison.Ordinal));
    }

    // -- mode = "none" ------------------------------------------------------------------------

    /// <summary>
    /// The <c>hosts.example-jump</c> shape: a shell-only host whose <c>[mount]</c> table carries
    /// nothing but <c>mode = "none"</c>. docs/CONFIG-SCHEMA.md makes <c>drive</c> and
    /// <c>remote_path</c> required only when <c>mode != "none"</c>. Catches a validator that
    /// requires them unconditionally, which would reject the exact file every new user copies.
    /// </summary>
    [Fact]
    public void Validate_ModeNone_WithNoDriveOrRemotePath_IsAccepted()
    {
        var config = ConfigOf(ValidHost("jump") with
        {
            Mount = new MountConfig
            {
                Mode = MountMode.None,
                Drive = null,
                RemotePath = null,
                VfsCacheMode = null,
                NetworkMode = null,
                IdleUnmountSeconds = null,
            },
        });

        var result = ConfigValidator.Validate(config, AlwaysExists);

        Assert.Empty(result.Errors);
    }

    /// <summary>
    /// The requirement is "mode != none", not "mode == persistent". Catches a check written
    /// against <see cref="MountMode.Persistent"/>, which would let an <c>on-demand</c> host with
    /// no <c>drive</c> through validation and only fail when the user clicks Mount in the tray —
    /// a config error surfacing as a runtime error.
    /// </summary>
    [Theory]
    [InlineData(MountMode.Persistent, true, false)]
    [InlineData(MountMode.Persistent, false, true)]
    [InlineData(MountMode.OnDemand, true, false)]
    [InlineData(MountMode.OnDemand, false, true)]
    public void Validate_MountingModeMissingDriveOrRemotePath_IsRejected(MountMode mode, bool dropDrive, bool dropRemotePath)
    {
        var config = ConfigOf(ValidHost("host-a") with
        {
            Mount = ValidMount() with
            {
                Mode = mode,
                Drive = dropDrive ? null : "N:",
                RemotePath = dropRemotePath ? null : "/srv/share",
            },
        });

        var result = ConfigValidator.Validate(config, AlwaysExists);

        Assert.Contains(result.Errors, e => e.Rule == "mount-missing-drive-or-remote-path");
    }

    /// <summary>
    /// An empty string is "no drive", not "a drive that happens to be empty". Catches a
    /// null-only check (<c>Drive is not null</c>) that would treat <c>drive = ""</c> as supplied
    /// and let a mount be attempted at no mount point.
    /// </summary>
    [Fact]
    public void Validate_EmptyDriveStringOnAMountingHost_IsRejectedAsMissing()
    {
        var config = ConfigOf(ValidHost("host-a") with { Mount = ValidMount() with { Drive = "" } });

        var result = ConfigValidator.Validate(config, AlwaysExists);

        Assert.Contains(result.Errors, e => e.Rule == "mount-missing-drive-or-remote-path");
    }

    /// <summary>
    /// docs/CONFIG-SCHEMA.md states the drive-letter rule unconditionally — it is not scoped to
    /// mounting modes. Catches a validator that skips field checks on <c>mode = "none"</c>
    /// hosts, which would let a bad letter sit in the file until the day the user flips the mode
    /// to <c>persistent</c> and gets an error about a line they did not touch.
    /// </summary>
    [Fact]
    public void Validate_ModeNone_WithAReservedDriveLetterStillPresent_IsRejected()
    {
        var config = ConfigOf(ValidHost("jump") with
        {
            Mount = new MountConfig { Mode = MountMode.None, Drive = "C:" },
        });

        var result = ConfigValidator.Validate(config, AlwaysExists);

        var error = Assert.Single(result.Errors);
        Assert.Equal("invalid-drive-letter", error.Rule);
    }

    // -- drive letters ------------------------------------------------------------------------

    /// <summary>
    /// <c>^[D-Z]:$</c>, with A–C reserved. Each case targets a distinct way the pattern gets
    /// written wrong: <c>A:</c>/<c>B:</c>/<c>C:</c> catch a widened <c>[A-Z]</c> range (mounting
    /// over the system drive); <c>d:</c> catches <c>RegexOptions.IgnoreCase</c>; <c>DD:</c>,
    /// <c>D:\</c>, <c>D:/</c>, <c> D:</c> and <c>D: </c> catch missing or one-sided anchors;
    /// <c>D</c> and <c>:</c> catch a colon made optional.
    /// </summary>
    [Theory]
    [InlineData("A:")]
    [InlineData("B:")]
    [InlineData("C:")]
    [InlineData("d:")]
    [InlineData("D")]
    [InlineData("D:\\")]
    [InlineData("D:/")]
    [InlineData("DD:")]
    [InlineData(" D:")]
    [InlineData("D: ")]
    [InlineData(":")]
    [InlineData("D:D:")]
    public void Validate_DriveOutsideTheDocumentedPattern_IsRejected(string drive)
    {
        var config = ConfigOf(ValidHost("host-a") with { Mount = ValidMount() with { Drive = drive } });

        var result = ConfigValidator.Validate(config, AlwaysExists);

        Assert.Contains(result.Errors, e => e.Rule == "invalid-drive-letter");
    }

    /// <summary>
    /// A drive value must be exactly a letter and a colon and nothing else. In .NET regex,
    /// <c>$</c> also matches immediately before a trailing <c>\n</c>, so a pattern anchored with
    /// <c>^…$</c> accepts <c>"D:\n"</c> — a value a TOML basic string can express. Catches that
    /// anchor mistake; the fix is <c>\A…\z</c>. Whatever is bound here is what reaches
    /// <c>mount/mount</c> as the mount point.
    /// </summary>
    /// <remarks>
    /// THIS TEST CURRENTLY FAILS, deliberately and knowingly. It is the one behaviour where the
    /// implementation and docs/CONFIG-SCHEMA.md disagree, and the spec wins:
    /// <c>ConfigValidator.DriveLetterPattern</c> is <c>new Regex("^[D-Z]:$")</c>, and .NET's
    /// <c>$</c> also matches before a trailing <c>\n</c>, so <c>"D:\n"</c> is accepted as a drive
    /// letter today. Fixing it is a one-character-class change to <c>\A[D-Z]:\z</c> in
    /// src/Bosun/Configuration/ConfigValidator.cs. Left red rather than skipped or weakened: a
    /// test author does not get to launder a validation bypass into expected behaviour, and this
    /// author is not permitted to edit src/.
    /// </remarks>
    [Fact]
    public void Validate_DriveWithATrailingNewline_IsRejected()
    {
        var config = ConfigOf(ValidHost("host-a") with { Mount = ValidMount() with { Drive = "D:\n" } });

        var result = ConfigValidator.Validate(config, AlwaysExists);

        Assert.Contains(result.Errors, e => e.Rule == "invalid-drive-letter");
    }

    // -- vfs_cache_mode (Invariant I6) --------------------------------------------------------

    /// <summary>
    /// Invariant I6 / docs/CONFIG-SCHEMA.md: <c>off</c> and <c>minimal</c> are rejected because
    /// they break editors and Office outright. Asserted here alongside the accepted values so a
    /// single test states the floor. Catches a deny-list quietly emptied or narrowed to
    /// <c>off</c> alone.
    /// </summary>
    [Theory]
    [InlineData("off", false)]
    [InlineData("minimal", false)]
    [InlineData("writes", true)]
    [InlineData("full", true)]
    public void Validate_VfsCacheModeAgainstTheDocumentedFloor(string mode, bool expectedValid)
    {
        var config = ConfigOf(ValidHost("host-a") with { Mount = ValidMount() with { VfsCacheMode = mode } });

        var result = ConfigValidator.Validate(config, AlwaysExists);

        Assert.Equal(expectedValid, result.IsValid);
    }

    /// <summary>
    /// Documents the CURRENT specified behaviour, not an endorsement of it: docs/CONFIG-SCHEMA.md
    /// specifies <c>vfs_cache_mode</c> as a deny-list (<c>{off, minimal}</c>), so an
    /// unrecognised value — a typo — passes validation and is handed to rclone as written. Open
    /// as <c>bs-lrd</c>. This test is the canary: if it fails because someone introduced an
    /// allow-list, that is very likely the right change and this test should be deliberately
    /// rewritten, not deleted. Until then it catches an accidental, undecided tightening.
    /// </summary>
    [Fact]
    public void Validate_UnrecognisedVfsCacheModeTypo_CurrentlyPasses_BecauseTheRuleIsADenyList()
    {
        var config = ConfigOf(ValidHost("host-a") with { Mount = ValidMount() with { VfsCacheMode = "wrties" } });

        var result = ConfigValidator.Validate(config, AlwaysExists);

        Assert.True(result.IsValid);
    }

    /// <summary>
    /// Documents CURRENT specified behaviour: the validation-rules list rejects only
    /// <c>{off, minimal}</c>, so an absent <c>vfs_cache_mode</c> is not a config error. That
    /// makes honouring Invariant I6 the mount layer's job — it must supply <c>writes</c> when
    /// the field is absent rather than letting rclone's own default (<c>off</c>) apply. Catches
    /// a validator that starts requiring the field (which would reject valid existing files)
    /// and pins down where the I6 obligation actually lives.
    /// </summary>
    [Fact]
    public void Validate_VfsCacheModeAbsentOnAMountingHost_IsNotAConfigError()
    {
        var config = ConfigOf(ValidHost("host-a") with { Mount = ValidMount() with { VfsCacheMode = null } });

        var result = ConfigValidator.Validate(config, AlwaysExists);

        Assert.True(result.IsValid);
    }

    // -- probe cadence boundaries (ADR-008 / ADR-011) -----------------------------------------

    /// <summary>
    /// The two adjacent settings treat the literal <c>0</c> in opposite directions, which is
    /// exactly the pair an implementer inverts: <c>probe.interval_seconds = 0</c> is the
    /// documented on-demand default (ADR-008) and must be accepted, while
    /// <c>global.mounted_probe_interval_seconds = 0</c> must be rejected (ADR-011) because it
    /// would disable the probe cadence that unmount-on-failure depends on (Invariant I2).
    /// Asserting both in one test makes an inversion impossible to miss.
    /// </summary>
    [Fact]
    public void Validate_ZeroIsAcceptedForIdleProbeIntervalAndRejectedForMountedProbeInterval()
    {
        var idleZero = ConfigOf(ValidHost("host-a") with
        {
            Probe = new ProbeConfig { IntervalSeconds = 0, DeepProbe = true },
        });
        var mountedZero = new BosunConfig
        {
            Global = new GlobalConfig { MountedProbeIntervalSeconds = 0 },
            Hosts = new Dictionary<string, HostConfig> { ["host-a"] = ValidHost("host-a") },
        };

        Assert.Empty(ConfigValidator.Validate(idleZero, AlwaysExists).Errors);

        var mountedResult = ConfigValidator.Validate(mountedZero, AlwaysExists);
        var error = Assert.Single(mountedResult.Errors);
        Assert.Equal("invalid-mounted-probe-interval", error.Rule);
    }

    /// <summary>
    /// <c>backoff_seconds</c> must be non-empty with only positive values. <c>0</c> in the
    /// ladder means an instant retry loop against an unreachable host; a negative value is
    /// nonsense. Catches a <c>&lt; 0</c> check where <c>&lt;= 0</c> is required, and a
    /// single-element ladder being mistaken for "empty".
    /// </summary>
    [Theory]
    [InlineData(new int[0], false)]
    [InlineData(new[] { 0 }, false)]
    [InlineData(new[] { 5, 15, -1 }, false)]
    [InlineData(new[] { 5 }, true)]
    [InlineData(new[] { 5, 15, 30, 60, 300 }, true)]
    public void Validate_BackoffLadderBoundaries(int[] ladder, bool expectedValid)
    {
        var config = new BosunConfig
        {
            Global = new GlobalConfig { BackoffSeconds = ladder },
            Hosts = new Dictionary<string, HostConfig> { ["host-a"] = ValidHost("host-a") },
        };

        var result = ConfigValidator.Validate(config, AlwaysExists);

        Assert.Equal(expectedValid, result.IsValid);
    }

    // -- fixtures -----------------------------------------------------------------------------

    private static BosunConfig ConfigOf(params HostConfig[] hosts) => new()
    {
        Global = new GlobalConfig(),
        Hosts = hosts.ToDictionary(h => h.Key, StringComparer.Ordinal),
    };

    private static HostConfig ValidHost(string key) => new()
    {
        Key = key,
        DisplayName = $"Display {key}",
        Hostname = $"{key}.example.internal",
        Port = 22,
        User = "someuser",
        IdentityFile = $"/keys/{key}",
        Mount = ValidMount(),
        Session = new SessionConfig
        {
            Autostart = false,
            Reconnect = true,
            Tmux = true,
            TmuxSession = "main",
            TabColor = "#2D5F3F",
            ColorScheme = "Campbell",
        },
        Probe = new ProbeConfig { IntervalSeconds = 60, DeepProbe = true },
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
}
