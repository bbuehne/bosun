using Bosun.Configuration;
using Bosun.Hosting;

namespace Bosun.Tests.Configuration;

/// <summary>
/// bs-vqh.2 / ADR-021 Decision 11: the top-level <c>[claude_sessions] enabled</c> switch. Absent
/// (table or field) means <c>false</c>; a wrong type is a parse error reported through the same
/// <see cref="ConfigParseException"/> channel as every other field; and
/// <see cref="HostConfigTomlWriter"/> must round-trip it, because under ADR-019 Bosun is the sole
/// writer of <c>hosts.toml</c> and a host edit that dropped <c>enabled = true</c> would silently
/// turn the feature off. Pure text in, pure text out: no file system, no real hosts.toml.
/// </summary>
public sealed class ClaudeSessionsConfigTests
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

    // -- parsing ------------------------------------------------------------------------------

    [Fact]
    public void Parse_NoClaudeSessionsTable_IsDisabled()
    {
        var config = ConfigParser.Parse(OneHost);

        Assert.False(config.ClaudeSessions.Enabled);
    }

    [Fact]
    public void Parse_EmptyDocument_IsDisabled()
    {
        var config = ConfigParser.Parse(string.Empty);

        Assert.False(config.ClaudeSessions.Enabled);
    }

    [Fact]
    public void Parse_TablePresentButFieldAbsent_IsDisabled()
    {
        var config = ConfigParser.Parse("[claude_sessions]\n\n" + OneHost);

        Assert.False(config.ClaudeSessions.Enabled);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void Parse_ExplicitValue_Binds(string literal, bool expected)
    {
        var config = ConfigParser.Parse($"[claude_sessions]\nenabled = {literal}\n\n{OneHost}");

        Assert.Equal(expected, config.ClaudeSessions.Enabled);
    }

    [Theory]
    [InlineData("\"yes\"")]
    [InlineData("\"true\"")]
    [InlineData("1")]
    public void Parse_WrongType_ThrowsConfigParseException(string literal)
    {
        var ex = Assert.Throws<ConfigParseException>(
            () => ConfigParser.Parse($"[claude_sessions]\nenabled = {literal}\n"));

        Assert.NotEmpty(ex.Diagnostics);
    }

    [Fact]
    public void Parse_ExampleConfig_LeavesClaudeSessionsDisabled()
    {
        // The shipped example documents the table but keeps it commented out.
        var config = ConfigParser.Parse(ReadExampleConfig(), "hosts.example.toml");

        Assert.False(config.ClaudeSessions.Enabled);
    }

    // -- writer round trip --------------------------------------------------------------------

    [Fact]
    public void WriteThenParse_EnabledTrue_Survives()
    {
        var original = ConfigParser.Parse($"[claude_sessions]\nenabled = true\n\n{OneHost}");

        var text = HostConfigTomlWriter.Write(original);
        var roundTripped = ConfigParser.Parse(text, "round-tripped");

        Assert.True(roundTripped.ClaudeSessions.Enabled);
        Assert.Contains("[claude_sessions]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteThenParse_ExplicitFalse_IsKeptInTheText()
    {
        var original = ConfigParser.Parse($"[claude_sessions]\nenabled = false\n\n{OneHost}");

        var text = HostConfigTomlWriter.Write(original);
        var roundTripped = ConfigParser.Parse(text, "round-tripped");

        Assert.False(roundTripped.ClaudeSessions.Enabled);
        // Preserving explicit user intent: the table and its explicit false are still written.
        Assert.Contains("[claude_sessions]\nenabled = false\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteThenParse_AbsentTable_StaysDisabled()
    {
        var original = ConfigParser.Parse(OneHost);

        var roundTripped = ConfigParser.Parse(HostConfigTomlWriter.Write(original), "round-tripped");

        Assert.False(roundTripped.ClaudeSessions.Enabled);
    }

    [Fact]
    public void SimulatedHostEdit_PreservesEnabledTrue()
    {
        // What HostConfigWriter does on a host edit: take the current model, replace one host,
        // serialize the whole thing, and the result must still turn the feature on.
        var current = ConfigParser.Parse($"[claude_sessions]\nenabled = true\n\n{OneHost}");
        var edited = current with
        {
            Hosts = new Dictionary<string, HostConfig>
            {
                ["jump"] = current.Hosts["jump"] with { DisplayName = "Renamed Jump" },
            },
        };

        var reparsed = ConfigParser.Parse(HostConfigTomlWriter.Write(edited), "after-edit");

        Assert.Equal("Renamed Jump", reparsed.Hosts["jump"].DisplayName);
        Assert.True(reparsed.ClaudeSessions.Enabled);
    }

    // -- first-run template -------------------------------------------------------------------

    [Fact]
    public void FirstRunTemplate_ParsesToDisabledClaudeSessions_AndDocumentsTheSwitch()
    {
        var text = FirstRunConfigTemplate.Content;

        var parsed = ConfigParser.Parse(text, "first-run-template");

        Assert.False(parsed.ClaudeSessions.Enabled);
        Assert.Contains("# [claude_sessions]", text, StringComparison.Ordinal);
        Assert.Contains("# enabled = false", text, StringComparison.Ordinal);
        Assert.Contains("ADR-021", text, StringComparison.Ordinal);
    }

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
