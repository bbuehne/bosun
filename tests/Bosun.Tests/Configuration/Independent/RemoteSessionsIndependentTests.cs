using Bosun.Configuration;
using Bosun.Tests.Configuration.Fakes;

namespace Bosun.Tests.Configuration.Independent;

/// <summary>
/// Independent test-author pass for bs-4xw.6 (ADR-024, docs/CONFIG-SCHEMA.md "Remote sessions" and
/// the "Reject the whole config, keep the previous one" validation list). Written from the spec, to
/// cover what <c>RemoteSessionsConfigTests</c> does not: writer fidelity for awkward paths, the rest
/// of the config surviving a rewrite, TOML spellings of the table, fail-safe handling of case and
/// type oddities, real-default validation of garbage paths, and the reload / host-edit paths
/// through <see cref="HostConfigStore"/> and <see cref="HostConfigWriter"/>.
/// </summary>
/// <remarks>
/// No real environment dependency beyond process-local variables this class sets and clears itself
/// (unique GUID names). Every directory that must exist is a fresh GUID directory under the temp
/// path, deleted in <see cref="Dispose"/>; every directory that must not exist is a GUID path under
/// it that is never created. The store is driven by <see cref="FakeConfigFileReader"/>,
/// <see cref="FakeConfigFileWatcher"/> and <see cref="FakeTimeProvider"/>: no sleeps, no wall clock,
/// no real watcher. <see cref="HostConfigWriter"/> writes only to a file inside the temp directory.
/// </remarks>
public sealed class RemoteSessionsIndependentTests : IDisposable
{
    private static readonly Func<string, bool> AlwaysExists = _ => true;

    private readonly string _tempRoot =
        Path.Combine(Path.GetTempPath(), "bosun-tests", "remote-sessions-independent", Guid.NewGuid().ToString("N"));

    private readonly List<string> _envVarsToClear = [];

