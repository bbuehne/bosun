using Bosun.Configuration;
using Bosun.Terminal;
using Bosun.Tests.Terminal.Support;

namespace Bosun.Tests.Terminal;

/// <summary>Covers bs-i0q: config -> profile mapping, both commandline forms, the reconnect
/// wrapper, mode="none" hosts still getting a profile, and the GUID identity contract
/// (ADR-013: derived from the config key, not display_name).</summary>
public sealed class FragmentProfileGeneratorTests
{
    [Fact]
    public void CreateProfile_non_tmux_host_gets_the_plain_ssh_form()
    {
        var host = TerminalHostFixtures.Host("example-nas", tmux: false);

        var profile = FragmentProfileGenerator.CreateProfile(host);

        Assert.Equal(
            "ssh -i \"~/.ssh/id_ed25519\" -p 22 someuser@example-nas.example.internal",
            profile.CommandLine);
    }

    [Fact]
    public void CreateProfile_tmux_host_gets_the_dash_t_tmux_new_form()
    {
        var host = TerminalHostFixtures.Host("example-nas", tmux: true, tmuxSession: "main");

        var profile = FragmentProfileGenerator.CreateProfile(host);

        Assert.Equal(
            "ssh -t -i \"~/.ssh/id_ed25519\" -p 22 someuser@example-nas.example.internal tmux new -A -s main",
            profile.CommandLine);
    }

