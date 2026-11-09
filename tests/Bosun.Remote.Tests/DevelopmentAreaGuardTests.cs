namespace Bosun.Remote.Tests;

/// <summary>
/// The guard is the security boundary of ADR-024, so this file is mostly escape attempts. Every
/// case uses a temp directory; nothing here touches Bosun's real state.
/// </summary>
public sealed class DevelopmentAreaGuardTests : IDisposable
{
    private readonly TempArea _temp = new();
    private readonly DevelopmentAreaGuard _guard;

    public DevelopmentAreaGuardTests() => _guard = _temp.Guard();

    public void Dispose() => _temp.Dispose();

    private void AssertRejected(string? input, PathRejection expected)
    {
        var result = _guard.Resolve(input);

        Assert.False(result.IsAllowed, $"'{input}' was allowed as '{result.FullPath}'");
        Assert.Null(result.FullPath);
        Assert.Equal(expected, result.Rejection);
        Assert.False(string.IsNullOrWhiteSpace(result.Detail));
        // A rejection is relayed to the remote caller, so it must not reveal where the area is.
        Assert.DoesNotContain(_temp.Area, result.Detail!, StringComparison.OrdinalIgnoreCase);
    }

    private string AssertAllowed(string input)
    {
        var result = _guard.Resolve(input);

        Assert.True(result.IsAllowed, $"'{input}' was rejected: {result.Rejection} {result.Detail}");
        Assert.Null(result.Rejection);
        Assert.NotNull(result.FullPath);
        Assert.True(DevelopmentAreaGuard.IsStrictlyInside(_guard.Root, result.FullPath!));
        return result.FullPath!;
    }

    // ---- Accepted paths -------------------------------------------------------------------

    [Theory]
    [InlineData("proj")]
    [InlineData("My Project")]
    [InlineData("proj-1_x")]
    [InlineData("a.b.c")]
    [InlineData(".hidden")]
    [InlineData("..foo")]
    [InlineData("caf\u00e9")]
    // Names that merely resemble reserved devices.
    [InlineData("console")]
    [InlineData("context")]
    [InlineData("com10")]
    [InlineData("comx")]
    [InlineData("nully")]
    [InlineData("lpt")]
    public void A_plain_name_resolves_directly_under_the_area(string name)
    {
        var full = AssertAllowed(name);

        Assert.Equal(Path.Combine(_temp.Area, name), full);
    }

    [Theory]
    [InlineData("proj\\sub", "proj", "sub")]
    [InlineData("proj/sub", "proj", "sub")]
    [InlineData("a/b\\c", "a", "b", "c")]
    public void A_nested_relative_path_resolves_with_either_separator(string input, params string[] parts)
    {
        var full = AssertAllowed(input);

        Assert.Equal(Path.Combine([_temp.Area, .. parts]), full);
    }

    [Fact]
    public void A_target_that_does_not_exist_yet_is_allowed()
    {
        var full = AssertAllowed("brand-new-project");

        Assert.False(Directory.Exists(full));
    }

    [Fact]
    public void A_deep_target_whose_parents_do_not_exist_is_allowed()
    {
        AssertAllowed("not\\yet\\created\\at\\all");
    }

    [Fact]
    public void An_existing_directory_is_allowed()
    {
        var dir = _temp.MakeDir("existing", "inner");

        Assert.Equal(dir, AssertAllowed("existing\\inner"));
    }

    [Fact]
    public void An_existing_file_is_allowed()
    {
        File.WriteAllText(Path.Combine(_temp.MakeDir("p"), "f.txt"), "x");

        AssertAllowed("p\\f.txt");
    }

