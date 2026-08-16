using Bosun.Configuration;

namespace Bosun.Tests.Configuration;

/// <summary>
/// End-to-end conformance across the parse → validate seam, driven from TOML text exactly as a
/// user would write it, plus the committed <c>config/hosts.example.toml</c>. Written against
/// docs/CONFIG-SCHEMA.md, ADR-008 and ADR-011 rather than against the implementation.
///
/// Rules that hold in <see cref="ConfigValidator"/> can still be lost at the seam — a value
/// defaulted during binding never reaches the rule that would have rejected it. Everything here
/// reads either in-line fixture text or the committed example file (fictional hostnames per
/// Invariant I9); never the maintainer's gitignored <c>config/hosts.toml</c>, and never his real
/// <c>~/.ssh</c> — the identity-file check is injected throughout.
/// </summary>
public sealed class ConfigSchemaConformanceTests
{
    private static readonly Func<string, bool> AlwaysExists = _ => true;
    private static readonly Func<string, bool> NeverExists = _ => false;

    // -- the shipped example file -------------------------------------------------------------

    /// <summary>
    /// <c>config/hosts.example.toml</c> is the file every user copies to <c>hosts.toml</c>. If it
    /// ever fails validation, Bosun refuses to start for every new user on their first run.
    /// Catches an example edited to add a drive collision, a reserved letter, a duplicate
    /// display name, or a <c>vfs_cache_mode</c> below <c>writes</c> — and equally a validator
    /// rule tightened without the example being updated to match.
    /// </summary>
    [Fact]
    public void ExampleConfig_ParsesAndValidatesCleanly()
    {
        var config = ConfigParser.Parse(ReadExampleConfig(), "hosts.example.toml");

        var result = ConfigValidator.Validate(config, AlwaysExists);

        Assert.Empty(result.Errors);
    }

    /// <summary>
    /// The only thing that can stand between the shipped example and a valid config is the
    /// user's SSH key not being where the example guesses. With the existence check forced to
    /// fail, every reported error must be that one rule — one per host, and nothing else.
    /// Catches an example whose validity depends on some second, incidental condition, and
    /// catches a validator that stops after the first identity-file failure instead of naming
    /// every host the user must fix.
    /// </summary>
    [Fact]
    public void ExampleConfig_WithNoIdentityFilesOnDisk_FailsOnlyOnTheIdentityFileRule()
    {
        var config = ConfigParser.Parse(ReadExampleConfig(), "hosts.example.toml");

        var result = ConfigValidator.Validate(config, NeverExists);

        Assert.Equal(config.Hosts.Count, result.Errors.Count);
        Assert.All(result.Errors, e => Assert.Equal("identity-file-not-found", e.Rule));
    }

    // -- mode = "none" through the whole pipeline ---------------------------------------------

    /// <summary>
    /// <c>hosts.example-jump</c>'s real shape, end to end: a <c>[mount]</c> table containing only
    /// <c>mode = "none"</c> must bind (absent fields as null, not as empty strings) and then
    /// validate. Catches a binder that materialises <c>""</c> for absent optional fields, which
    /// the validator would then read as a supplied-but-invalid drive — a rejection the user
    /// cannot explain from anything written in the file.
    /// </summary>
    [Fact]
    public void Toml_ShellOnlyHostWithModeNoneAndNothingElse_ParsesAndValidates()
    {
        var toml = Host("jump", mount: """
              [hosts.jump.mount]
              mode = "none"
            """);

        var config = ConfigParser.Parse(toml);

        Assert.Null(config.Hosts["jump"].Mount.Drive);
        Assert.Null(config.Hosts["jump"].Mount.RemotePath);
        Assert.Empty(ConfigValidator.Validate(config, AlwaysExists).Errors);
    }

    // -- the two adjacent zeroes (ADR-008 vs ADR-011) -----------------------------------------

    /// <summary>
    /// ADR-011: an absent <c>mounted_probe_interval_seconds</c> defaults to <c>60</c> and is not
    /// an error — rejecting it would break every existing <c>hosts.toml</c> on upgrade. Asserted
    /// with <c>[global]</c> present but that key absent, and with a host at
    /// <c>interval_seconds = 0</c>, so the whole ADR-008 + ADR-011 pairing is exercised in one
    /// file. Catches a "required key" reading of ADR-011, and catches a default applied at the
    /// wrong layer (a validator defaulting after its own check would still reject).
    /// </summary>
    [Fact]
    public void Toml_MountedProbeIntervalAbsentAndIdleIntervalZero_IsValidAndDefaultsToSixty()
    {
        var toml = """
            [global]
            rclone_rc_port = 5572

            """ + Host("remote", probeInterval: 0);

        var config = ConfigParser.Parse(toml);

        Assert.Equal(60, config.Global.MountedProbeIntervalSeconds);
        Assert.Empty(ConfigValidator.Validate(config, AlwaysExists).Errors);
    }

