using Bosun.Configuration;
using Bosun.Hosting;

namespace Bosun.Tests.Configuration;

/// <summary>
/// bs-4xw.6 / ADR-024: the top-level <c>[remote_sessions]</c> table. Binding defaults, the
/// "required when enabled" validation (existence is deliberately not checked, bs-4xw.16), literal-until-expanded <c>%VAR%</c> handling,
/// and the <see cref="HostConfigTomlWriter"/> round trip (ADR-019: Bosun is the sole writer, so a
/// host edit must never drop <c>enabled = true</c> or the area). Pure text in, pure text out; the
/// environment expander is injected, so no test depends on the real environment, and validation
/// itself performs no file-system access for this table. The one test that makes a real directory uses a unique temp
/// directory it deletes again.
/// </summary>
public sealed class RemoteSessionsConfigTests
{
    private const string OneHost = """
        [hosts.jump]
        display_name  = "Jump"
        hostname      = "jump.example.internal"
        port          = 22
        user          = "someuser"
        identity_file = "~/.ssh/id_ed25519"

          [hosts.jump.mount]
          mode = "none"

          [hosts.jump.session]
          autostart    = false
          reconnect    = true
          tmux         = false
          tab_color    = "#3A4A6B"
          color_scheme = "Campbell"

          [hosts.jump.probe]
          interval_seconds = 0
          deep_probe       = false
        """;

    private const string FakeProfile = @"C:\FakeProfile";

    // Fake expander: replaces only %USERPROFILE%, never reads the real environment.
    private static string FakeExpand(string s) =>
        s.Replace("%USERPROFILE%", FakeProfile, StringComparison.Ordinal);

    // -- binding ------------------------------------------------------------------------------

    [Fact]
    public void Parse_NoRemoteSessionsTable_IsDisabledWithNoArea()
    {
        var config = ConfigParser.Parse(OneHost);

        Assert.False(config.RemoteSessions.Enabled);
        Assert.Null(config.RemoteSessions.DevelopmentArea);
    }

    [Fact]
    public void Parse_EmptyDocument_IsDisabledWithNoArea()
    {
        var config = ConfigParser.Parse(string.Empty);

        Assert.False(config.RemoteSessions.Enabled);
        Assert.Null(config.RemoteSessions.DevelopmentArea);
    }