    [Fact]
    public void A_path_that_runs_through_a_file_does_not_throw()
    {
        File.WriteAllText(Path.Combine(_temp.Area, "plain.txt"), "x");

        // Not an escape; whether it is refused is the caller's business. It must not throw.
        var result = _guard.Resolve("plain.txt\\child");

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void The_result_is_matched_case_insensitively_against_the_root()
    {
        var upperCaseRoot = new DevelopmentAreaGuard(_temp.Area.ToUpperInvariant());

        var result = upperCaseRoot.Resolve("proj");

        Assert.True(result.IsAllowed);
        Assert.Equal(Path.Combine(upperCaseRoot.Root, "proj"), result.FullPath);
    }

    // ---- Empty and whitespace -----------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\r\n")]
    public void Empty_or_whitespace_input_is_rejected(string? input) => AssertRejected(input, PathRejection.Empty);

    [Fact]
    public void A_whitespace_only_segment_is_rejected()
    {
        AssertRejected("a\\ \\b", PathRejection.Empty);
    }

    // ---- Traversal ------------------------------------------------------------------------

    [Theory]
    [InlineData("..")]
    [InlineData("..\\")]
    [InlineData("..\\outside")]
    [InlineData("../outside")]
    [InlineData("..\\..\\..\\Windows")]
    [InlineData("a\\..\\..\\outside")]
    [InlineData("a/../../outside")]
    [InlineData("a\\..")]
    [InlineData("a\\..\\b")]
    [InlineData(".")]
    [InlineData(".\\a")]
    [InlineData("a\\.\\b")]
    [InlineData("..\\Development\\x")] // sibling-prefix: C:\Dev vs C:\Development
    public void Dot_segments_are_rejected(string input) => AssertRejected(input, PathRejection.TraversalSegment);

    [Theory]
    [InlineData("...")]
    [InlineData("a\\....")]
    [InlineData(". .")]
    public void Dot_runs_that_Windows_would_strip_are_rejected(string input)
        => AssertRejected(input, PathRejection.TrailingDotOrSpace);

    // ---- Rooted, drive, UNC and device paths ------------------------------------------

    [Theory]
    [InlineData("\\Windows")]
    [InlineData("/Windows")]
    [InlineData("\\")]
    [InlineData("/")]
    [InlineData("\\outside\\secret.txt")]
    public void Rooted_paths_are_rejected(string input) => AssertRejected(input, PathRejection.Rooted);

    [Theory]
    [InlineData("C:\\Windows")]
    [InlineData("c:\\windows\\system32")]
    [InlineData("D:\\data")]
    [InlineData("Z:\\")]
    [InlineData("C:foo")]
    [InlineData("C:")]
    [InlineData("d:..\\x")]
    public void Absolute_and_drive_relative_paths_are_rejected(string input)
        => AssertRejected(input, PathRejection.DriveSpecifier);

    [Fact]
    public void The_areas_own_absolute_path_is_rejected_because_callers_pass_relative_paths()
    {
        AssertRejected(_temp.Area, PathRejection.DriveSpecifier);
        AssertRejected(Path.Combine(_temp.Area, "proj"), PathRejection.DriveSpecifier);
    }

    [Theory]
    [InlineData("\\\\server\\share\\x")]
    [InlineData("//server/share/x")]
    [InlineData("\\\\?\\C:\\Windows")]
    [InlineData("\\\\?\\UNC\\server\\share")]
    [InlineData("\\\\.\\PhysicalDrive0")]
    [InlineData("\\\\.\\pipe\\x")]
    [InlineData("//?/C:/Windows")]
    [InlineData("//./COM1")]
    [InlineData("\\\\?\\GLOBALROOT\\Device\\HarddiskVolume1")]
    public void Unc_and_device_paths_are_rejected(string input)
        => AssertRejected(input, PathRejection.UncOrDevicePath);

    [Fact]
    public void A_device_prefix_that_is_not_at_the_start_is_still_refused()
    {
        // "?" is invalid in a name and ":" is refused outright; either way it is not allowed.
        Assert.False(_guard.Resolve("a\\\\?\\C:\\Windows").IsAllowed);
        Assert.False(_guard.Resolve("a\\?\\C:").IsAllowed);
    }

    // ---- Alternate data streams -----------------------------------------------------------

    [Theory]
    [InlineData("ab:c")]
    [InlineData("file.txt:hidden")]
    [InlineData("file.txt::$DATA")]
    [InlineData("proj\\file.txt:stream:$DATA")]
    [InlineData("proj:$I30:$INDEX_ALLOCATION")]
    [InlineData("a\\b:")]
    [InlineData("a\\:b")]
    public void Colons_in_a_segment_are_rejected(string input)
        => AssertRejected(input, PathRejection.AlternateDataStream);

    // ---- Reserved device names -----------------------------------------------------------

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("Con")]
    [InlineData("PRN")]
    [InlineData("AUX")]
    [InlineData("NUL")]
    [InlineData("nul")]
    [InlineData("CONIN$")]
    [InlineData("CONOUT$")]
    [InlineData("CLOCK$")]
    [InlineData("COM0")]
    [InlineData("COM1")]
    [InlineData("com9")]
    [InlineData("LPT0")]
    [InlineData("LPT1")]
    [InlineData("lpt9")]
    [InlineData("COM\u00B9")]
    [InlineData("LPT\u00B2")]
    // An extension does not stop Windows treating it as the device.
    [InlineData("CON.txt")]
    [InlineData("nul.tar.gz")]
    [InlineData("NUL.txt")]
    [InlineData("a\\COM1")]
    [InlineData("a\\com2\\b")]
    [InlineData("a\\LPT3.log\\b")]
    [InlineData("COM1 .txt")]
    [InlineData("NUL  .x")]
    public void Reserved_device_names_are_rejected(string input)
        => AssertRejected(input, PathRejection.ReservedDeviceName);

