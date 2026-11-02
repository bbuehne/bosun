using Bosun.Configuration;
using Bosun.Terminal;
using Bosun.UI.HostEditor;
using System.Text.Json;

namespace Bosun.Tests.Configuration;

/// <summary>
/// bs-aoq (maintainer decision 2026-11-01): every <c>[session]</c> field is optional, the whole
/// table is optional, and absent colours mean Terminal's own default. <c>[probe]</c>
/// <c>interval_seconds</c> and <c>deep_probe</c> stay required. Pure text in, pure text out -- no
/// file system, no real hosts.toml, no real fragment path.
/// </summary>
public sealed class SessionOptionalityTests
{
    private static readonly Func<string, bool> AlwaysExists = _ => true;

    // -- parsing ------------------------------------------------------------------------------

    [Fact]
    public void Parse_MinimalHostWithoutSessionTable_BindsWithAllDefaults()
    {
        var host = ConfigParser.Parse(Host(session: null)).Hosts["jump"];

        Assert.False(host.Session.Autostart);
        Assert.True(host.Session.Reconnect);
        Assert.False(host.Session.Tmux);
        Assert.Null(host.Session.TmuxSession);
        Assert.Null(host.Session.TabColor);
        Assert.Null(host.Session.ColorScheme);
    }

    [Fact]
    public void Parse_EmptySessionTable_BindsTheSameAsAbsent()
    {
        var absent = ConfigParser.Parse(Host(session: null)).Hosts["jump"].Session;
        var empty = ConfigParser.Parse(Host(session: "  [hosts.jump.session]\n")).Hosts["jump"].Session;

        Assert.Equal(absent, empty);
    }

    [Fact]
    public void Parse_MinimalHostWithoutSession_PassesValidation()
    {
        var config = ConfigParser.Parse(Host(session: null));

        Assert.Empty(ConfigValidator.Validate(config, AlwaysExists).Errors);
    }

    [Fact]
    public void Parse_SessionFieldsOmittedOneAtATime_EachFallsBackToItsOwnDefaultAndTheRestSurvive()
    {
        // A full, deliberately non-default session. Removing any one line must change only that
        // field. Catches a field that is still required, and catches a default that leaks onto a
        // neighbour.
        var lines = new Dictionary<string, string>
        {
            ["autostart"] = "autostart = true",
            ["reconnect"] = "reconnect = false",
            ["tmux"] = "tmux = true",
            ["tmux_session"] = "tmux_session = \"work\"",
            ["tab_color"] = "tab_color = \"#112233\"",
            ["color_scheme"] = "color_scheme = \"One Half Dark\"",
        };

        foreach (var omitted in lines.Keys)
        {
            var body = string.Join('\n', lines.Where(kv => kv.Key != omitted).Select(kv => "    " + kv.Value));
            var toml = Host(session: $"  [hosts.jump.session]\n{body}\n");

            var session = ConfigParser.Parse(toml).Hosts["jump"].Session;

            Assert.Equal(omitted == "autostart" ? SessionConfig.DefaultAutostart : true, session.Autostart);
            Assert.Equal(omitted == "reconnect" ? SessionConfig.DefaultReconnect : false, session.Reconnect);
            Assert.Equal(omitted == "tmux" ? SessionConfig.DefaultTmux : true, session.Tmux);
            Assert.Equal(omitted == "tmux_session" ? null : "work", session.TmuxSession);
            Assert.Equal(omitted == "tab_color" ? null : "#112233", session.TabColor);
            Assert.Equal(omitted == "color_scheme" ? null : "One Half Dark", session.ColorScheme);
        }
    }

    [Fact]
    public void Parse_BlankColours_AreTreatedAsAbsent()
    {
        var toml = Host(session: "  [hosts.jump.session]\n    tab_color = \"\"\n    color_scheme = \"  \"\n");

        var session = ConfigParser.Parse(toml).Hosts["jump"].Session;

        Assert.Null(session.TabColor);
        Assert.Null(session.ColorScheme);
    }

    [Fact]
    public void Validate_TmuxTrueWithNoTmuxSession_StillAnError()
    {
        // The existing tmux rule survives the change: tmux = true needs a session name, and an
        // omitted tmux_session is the case a minimal [session] table makes easy to hit.
        var toml = Host(session: "  [hosts.jump.session]\n    tmux = true\n");

        var config = ConfigParser.Parse(toml);

        var error = Assert.Single(ConfigValidator.Validate(config, AlwaysExists).Errors);
        Assert.Equal("tmux-requires-session", error.Rule);
    }

