using System.Runtime.InteropServices;

namespace Bosun.Remote.Tests;

/// <summary>
/// Independent adversarial pass on <see cref="DevelopmentAreaGuard"/> (bs-4xw.7), written from
/// ADR-024 Decision 2 and the "what the remote tools may do" addendum rather than from the
/// implementation. The requirement under test: every caller-supplied path is resolved and
/// canonicalised and stays inside the development area; <c>..</c>, rooted paths, other drives and
/// reparse points that leave the area are refused; a rejection never reveals where the area is.
///
/// <para>Where the spec does not say whether an odd input should be allowed (Unicode lookalikes,
/// leading spaces), these tests assert only the safety property: either the input is refused, or
/// the result is the literal child of the area that the input names, with no character
/// reinterpreted as a separator, a dot segment or a drive. Several of them also create the
/// resolved path on disk to prove the file system agrees with the guard.</para>
///
/// <para>Everything happens under a fresh temp directory (<see cref="TempArea"/>). Nothing here starts
/// Claude Code, opens a pseudo console, uses the network, or touches Bosun's real state.</para>
/// </summary>
public sealed class PathGuardAdversarialTests : IDisposable
{
    private readonly TempArea _temp = new();
    private readonly DevelopmentAreaGuard _guard;

    public PathGuardAdversarialTests() => _guard = _temp.Guard();

    public void Dispose() => _temp.Dispose();

    // ---- Helpers ---------------------------------------------------------------------------

    private static readonly char[] RealSeparators = ['\\', '/'];

    /// <summary>
    /// The safety property for inputs the spec does not classify: refused, or resolved to exactly
    /// <c>Root\seg1\seg2...</c> where the segments are the input split on the two real separators.
    /// </summary>
    private static void AssertRefusedOrLiteralChild(DevelopmentAreaGuard guard, string input)
    {
        var result = guard.Resolve(input);
        if (!result.IsAllowed)
        {
            AssertRejectionIsSafe(guard, result);
            return;
        }

        var full = result.FullPath!;
        Assert.True(DevelopmentAreaGuard.IsStrictlyInside(guard.Root, full), $"'{input}' resolved outside: '{full}'");
        Assert.Equal(Path.Combine([guard.Root, .. input.Split(RealSeparators)]), full);
    }

    private static void AssertRejectionIsSafe(DevelopmentAreaGuard guard, PathResolution result)
    {
        Assert.False(result.IsAllowed);
        Assert.Null(result.FullPath);
        Assert.NotNull(result.Rejection);
        Assert.False(string.IsNullOrWhiteSpace(result.Detail));
        Assert.DoesNotContain(guard.Root, result.Detail!, StringComparison.OrdinalIgnoreCase);
    }

    private void AssertRejected(string input, PathRejection expected)
    {
        var result = _guard.Resolve(input);
        Assert.False(result.IsAllowed, $"'{input}' was allowed as '{result.FullPath}'");
        Assert.Equal(expected, result.Rejection);
        AssertRejectionIsSafe(_guard, result);
    }