    // ---- Trailing dots and spaces ------------------------------------------------------

    [Theory]
    [InlineData("foo.")]
    [InlineData("foo ")]
    [InlineData("foo..")]
    [InlineData("foo. ")]
    [InlineData("foo \\bar")]
    [InlineData("a\\bar.")]
    [InlineData("CON ")]
    [InlineData("CON.")]
    public void Names_ending_in_a_dot_or_space_are_rejected(string input)
        => AssertRejected(input, PathRejection.TrailingDotOrSpace);

    // ---- Empty segments --------------------------------------------------------------------

    [Theory]
    [InlineData("a\\\\b")]
    [InlineData("a//b")]
    [InlineData("a\\/b")]
    [InlineData("a\\")]
    [InlineData("a/")]
    [InlineData("a\\b\\")]
    public void Empty_segments_and_trailing_separators_are_rejected(string input)
        => AssertRejected(input, PathRejection.EmptySegment);

    // ---- Invalid characters ----------------------------------------------------------------

    [Theory]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a|b")]
    [InlineData("a\"b")]
    [InlineData("a\tb")]
    [InlineData("a\nb")]
    [InlineData("a\0b")]
    [InlineData("a\u001Fb")]
    [InlineData("*")]
    [InlineData("proj\\*.txt")]
    public void Wildcards_and_control_characters_are_rejected(string input)
        => AssertRejected(input, PathRejection.InvalidCharacter);

    [Fact]
    public void A_segment_over_255_characters_is_rejected_and_255_is_accepted()
    {
        AssertRejected(new string('a', 256), PathRejection.SegmentTooLong);
        AssertAllowed(new string('a', 255));
    }

    // ---- ResolveProject ------------------------------------------------------------------

    [Fact]
    public void ResolveProject_accepts_a_single_name()
    {
        var result = _guard.ResolveProject("my-project");

        Assert.True(result.IsAllowed);
        Assert.Equal(Path.Combine(_temp.Area, "my-project"), result.FullPath);
    }

    [Theory]
    [InlineData("a\\b")]
    [InlineData("a/b")]
    [InlineData("a\\..\\b")]
    public void ResolveProject_rejects_more_than_one_segment(string input)
    {
        var result = _guard.ResolveProject(input);

        Assert.False(result.IsAllowed);
        Assert.Equal(PathRejection.NotASingleSegment, result.Rejection);
    }

    [Theory]
    [InlineData("..", PathRejection.TraversalSegment)]
    [InlineData(".", PathRejection.TraversalSegment)]
    [InlineData("", PathRejection.Empty)]
    [InlineData(null, PathRejection.Empty)]
    [InlineData("C:\\x", PathRejection.DriveSpecifier)]
    [InlineData("\\\\server\\share", PathRejection.UncOrDevicePath)]
    [InlineData("\\x", PathRejection.Rooted)]
    [InlineData("ab:c", PathRejection.AlternateDataStream)]
    [InlineData("NUL", PathRejection.ReservedDeviceName)]
    [InlineData("x.", PathRejection.TrailingDotOrSpace)]
    public void ResolveProject_applies_every_resolve_check(string? input, PathRejection expected)
    {
        var result = _guard.ResolveProject(input);

        Assert.False(result.IsAllowed);
        Assert.Equal(expected, result.Rejection);
    }

    // ---- Root handling -----------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Dev")]
    [InlineData("\\Dev")]
    [InlineData("..\\Dev")]
    [InlineData("\\\\server\\share\\Dev")]
    [InlineData("//server/share/Dev")]
    [InlineData("\\\\?\\C:\\Dev")]
    [InlineData("\\\\.\\C:\\Dev")]
    [InlineData("C:")]
    [InlineData("C:Dev")]
    [InlineData("C:\\")]
    [InlineData("D:/")]
    [InlineData("C:\\Dev:stream")]
    [InlineData("C:\\Dev\\a:b")]
    public void An_unusable_root_is_refused_at_construction(string root)
    {
        Assert.Throws<ArgumentException>(() => new DevelopmentAreaGuard(root));
    }