    [Fact]
    public void CreateProfile_connects_by_hostname_never_by_the_config_key_or_display_name()
    {
        // bs-dkm / ADR-013 Amendment 1. The key is Bosun's identity, not a network name: handing
        // it to ssh made it a DNS lookup that only succeeded if the user had hand-written a
        // matching `Host <key>` block in ~/.ssh/config. A key deliberately unrelated to the
        // hostname here, so "contains the key" cannot pass by substring accident.
        var host = TerminalHostFixtures.Host("example-nas", displayName: "My Fancy NAS") with
        {
            Hostname = "nas.internal.example.com",
        };

        var profile = FragmentProfileGenerator.CreateProfile(host);

        Assert.Contains("someuser@nas.internal.example.com", profile.CommandLine, StringComparison.Ordinal);
        Assert.DoesNotContain("example-nas", profile.CommandLine, StringComparison.Ordinal);
        Assert.DoesNotContain("My Fancy NAS", profile.CommandLine, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateProfile_carries_the_port_and_identity_file_the_user_configured()
    {
        var host = TerminalHostFixtures.Host("example-nas") with
        {
            Port = 2222,
            IdentityFile = @"C:\keys\nas_ed25519",
        };

        var profile = FragmentProfileGenerator.CreateProfile(host);

        Assert.Contains("-p 2222", profile.CommandLine, StringComparison.Ordinal);
        Assert.Contains(@"-i ""C:\keys\nas_ed25519""", profile.CommandLine, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateProfile_omits_dash_i_entirely_when_no_identity_file_is_set()
    {
        // A bare "-i " would make ssh consume the NEXT token as the key path -- here "-p" --
        // silently changing what it connects to. Omitting the flag is the only safe handling.
        var host = TerminalHostFixtures.Host("example-nas") with { IdentityFile = "   " };

        var invocation = FragmentProfileGenerator.BuildSshInvocation(host);

        Assert.DoesNotContain("-i", invocation, StringComparison.Ordinal);
        Assert.Equal("ssh -p 22 someuser@example-nas.example.internal", invocation);
    }

    [Fact]
    public void CreateProfile_uses_display_name_for_the_visible_name_field()
    {
        var host = TerminalHostFixtures.Host("example-nas", displayName: "My Fancy NAS");

        var profile = FragmentProfileGenerator.CreateProfile(host);

        Assert.Equal("My Fancy NAS", profile.Name);
    }

    [Fact]
    public void CreateProfile_guid_is_derived_from_the_config_key_not_display_name()
    {
        var withOneDisplayName = TerminalHostFixtures.Host("example-nas", displayName: "Alpha");
        var withAnotherDisplayName = TerminalHostFixtures.Host("example-nas", displayName: "Beta");

        var profileA = FragmentProfileGenerator.CreateProfile(withOneDisplayName);
        var profileB = FragmentProfileGenerator.CreateProfile(withAnotherDisplayName);

        // Renaming display_name must not change the GUID (ADR-013) -- otherwise Terminal would see
        // a brand-new profile and orphan the user's customisation of the old one.
        Assert.Equal(profileA.Guid, profileB.Guid);
        Assert.Equal(TerminalGuid.DeriveProfileGuid(FragmentProfileGenerator.AppName, "example-nas"), profileA.Guid);
    }

    [Fact]
    public void CreateProfile_different_hosts_get_different_guids()
    {
        var a = FragmentProfileGenerator.CreateProfile(TerminalHostFixtures.Host("example-nas"));
        var b = FragmentProfileGenerator.CreateProfile(TerminalHostFixtures.Host("example-remote"));

        Assert.NotEqual(a.Guid, b.Guid);
    }

    [Fact]
    public void CreateProfile_sets_starting_directory_explicitly()
    {
        var profile = FragmentProfileGenerator.CreateProfile(TerminalHostFixtures.Host("example-nas"));

        Assert.False(string.IsNullOrWhiteSpace(profile.StartingDirectory));
    }

    [Fact]
    public void CreateProfile_passes_through_tab_color_and_color_scheme_by_name()
    {
        var host = TerminalHostFixtures.Host("example-nas", tabColor: "#7A1F1F", colorScheme: "Solarized Dark");

        var profile = FragmentProfileGenerator.CreateProfile(host);

        Assert.Equal("#7A1F1F", profile.TabColor);
        Assert.Equal("Solarized Dark", profile.ColorScheme);
    }

    [Theory]
    [InlineData(MountMode.None)]
    [InlineData(MountMode.OnDemand)]
    [InlineData(MountMode.Persistent)]
    public void CreateDocument_includes_a_profile_for_every_mount_mode_including_none(MountMode mode)
    {
        var host = TerminalHostFixtures.Host("example-jump", mountMode: mode);

        var document = FragmentProfileGenerator.CreateDocument([host]);

        var profile = Assert.Single(document.Profiles);
        Assert.Equal(FragmentProfileGenerator.BuildSshInvocation(host), profile.CommandLine);
    }

    [Fact]
    public void CreateDocument_produces_one_profile_per_host()
    {
        var hosts = new[]
        {
            TerminalHostFixtures.Host("host-a"),
            TerminalHostFixtures.Host("host-b"),
            TerminalHostFixtures.Host("host-c", mountMode: MountMode.None),
        };

        var document = FragmentProfileGenerator.CreateDocument(hosts);

        Assert.Equal(3, document.Profiles.Count);
    }

    // ------------------------------------------------------------------------------------------
    // Reconnect wrapper (session.reconnect = true)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void CreateProfile_reconnect_wraps_the_ssh_invocation_in_a_retry_loop()
    {
        var host = TerminalHostFixtures.Host("example-remote", reconnect: true);

        var profile = FragmentProfileGenerator.CreateProfile(host);

        Assert.Contains(FragmentProfileGenerator.BuildSshInvocation(host), profile.CommandLine, StringComparison.Ordinal);
        Assert.Contains("cmd.exe", profile.CommandLine);
        Assert.Contains("255", profile.CommandLine); // retries on the dropped-connection exit code
    }

    [Fact]
    public void CreateProfile_reconnect_wraps_with_a_bounded_retry_count_and_a_giveup_message()
    {
        // bs-ew1: an unbounded loop cannot tell "connection keeps dropping" apart from "ssh.exe
        // cannot be launched at all" (cmd.exe's own not-recognised exit code, 9009, is >= 255 and
        // so satisfies the same retry condition as a dropped connection) and spins forever with
        // nothing surfaced. Verified against a real cmd.exe (see FragmentProfileGenerator's remarks
        // on WrapWithReconnectLoop) -- this test only pins the generated string's shape.
        var host = TerminalHostFixtures.Host("example-remote", reconnect: true);

        var profile = FragmentProfileGenerator.CreateProfile(host);

        // Bounded, not the old "for /l %n in (1,0,2)" infinite-step-zero form.
        Assert.Contains($"for /l %n in (1,1,{FragmentProfileGenerator.MaxReconnectAttempts})", profile.CommandLine);
        Assert.DoesNotContain("(1,0,2)", profile.CommandLine);

        // A give-up message fires only after the cap, reporting the last exit code -- so the tab
        // says what happened instead of scrolling forever.
        Assert.Contains($"giving up after {FragmentProfileGenerator.MaxReconnectAttempts} attempts", profile.CommandLine);
        Assert.Contains("last exit code", profile.CommandLine);

        // Delayed expansion is required for !errorlevel! to reflect the real, current value in a
        // single-line `cmd /c "..."` invocation (see the remarks for why %errorlevel% is stale there).
        Assert.Contains("/v:on", profile.CommandLine);
    }

    [Fact]
    public void CreateProfile_reconnect_false_does_not_wrap_at_all()
    {
        var host = TerminalHostFixtures.Host("example-remote", reconnect: false);

        var profile = FragmentProfileGenerator.CreateProfile(host);

        Assert.Equal("ssh -i \"~/.ssh/id_ed25519\" -p 22 someuser@example-remote.example.internal", profile.CommandLine);
    }

    [Fact]
    public void BuildSshInvocation_is_identical_whether_or_not_reconnect_wraps_it()
    {
        // The wrapper is a shell loop AROUND the ssh invocation -- it must never change what
        // ssh.exe itself is launched with (see the E7/E8 contract test below).
        var withReconnect = TerminalHostFixtures.Host("example-remote", tmux: true, tmuxSession: "main", reconnect: true);
        var withoutReconnect = TerminalHostFixtures.Host("example-remote", tmux: true, tmuxSession: "main", reconnect: false);

        Assert.Equal(FragmentProfileGenerator.BuildSshInvocation(withReconnect), FragmentProfileGenerator.BuildSshInvocation(withoutReconnect));
    }

    [Fact]
    public void TmuxSession_falls_back_when_tmux_true_but_session_name_missing()
    {
        // ConfigValidator does not currently enforce that tmux=true implies a non-null
        // tmux_session (a discovered-work gap, reported separately) -- the generator must not
        // throw or produce a malformed commandline over it.
        var host = TerminalHostFixtures.Host("example-nas", tmux: true, tmuxSession: null);

        var invocation = FragmentProfileGenerator.BuildSshInvocation(host);

        Assert.Equal(
            "ssh -t -i \"~/.ssh/id_ed25519\" -p 22 someuser@example-nas.example.internal tmux new -A -s main",
            invocation);
    }
}