    [Fact]
    public void Parse_TablePresentButEmpty_IsDisabledWithNoArea()
    {
        var config = ConfigParser.Parse("[remote_sessions]\n\n" + OneHost);

        Assert.False(config.RemoteSessions.Enabled);
        Assert.Null(config.RemoteSessions.DevelopmentArea);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void Parse_ExplicitEnabled_Binds(string literal, bool expected)
    {
        var config = ConfigParser.Parse($"[remote_sessions]\nenabled = {literal}\n\n{OneHost}");

        Assert.Equal(expected, config.RemoteSessions.Enabled);
    }

    [Fact]
    public void Parse_DevelopmentArea_BindsLiterally_PercentVarUnexpanded()
    {
        var config = ConfigParser.Parse(
            "[remote_sessions]\nenabled = true\ndevelopment_area = \"%USERPROFILE%\\\\Development\"\n");

        Assert.Equal(@"%USERPROFILE%\Development", config.RemoteSessions.DevelopmentArea);
    }

    [Fact]
    public void Parse_DevelopmentAreaWithoutEnabled_BindsTheAreaButStaysDisabled()
    {
        var config = ConfigParser.Parse("[remote_sessions]\ndevelopment_area = 'D:\\dev'\n");

        Assert.False(config.RemoteSessions.Enabled);
        Assert.Equal(@"D:\dev", config.RemoteSessions.DevelopmentArea);
    }

    [Fact]
    public void Parse_UnknownKeyInTable_IsIgnoredLikeEveryOtherTable()
    {
        // Same as [claude_sessions] and [global]: Tomlyn ignores keys the schema does not name, and
        // the parser does not add an unknown-key check of its own. Pinned so a change is deliberate.
        var config = ConfigParser.Parse(
            "[remote_sessions]\nenabled = true\ndevelopment_area = 'D:\\dev'\ndevelopmnt_area = 'oops'\n");

        Assert.True(config.RemoteSessions.Enabled);
        Assert.Equal(@"D:\dev", config.RemoteSessions.DevelopmentArea);
    }

    [Theory]
    [InlineData("enabled = \"yes\"")]
    [InlineData("enabled = \"true\"")]
    [InlineData("enabled = 1")]
    [InlineData("development_area = 5")]
    [InlineData("development_area = true")]
    [InlineData("development_area = [\"D:\\\\dev\"]")]
    public void Parse_WrongType_ThrowsConfigParseException(string line)
    {
        var ex = Assert.Throws<ConfigParseException>(
            () => ConfigParser.Parse($"[remote_sessions]\n{line}\n"));

        Assert.NotEmpty(ex.Diagnostics);
    }

    [Fact]
    public void Parse_ExampleConfig_LeavesRemoteSessionsDisabled_AndDocumentsTheTable()
    {
        var text = ReadExampleConfig();

        var config = ConfigParser.Parse(text, "hosts.example.toml");

        Assert.False(config.RemoteSessions.Enabled);
        Assert.Contains("# [remote_sessions]", text, StringComparison.Ordinal);
        Assert.Contains("# development_area = \"%USERPROFILE%", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FirstRunTemplate_ParsesToDisabledRemoteSessions_AndDocumentsTheTable()
    {
        var text = FirstRunConfigTemplate.Content;

        var parsed = ConfigParser.Parse(text, "first-run-template");

        Assert.False(parsed.RemoteSessions.Enabled);
        Assert.Contains("# [remote_sessions]", text, StringComparison.Ordinal);
        Assert.Contains("ADR-024", text, StringComparison.Ordinal);
    }

    // -- expansion ----------------------------------------------------------------------------

    [Fact]
    public void ResolveDevelopmentArea_ExpandsThroughTheInjectedExpander_AndLeavesTheModelLiteral()
    {
        var config = new RemoteSessionsConfig { Enabled = true, DevelopmentArea = @"%USERPROFILE%\Development" };

        var resolved = config.ResolveDevelopmentArea(FakeExpand);

        Assert.Equal(@"C:\FakeProfile\Development", resolved);
        Assert.Equal(@"%USERPROFILE%\Development", config.DevelopmentArea);
    }

    [Fact]
    public void ResolveDevelopmentArea_PassesTheLiteralValueToTheExpander()
    {
        string? seen = null;
        var config = new RemoteSessionsConfig { DevelopmentArea = @"%FOO%\x" };

        config.ResolveDevelopmentArea(s => { seen = s; return s; });

        Assert.Equal(@"%FOO%\x", seen);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveDevelopmentArea_NullOrBlank_IsNullAndNeverCallsTheExpander(string? value)
    {
        var config = new RemoteSessionsConfig { DevelopmentArea = value };

        var resolved = config.ResolveDevelopmentArea(_ => throw new InvalidOperationException("must not expand"));

        Assert.Null(resolved);
    }

    [Fact]
    public void ResolveDevelopmentArea_DefaultExpander_UsesTheProcessEnvironment()
    {
        // Proves the default really is Environment.ExpandEnvironmentVariables, against a variable
        // that is set only for this process and only here.
        var name = "BOSUN_TEST_REMOTE_AREA_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, @"C:\FromEnv");
        try
        {
            var config = new RemoteSessionsConfig { DevelopmentArea = $@"%{name}%\dev" };

            Assert.Equal(@"C:\FromEnv\dev", config.ResolveDevelopmentArea());
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("%BOSUN_TEST_BLANK_EXPANSION%")]
    [InlineData("%BOSUN_TEST_BLANK_EXPANSION%  ")]
    public void ResolveDevelopmentArea_NonBlankLiteralThatExpandsToBlank_IsNull(string literal)
    {
        // An unset variable can expand to a blank string; a consumer must never be handed one.
        var config = new RemoteSessionsConfig { DevelopmentArea = literal };

        var resolved = config.ResolveDevelopmentArea(
            s => s.Replace("%BOSUN_TEST_BLANK_EXPANSION%", string.Empty, StringComparison.Ordinal));

        Assert.Null(resolved);
    }

    [Fact]
    public void ResolveDevelopmentArea_DoesNotCheckThatTheFolderExists()
    {
        var config = new RemoteSessionsConfig { Enabled = true, DevelopmentArea = @"Z:\does\not\exist" };

        Assert.Equal(@"Z:\does\not\exist", config.ResolveDevelopmentArea(s => s));
    }

    // -- validation ---------------------------------------------------------------------------

    [Fact]
    public void Validate_Disabled_IsValid_EvenWhenTheAreaIsMissingOrStale()
    {
        var config = ConfigParser.Parse(
            "[remote_sessions]\nenabled = false\ndevelopment_area = 'Z:\\does\\not\\exist'\n\n" + OneHost);

        var result = Validate(config);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void Validate_TableAbsent_IsValid()
    {
        var result = Validate(ConfigParser.Parse(OneHost));

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void Validate_EnabledWithoutDevelopmentArea_IsRejected_NoDefaultFolderIsGuessed()
    {
        var config = ConfigParser.Parse("[remote_sessions]\nenabled = true\n\n" + OneHost);

        var result = Validate(config);

        var error = Assert.Single(result.Errors);
        Assert.Equal("remote-sessions-development-area-required", error.Rule);
        Assert.Contains("remote_sessions.development_area", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_EnabledWithBlankDevelopmentArea_IsRejectedAsMissing(string blank)
    {
        var config = ConfigParser.Parse(
            $"[remote_sessions]\nenabled = true\ndevelopment_area = \"{blank}\"\n\n{OneHost}");

        var result = Validate(config);

        Assert.Equal("remote-sessions-development-area-required", Assert.Single(result.Errors).Rule);
    }

    // bs-4xw.16: a development folder that does not exist must not reject the config. Bosun rejects a
    // whole config on any validation error, so an existence rule would stop startup, every reload and
    // every host save when the folder is renamed or deleted. The remote tools check it when they run.

    [Fact]
    public void Validate_EnabledWithNonexistentDirectory_IsValid()
    {
        var missing = Path.Combine(Path.GetTempPath(), "bosun-test-missing-" + Guid.NewGuid().ToString("N"));

        var result = ConfigValidator.Validate(ConfigWithArea(missing));

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void Validate_EnabledWithExistingTempDirectory_IsValid()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bosun-test-area-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var result = ConfigValidator.Validate(ConfigWithArea(dir));

            Assert.True(result.IsValid, string.Join("; ", result.Errors));
        }
        finally
        {
            Directory.Delete(dir);
        }
    }

    [Fact]
    public void Validate_ExistingFileInsteadOfDirectory_IsValid()
    {
        var file = Path.Combine(Path.GetTempPath(), "bosun-test-file-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(file, string.Empty);
        try
        {
            var result = ConfigValidator.Validate(ConfigWithArea(file));

            Assert.True(result.IsValid, string.Join("; ", result.Errors));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Validate_EnabledWithUnsetPercentVar_IsValid()
    {
        // The validator reads the literal and does not expand it, so a variable that is unset (or
        // expands to nothing) is not a config error either.
        var name = "BOSUN_TEST_UNSET_" + Guid.NewGuid().ToString("N");

        var result = ConfigValidator.Validate(ConfigWithArea($@"%{name}%\Development"));

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void Validate_PercentVarInTheArea_IsValid()
    {
        var config = ConfigParser.Parse(
            "[remote_sessions]\nenabled = true\ndevelopment_area = \"%USERPROFILE%\\\\Development\"\n\n" + OneHost);

        var result = Validate(config);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Theory]
    [InlineData(@"Z:\does\not\exist")]
    [InlineData(@"\\no-such-server.invalid\share\dev")]
    [InlineData("C:\\bad<>|?*\"chars")]
    [InlineData("has\0nul")]
    [InlineData("%%%")]
    [InlineData("%UNCLOSED")]
    [InlineData("   padded   ")]
    public void Validate_EnabledWithPathsThatCannotExistOrAreGarbage_IsValidAndDoesNotThrow(string area)
    {
        // Validation reads only the literal text: it neither touches the file system nor expands the
        // value, so nothing about the path's contents can make it throw or reject.
        var result = ConfigValidator.Validate(ConfigWithArea(area));

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void Validate_RemoteSessionsErrorIsReportedAlongsideOtherErrors()
    {
        // docs/CONFIG-SCHEMA.md: a config violating two rules reports both.
        var config = ConfigParser.Parse(
            "[global]\nfailures_before_unmount = 0\n\n[remote_sessions]\nenabled = true\n\n" + OneHost);

        var result = Validate(config);

        Assert.Contains(result.Errors, e => e.Rule == "invalid-failures-before-unmount");
        Assert.Contains(result.Errors, e => e.Rule == "remote-sessions-development-area-required");
    }

    // -- writer round trip / reload carry-through ---------------------------------------------

    [Fact]
    public void WriteThenParse_EnabledWithArea_SurvivesWithThePercentVarStillLiteral()
    {
        var original = ConfigParser.Parse(
            "[remote_sessions]\nenabled = true\ndevelopment_area = \"%USERPROFILE%\\\\Development\"\n\n" + OneHost);

        var text = HostConfigTomlWriter.Write(original);
        var roundTripped = ConfigParser.Parse(text, "round-tripped");

        Assert.True(roundTripped.RemoteSessions.Enabled);
        Assert.Equal(@"%USERPROFILE%\Development", roundTripped.RemoteSessions.DevelopmentArea);
        Assert.Equal(original.RemoteSessions, roundTripped.RemoteSessions);
    }

    [Fact]
    public void WriteThenParse_AbsentTable_StaysDisabledWithNoArea()
    {
        var original = ConfigParser.Parse(OneHost);

        var text = HostConfigTomlWriter.Write(original);
        var roundTripped = ConfigParser.Parse(text, "round-tripped");

        Assert.False(roundTripped.RemoteSessions.Enabled);
        Assert.Null(roundTripped.RemoteSessions.DevelopmentArea);
        Assert.DoesNotContain("development_area", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteThenParse_DisabledWithArea_KeepsTheAreaSoReEnablingNeedsNoRetyping()
    {
        var original = ConfigParser.Parse(
            "[remote_sessions]\nenabled = false\ndevelopment_area = 'D:\\dev'\n\n" + OneHost);

        var roundTripped = ConfigParser.Parse(HostConfigTomlWriter.Write(original), "round-tripped");

        Assert.False(roundTripped.RemoteSessions.Enabled);
        Assert.Equal(@"D:\dev", roundTripped.RemoteSessions.DevelopmentArea);
    }

    [Fact]
    public void SimulatedHostEdit_PreservesRemoteSessions_AndClaudeSessionsIsIndependent()
    {
        // What HostConfigWriter does on a host edit (and what a reload then re-reads): replace one
        // host in the current model and serialize the whole thing. [remote_sessions] must ride along,
        // and must not disturb [claude_sessions].
        var current = ConfigParser.Parse(
            "[claude_sessions]\nenabled = false\n\n[remote_sessions]\nenabled = true\ndevelopment_area = 'D:\\dev'\n\n" + OneHost);
        var edited = current with
        {
            Hosts = new Dictionary<string, HostConfig>
            {
                ["jump"] = current.Hosts["jump"] with { DisplayName = "Renamed Jump" },
            },
        };

        var reparsed = ConfigParser.Parse(HostConfigTomlWriter.Write(edited), "after-edit");

        Assert.Equal("Renamed Jump", reparsed.Hosts["jump"].DisplayName);
        Assert.True(reparsed.RemoteSessions.Enabled);
        Assert.Equal(@"D:\dev", reparsed.RemoteSessions.DevelopmentArea);
        Assert.False(reparsed.ClaudeSessions.Enabled);
    }

    // -----------------------------------------------------------------------------------------

    private static ConfigValidationResult Validate(BosunConfig config) =>
        ConfigValidator.Validate(config, identityFileExists: _ => true);

    private static BosunConfig ConfigWithArea(string area) => new()
    {
        Global = new GlobalConfig(),
        Hosts = new Dictionary<string, HostConfig>(),
        RemoteSessions = new RemoteSessionsConfig { Enabled = true, DevelopmentArea = area },
    };

    private static string ReadExampleConfig()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "config", "hosts.example.toml")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir.FullName, "config", "hosts.example.toml"));
    }
}