    // -- [probe] stays required ---------------------------------------------------------------

    [Theory]
    [InlineData("interval_seconds", "hosts.jump.probe.interval_seconds")]
    [InlineData("deep_probe", "hosts.jump.probe.deep_probe")]
    public void Parse_MissingProbeField_FailsNamingTheFieldPath(string omitted, string expectedPath)
    {
        var fields = new Dictionary<string, string>
        {
            ["interval_seconds"] = "interval_seconds = 60",
            ["deep_probe"] = "deep_probe = true",
        };
        var body = string.Join('\n', fields.Where(kv => kv.Key != omitted).Select(kv => "    " + kv.Value));
        var toml = Host(session: null, probe: $"  [hosts.jump.probe]\n{body}\n");

        var ex = Assert.Throws<ConfigParseException>(() => ConfigParser.Parse(toml));

        Assert.Contains(expectedPath, ex.Message);
        Assert.Contains("required", ex.Message);
    }

    [Fact]
    public void Parse_MissingProbeTable_FailsNamingTheTable()
    {
        var toml = Host(session: null, probe: string.Empty);

        var ex = Assert.Throws<ConfigParseException>(() => ConfigParser.Parse(toml));

        Assert.Contains("hosts.jump.probe", ex.Message);
        Assert.Contains("required", ex.Message);
    }

    // -- fragment -----------------------------------------------------------------------------

    [Fact]
    public void Fragment_ForAHostWithNoColours_OmitsBothKeysRatherThanWritingEmptyStrings()
    {
        var host = ConfigParser.Parse(Host(session: null)).Hosts["jump"];

        var json = FragmentSerializer.Serialize(FragmentProfileGenerator.CreateDocument([host]));

        using var parsed = JsonDocument.Parse(json);
        var profile = parsed.RootElement.GetProperty("profiles")[0];
        Assert.False(profile.TryGetProperty("tabColor", out _));
        Assert.False(profile.TryGetProperty("colorScheme", out _));
        Assert.True(profile.TryGetProperty("commandline", out _));
    }

    [Fact]
    public void Fragment_ForAHostWithOnlyOneColour_EmitsThatColourOnly()
    {
        var host = ConfigParser.Parse(Host(session: "  [hosts.jump.session]\n    tab_color = \"#112233\"\n")).Hosts["jump"];

        var json = FragmentSerializer.Serialize(FragmentProfileGenerator.CreateDocument([host]));

        using var parsed = JsonDocument.Parse(json);
        var profile = parsed.RootElement.GetProperty("profiles")[0];
        Assert.Equal("#112233", profile.GetProperty("tabColor").GetString());
        Assert.False(profile.TryGetProperty("colorScheme", out _));
    }

    [Fact]
    public void Fragment_BlankColourInAHandBuiltHost_IsStillOmitted()
    {
        // Defence in depth: a HostConfig built in code (not via ConfigParser) with an empty string.
        var host = ConfigParser.Parse(Host(session: null)).Hosts["jump"] with
        {
            Session = new SessionConfig { TabColor = "", ColorScheme = " " },
        };

        var profile = FragmentProfileGenerator.CreateProfile(host);

        Assert.Null(profile.TabColor);
        Assert.Null(profile.ColorScheme);
    }

    // -- host editor --------------------------------------------------------------------------

    [Fact]
    public void Editor_FormForAHostWithNoColours_HasBlankStringsNotNull()
    {
        var host = ConfigParser.Parse(Host(session: null)).Hosts["jump"];

        var form = HostEditorController.CreateEditForm(host);

        Assert.Equal(string.Empty, form.TabColor);
        Assert.Equal(string.Empty, form.ColorScheme);
    }

    [Fact]
    public void Editor_BlankColourFields_BuildToNullColours()
    {
        var host = ConfigParser.Parse(Host(session: null)).Hosts["jump"];
        var form = HostEditorController.CreateEditForm(host);
        form.TabColor = "  ";
        form.ColorScheme = string.Empty;

        var result = HostEditorController.Build(form);

        Assert.True(result.Succeeded);
        Assert.Null(result.Host!.Session.TabColor);
        Assert.Null(result.Host.Session.ColorScheme);
    }