    /// <summary>
    /// After creating something at an allowed path, the temp root must still hold exactly the three
    /// folders <see cref="TempArea"/> made and <c>outside</c> must be untouched: proof that the file
    /// system, not just the guard's string handling, kept the write inside the area.
    /// </summary>
    private void AssertNothingWasCreatedOutsideTheArea()
    {
        var rootChildren = Directory.EnumerateFileSystemEntries(_temp.Root)
            .Select(p => Path.GetFileName(p)).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["Dev", "Development", "outside"], rootChildren);
        Assert.Equal(["secret.txt"], Directory.EnumerateFileSystemEntries(_temp.Outside).Select(p => Path.GetFileName(p)).ToArray());
        Assert.Empty(Directory.EnumerateFileSystemEntries(_temp.Sibling));
    }

    // ---- Unicode lookalikes of '.', '\', '/' and ':' ---------------------------------------

    /// <summary>
    /// Bug caught: a normalisation step (NFKC, best-fit, or a "helpful" mapping of fullwidth forms)
    /// turning a lookalike into a real separator, dot or colon after the segment checks ran.
    /// Win32 wide APIs treat none of these as special, so each must be a literal name.
    /// </summary>
    [Theory]
    [InlineData("\uFF0E\uFF0E")]                       // fullwidth full stop x2
    [InlineData("\uFF0E\uFF0E\uFF3Coutside")]          // ．．＼outside
    [InlineData("\uFF0E\uFF0E\uFF0Foutside")]          // ．．／outside
    [InlineData("a\uFF3C\uFF0E\uFF0E\uFF3C\uFF0E\uFF0E\uFF3Coutside")]
    [InlineData("\uFF0E\uFF0E/proj")]                  // lookalike dots, real separator
    [InlineData("\u2024\u2024")]                       // one dot leader x2
    [InlineData("\u2025")]                             // two dot leader
    [InlineData("\uFE52\uFE52")]                       // small full stop x2
    [InlineData("a\u2215b")]                           // division slash
    [InlineData("a\u2044b")]                           // fraction slash
    [InlineData("a\u29F5b")]                           // reverse solidus operator
    [InlineData("a\uFE68b")]                           // small reverse solidus
    [InlineData("C\uFF1A\uFF3CWindows")]               // Ｃ：＼Windows
    [InlineData("file.txt\uFF1A\uFF1A$DATA")]          // fullwidth colons, not a stream
    [InlineData("a\uA789b")]                           // modifier letter colon
    [InlineData("\uFF3C\uFF3Cserver\uFF3Cshare")]      // ＼＼server＼share
    public void Unicode_lookalikes_are_literal_names_never_separators_dots_or_colons(string input)
    {
        AssertRefusedOrLiteralChild(_guard, input);

        var result = _guard.Resolve(input);
        if (result.IsAllowed)
        {
            Directory.CreateDirectory(result.FullPath!);
            Assert.True(Directory.Exists(result.FullPath));
            AssertNothingWasCreatedOutsideTheArea();
        }
    }

    /// <summary>
    /// Proposed hardening, not current behaviour. <see cref="DevelopmentAreaGuard.Resolve"/> accepts
    /// these as literal names, which is correct for every wide (UTF-16) Windows API. But Windows
    /// "best-fit" conversion to the ANSI code page maps them to real metacharacters: verified on this
    /// machine (ACP 1252) with
    /// <c>WideCharToMultiByte(1252, 0, ...)</c>, <c>C:\Dev\．．＼x∕y：＂＆</c> becomes
    /// <c>C:\Dev\..\x/y:"&amp;</c>. Any program that receives an allowed FullPath
    /// through an ANSI API (a <c>main</c> rather than <c>wmain</c> entry point, <c>GetCommandLineA</c>)
    /// would see a traversal or a quote break-out. Unskip when bs-4xw.18 decides the characters
    /// allowed in paths inside a project. Project names are already refused:
    /// <see cref="Names_that_best_fit_to_metacharacters_are_refused_as_project_names"/>.
    /// </summary>
    [Theory(Skip = "Waits on bs-4xw.18 (rule for tools that write files), which settles the characters " +
                   "allowed in paths inside a project. Resolve accepts names that become " +
                   "'..', '\\', '/', ':' or '\"' after ANSI best-fit conversion. Repro: new DevelopmentAreaGuard(area)" +
                   ".Resolve(\"\\uFF0E\\uFF0E\\uFF3C\\uFF0E\\uFF0E\\uFF3CWindows\") is allowed; WideCharToMultiByte(1252, 0) " +
                   "of its FullPath yields '<area>\\..\\..\\Windows'.")]
    [InlineData("\uFF0E\uFF0E\uFF3C\uFF0E\uFF0E\uFF3CWindows")]
    [InlineData("\uFF0E\uFF0E")]
    [InlineData("a\u2215b")]
    [InlineData("a\uFF1Ab")]
    [InlineData("a\uFF02 --flag \uFF02b")]
    public void Proposed_paths_that_best_fit_to_metacharacters_are_refused(string input)
    {
        Assert.False(_guard.Resolve(input).IsAllowed);
    }

    /// <summary>
    /// The project-name rule (ADR-024 addendum, bs-4xw.17) is ASCII only, so the lookalikes that
    /// Windows best-fit conversion turns into <c>..</c>, a separator, a colon or a quote are refused
    /// as project names, with the name-rule reason rather than a path-shape reason.
    /// </summary>
    [Theory]
    [InlineData("\uFF0E\uFF0E\uFF3C\uFF0E\uFF0E\uFF3CWindows")]
    [InlineData("\uFF0E\uFF0E")]
    [InlineData("a\u2215b")]
    [InlineData("a\uFF1Ab")]
    [InlineData("a\uFF02 --flag \uFF02b")]
    public void Names_that_best_fit_to_metacharacters_are_refused_as_project_names(string input)
    {
        var result = _guard.ResolveProject(input);

        Assert.Equal(PathRejection.NameNotAllowed, result.Rejection);
        AssertRejectionIsSafe(_guard, result);
    }

    /// <summary>
    /// A project name ends up in a session name
    /// (<c>bosun · &lt;project&gt;</c>) and, in later tools, near command lines. Windows allows these
    /// characters in names; cmd.exe, PowerShell and <c>wt.exe</c> (which splits on <c>;</c>) give them
    /// meaning. Tools must never build a shell command line from them (IRemoteTool contract), but
    /// the project-name alphabet (ADR-024 addendum, bs-4xw.17) makes that mistake harmless: none of
    /// these characters is in it.
    /// </summary>
    [Theory]
    [InlineData("x&calc")]
    [InlineData("x;new-tab cmd")]
    [InlineData("x^y")]
    [InlineData("%COMSPEC%")]
    [InlineData("x!PATH!")]
    [InlineData("x`whoami`")]
    [InlineData("x$(whoami)")]
    public void Project_names_with_shell_metacharacters_are_refused(string input)
    {
        var result = _guard.ResolveProject(input);

        Assert.Equal(PathRejection.NameNotAllowed, result.Rejection);
        AssertRejectionIsSafe(_guard, result);
    }

    // ---- Whitespace tricks ------------------------------------------------------------------

    /// <summary>
    /// Bug caught: a later <c>Trim()</c> of the input or of a segment (after the rooted, drive and
    /// UNC checks) turning a harmless-looking input into a rooted, drive or <c>..</c> path. Today the
    /// empty-segment and colon checks would still catch most of these if such a trim were added, so
    /// this pins the end-to-end property across the layered checks rather than any one of them.
    /// </summary>
    [Theory]
    [InlineData(" \\Windows")]
    [InlineData(" /Windows")]
    [InlineData(" C:\\Windows")]
    [InlineData("\tC:\\Windows")]
    [InlineData("\u00A0\\Windows")]
    [InlineData(" \\\\server\\share")]
    [InlineData(" ..\\outside")]
    [InlineData(".. \\outside")]
    [InlineData("..\u00A0\\outside")]
    [InlineData(" .\\outside")]
    public void Leading_or_trailing_whitespace_never_smuggles_a_rooted_drive_or_traversal_path(string input)
    {
        var result = _guard.Resolve(input);

        if (result.IsAllowed)
        {
            // Only acceptable if every segment is a literal child (e.g. "..<NBSP>" is an ordinary
            // name to Windows). Prove it on disk.
            AssertRefusedOrLiteralChild(_guard, input);
            Directory.CreateDirectory(result.FullPath!);
            AssertNothingWasCreatedOutsideTheArea();
        }
        else
        {
            AssertRejectionIsSafe(_guard, result);
        }
    }

    /// <summary>
    /// Bug caught: trailing-dot/space stripping applied to the whole input instead of per segment,
    /// so ".. " or "... " in the middle of a path collapses to ".." after the check.
    /// </summary>
    [Theory]
    [InlineData(".. ")]
    [InlineData(".. \\outside")]
    [InlineData("a\\.. \\..\\outside")]
    [InlineData("a\\... \\x")]
    [InlineData(". \\x")]
    [InlineData("..\t")]
    public void Dot_runs_with_trailing_space_are_refused_wherever_they_appear(string input)
    {
        var result = _guard.Resolve(input);

        Assert.False(result.IsAllowed, $"'{input}' was allowed as '{result.FullPath}'");
        AssertRejectionIsSafe(_guard, result);
    }

    [Theory]
    [InlineData(" proj")]
    [InlineData("  leading spaces")]
    [InlineData("a\\ b")]
    public void A_name_with_leading_spaces_stays_a_literal_child(string input)
    {
        AssertRefusedOrLiteralChild(_guard, input);

        var result = _guard.Resolve(input);
        if (result.IsAllowed)
        {
            Directory.CreateDirectory(result.FullPath!);
            AssertNothingWasCreatedOutsideTheArea();
        }
    }

    // ---- Device and NT-namespace prefixes embedded in the path --------------------------------

    /// <summary>
    /// Bug caught: device-prefix detection that only looks at the start of the input, or that
    /// misses the <c>\??\</c> NT object-manager form.
    /// </summary>
    [Theory]
    [InlineData("\\??\\C:\\Windows")]
    [InlineData("/??/C:/Windows")]
    [InlineData("a\\??\\C:\\Windows")]
    [InlineData("a/??/C:/Windows")]
    [InlineData("a\\\\.\\C:\\Windows")]
    [InlineData("a\\\\?\\UNC\\server\\share")]
    [InlineData("\\\\?\\")]
    [InlineData("\\\\.\\")]
    [InlineData("\\/?/C:/x")]
    [InlineData("/\\.\\C:")]
    public void Device_and_nt_object_prefixes_anywhere_in_the_path_are_refused(string input)
    {
        var result = _guard.Resolve(input);

        Assert.False(result.IsAllowed, $"'{input}' was allowed as '{result.FullPath}'");
        AssertRejectionIsSafe(_guard, result);
    }

    // ---- NTFS stream and attribute syntax ------------------------------------------------------

    [Theory]
    [InlineData("proj::$DATA")]
    [InlineData("proj\\::$DATA")]
    [InlineData("proj:$I30:$INDEX_ALLOCATION\\..\\..\\outside")]
    [InlineData("proj::$INDEX_ALLOCATION")]
    [InlineData("::$DATA")]
    public void Ntfs_stream_and_attribute_syntax_is_refused(string input)
        => AssertRejected(input, PathRejection.AlternateDataStream);

    // ---- Length -------------------------------------------------------------------------------

    [Fact]
    public void A_path_longer_than_max_path_made_of_valid_names_resolves_inside_the_area()
    {
        var segments = Enumerable.Range(0, 12).Select(i => $"segment{i:D2}-" + new string('x', 40)).ToArray();
        var input = string.Join('\\', segments);
        Assert.True(Path.Combine([_guard.Root, .. segments]).Length > 260);

        AssertRefusedOrLiteralChild(_guard, input);
        Assert.True(_guard.Resolve(input).IsAllowed);
    }

    /// <summary>
    /// Bug caught: an exception type the guard does not catch (an uncaught <c>IOException</c> or
    /// <c>PathTooLongException</c> from canonicalisation) escaping to the tool as a crash, or a
    /// truncation that lets the result differ from the input.
    /// </summary>
    [Fact]
    public void An_input_longer_than_the_32767_character_windows_limit_does_not_throw_and_does_not_escape()
    {
        var segments = Enumerable.Range(0, 140).Select(_ => new string('a', 255)).ToArray();
        var input = string.Join('\\', segments);
        Assert.True(input.Length > short.MaxValue);

        var exception = Record.Exception(() => AssertRefusedOrLiteralChild(_guard, input));

        Assert.Null(exception);
    }

    [Fact]
    public void An_overlong_input_whose_first_segment_exists_does_not_throw()
    {
        _temp.MakeDir(new string('a', 255));
        var input = string.Join('\\', Enumerable.Range(0, 140).Select(_ => new string('a', 255)));

        var exception = Record.Exception(() => AssertRefusedOrLiteralChild(_guard, input));

        Assert.Null(exception);
    }

    /// <summary>
    /// Bug caught: the reparse-point walk silently giving up (or using a MAX_PATH-limited API) once
    /// the path exceeds 260 characters, so a junction placed deep enough is never seen.
    /// </summary>
    [Fact]
    public void A_junction_deeper_than_max_path_is_still_refused()
    {
        var deepSegments = Enumerable.Range(0, 6).Select(i => $"deep{i}-" + new string('d', 45)).ToArray();
        var deepParent = _temp.MakeDir(deepSegments);

        // mklink cannot create a link at a long path, so build it short and move the folder in.
        var stage = Path.Combine(_temp.Root, "stage");
        Directory.CreateDirectory(stage);
        TempArea.CreateJunction(Path.Combine(stage, "link"), _temp.Outside);
        var moved = Path.Combine(deepParent, "stage");
        Directory.Move(stage, moved);
        var junction = Path.Combine(moved, "link");
        Assert.True(junction.Length > 260, $"test setup: path is only {junction.Length} characters");
        Assert.True((File.GetAttributes(junction) & FileAttributes.ReparsePoint) != 0);

        var relative = string.Join('\\', [.. deepSegments, "stage", "link"]);
        AssertRejected(relative, PathRejection.ReparsePoint);
        AssertRejected(relative + "\\secret.txt", PathRejection.ReparsePoint);
        AssertRejected(relative + "\\not-there-yet\\x", PathRejection.ReparsePoint);
    }

    // ---- Junctions in less obvious places -----------------------------------------------------

    [Fact]
    public void A_junction_four_levels_down_under_ordinary_folders_is_refused()
    {
        var parent = _temp.MakeDir("p", "q", "r", "s");
        TempArea.CreateJunction(Path.Combine(parent, "j"), _temp.Outside);

        AssertRejected("p\\q\\r\\s\\j", PathRejection.ReparsePoint);
        AssertRejected("p/q/r/s/j/secret.txt", PathRejection.ReparsePoint);
        Assert.True(_guard.Resolve("p\\q\\r\\s").IsAllowed);
    }

    /// <summary>Bug caught: a case-sensitive comparison anywhere in the reparse-point walk.</summary>
    [Fact]
    public void A_junction_is_refused_whatever_case_the_caller_spells_it_in()
    {
        TempArea.CreateJunction(Path.Combine(_temp.Area, "MixedLink"), _temp.Outside);

        AssertRejected("mixedlink", PathRejection.ReparsePoint);
        AssertRejected("MIXEDLINK\\secret.txt", PathRejection.ReparsePoint);
    }

    /// <summary>
    /// Bug caught: trust of a junction root leaking downwards, e.g. resolving the root's target once
    /// and then skipping the walk, or only checking components that differ from the configured text.
    /// </summary>
    [Fact]
    public void A_junction_below_a_root_that_is_itself_a_junction_is_still_refused()
    {
        var real = Path.Combine(_temp.Root, "real-area");
        Directory.CreateDirectory(Path.Combine(real, "proj"));
        var rootLink = Path.Combine(_temp.Root, "area-link");
        TempArea.CreateJunction(rootLink, real);
        TempArea.CreateJunction(Path.Combine(real, "proj", "escape"), _temp.Outside);
        var guard = new DevelopmentAreaGuard(rootLink);

        Assert.True(guard.Resolve("proj").IsAllowed);
        var result = guard.Resolve("proj\\escape\\secret.txt");
        Assert.Equal(PathRejection.ReparsePoint, result.Rejection);
        AssertRejectionIsSafe(guard, result);
    }

    [SymlinkFact]
    public void A_file_symlink_as_the_final_component_deep_in_a_project_is_refused()
    {
        var dir = _temp.MakeDir("proj", "src");
        File.CreateSymbolicLink(Path.Combine(dir, "config.json"), Path.Combine(_temp.Outside, "secret.txt"));

        AssertRejected("proj\\src\\config.json", PathRejection.ReparsePoint);
        Assert.True(_guard.Resolve("proj\\src").IsAllowed);
    }

    [SymlinkFact]
    public void A_file_symlink_that_points_inside_the_area_is_refused_by_the_conservative_policy()
    {
        var dir = _temp.MakeDir("proj");
        File.WriteAllText(Path.Combine(dir, "real.txt"), "x");
        File.CreateSymbolicLink(Path.Combine(dir, "alias.txt"), Path.Combine(dir, "real.txt"));

        AssertRejected("proj\\alias.txt", PathRejection.ReparsePoint);
    }

    // ---- 8.3 short names ------------------------------------------------------------------

    /// <summary>
    /// Bug caught: a name-based link check (e.g. comparing against a list of known link names, or
    /// enumerating the parent and matching long names only) that a short-name alias walks past.
    /// </summary>
    [ShortNameFact]
    public void A_junction_addressed_by_its_8dot3_short_name_is_refused()
    {
        var link = Path.Combine(_temp.Area, "averylongjunctionname");
        TempArea.CreateJunction(link, _temp.Outside);
        var shortName = ShortNames.ShortLeafName(link);
        Assert.NotEqual("averylongjunctionname", shortName, StringComparer.OrdinalIgnoreCase);

        AssertRejected(shortName, PathRejection.ReparsePoint);
        AssertRejected(shortName + "\\secret.txt", PathRejection.ReparsePoint);
    }

    [ShortNameFact]
    public void A_junction_under_a_folder_addressed_by_its_8dot3_short_name_is_refused()
    {
        var parent = _temp.MakeDir("averylongprojectname");
        TempArea.CreateJunction(Path.Combine(parent, "link"), _temp.Outside);
        var shortParent = ShortNames.ShortLeafName(parent);
        Assert.NotEqual("averylongprojectname", shortParent, StringComparer.OrdinalIgnoreCase);

        AssertRejected(shortParent + "\\link", PathRejection.ReparsePoint);
        AssertRejected(shortParent + "\\link\\secret.txt", PathRejection.ReparsePoint);
    }

    /// <summary>
    /// Canonicalisation (Path.GetFullPath) expands an existing entry's short name to its long name,
    /// so the result is the one canonical spelling. Bug caught: a result that keeps the alias, so two
    /// spellings of one project compare unequal downstream (duplicate-session checks, ADR-024 tool
    /// "list running sessions"), or one that expands to something other than the aliased entry.
    /// </summary>
    [ShortNameFact]
    public void A_short_name_alias_of_an_ordinary_folder_resolves_to_its_long_name_inside_the_area()
    {
        var dir = _temp.MakeDir("averylongordinaryname");
        var shortName = ShortNames.ShortLeafName(dir);
        Assert.NotEqual("averylongordinaryname", shortName, StringComparer.OrdinalIgnoreCase);

        var result = _guard.Resolve(shortName + "\\child");

        Assert.True(result.IsAllowed, $"{result.Rejection} {result.Detail}");
        Assert.Equal(Path.Combine(dir, "child"), result.FullPath);
    }

    // ---- Root handling -------------------------------------------------------------------------

    /// <summary>
    /// Bug caught: a root kept with its trailing dot or space. Windows strips them, so
    /// <c>Dev.</c> is <c>Dev</c>; a non-canonical <see cref="DevelopmentAreaGuard.Root"/> would make
    /// every textual comparison against canonical paths (sessions, project lists) disagree.
    /// </summary>
    [Theory]
    [InlineData(".")]
    [InlineData("...")]
    [InlineData(" ")]
    [InlineData(". .")]
    public void A_root_with_trailing_dots_or_spaces_is_canonicalised_to_the_real_folder(string suffix)
    {
        var guard = new DevelopmentAreaGuard(_temp.Area + suffix);

        Assert.Equal(_temp.Area, guard.Root);
        Assert.Equal(Path.Combine(_temp.Area, "proj"), guard.Resolve("proj").FullPath);
    }

    [Fact]
    public void A_root_written_entirely_with_forward_slashes_and_odd_case_is_canonicalised()
    {
        var odd = _temp.Area.Replace('\\', '/').ToLowerInvariant();
        var guard = new DevelopmentAreaGuard(odd);

        Assert.Equal(_temp.Area, guard.Root, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain('/', guard.Root);
        TempArea.CreateJunction(Path.Combine(_temp.Area, "link"), _temp.Outside);
        Assert.Equal(PathRejection.ReparsePoint, guard.Resolve("LINK").Rejection);
    }

    /// <summary>
    /// Bug caught: a root whose dot segments climb to a drive root being accepted after
    /// normalisation (the constructor must check the canonical form, not the text).
    /// </summary>
    [Theory]
    [InlineData("C:\\Dev\\..")]
    [InlineData("C:\\Dev\\..\\..\\..")]
    [InlineData("C:/Dev/../")]
    [InlineData("C:\\.")]
    [InlineData("C:\\. ")]
    [InlineData("C:\\..")]
    public void A_root_that_normalises_to_a_whole_drive_is_refused(string root)
    {
        Assert.Throws<ArgumentException>(() => new DevelopmentAreaGuard(root));
    }

    [Theory]
    [InlineData("\\??\\C:\\Dev")]
    [InlineData("/??/C:/Dev")]
    [InlineData("\\\\?\\C:")]
    [InlineData("\\/?/C:/Dev")]
    [InlineData("C:\\Dev\\file.txt::$DATA")]
    public void A_root_in_nt_device_or_stream_form_is_refused(string root)
    {
        Assert.Throws<ArgumentException>(() => new DevelopmentAreaGuard(root));
    }

    /// <summary>
    /// Bug caught: a prefix comparison that omits the separator, so an area <c>X\Dev</c> accepts
    /// results under the sibling <c>X\Dev2</c> once some later code path builds them.
    /// </summary>
    [Fact]
    public void A_sibling_dev2_next_to_dev_is_not_reachable_by_any_spelling()
    {
        var dev2 = Path.Combine(_temp.Root, "Dev2");
        Directory.CreateDirectory(dev2);
        try
        {
            foreach (var input in new[]
                     {
                         "..\\Dev2", "../Dev2/x", "..\\dev2", "..\\DEV2\\x", "\\..\\Dev2", dev2,
                         dev2.Replace('\\', '/'), "2", "..2", "\uFF0E\uFF0E\\Dev2",
                     })
            {
                var result = _guard.Resolve(input);
                if (result.IsAllowed)
                {
                    Assert.True(DevelopmentAreaGuard.IsStrictlyInside(_guard.Root, result.FullPath!));
                    Assert.False(result.FullPath!.StartsWith(dev2, StringComparison.OrdinalIgnoreCase), input);
                }
            }

            Assert.False(DevelopmentAreaGuard.IsStrictlyInside(_guard.Root, Path.Combine(dev2, "x")));
            Assert.False(DevelopmentAreaGuard.IsStrictlyInside(_guard.Root, dev2));
        }
        finally
        {
            Directory.Delete(dev2);
        }
    }

    // ---- Every allowed result is canonical and literal -----------------------------------------

    /// <summary>
    /// Bug caught: an allowed result that is not in canonical form (forward slashes, doubled or
    /// trailing separators, a dot segment), which a later string comparison or a less careful
    /// consumer could reinterpret.
    /// </summary>
    [Theory]
    [InlineData("proj")]
    [InlineData("a/b/c")]
    [InlineData("a\\b/c")]
    [InlineData("..foo/...bar")]
    [InlineData(".git/hooks")]
    [InlineData("~")]
    [InlineData("~1")]
    [InlineData("%USERPROFILE%")]
    [InlineData("$env")]
    [InlineData("a b/c d")]
    [InlineData("GLOBALROOT/Device/HarddiskVolume1")] // special only after a device prefix, which cannot be spelled here
    public void Every_allowed_result_is_canonical_and_matches_the_input_segment_for_segment(string input)
    {
        var result = _guard.Resolve(input);
        Assert.True(result.IsAllowed, $"'{input}' was rejected: {result.Rejection} {result.Detail}");

        var full = result.FullPath!;
        Assert.Equal(Path.GetFullPath(full), full);
        Assert.DoesNotContain('/', full);
        Assert.DoesNotContain("\\\\", full, StringComparison.Ordinal);
        Assert.False(Path.EndsInDirectorySeparator(full));
        Assert.Equal(input.Split(RealSeparators), full[(_guard.Root.Length + 1)..].Split('\\'));
    }

    // ---- Rejection messages never reveal the area ----------------------------------------------

    /// <summary>
    /// Bug caught: a rejection detail (or the record's generated <c>ToString</c>, which a tool might
    /// relay or log to the caller) built from a combined or canonical path rather than from the
    /// caller's own text, revealing the area's location or the user profile.
    /// </summary>
    [Fact]
    public void No_rejection_detail_or_string_form_reveals_the_area_its_parent_or_the_profile()
    {
        TempArea.CreateJunction(Path.Combine(_temp.Area, "link"), _temp.Outside);
        var parent = Path.GetDirectoryName(_temp.Area)!;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var tempPath = Path.TrimEndingDirectorySeparator(Path.GetTempPath());

        string[] inputs =
        [
            "..", "..\\outside", "a\\..\\..", ".", "...", "foo.", "foo ", "a\\\\b", "a\\",
            "CON", "lpt1.txt", "a*b", "a:b", "x::$DATA", new string('z', 300),
            "link", "link\\secret.txt", "link\\new\\deeper",
            "\\Windows", "/", "C:\\Windows", "C:", "\\\\server\\share", "\\\\?\\C:\\",
            _temp.Area, _temp.Area + "\\proj", _temp.Area.Replace('\\', '/'),
            _temp.Area[2..], _temp.Area[2..] + "\\..\\outside",
        ];

        foreach (var input in inputs)
        {
            var result = _guard.Resolve(input);
            Assert.False(result.IsAllowed, $"'{input}' should have been refused");

            var callerTextContainsRoot = input.Contains(_guard.Root, StringComparison.OrdinalIgnoreCase)
                                         || input.Contains(_guard.Root.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
            if (callerTextContainsRoot)
            {
                // Echoing the caller's own text is not a leak, but the guard should not need to.
                continue;
            }

            foreach (var secret in new[] { _guard.Root, parent, profile, tempPath, _temp.Root })
            {
                Assert.DoesNotContain(secret, result.Detail!, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(secret, result.ToString(), StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void Rejection_details_for_the_areas_own_path_do_not_echo_it()
    {
        // The caller already knows what it sent, but a guard that echoes input would leak the area
        // the moment some layer combines first. Pin that the details for absolute inputs are static.
        foreach (var input in new[] { _temp.Area, _temp.Area + "\\proj", _temp.Area[2..] })
        {
            var result = _guard.Resolve(input);

            Assert.False(result.IsAllowed);
            Assert.DoesNotContain(_temp.Area[3..], result.Detail!, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---- Concurrency -------------------------------------------------------------------------

    /// <summary>
    /// Bug caught: hidden shared mutable state (a cached buffer, a lazily built table, a static
    /// "current path") that makes one caller's resolution bleed into another's.
    /// </summary>
    [Fact]
    public void Concurrent_resolution_from_many_threads_matches_sequential_resolution()
    {
        _temp.MakeDir("proj", "sub");
        TempArea.CreateJunction(Path.Combine(_temp.Area, "proj", "link"), _temp.Outside);

        string[] inputs =
        [
            "proj", "proj\\sub", "proj\\link", "proj\\link\\secret.txt", "..\\outside", "CON", "a:b",
            "new\\deep\\path", "\\Windows", "C:\\Windows", "x.", "a\\\\b", "\uFF0E\uFF0E", "a*b",
        ];
        var expected = inputs.ToDictionary(i => i, i => _guard.Resolve(i));
        var expectedProject = inputs.ToDictionary(i => i, i => _guard.ResolveProject(i));
        var mismatches = new System.Collections.Concurrent.ConcurrentBag<string>();

        Parallel.For(0, 4000, new ParallelOptions { MaxDegreeOfParallelism = 32 }, n =>
        {
            var input = inputs[n % inputs.Length];
            var project = n % 2 == 0;
            var actual = project ? _guard.ResolveProject(input) : _guard.Resolve(input);
            var baseline = project ? expectedProject[input] : expected[input];
            if (actual != baseline)
            {
                mismatches.Add($"{input}: {actual} vs {baseline}");
            }

            if (actual.IsAllowed && !DevelopmentAreaGuard.IsStrictlyInside(_guard.Root, actual.FullPath!))
            {
                mismatches.Add($"{input}: escaped to {actual.FullPath}");
            }
        });

        Assert.Empty(mismatches);
    }

    // ---- ResolveProject with lookalike separators --------------------------------------------

    /// <summary>
    /// Bug caught: ResolveProject counting segments on a normalised form, or accepting a single
    /// "segment" that the file system splits. Lookalike separators must yield one literal folder
    /// directly in the area, never a nested or escaping one.
    /// </summary>
    [Theory]
    [InlineData("a\uFF3Cb")]
    [InlineData("a\uFF0Fb")]
    [InlineData("\uFF0E\uFF0E")]
    [InlineData("a\u2215b")]
    public void ResolveProject_with_lookalike_separators_yields_one_folder_directly_in_the_area(string input)
    {
        var result = _guard.ResolveProject(input);
        if (!result.IsAllowed)
        {
            AssertRejectionIsSafe(_guard, result);
            return;
        }

        Assert.Equal(Path.Combine(_guard.Root, input), result.FullPath);
        Assert.Equal(_guard.Root, Path.GetDirectoryName(result.FullPath));
        Directory.CreateDirectory(result.FullPath!);
        Assert.Contains(input, Directory.EnumerateDirectories(_temp.Area).Select(Path.GetFileName));
        AssertNothingWasCreatedOutsideTheArea();
    }
}

/// <summary>Reads a file system entry's 8.3 short name through Win32.</summary>
internal static class ShortNames
{
    public static string ShortLeafName(string path)
    {
        var buffer = new char[1024];
        var length = GetShortPathNameW(path, buffer, (uint)buffer.Length);
        if (length == 0 || length >= buffer.Length)
        {
            throw new InvalidOperationException($"GetShortPathNameW failed: {Marshal.GetLastPInvokeError()}");
        }

        return Path.GetFileName(new string(buffer, 0, (int)length));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint GetShortPathNameW(string longPath, char[] shortPath, uint bufferLength);
}

/// <summary>
/// A fact that is skipped, with the reason, when the temp volume does not generate 8.3 short names
/// for new entries (<c>fsutil 8dot3name</c> disabled). Probed once at discovery, like
/// <see cref="SymlinkFactAttribute"/>.
/// </summary>
internal sealed class ShortNameFactAttribute : FactAttribute
{
    private static readonly Lazy<string?> UnavailableReason = new(Probe);

    public ShortNameFactAttribute()
    {
        if (UnavailableReason.Value is { } reason)
        {
            Skip = "8.3 short names are not generated on the temp volume: " + reason;
        }
    }

    private static string? Probe()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bosun-remote-83-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            var child = Path.Combine(dir, "averylongdirectorynameforprobing");
            Directory.CreateDirectory(child);
            var shortName = ShortNames.ShortLeafName(child);
            return string.Equals(shortName, "averylongdirectorynameforprobing", StringComparison.OrdinalIgnoreCase)
                ? "the probe folder has no short name"
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return ex.Message;
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