    public void Dispose()
    {
        foreach (var name in _envVarsToClear)
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    /// <summary>Non-default [global] and [claude_sessions] plus two hosts that differ in every
    /// optional area, so a rewrite that loses or reorders anything has something to get wrong.</summary>
    private const string RichBody = """
        [global]
        rclone_rc_port                      = 5599
        probe_timeout_seconds               = 7
        failures_before_unmount             = 4
        backoff_seconds                     = [3, 9, 27]
        mounted_probe_interval_seconds      = 45
        mounted_deep_probe_interval_seconds = 240
        suspend_unmount_timeout_seconds     = 2
        start_with_windows                  = false

        [claude_sessions]
        enabled = true

        [hosts.nas]
        display_name  = "NAS"
        hostname      = "nas.example.internal"
        port          = 2222
        user          = "someuser"
        identity_file = "~/.ssh/id_ed25519"

          [hosts.nas.mount]
          mode                 = "persistent"
          drive                = "N:"
          remote_path          = "/volume1/share"
          vfs_cache_mode       = "full"
          network_mode         = true
          idle_unmount_seconds = 0

          [hosts.nas.session]
          autostart    = true
          reconnect    = false
          tmux         = true
          tmux_session = "work"
          tab_color    = "#2D5F3F"
          color_scheme = "Campbell"

          [hosts.nas.probe]
          interval_seconds = 60
          deep_probe       = true

        [hosts.jump]
        display_name  = "Jump"
        hostname      = "jump.example.com"
        port          = 22
        user          = "someuser"
        identity_file = "~/.ssh/id_ed25519"

          [hosts.jump.mount]
          mode = "none"

          [hosts.jump.probe]
          interval_seconds = 0
          deep_probe       = false
        """;

    // ===================================================================================
    // Writer fidelity: parse -> write -> parse must reproduce the literal development_area
    // ===================================================================================

    /// <summary>Paths a Windows user can plausibly write, chosen so each stresses a different part of
    /// TOML basic-string escaping.</summary>
    public static TheoryData<string> AwkwardAreas => new()
    {
        @"%USERPROFILE%\Development",                 // the documented example
        @"\\fileserver\share\dev",                    // UNC: leading double backslash
        @"D:\dev\",                                   // trailing backslash (would escape the closing quote if unescaped)
        @"D:\My ""Quoted"" Projects",                 // embedded double quotes
        @"D:\it's mine",                              // single quote (breaks a literal-string writer)
        @"D:\dev # not a comment",                    // '#' inside the value
        @"D:\a=b\[x]\{y}",                            // TOML structural characters
        @"%HOMEDRIVE%%HOMEPATH%\dev",                 // adjacent %VAR% tokens
        @"D:\100%\dev",                               // a lone percent that is not a token
        @"D:\Développement\項目",                      // non-ASCII
        "D:\\tab\there",                              // a real tab character
        "   ",                                        // whitespace only (disabled tables can carry it)
        "",                                           // empty string, which is not the same as absent
    };

    [Theory]
    [MemberData(nameof(AwkwardAreas))]
    public void WriteThenParse_AwkwardDevelopmentArea_SurvivesExactly(string area)
    {
        // Bug class: the writer emits a value TOML cannot re-read (unescaped quote or trailing
        // backslash), or re-reads to a different string (lost characters, normalized whitespace,
        // blank dropped to absent). ADR-019: Bosun is the sole writer, so any loss here is
        // permanent the first time the user saves a host.
        var original = new BosunConfig
        {
            Global = new GlobalConfig(),
            Hosts = new Dictionary<string, HostConfig>(),
            RemoteSessions = new RemoteSessionsConfig { Enabled = false, DevelopmentArea = area },
        };

        var text = HostConfigTomlWriter.Write(original);
        var reparsed = ConfigParser.Parse(text, "round-trip");

        Assert.Equal(area, reparsed.RemoteSessions.DevelopmentArea);
        Assert.False(reparsed.RemoteSessions.Enabled);
    }

    [Theory]
    [MemberData(nameof(AwkwardAreas))]
    public void WriteIsAFixedPoint_SecondRewriteProducesIdenticalText(string area)
    {
        // Bug class: a writer that is lossy only on the second pass (double-escaping, or escaping
        // that the parser half-undoes). Each host edit is a rewrite, so drift compounds per save.
        var config = ConfigParser.Parse(RichBody) with
        {
            RemoteSessions = new RemoteSessionsConfig { Enabled = true, DevelopmentArea = area },
        };

        var first = HostConfigTomlWriter.Write(config);
        var second = HostConfigTomlWriter.Write(ConfigParser.Parse(first, "first"));

        Assert.Equal(first, second);
    }

    [Fact]
    public void ParseAndWrite_NeverExpandPercentVar_EvenWhenTheVariableIsSet()
    {
        // Bug class: the parser or writer calls Environment.ExpandEnvironmentVariables, baking this
        // machine's expansion into the file. The existing suite uses %USERPROFILE%, whose real value
        // the test cannot see; here the variable is set to a sentinel so any expansion is visible.
        var name = SetProcessVariable(@"C:\SENTINEL-EXPANDED");
        var literal = $@"%{name}%\dev";

        var parsed = ConfigParser.Parse($"[remote_sessions]\nenabled = true\ndevelopment_area = '{literal}'\n");
        var written = HostConfigTomlWriter.Write(parsed);

        Assert.Equal(literal, parsed.RemoteSessions.DevelopmentArea);
        Assert.DoesNotContain("SENTINEL-EXPANDED", written, StringComparison.Ordinal);
        Assert.Contains($"%{name}%", written, StringComparison.Ordinal);
        // ...while the one sanctioned expansion point does expand it.
        Assert.Equal(@"C:\SENTINEL-EXPANDED\dev", parsed.RemoteSessions.ResolveDevelopmentArea());
    }

    [Fact]
    public void WriteThenParse_RestOfConfigSurvives_WithRemoteSessionsEnabled()
    {
        // Bug class: adding [remote_sessions] to the writer corrupts what follows or precedes it --
        // e.g. development_area emitted under the wrong table header, the table written after a host
        // so its keys bind into that host, or [claude_sessions] overwritten. The existing suite checks
        // one renamed display name; this checks every section.
        var original = ConfigParser.Parse(
            RichBody + "\n\n[remote_sessions]\nenabled = true\ndevelopment_area = 'D:\\dev'\n");

        var reparsed = ConfigParser.Parse(HostConfigTomlWriter.Write(original), "round-trip");

        Assert.True(reparsed.RemoteSessions.Enabled);
        Assert.Equal(@"D:\dev", reparsed.RemoteSessions.DevelopmentArea);
        Assert.True(reparsed.ClaudeSessions.Enabled);

        Assert.Equal(5599, reparsed.Global.RcloneRcPort);
        Assert.Equal(7, reparsed.Global.ProbeTimeoutSeconds);
        Assert.Equal(4, reparsed.Global.FailuresBeforeUnmount);
        Assert.Equal([3, 9, 27], reparsed.Global.BackoffSeconds);
        Assert.Equal(45, reparsed.Global.MountedProbeIntervalSeconds);
        Assert.Equal(240, reparsed.Global.MountedDeepProbeIntervalSeconds);
        Assert.Equal(2, reparsed.Global.SuspendUnmountTimeoutSeconds);
        Assert.False(reparsed.Global.StartWithWindows);

        Assert.Equal(["jump", "nas"], reparsed.Hosts.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(original.Hosts["nas"].Mount, reparsed.Hosts["nas"].Mount);
        Assert.Equal(original.Hosts["nas"].Session, reparsed.Hosts["nas"].Session);
        Assert.Equal(original.Hosts["nas"].Probe, reparsed.Hosts["nas"].Probe);
        Assert.Equal(original.Hosts["jump"].Mount, reparsed.Hosts["jump"].Mount);
        Assert.Equal(original.Hosts["jump"].Session, reparsed.Hosts["jump"].Session);
        Assert.Equal(original.Hosts["jump"].Probe, reparsed.Hosts["jump"].Probe);
    }

    // ===================================================================================
    // Binding: any valid TOML spelling of the table; fail-safe on case and type oddities
    // ===================================================================================

    [Theory]
    [InlineData("remote_sessions = { enabled = true, development_area = 'D:\\dev' }")]
    [InlineData("remote_sessions.enabled = true\nremote_sessions.development_area = 'D:\\dev'")]
    [InlineData("[remote_sessions]\nenabled = true\ndevelopment_area = \"D:\\\\dev\"")]
    [InlineData("[remote_sessions]\nenabled = true\ndevelopment_area = '''D:\\dev'''")]
    public void Parse_EquivalentTomlSpellings_BindIdentically(string table)
    {
        // Bug class: binding that depends on the [table] header form (a text pre-pass, or a
        // hand-rolled section reader) rather than the TOML data model. All four are the same TOML.
        var config = ConfigParser.Parse(table + "\n");

        Assert.Equal(new RemoteSessionsConfig { Enabled = true, DevelopmentArea = @"D:\dev" }, config.RemoteSessions);
    }

    [Fact]
    public void Parse_TableDeclaredAfterTheHosts_StillBindsAsTopLevel()
    {
        // Bug class: a parser (or a future writer change) that only recognises [remote_sessions]
        // near the top. Users append new tables at the end of hosts.toml; that must still work and
        // must not leave the hosts disturbed.
        var config = ConfigParser.Parse(RichBody + "\n\n[remote_sessions]\nenabled = true\ndevelopment_area = 'D:\\dev'\n");

        Assert.True(config.RemoteSessions.Enabled);
        Assert.Equal(@"D:\dev", config.RemoteSessions.DevelopmentArea);
        Assert.Equal(2, config.Hosts.Count);
    }

    [Theory]
    [InlineData("[Remote_Sessions]\nenabled = true")]
    [InlineData("[REMOTE_SESSIONS]\nenabled = true")]
    [InlineData("[remote-sessions]\nenabled = true")]
    [InlineData("[remote_sessions]\nEnabled = true")]
    [InlineData("[remote_sessions]\nENABLED = true")]
    public void Parse_MisspeltOrMiscasedNames_LeaveTheFeatureOff(string toml)
    {
        // TOML keys are case-sensitive. This feature opens a way into the machine from outside
        // (ADR-024), so a near-miss spelling must fall to the safe side -- off -- and must not, by
        // case-insensitive binding, turn it on. It must also not trip the "area required" rule,
        // since the feature is not enabled.
        var config = ConfigParser.Parse(toml + "\n");

        Assert.False(config.RemoteSessions.Enabled);
        var result = ConfigValidator.Validate(config, AlwaysExists, directoryExists: _ => false, expandEnvironmentVariables: s => s);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Theory]
    [InlineData("enabled = True")]
    [InlineData("enabled = TRUE")]
    [InlineData("enabled = yes")]
    [InlineData("enabled = on")]
    [InlineData("enabled = 'true'")]
    [InlineData("enabled = 0")]
    [InlineData("enabled = [true]")]
    [InlineData("enabled = { value = true }")]
    public void Parse_EnabledThatIsNotATomlBoolean_IsAParseErrorNotSilentlyTrueOrFalse(string line)
    {
        // Bug class: a lenient converter that maps "True"/"yes"/1 onto bool. The schema says enabled
        // is a boolean; anything else must be rejected loudly so the user is told, rather than the
        // value being guessed in either direction.
        var ex = Assert.Throws<ConfigParseException>(() => ConfigParser.Parse($"[remote_sessions]\n{line}\n"));

        Assert.NotEmpty(ex.Diagnostics);
    }

    [Fact]
    public void Parse_RemoteSessionsAsAScalar_IsAParseError()
    {
        // `remote_sessions = true` is a natural mistake for "turn it on". It must not bind to
        // enabled = true by some coercion, and must not be silently ignored either.
        Assert.Throws<ConfigParseException>(() => ConfigParser.Parse("remote_sessions = true\n"));
    }

    [Fact]
    public void Parse_RemoteSessionsUnderAHost_DoesNotEnableTheTopLevelFeature()
    {
        // The table is top-level only (CONFIG-SCHEMA.md; BosunConfig remarks "Not per host"). A
        // user who nests it under a host must not get the feature on, by some flattening binder.
        var toml = RichBody.Replace(
            "  [hosts.jump.probe]",
            "  [hosts.jump.remote_sessions]\n  enabled = true\n  development_area = 'D:\\dev'\n\n  [hosts.jump.probe]",
            StringComparison.Ordinal);

        BosunConfig? config = null;
        var ex = Record.Exception(() => config = ConfigParser.Parse(toml));

        // Either rejected as unknown, or ignored -- never bound to the top-level table.
        if (ex is null)
        {
            Assert.False(config!.RemoteSessions.Enabled);
            Assert.Null(config.RemoteSessions.DevelopmentArea);
        }
        else
        {
            Assert.IsType<ConfigParseException>(ex);
        }
    }

    // ===================================================================================
    // Validation with the REAL defaults (Directory.Exists, Environment expansion)
    // ===================================================================================

    public static TheoryData<string> GarbageAreas => new()
    {
        "D:\\bad<>|?*name",
        "D:\\nul\0byte",
        "\t\n",
        "::::",
        new string('x', 40_000),
        "%BOSUN_DEFINITELY_UNSET_" + Guid.NewGuid().ToString("N") + "%\\dev",
    };

    [Theory]
    [MemberData(nameof(GarbageAreas))]
    public void Validate_DisabledWithGarbageArea_RealDefaults_IsValidAndDoesNotThrow(string garbage)
    {
        // Spec: "Nothing in [remote_sessions] is checked while enabled is false." The existing test
        // proves the injected check is not called; this proves the *default* path does not normalise
        // or expand the value either (Path.GetFullPath throws on '\0' and on some illegal forms), so
        // a stale value cannot crash startup.
        var config = Config(enabled: false, area: garbage);

        var result = ConfigValidator.Validate(config, AlwaysExists);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Theory]
    [MemberData(nameof(GarbageAreas))]
    public void Validate_EnabledWithGarbageArea_RealDefaults_IsRejectedNotThrown(string garbage)
    {
        // Bug class: an exception escaping the validator. On a reload that would not be a
        // "rejected config, previous retained" -- it would be an unhandled fault on a timer thread.
        // The value must be reported as a validation error instead.
        var config = Config(enabled: true, area: garbage);

        var result = ConfigValidator.Validate(config, AlwaysExists);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            e => e.Rule is "remote-sessions-development-area-not-found" or "remote-sessions-development-area-required");
    }

    [Fact]
    public void Validate_EnabledWithUnsetPercentVar_RealDefaults_IsNotFound()
    {
        // Environment.ExpandEnvironmentVariables leaves an unknown %VAR% in place. The resulting
        // literal path does not exist, so the config must be rejected -- not accepted because the
        // token "looked like" a path, and not reported as "required" because something blanked it.
        var area = $@"%BOSUN_UNSET_{Guid.NewGuid():N}%\Development";

        var result = ConfigValidator.Validate(Config(enabled: true, area: area), AlwaysExists);

        var error = Assert.Single(result.Errors);
        Assert.Equal("remote-sessions-development-area-not-found", error.Rule);
    }

    [Fact]
    public void Validate_EnabledWithPercentVarPointingAtARealTempDirectory_RealDefaults_IsValid()
    {
        // End to end through the real expander and the real Directory.Exists: the value is kept
        // literal in the model, expanded only for the check, and the check passes.
        var dir = CreateTempDirectory();
        var name = SetProcessVariable(dir);
        var config = Config(enabled: true, area: $"%{name}%");

        var result = ConfigValidator.Validate(config, AlwaysExists);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Equal($"%{name}%", config.RemoteSessions.DevelopmentArea);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_ExpansionYieldingBlank_IsRejected(string expansion)
    {
        // A non-blank literal whose expansion is blank leaves no folder at all. Bug class: a
        // validator that treats a blank *expanded* value as "nothing to check" and returns early,
        // so an empty path reaches a consumer as "the development area" (it would resolve to the
        // current directory). Real Directory.Exists; only the expander is injected.
        var config = Config(enabled: true, area: "%EMPTY%");

        var result = ConfigValidator.Validate(
            config,
            AlwaysExists,
            directoryExists: null,
            expandEnvironmentVariables: _ => expansion);

        Assert.False(result.IsValid, "an area that expands to a blank string was accepted");
    }

    [Fact]
    public void Validate_ExpansionGoesThroughTheInjectedExpanderOnly()
    {
        // Bug class: the validator expanding by itself (Environment.ExpandEnvironmentVariables
        // directly) instead of via ResolveDevelopmentArea with the injected expander -- i.e. two
        // expansion points. The variable is set in the real environment to a path that DOES exist,
        // but the injected expander maps it somewhere that does not; only the injected answer may
        // be checked.
        var realDir = CreateTempDirectory();
        var name = SetProcessVariable(realDir);
        var checkedPaths = new List<string>();

        var result = ConfigValidator.Validate(
            Config(enabled: true, area: $"%{name}%"),
            AlwaysExists,
            directoryExists: p => { checkedPaths.Add(p); return Directory.Exists(p); },
            expandEnvironmentVariables: _ => @"Q:\injected\answer");

        Assert.Equal([@"Q:\injected\answer"], checkedPaths);
        Assert.Equal("remote-sessions-development-area-not-found", Assert.Single(result.Errors).Rule);
    }

    [Fact]
    public void Validate_EnabledValidArea_AddsNoErrorToAnOtherwiseValidRichConfig()
    {
        // Guards the other direction: the new rule must not produce false positives against a
        // realistic config (two hosts, claude_sessions on).
        var dir = CreateTempDirectory();
        var config = ConfigParser.Parse(RichBody) with
        {
            RemoteSessions = new RemoteSessionsConfig { Enabled = true, DevelopmentArea = dir },
        };

        var result = ConfigValidator.Validate(config, AlwaysExists);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    // ===================================================================================
    // Reload: an invalid [remote_sessions] rejects the whole config and keeps the previous
    // ===================================================================================

    [Fact]
    public void Reload_EnabledWithoutArea_IsRejected_AndThePreviousConfigIsKeptWhole()
    {
        // CONFIG-SCHEMA.md: "Reject the whole config, keep the previous one, and surface the error."
        // The candidate also changes a host and [global]; none of that may leak in.
        using var harness = StoreHarness.Load(RichBody);
        var before = harness.Store.Current;
        var failures = new List<ConfigReloadFailedEventArgs>();
        var changes = 0;
        harness.Store.ConfigReloadFailed += (_, e) => failures.Add(e);
        harness.Store.ConfigChanged += (_, _) => changes++;

        harness.Reload(
            RichBody.Replace("rclone_rc_port                      = 5599", "rclone_rc_port = 6000", StringComparison.Ordinal)
                    .Replace("display_name  = \"NAS\"", "display_name  = \"NAS changed\"", StringComparison.Ordinal)
            + "\n\n[remote_sessions]\nenabled = true\n");

        Assert.Same(before, harness.Store.Current);
        Assert.Equal(0, changes);
        var failure = Assert.Single(failures);
        Assert.Equal(ConfigReloadFailureReason.ValidationFailed, failure.Reason);
        Assert.Contains(failure.Errors, m => m.Contains("remote_sessions.development_area", StringComparison.Ordinal));
        Assert.False(harness.Store.Current.RemoteSessions.Enabled);
        Assert.Equal(5599, harness.Store.Current.Global.RcloneRcPort);
        Assert.Equal("NAS", harness.Store.Current.Hosts["nas"].DisplayName);
    }

    [Fact]
    public void Reload_EnabledWithMissingDirectory_IsRejected_AndThePreviousConfigIsKept()
    {
        using var harness = StoreHarness.Load(RichBody);
        var before = harness.Store.Current;
        var failures = new List<ConfigReloadFailedEventArgs>();
        harness.Store.ConfigReloadFailed += (_, e) => failures.Add(e);

        harness.Reload(RichBody + $"\n\n[remote_sessions]\nenabled = true\ndevelopment_area = '{MissingDirectory()}'\n");

        Assert.Same(before, harness.Store.Current);
        Assert.Equal(ConfigReloadFailureReason.ValidationFailed, Assert.Single(failures).Reason);
    }

    [Fact]
    public void Reload_EnablingWithAnExistingDirectory_IsAdopted_WithTheAreaStillLiteral()
    {
        // The positive path through the store: a valid enable is adopted, and what the store holds
        // is the literal %VAR% text, not the expansion it checked against.
        var dir = CreateTempDirectory();
        var name = SetProcessVariable(dir);
        using var harness = StoreHarness.Load(RichBody);
        BosunConfig? changed = null;
        harness.Store.ConfigChanged += (_, e) => changed = e.Config;

        harness.Reload(RichBody + $"\n\n[remote_sessions]\nenabled = true\ndevelopment_area = '%{name}%'\n");

        Assert.NotNull(changed);
        Assert.Same(changed, harness.Store.Current);
        Assert.True(harness.Store.Current.RemoteSessions.Enabled);
        Assert.Equal($"%{name}%", harness.Store.Current.RemoteSessions.DevelopmentArea);
    }

    [Fact]
    public void Reload_AfterTheDevelopmentAreaWasDeleted_IsRejected_AndTheEnabledConfigIsKept()
    {
        // The folder existed at load and was later removed or renamed. The next reload (any edit,
        // even an unrelated one) is rejected and the previous -- enabled -- config stays in force.
        // Pinned per the validation list; see the report for the design question this raises.
        var dir = CreateTempDirectory();
        var withArea = RichBody + $"\n\n[remote_sessions]\nenabled = true\ndevelopment_area = '{dir}'\n";
        using var harness = StoreHarness.Load(withArea);
        var before = harness.Store.Current;
        Directory.Delete(dir);
        var failures = new List<ConfigReloadFailedEventArgs>();
        harness.Store.ConfigReloadFailed += (_, e) => failures.Add(e);

        harness.Reload(withArea.Replace("display_name  = \"Jump\"", "display_name  = \"Jump 2\"", StringComparison.Ordinal));

        Assert.Same(before, harness.Store.Current);
        Assert.Equal(ConfigReloadFailureReason.ValidationFailed, Assert.Single(failures).Reason);
    }

    [Fact]
    public void Reload_RejectedRemoteSessions_ThenCorrected_IsAdopted()
    {
        // Bug class: a store that latches after one validation failure. Fixing the file must work.
        var dir = CreateTempDirectory();
        using var harness = StoreHarness.Load(RichBody);
        harness.Reload(RichBody + "\n\n[remote_sessions]\nenabled = true\n");
        Assert.False(harness.Store.Current.RemoteSessions.Enabled);

        harness.Reload(RichBody + $"\n\n[remote_sessions]\nenabled = true\ndevelopment_area = '{dir}'\n");

        Assert.True(harness.Store.Current.RemoteSessions.Enabled);
        Assert.Equal(dir, harness.Store.Current.RemoteSessions.DevelopmentArea);
    }

    [Fact]
    public void Reload_DisablingWithAStalePath_IsAdopted()
    {
        // Turning the feature off must always be possible, even when the configured folder is gone.
        // A validator that checked the area regardless of `enabled` would trap the user: they could
        // neither fix the folder (it is gone) nor switch the feature off.
        var dir = CreateTempDirectory();
        var enabled = RichBody + $"\n\n[remote_sessions]\nenabled = true\ndevelopment_area = '{dir}'\n";
        using var harness = StoreHarness.Load(enabled);
        Directory.Delete(dir);

        harness.Reload(enabled.Replace("enabled = true\ndevelopment_area", "enabled = false\ndevelopment_area", StringComparison.Ordinal));

        Assert.False(harness.Store.Current.RemoteSessions.Enabled);
        Assert.Equal(dir, harness.Store.Current.RemoteSessions.DevelopmentArea);
    }

    [Fact]
    public void InitialLoad_EnabledWithoutArea_IsFatal_NoConfigIsServed()
    {
        // There is no previous config at startup, so the rule makes the first load fail
        // (HostConfigStore.Load: InvalidConfigException). Pinned per the validation list; see the
        // report -- the schema prose ("refuses the tools") reads narrower than this.
        var reader = new FakeConfigFileReader { Content = RichBody + "\n\n[remote_sessions]\nenabled = true\n" };
        var watcher = new FakeConfigFileWatcher();

        var ex = Assert.Throws<InvalidConfigException>(
            () => HostConfigStore.Load("hosts.toml", reader, watcher, new FakeTimeProvider(), AlwaysExists));

        Assert.Contains("remote_sessions.development_area", ex.Message + string.Join(" ", ex.Errors), StringComparison.Ordinal);
        Assert.Equal(1, watcher.DisposeCallCount);
    }

    // ===================================================================================
    // Host edits through HostConfigWriter
    // ===================================================================================

    [Fact]
    public async Task SaveHost_WritesRemoteSessionsToDisk_LiteralAndEnabled()
    {
        // The real write path (validate, serialize, atomic replace) on a temp file -- not just the
        // serializer. A host edit must carry [remote_sessions] through, with %VAR% unexpanded.
        var dir = CreateTempDirectory();
        var name = SetProcessVariable(dir);
        var initial = RichBody + $"\n\n[remote_sessions]\nenabled = true\ndevelopment_area = '%{name}%'\n";
        using var harness = StoreHarness.Load(initial);
        var configPath = Path.Combine(CreateTempDirectory(), "hosts.toml");
        File.WriteAllText(configPath, initial);
        var writer = new HostConfigWriter(configPath, harness.Store, AlwaysExists, new RecordingDelayTimeProvider());

        var result = await writer.SaveHostAsync(harness.Store.Current.Hosts["jump"] with { DisplayName = "Jump edited" });

        Assert.True(result.Succeeded, $"Error='{result.Error}'; [{string.Join("; ", result.ValidationErrors)}]");
        var written = File.ReadAllText(configPath);
        Assert.DoesNotContain(dir, written, StringComparison.OrdinalIgnoreCase);
        var reparsed = ConfigParser.Parse(written, "written");
        Assert.True(reparsed.RemoteSessions.Enabled);
        Assert.Equal($"%{name}%", reparsed.RemoteSessions.DevelopmentArea);
        Assert.Equal("Jump edited", reparsed.Hosts["jump"].DisplayName);
        Assert.Equal(reparsed.RemoteSessions, harness.Store.Current.RemoteSessions);
    }

    [Fact]
    public async Task SaveHost_WhenTheEnabledAreaNoLongerExists_IsRefusedAndWritesNothing()
    {
        // HostConfigWriter's contract: "nothing this method writes can be rejected by the loader on
        // next start". With the area gone, the loader would reject the file, so the writer must
        // refuse too, with the same rule, and leave the file untouched.
        var dir = CreateTempDirectory();
        var initial = RichBody + $"\n\n[remote_sessions]\nenabled = true\ndevelopment_area = '{dir}'\n";
        using var harness = StoreHarness.Load(initial);
        var configPath = Path.Combine(CreateTempDirectory(), "hosts.toml");
        File.WriteAllText(configPath, initial);
        var writer = new HostConfigWriter(configPath, harness.Store, AlwaysExists, new RecordingDelayTimeProvider());
        Directory.Delete(dir);

        var result = await writer.SaveHostAsync(harness.Store.Current.Hosts["jump"] with { DisplayName = "Jump edited" });

        Assert.False(result.Succeeded);
        Assert.Contains(result.ValidationErrors, e => e.Rule == "remote-sessions-development-area-not-found");
        Assert.Equal(initial, File.ReadAllText(configPath));
    }

    // -----------------------------------------------------------------------------------

    private static BosunConfig Config(bool enabled, string? area) => new()
    {
        Global = new GlobalConfig(),
        Hosts = new Dictionary<string, HostConfig>(),
        RemoteSessions = new RemoteSessionsConfig { Enabled = enabled, DevelopmentArea = area },
    };

    private string CreateTempDirectory()
    {
        var dir = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string MissingDirectory() => Path.Combine(_tempRoot, "never-created-" + Guid.NewGuid().ToString("N"));

    private string SetProcessVariable(string value)
    {
        var name = "BOSUN_TEST_RS_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, value);
        _envVarsToClear.Add(name);
        return name;
    }

    private sealed class StoreHarness : IDisposable
    {
        private StoreHarness(HostConfigStore store, FakeConfigFileReader reader, FakeConfigFileWatcher watcher, FakeTimeProvider time, HostConfigStoreOptions options)
        {
            Store = store;
            Reader = reader;
            Watcher = watcher;
            Time = time;
            Options = options;
        }

        public HostConfigStore Store { get; }
        public FakeConfigFileReader Reader { get; }
        public FakeConfigFileWatcher Watcher { get; }
        public FakeTimeProvider Time { get; }
        public HostConfigStoreOptions Options { get; }

        public static StoreHarness Load(string initialText)
        {
            var reader = new FakeConfigFileReader { Content = initialText };
            var watcher = new FakeConfigFileWatcher();
            var time = new FakeTimeProvider();
            var options = new HostConfigStoreOptions();
            var store = HostConfigStore.Load("hosts.toml", reader, watcher, time, AlwaysExists, options);
            return new StoreHarness(store, reader, watcher, time, options);
        }

        public void Reload(string newText)
        {
            Reader.Content = newText;
            Watcher.RaiseChanged();
            Time.Advance(Options.DebounceDelay);
        }

        public void Dispose() => Store.Dispose();
    }
}
