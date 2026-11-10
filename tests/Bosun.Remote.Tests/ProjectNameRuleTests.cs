namespace Bosun.Remote.Tests;

/// <summary>
/// The project-name rule (ADR-024 addendum, bs-4xw.17), written from the maintainer's decision: a
/// project name is accepted only when every character is an ASCII letter, an ASCII digit, a space
/// (U+0020 only), <c>.</c>, <c>_</c> or <c>-</c>; the first character is an ASCII letter or digit;
/// the last is not a space or a dot; and it is 1 to 64 characters long. It applies to
/// <see cref="DevelopmentAreaGuard.ResolveProject"/> only: the characters allowed in a multi-segment
/// path (<see cref="DevelopmentAreaGuard.Resolve"/>) are a separate, undecided question (bs-4xw.18).
/// Every test uses a temp directory as the area, never the real development folder.
/// </summary>
public sealed class ProjectNameRuleTests : IDisposable
{
    private readonly TempArea _temp = new();
    private readonly DevelopmentAreaGuard _guard;

    public ProjectNameRuleTests() => _guard = _temp.Guard();

    public void Dispose() => _temp.Dispose();

    private void AssertRefused(string? input, PathRejection expected)
    {
        var result = _guard.ResolveProject(input);

        Assert.False(result.IsAllowed, $"'{input}' was allowed as '{result.FullPath}'");
        Assert.Null(result.FullPath);
        Assert.Equal(expected, result.Rejection);
        Assert.False(string.IsNullOrWhiteSpace(result.Detail));
        Assert.DoesNotContain(_temp.Area, result.Detail!, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Accepted -----------------------------------------------------------------------------

    [Theory]
    [InlineData("bosun")]
    [InlineData("Jobber MCP")]
    [InlineData("Trimlight Job Tracker")]
    [InlineData("a")]
    [InlineData("A1")]
    [InlineData("my.project_v2-final")]
    [InlineData("1project")]
    [InlineData("a  b")]            // consecutive spaces inside
    [InlineData("a - b")]
    [InlineData("v1.0.0")]
    public void A_name_in_the_alphabet_resolves_to_the_literal_child_of_the_area(string name)
    {
        var result = _guard.ResolveProject(name);

        Assert.True(result.IsAllowed, $"'{name}' was refused: {result.Rejection} {result.Detail}");
        Assert.Null(result.Rejection);
        Assert.Equal(Path.Combine(_temp.Area, name), result.FullPath);
    }

    [Fact]
    public void A_64_character_name_is_accepted()
    {
        var name = new string('a', 64);

        Assert.Equal(Path.Combine(_temp.Area, name), _guard.ResolveProject(name).FullPath);
    }

    [Fact]
    public void A_name_with_a_space_names_the_folder_with_that_space_in_it()
    {
        var result = _guard.ResolveProject("Jobber MCP");

        Directory.CreateDirectory(result.FullPath!);

        Assert.Equal(_temp.Area, Path.GetDirectoryName(result.FullPath));
        Assert.Equal(["Jobber MCP"], Directory.EnumerateDirectories(_temp.Area).Select(d => Path.GetFileName(d)).ToArray());
    }

    [Fact]
    public void An_allowed_name_is_not_trimmed_normalised_or_case_folded()
    {
        Assert.Equal(Path.Combine(_temp.Area, "MiXeD  Case"), _guard.ResolveProject("MiXeD  Case").FullPath);
    }

    // ---- Refused with the name-rule reason ---------------------------------------------------

    [Fact]
    public void A_65_character_name_is_refused()
    {
        AssertRefused(new string('a', 65), PathRejection.NameNotAllowed);
    }

    [Theory]
    [InlineData("-project")]
    [InlineData(".project")]
    [InlineData("..project")]
    [InlineData("_project")]
    [InlineData(" project")]
    [InlineData("  project")]
    public void A_name_that_does_not_start_with_a_letter_or_digit_is_refused(string name)
    {
        AssertRefused(name, PathRejection.NameNotAllowed);
    }

    [Theory]
    [InlineData('&')]
    [InlineData(';')]
    [InlineData('%')]
    [InlineData('!')]
    [InlineData('$')]
    [InlineData('^')]
    [InlineData('\'')]
    [InlineData('(')]
    [InlineData(')')]
    [InlineData('[')]
    [InlineData(']')]
    [InlineData('{')]
    [InlineData('}')]
    [InlineData('=')]
    [InlineData(',')]
    [InlineData('+')]
    [InlineData('~')]
    [InlineData('#')]
    [InlineData('@')]
    [InlineData('`')]
    public void A_character_outside_the_alphabet_is_refused_wherever_it_sits(char c)
    {
        AssertRefused($"a{c}b", PathRejection.NameNotAllowed);
        AssertRefused($"ab{c}", PathRejection.NameNotAllowed);
        AssertRefused($"{c}ab", PathRejection.NameNotAllowed);
    }

    [Theory]
    [InlineData("a\u00A0b")]            // no-break space
    [InlineData("a\u00A0")]
    [InlineData("\u00A0a")]
    [InlineData("a\u2003b")]            // em space
    [InlineData("a\u3000b")]            // ideographic space
    [InlineData("a\u2028b")]            // line separator
    [InlineData("a\u0085b")]            // next line
    [InlineData("a\u202Eb")]            // right-to-left override
    [InlineData("\u00E9")]              // e acute
    [InlineData("caf\u00E9")]
    [InlineData("\u0430bc")]            // Cyrillic a
    [InlineData("\uFF21bc")]            // fullwidth A
    [InlineData("a\uFF11")]             // fullwidth 1
    [InlineData("a\u0663")]             // Arabic-Indic digit three
    [InlineData("\u0663a")]
    [InlineData("a\u200Bb")]            // zero width space
    [InlineData("a\u200B")]
    [InlineData("a\uFEFFb")]            // zero width no-break space / BOM
    [InlineData("\uFEFFabc")]
    [InlineData("e\u0301")]             // combining acute accent
    [InlineData("a\u0301bc")]
    [InlineData("\u212Aelvin")]         // Kelvin sign, folds to K
    [InlineData("\u0131")]              // dotless i, folds to I under some cultures
    public void A_non_ASCII_character_is_refused(string name)
    {
        AssertRefused(name, PathRejection.NameNotAllowed);
    }

    /// <summary>
    /// The lookalikes of dots, separators and colons from the guard's Unicode theory, as project
    /// names. They are a single segment, so the shape checks pass them and the name rule must stop them.
    /// </summary>
    [Theory]
    [InlineData("\uFF0E\uFF0E")]
    [InlineData("\uFF0E\uFF0E\uFF3Coutside")]
    [InlineData("\uFF0E\uFF0E\uFF0Foutside")]
    [InlineData("a\uFF3C\uFF0E\uFF0E\uFF3C\uFF0E\uFF0E\uFF3Coutside")]
    [InlineData("\u2024\u2024")]
    [InlineData("\u2025")]
    [InlineData("\uFE52\uFE52")]
    [InlineData("a\u2215b")]
    [InlineData("a\u2044b")]
    [InlineData("a\u29F5b")]
    [InlineData("a\uFE68b")]
    [InlineData("C\uFF1A\uFF3CWindows")]
    [InlineData("file.txt\uFF1A\uFF1A$DATA")]
    [InlineData("a\uA789b")]
    [InlineData("\uFF3C\uFF3Cserver\uFF3Cshare")]
    public void Lookalikes_of_dots_separators_and_colons_are_refused(string name)
    {
        AssertRefused(name, PathRejection.NameNotAllowed);
    }

    // ---- Refused earlier, by an existing check -----------------------------------------------
    //
    // These characters are in Path.GetInvalidFileNameChars, so the existing check refuses them
    // first, with its own reason. The name rule would refuse them as well; the order only decides
    // which reason the caller sees.

    [Theory]
    [InlineData("a\tb")]
    [InlineData("a\nb")]
    [InlineData("name\n")]
    [InlineData("name\r")]
    [InlineData("name\r\n")]
    [InlineData("a\0b")]
    [InlineData("name\0")]
    [InlineData("a\"b")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a|b")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    public void A_character_Windows_forbids_in_a_name_keeps_its_existing_reason(string name)
    {
        AssertRefused(name, PathRejection.InvalidCharacter);
    }

    // ---- Existing reasons are preserved -------------------------------------------------------

    [Theory]
    [InlineData("..", PathRejection.TraversalSegment)]
    [InlineData(".", PathRejection.TraversalSegment)]
    [InlineData("a/b", PathRejection.NotASingleSegment)]
    [InlineData("a\\b", PathRejection.NotASingleSegment)]
    [InlineData("a/", PathRejection.NotASingleSegment)]
    [InlineData("CON", PathRejection.ReservedDeviceName)]
    [InlineData("con", PathRejection.ReservedDeviceName)]
    [InlineData("NUL.txt", PathRejection.ReservedDeviceName)]
    [InlineData("COM1", PathRejection.ReservedDeviceName)]
    [InlineData("name.", PathRejection.TrailingDotOrSpace)]
    [InlineData("name ", PathRejection.TrailingDotOrSpace)]
    [InlineData("name. ", PathRejection.TrailingDotOrSpace)]
    [InlineData("", PathRejection.Empty)]
    [InlineData(null, PathRejection.Empty)]
    [InlineData(" ", PathRejection.Empty)]
    [InlineData("   ", PathRejection.Empty)]
    [InlineData("\t", PathRejection.Empty)]
    [InlineData("ab:c", PathRejection.AlternateDataStream)]
    [InlineData("C:\\x", PathRejection.DriveSpecifier)]
    [InlineData("\\x", PathRejection.Rooted)]
    [InlineData("\\\\server\\share", PathRejection.UncOrDevicePath)]
    public void The_specific_reasons_of_the_shared_checks_still_win(string? input, PathRejection expected)
    {
        AssertRefused(input, expected);
    }

    [Fact]
    public void A_name_over_255_characters_is_still_reported_as_too_long()
    {
        AssertRefused(new string('a', 256), PathRejection.SegmentTooLong);
    }

    // ---- Resolve is unchanged -----------------------------------------------------------------

    /// <summary>
    /// The characters allowed in a multi-segment path are not decided (bs-4xw.18), so the project-name
    /// rule must not leak into <see cref="DevelopmentAreaGuard.Resolve"/>.
    /// </summary>
    [Theory]
    [InlineData("my project/.git")]
    [InlineData("proj\\sub folder\\file.txt")]
    [InlineData(".hidden")]
    [InlineData("..foo")]
    [InlineData("-dash")]
    [InlineData("caf\u00E9")]
    [InlineData("a&b")]
    [InlineData("x;y/z")]
    [InlineData("a\u00A0b")]
    [InlineData("a[1]")]
    public void Resolve_still_allows_what_it_allowed_before(string path)
    {
        var result = _guard.Resolve(path);

        Assert.True(result.IsAllowed, $"'{path}' was refused: {result.Rejection} {result.Detail}");
        Assert.Equal(Path.Combine([_temp.Area, .. path.Split('\\', '/')]), result.FullPath);
    }

    [Fact]
    public void Resolve_never_reports_the_name_rule_reason()
    {
        foreach (var path in new[] { "a&b", "\u00E9", "a\u200Bb", new string('a', 65), "-x", " x" })
        {
            Assert.NotEqual(PathRejection.NameNotAllowed, _guard.Resolve(path).Rejection);
        }
    }

    // ---- Where the rule sits among the other checks -------------------------------------------

    [Fact]
    public void A_bad_name_is_reported_as_bad_even_when_the_area_is_missing()
    {
        var missing = new DevelopmentAreaGuard(Path.Combine(_temp.Root, "no-such-area"));

        Assert.Equal(PathRejection.NameNotAllowed, missing.ResolveProject("a&b").Rejection);
        Assert.Equal(PathRejection.AreaUnavailable, missing.ResolveProject("proj").Rejection);
    }

    [Fact]
    public void The_rule_runs_before_the_file_system_is_walked()
    {
        // A junction with a non-ASCII name: Resolve walks the file system and reports the link, so
        // ResolveProject reporting the name rule shows the rule ran first.
        var name = "caf\u00E9";
        TempArea.CreateJunction(Path.Combine(_temp.Area, name), _temp.Outside);

        Assert.Equal(PathRejection.ReparsePoint, _guard.Resolve(name).Rejection);
        Assert.Equal(PathRejection.NameNotAllowed, _guard.ResolveProject(name).Rejection);
    }

    [Fact]
    public void An_allowed_name_that_is_a_junction_is_still_refused_as_a_link()
    {
        TempArea.CreateJunction(Path.Combine(_temp.Area, "linked project"), _temp.Outside);

        Assert.Equal(PathRejection.ReparsePoint, _guard.ResolveProject("linked project").Rejection);
    }

    // ---- What a rejection says ----------------------------------------------------------------

    [Theory]
    [InlineData("a&b")]
    [InlineData("-x")]
    [InlineData("a\u202Eb")]
    [InlineData("a\u200Bb")]
    [InlineData("caf\u00E9")]
    [InlineData("\uFF0E\uFF0E")]
    [InlineData("x;new-tab cmd")]
    public void The_detail_states_the_rule_without_the_area_or_the_name(string name)
    {
        var result = _guard.ResolveProject(name);

        Assert.Equal(PathRejection.NameNotAllowed, result.Rejection);
        Assert.DoesNotContain(_temp.Area, result.Detail!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_temp.Root, result.Detail!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(name, result.Detail!, StringComparison.Ordinal);
        Assert.All(result.Detail!, c => Assert.True(c >= ' ' && c <= '~', $"non-printable-ASCII U+{(int)c:X4} in the detail"));
        Assert.Contains("64", result.Detail!, StringComparison.Ordinal);
    }
}
