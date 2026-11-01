using Bosun.ClaudeSessions;

namespace Bosun.Tests.ClaudeSessions;

public sealed class WtRestoreArgumentBuilderTests
{
    private const string Pwsh = @"C:\Program Files\PowerShell\7\pwsh.exe";
    private const string Claude = @"C:\Users\x\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe";
    private const string Profile = "{61c54bbd-c2c6-5271-96e7-009a87ff44bf}";
    private const string Id = "0a1b2c3d-1111-2222-3333-444455556666";

    private static ClaudeRestoreTab Tab(
        string cwd = @"C:\work\repo",
        string? title = "t",
        string? profile = Profile,
        string? root = Pwsh,
        string id = Id) =>
        new(id, cwd, title, profile, root, Claude);

    private static IReadOnlyList<string> Build(params ClaudeRestoreTab[] tabs) => WtRestoreArgumentBuilder.Build(tabs);

    [Fact]
    public void Pwsh_tab_keeps_shell_open_after_claude_exits()
    {
        var args = Build(Tab());

        Assert.Equal(
            ["-w", "0", "new-tab", "-p", Profile, "-d", @"C:\work\repo", "--title", "t",
             Pwsh, "-NoExit", "-Command", "claude", "--resume", Id],
            args);
    }

    [Fact]
    public void Unset_title_with_semicolon_in_cwd_matches_documented_example()
    {
        var args = Build(Tab(cwd: @"C:\Users\x\my;repo", title: null));

        Assert.Equal(
            ["-w", "0", "new-tab", "-p", Profile, "-d", @"C:\Users\x\my\;repo", "--title", @"my\;repo",
             Pwsh, "-NoExit", "-Command", "claude", "--resume", Id],
            args);
    }

    [Fact]
    public void Windows_powershell_root_is_matched_case_insensitively()
    {
        var root = @"C:\WINDOWS\System32\WindowsPowerShell\v1.0\PowerShell.EXE";

        var args = Build(Tab(root: root));

        Assert.Equal([root, "-NoExit", "-Command", "claude", "--resume", Id], args.Skip(args.Count - 6));
    }

    [Fact]
    public void Cmd_root_uses_slash_k()
    {
        var root = @"C:\Windows\System32\cmd.exe";

        var args = Build(Tab(root: root));

        Assert.Equal([root, "/k", "claude", "--resume", Id], args.Skip(args.Count - 5));
    }

    [Fact]
    public void Tab_root_that_is_claude_itself_gets_no_wrapper()
    {
        var args = Build(new ClaudeRestoreTab(Id, @"C:\work\repo", "t", Profile, Claude.ToUpperInvariant(), Claude));

        Assert.Equal([Claude, "--resume", Id], args.Skip(args.Count - 3));
        Assert.DoesNotContain("-NoExit", args);
        Assert.DoesNotContain("claude", args);
    }

    [Theory]
    [InlineData(@"C:\msys64\usr\bin\bash.exe")]
    [InlineData(@"C:\Windows\System32\wsl.exe")]
    [InlineData("")]
    [InlineData(null)]
    public void Unknown_or_missing_tab_root_launches_recorded_claude_image_directly(string? root)
    {
        var args = Build(Tab(root: root));

        Assert.Equal([Claude, "--resume", Id], args.Skip(args.Count - 3));
        Assert.DoesNotContain(args, a => a is "-NoExit" or "/k" or "claude");
        if (!string.IsNullOrEmpty(root))
        {
            Assert.DoesNotContain(root, args);
        }
    }

    [Fact]
    public void Path_with_spaces_stays_a_single_argument()
    {
        var args = Build(Tab(cwd: @"C:\My Projects\some repo", title: "my title"));

        Assert.Contains(@"C:\My Projects\some repo", args);
        Assert.Contains("my title", args);
        Assert.Contains(Pwsh, args); // also contains spaces
    }

    [Fact]
    public void Semicolons_in_cwd_and_title_are_escaped_for_wt()
    {
        var args = Build(Tab(cwd: @"C:\a;b\c", title: "fix; then ship;"));

        Assert.Equal(@"C:\a\;b\c", args[args.ToList().IndexOf("-d") + 1]);
        Assert.Equal(@"fix\; then ship\;", args[args.ToList().IndexOf("--title") + 1]);
        // No argument is, or ends with, an unescaped ';' -- wt would split a new command there.
        Assert.DoesNotContain(";", args);
        Assert.All(args, a => Assert.DoesNotMatch(@"(^|[^\\]);", a));
    }