    /// <summary>
    /// ADR-011's other half: an explicit <c>mounted_probe_interval_seconds = 0</c> must be
    /// rejected, because a mounted host that is never probed never accumulates failures, never
    /// reaches <c>Draining</c>, and leaves a drive letter pointing at a dead host (Invariant I2).
    /// The bug this catches is the classic "absent" conflation — binding through
    /// <c>value == 0 ? 60 : value</c> or a non-nullable default — which turns an explicit zero
    /// into the default and silently disables the guarantee the rule exists to protect. Only an
    /// end-to-end test can catch it: the validator alone never sees the zero.
    /// </summary>
    [Fact]
    public void Toml_ExplicitZeroMountedProbeInterval_IsRejectedAndNotSilentlyDefaultedToSixty()
    {
        var toml = """
            [global]
            mounted_probe_interval_seconds = 0

            """ + Host("nas", probeInterval: 60);

        var config = ConfigParser.Parse(toml);

        Assert.Equal(0, config.Global.MountedProbeIntervalSeconds);
        var error = Assert.Single(ConfigValidator.Validate(config, AlwaysExists).Errors);
        Assert.Equal("invalid-mounted-probe-interval", error.Rule);
    }

    /// <summary>
    /// ADR-008's zero, end to end and in both directions: <c>probe.interval_seconds = 0</c> is
    /// the documented on-demand default and must survive binding as <c>0</c> and validate, while
    /// a negative value must be rejected. Catches a <c>&lt;= 0</c> check (which would reject the
    /// shipped <c>example-remote</c>) and a binder that treats an explicit <c>0</c> as absent
    /// and substitutes a polling interval — silently reintroducing the polling of on-demand
    /// hosts that ADR-008 forbids.
    /// </summary>
    [Fact]
    public void Toml_IdleProbeIntervalZeroIsAcceptedAndNegativeIsRejected()
    {
        var zero = ConfigParser.Parse(Host("remote", probeInterval: 0));
        var negative = ConfigParser.Parse(Host("remote", probeInterval: -1));

        Assert.Equal(0, zero.Hosts["remote"].Probe.IntervalSeconds);
        Assert.Empty(ConfigValidator.Validate(zero, AlwaysExists).Errors);

        var error = Assert.Single(ConfigValidator.Validate(negative, AlwaysExists).Errors);
        Assert.Equal("negative-probe-interval", error.Rule);
    }

    // -- host identity ------------------------------------------------------------------------

    /// <summary>
    /// docs/CONFIG-SCHEMA.md lists "duplicate host key" as grounds for rejecting the whole
    /// config. TOML forbids redefining a table, so this must surface as a parse failure — but it
    /// has to surface somewhere. Catches a binder that quietly lets the last definition win,
    /// which would silently drop a host (no profile, no mount) with nothing reported anywhere.
    /// </summary>
    [Fact]
    public void Toml_DuplicateHostKey_IsRejectedRatherThanLastOneWinning()
    {
        var toml = Host("jump", hostname: "first.example.com")
            + Environment.NewLine
            + Host("jump", hostname: "second.example.com");

        Assert.Throws<ConfigParseException>(() => ConfigParser.Parse(toml, "hosts.toml"));
    }

    /// <summary>
    /// Two hosts, same drive, straight from TOML — the collision must survive the seam intact.
    /// Catches a binder that normalises or de-duplicates drive values (for example keying an
    /// internal map by drive letter), which would make the collision disappear before the rule
    /// that rejects it ever runs.
    /// </summary>
    [Fact]
    public void Toml_TwoHostsClaimingTheSameDrive_IsRejectedEndToEnd()
    {
        var toml = Host("nas", drive: "N:") + Environment.NewLine + Host("archive", drive: "N:");

        var config = ConfigParser.Parse(toml);

        Assert.Equal(2, config.Hosts.Count);
        var error = Assert.Single(ConfigValidator.Validate(config, AlwaysExists).Errors);
        Assert.Equal("drive-collision", error.Rule);
    }

    // -- fixtures -----------------------------------------------------------------------------

    /// <summary>
    /// One complete, schema-valid host table. Defaults produce a mounting host so that a test
    /// changing exactly one field is changing exactly one thing.
    /// </summary>
    private static string Host(
        string key,
        string? hostname = null,
        string? drive = null,
        int probeInterval = 60,
        string? mount = null)
    {
        var mountTable = mount ?? $"""
              [hosts.{key}.mount]
              mode                 = "persistent"
              drive                = "{drive ?? "N:"}"
              remote_path          = "/srv/share"
              vfs_cache_mode       = "writes"
              network_mode         = true
              idle_unmount_seconds = 0
            """;

        return $"""
            [hosts.{key}]
            display_name  = "Display {key}"
            hostname      = "{hostname ?? $"{key}.example.internal"}"
            port          = 22
            user          = "someuser"
            identity_file = "~/.ssh/id_ed25519"

            {mountTable}

              [hosts.{key}.session]
              autostart    = false
              reconnect    = true
              tmux         = true
              tmux_session = "main"
              tab_color    = "#2D5F3F"
              color_scheme = "Campbell"

              [hosts.{key}.probe]
              interval_seconds = {probeInterval}
              deep_probe       = true

            """;
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