    [Fact]
    public void A_null_root_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new DevelopmentAreaGuard(null!));
    }

    [Fact]
    public void A_root_with_a_trailing_separator_behaves_like_one_without()
    {
        var withSlash = new DevelopmentAreaGuard(_temp.Area + "\\");
        var withForwardSlash = new DevelopmentAreaGuard(_temp.Area.Replace('\\', '/') + "/");

        Assert.Equal(_temp.Area, withSlash.Root);
        Assert.Equal(_temp.Area, withForwardSlash.Root);
        Assert.Equal(_guard.Resolve("proj").FullPath, withSlash.Resolve("proj").FullPath);
        Assert.Equal(_guard.Resolve("proj").FullPath, withForwardSlash.Resolve("proj").FullPath);
    }

    [Fact]
    public void A_root_containing_dot_segments_is_normalised()
    {
        var guard = new DevelopmentAreaGuard(Path.Combine(_temp.Area, "x", "..", "."));

        Assert.Equal(_temp.Area, guard.Root);
        Assert.Equal(Path.Combine(_temp.Area, "proj"), guard.Resolve("proj").FullPath);
    }

    [Fact]
    public void A_root_that_does_not_exist_is_accepted()
    {
        var guard = new DevelopmentAreaGuard(Path.Combine(_temp.Root, "not-created"));

        Assert.True(guard.Resolve("proj").IsAllowed);
    }

    [Fact]
    public void The_area_root_itself_is_never_a_result()
    {
        AssertRejected(".", PathRejection.TraversalSegment);
        Assert.False(DevelopmentAreaGuard.IsStrictlyInside(_guard.Root, _guard.Root));
        Assert.False(DevelopmentAreaGuard.IsStrictlyInside(_guard.Root, _guard.Root + "\\"));
    }

    // ---- Containment backstop ----------------------------------------------------------------

    [Theory]
    [InlineData("C:\\Dev", "C:\\Dev\\proj", true)]
    [InlineData("C:\\Dev", "C:\\DEV\\Proj\\x", true)]
    [InlineData("C:\\Dev", "C:\\Dev", false)]
    [InlineData("C:\\Dev", "C:\\Development", false)]
    [InlineData("C:\\Dev", "C:\\Development\\proj", false)]
    [InlineData("C:\\Dev", "C:\\Dev2\\proj", false)]
    [InlineData("C:\\Dev", "C:\\Dev.old\\proj", false)]
    [InlineData("C:\\Dev", "C:\\", false)]
    [InlineData("C:\\Dev", "D:\\Dev\\proj", false)]
    [InlineData("C:\\Dev\\a", "C:\\Dev\\b", false)]
    public void Containment_compares_whole_path_components_ignoring_case(string root, string candidate, bool expected)
    {
        Assert.Equal(expected, DevelopmentAreaGuard.IsStrictlyInside(root, candidate));
    }

    [Fact]
    public void A_sibling_whose_name_starts_with_the_areas_name_is_not_reachable()
    {
        // root\Dev is the area; root\Development is a sibling sharing the prefix.
        AssertRejected("..\\Development", PathRejection.TraversalSegment);
        AssertRejected("..\\Development\\x", PathRejection.TraversalSegment);
        AssertRejected(_temp.Sibling, PathRejection.DriveSpecifier);
    }

    // ---- Reparse points: junctions ------------------------------------------------------

    [Fact]
    public void A_junction_to_a_folder_outside_the_area_is_rejected()
    {
        TempArea.CreateJunction(Path.Combine(_temp.Area, "link"), _temp.Outside);

        AssertRejected("link", PathRejection.ReparsePoint);
    }

    [Theory]
    [InlineData("link\\secret.txt")]
    [InlineData("link\\sub\\deeper")]
    [InlineData("link\\does-not-exist-yet")]
    public void Anything_below_a_junction_that_leaves_the_area_is_rejected_even_if_it_does_not_exist(string input)
    {
        TempArea.CreateJunction(Path.Combine(_temp.Area, "link"), _temp.Outside);

        AssertRejected(input, PathRejection.ReparsePoint);
    }

    [Fact]
    public void A_junction_in_the_middle_of_the_path_is_rejected()
    {
        var project = _temp.MakeDir("proj");
        TempArea.CreateJunction(Path.Combine(project, "inner"), _temp.Outside);

        AssertRejected("proj\\inner\\secret.txt", PathRejection.ReparsePoint);
        AssertRejected("proj\\inner", PathRejection.ReparsePoint);
        AssertAllowed("proj");
    }

    [Fact]
    public void A_junction_to_the_sibling_prefix_folder_is_rejected()
    {
        TempArea.CreateJunction(Path.Combine(_temp.Area, "dev-copy"), _temp.Sibling);

        AssertRejected("dev-copy", PathRejection.ReparsePoint);
        AssertRejected("dev-copy\\x", PathRejection.ReparsePoint);
    }

    [Fact]
    public void A_junction_that_points_back_inside_the_area_is_also_rejected_by_policy()
    {
        // Documented conservative choice: no reparse point below the root is accepted, whatever it
        // targets. Tightening this to "only links that leave the area" would be a deliberate change.
        _temp.MakeDir("real");
        TempArea.CreateJunction(Path.Combine(_temp.Area, "alias"), Path.Combine(_temp.Area, "real"));

        AssertRejected("alias", PathRejection.ReparsePoint);
        AssertAllowed("real");
    }

    [Fact]
    public void A_junction_whose_target_was_deleted_is_still_rejected()
    {
        var gone = Path.Combine(_temp.Root, "gone");
        Directory.CreateDirectory(gone);
        TempArea.CreateJunction(Path.Combine(_temp.Area, "dangling"), gone);
        Directory.Delete(gone);

        AssertRejected("dangling", PathRejection.ReparsePoint);
        AssertRejected("dangling\\x", PathRejection.ReparsePoint);
    }

    [Fact]
    public void A_junction_that_is_the_area_root_itself_is_trusted()
    {
        // The maintainer may point development_area at a junction; the root is configuration, not
        // caller input, so only links BELOW it are refused.
        var real = Path.Combine(_temp.Root, "real-area");
        Directory.CreateDirectory(Path.Combine(real, "proj"));
        var link = Path.Combine(_temp.Root, "area-link");
        TempArea.CreateJunction(link, real);
        var guard = new DevelopmentAreaGuard(link);

        var result = guard.Resolve("proj");

        Assert.True(result.IsAllowed);
        Assert.Equal(Path.Combine(link, "proj"), result.FullPath);
    }

    [Fact]
    public void Ordinary_folders_next_to_a_junction_stay_allowed()
    {
        TempArea.CreateJunction(Path.Combine(_temp.Area, "link"), _temp.Outside);
        _temp.MakeDir("normal");

        AssertAllowed("normal");
        AssertAllowed("brand-new");
    }

    // ---- Reparse points: symlinks (skipped when the machine cannot create them) -----------

    [SymlinkFact]
    public void A_directory_symlink_to_a_folder_outside_the_area_is_rejected()
    {
        Directory.CreateSymbolicLink(Path.Combine(_temp.Area, "slink"), _temp.Outside);

        AssertRejected("slink", PathRejection.ReparsePoint);
        AssertRejected("slink\\secret.txt", PathRejection.ReparsePoint);
    }

    [SymlinkFact]
    public void A_file_symlink_to_a_file_outside_the_area_is_rejected()
    {
        File.CreateSymbolicLink(Path.Combine(_temp.Area, "secret-link.txt"), Path.Combine(_temp.Outside, "secret.txt"));

        AssertRejected("secret-link.txt", PathRejection.ReparsePoint);
    }

    [SymlinkFact]
    public void A_symlink_with_a_relative_target_is_rejected()
    {
        Directory.CreateSymbolicLink(Path.Combine(_temp.Area, "rel"), "..\\outside");

        AssertRejected("rel", PathRejection.ReparsePoint);
    }

    [SymlinkFact]
    public void A_dangling_symlink_is_rejected()
    {
        Directory.CreateSymbolicLink(Path.Combine(_temp.Area, "dangling-sym"), Path.Combine(_temp.Root, "nowhere"));

        AssertRejected("dangling-sym", PathRejection.ReparsePoint);
    }

    [SymlinkFact]
    public void A_symlink_in_the_middle_of_the_path_is_rejected()
    {
        var project = _temp.MakeDir("proj");
        Directory.CreateSymbolicLink(Path.Combine(project, "sym"), _temp.Outside);

        AssertRejected("proj\\sym\\secret.txt", PathRejection.ReparsePoint);
    }
}