    [Fact]
    public void Semicolon_already_preceded_by_backslash_is_still_not_a_separator()
    {
        // wt de-escapes exactly one backslash, so "a\;b" must be sent as "a\\;b".
        var args = Build(Tab(title: @"a\;b"));

        Assert.Equal(@"a\\;b", args[args.ToList().IndexOf("--title") + 1]);
    }

    [Fact]
    public void Semicolon_in_profile_or_image_path_is_escaped()
    {
        var args = Build(new ClaudeRestoreTab(Id, @"C:\w", "t", "p;q", @"C:\x;y\bash.exe", @"C:\x;y\claude.exe"));

        Assert.Contains(@"p\;q", args);
        Assert.Contains(@"C:\x\;y\claude.exe", args);
    }

    [Theory]
    [InlineData(@"C:\work\repo", "repo")]
    [InlineData(@"C:\work\repo\", "repo")]
    [InlineData(@"C:\work\repo\\", "repo")]
    [InlineData(@"C:\", "C:")]
    [InlineData(@"\\server\share", "share")]
    [InlineData(@"\\server\share\", "share")]
    public void Unset_or_blank_title_falls_back_to_folder_name(string cwd, string expected)
    {
        Assert.Equal(expected, TitleOf(Build(Tab(cwd: cwd, title: null))));
        Assert.Equal(expected, TitleOf(Build(Tab(cwd: cwd, title: "  "))));
    }

    [Fact]
    public void Set_title_wins_over_folder_name()
    {
        Assert.Equal("auth refactor", TitleOf(Build(Tab(title: "auth refactor"))));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Missing_profile_id_omits_dash_p(string? profile)
    {
        var args = Build(Tab(profile: profile));

        Assert.DoesNotContain("-p", args);
        Assert.Equal(["-w", "0", "new-tab", "-d"], args.Take(4));
    }

    [Fact]
    public void Multiple_tabs_are_chained_with_bare_semicolons_and_one_leading_window_target()
    {
        var args = Build(
            Tab(id: "id-1", cwd: @"C:\one"),
            Tab(id: "id-2", cwd: @"C:\two", root: @"C:\Windows\System32\cmd.exe"),
            Tab(id: "id-3", cwd: @"C:\three", root: null));

        Assert.Equal(["-w", "0", "new-tab"], args.Take(3));
        Assert.Single(args, "-w");
        Assert.Equal(3, args.Count(a => a == "new-tab"));
        Assert.Equal(2, args.Count(a => a == ";"));

        var sepIndexes = Enumerable.Range(0, args.Count).Where(i => args[i] == ";").ToArray();
        Assert.All(sepIndexes, i => Assert.Equal("new-tab", args[i + 1]));
        Assert.Equal("id-1", args[sepIndexes[0] - 1]);
        Assert.Equal("id-2", args[sepIndexes[1] - 1]);
        Assert.Equal("id-3", args[^1]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a;b")]
    [InlineData("a b")]
    [InlineData("a\tb")]
    [InlineData(";")]
    public void Invalid_session_id_is_rejected(string id)
    {
        Assert.Throws<ArgumentException>(() => Build(Tab(id: id)));
    }

    [Fact]
    public void One_invalid_session_id_rejects_the_whole_batch()
    {
        Assert.Throws<ArgumentException>(() => Build(Tab(), Tab(id: "bad id")));
    }

    [Fact]
    public void Empty_cwd_or_claude_path_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => Build(Tab(cwd: " ")));
        Assert.Throws<ArgumentException>(() => Build(new ClaudeRestoreTab(Id, @"C:\w", "t", Profile, Pwsh, "")));
    }

    [Fact]
    public void Empty_input_list_throws_rather_than_returning_a_bare_window_target()
    {
        Assert.Throws<ArgumentException>(() => WtRestoreArgumentBuilder.Build([]));
    }

    private static string TitleOf(IReadOnlyList<string> args) => args[args.ToList().IndexOf("--title") + 1];
}