    [Fact]
    public void Editor_FilledColourFields_AreTrimmedAndKept()
    {
        var host = ConfigParser.Parse(Host(session: null)).Hosts["jump"];
        var form = HostEditorController.CreateEditForm(host);
        form.TabColor = " #ABCDEF ";
        form.ColorScheme = " Campbell ";

        var result = HostEditorController.Build(form);

        Assert.True(result.Succeeded);
        Assert.Equal("#ABCDEF", result.Host!.Session.TabColor);
        Assert.Equal("Campbell", result.Host.Session.ColorScheme);
    }

    // -- writer -------------------------------------------------------------------------------

    [Fact]
    public void Writer_MinimalHostWithoutSession_StaysMinimal()
    {
        var config = ConfigParser.Parse(Host(session: null));

        var text = HostConfigTomlWriter.Write(config);

        Assert.DoesNotContain(".session]", text);
        Assert.DoesNotContain("tab_color", text);
        Assert.DoesNotContain("color_scheme", text);
        AssertSessionRoundTrips(config);
    }

    [Fact]
    public void Writer_FullHost_RoundTripsAndWritesEverythingThatDiffersFromDefault()
    {
        var config = ConfigParser.Parse(Host(session: """
              [hosts.jump.session]
              autostart    = true
              reconnect    = false
              tmux         = true
              tmux_session = "work"
              tab_color    = "#112233"
              color_scheme = "One Half Dark"
            """));

        var text = HostConfigTomlWriter.Write(config);

        Assert.Contains("autostart = true", text);
        Assert.Contains("reconnect = false", text);
        Assert.Contains("tmux = true", text);
        Assert.Contains("tmux_session = \"work\"", text);
        Assert.Contains("tab_color = \"#112233\"", text);
        Assert.Contains("color_scheme = \"One Half Dark\"", text);
        AssertSessionRoundTrips(config);
    }

    [Fact]
    public void Writer_ExplicitDefaultValues_RoundTripToTheSameModel()
    {
        // Spelled-out defaults and an absent table are the same model; the writer may drop the
        // spelled-out form, but the value must come back identical.
        var config = ConfigParser.Parse(Host(session: """
              [hosts.jump.session]
              autostart = false
              reconnect = true
              tmux      = false
            """));

        AssertSessionRoundTrips(config);
        Assert.Equal(ConfigParser.Parse(Host(session: null)).Hosts["jump"].Session, config.Hosts["jump"].Session);
    }

    [Theory]
    [InlineData("autostart = true", "autostart = true")]
    [InlineData("reconnect = false", "reconnect = false")]
    [InlineData("tmux_session = \"leftover\"", "tmux_session = \"leftover\"")]
    [InlineData("tab_color = \"#112233\"", "tab_color = \"#112233\"")]
    [InlineData("color_scheme = \"Campbell\"", "color_scheme = \"Campbell\"")]
    public void Writer_AnySingleNonDefaultField_IsWrittenAndSurvives(string input, string expectedLine)
    {
        var config = ConfigParser.Parse(Host(session: $"  [hosts.jump.session]\n    {input}\n"));

        var text = HostConfigTomlWriter.Write(config);

        Assert.Contains(expectedLine, text);
        AssertSessionRoundTrips(config);
    }

    [Fact]
    public void Writer_ProbeFieldsAreAlwaysWritten_EvenWhenSessionIsOmitted()
    {
        var config = ConfigParser.Parse(Host(session: null));

        var reparsed = ConfigParser.Parse(HostConfigTomlWriter.Write(config));

        Assert.Equal(60, reparsed.Hosts["jump"].Probe.IntervalSeconds);
        Assert.True(reparsed.Hosts["jump"].Probe.DeepProbe);
    }

    private static void AssertSessionRoundTrips(BosunConfig config)
    {
        var reparsed = ConfigParser.Parse(HostConfigTomlWriter.Write(config), "round-trip");

        Assert.Equal(config.Hosts["jump"].Session, reparsed.Hosts["jump"].Session);
        Assert.Empty(ConfigValidator.Validate(reparsed, AlwaysExists).Errors);
    }

    // -- fixture ------------------------------------------------------------------------------

    private static string Host(string? session, string? probe = null) => $"""
        [hosts.jump]
        display_name  = "Jump"
        hostname      = "jump.example.internal"
        port          = 22
        user          = "someuser"
        identity_file = "~/.ssh/id_ed25519"

          [hosts.jump.mount]
          mode = "none"

        {session}
        {probe ?? "  [hosts.jump.probe]\n  interval_seconds = 60\n  deep_probe = true\n"}
        """;
}
