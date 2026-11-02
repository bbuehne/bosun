using Bosun.Rclone.Process;
using Bosun.Tests.Rclone.Process.Fakes;

namespace Bosun.Tests.Rclone.Process;

/// <summary>
/// bs-64k: <see cref="RcloneExecutableResolver"/> finds rclone on PATH first, then in the usual
/// install locations. Everything goes through a fake <see cref="IExecutableSearchEnvironment"/>:
/// no real PATH, no real filesystem.
/// </summary>
public sealed class RcloneExecutableResolverTests
{
    private const string Local = FakeExecutableSearchEnvironment.LocalAppData;
    private const string Pf = FakeExecutableSearchEnvironment.ProgramFiles;

    private const string WingetUserPackage =
        Local + @"\Microsoft\WinGet\Packages\Rclone.Rclone_Microsoft.Winget.Source_8wekyb3d8bbwe\rclone-v1.75.2-windows-amd64\rclone.exe";

    private const string WingetMachinePackage =
        Pf + @"\WinGet\Packages\Rclone.Rclone_Microsoft.Winget.Source_8wekyb3d8bbwe\rclone-v1.75.2-windows-amd64\rclone.exe";

    /// <summary>Every location, in the order the resolver must try them, with the source label it
    /// reports. The order test walks this list.</summary>
    private static readonly (string Source, string Path)[] FallbacksInOrder =
    [
        (@"%LOCALAPPDATA%\rclone", Local + @"\rclone\rclone.exe"),
        (@"%ProgramFiles%\rclone", Pf + @"\rclone\rclone.exe"),
        ("winget Links", Local + @"\Microsoft\WinGet\Links\rclone.exe"),
        ("winget packages (user)", WingetUserPackage),
        ("winget packages (machine)", WingetMachinePackage),
        ("scoop", FakeExecutableSearchEnvironment.UserProfile + @"\scoop\shims\rclone.exe"),
        ("Chocolatey", FakeExecutableSearchEnvironment.ProgramData + @"\chocolatey\bin\rclone.exe"),
    ];

    private static RcloneExecutableResolution Resolve(FakeExecutableSearchEnvironment env, string configured = "rclone") =>
        new RcloneExecutableResolver(env).Resolve(configured);

    // -- PATH ----------------------------------------------------------------------------------

    [Fact]
    public void A_PATH_hit_wins_even_when_every_fallback_location_also_has_rclone()
    {
        var env = FakeExecutableSearchEnvironment.Standard();
        env.SetVariable("PATH", @"C:\Windows;C:\tools\bin");
        env.AddFile(@"C:\tools\bin\rclone.exe");
        foreach (var (_, path) in FallbacksInOrder)
        {
            env.AddFile(path);
        }

        var result = Resolve(env);

        Assert.True(result.Found);
        Assert.Equal(@"C:\tools\bin\rclone.exe", result.Path);
        Assert.Equal("PATH", result.Source);
    }

    [Fact]
    public void A_PATH_hit_does_not_look_at_any_fallback_location()
    {
        var env = FakeExecutableSearchEnvironment.Standard();
        env.SetVariable("PATH", @"C:\tools\bin");
        env.AddFile(@"C:\tools\bin\rclone.exe");

        Resolve(env);

        Assert.Equal([@"C:\tools\bin\rclone.exe"], env.FileExistsCalls);
    }

    [Fact]
    public void The_first_PATH_directory_holding_rclone_wins()
    {
        var env = FakeExecutableSearchEnvironment.Standard();
        env.SetVariable("PATH", @"C:\one;C:\two;C:\three");
        env.AddFile(@"C:\two\rclone.exe");
        env.AddFile(@"C:\three\rclone.exe");

        Assert.Equal(@"C:\two\rclone.exe", Resolve(env).Path);
    }

    [Fact]
    public void Quoted_and_empty_PATH_entries_are_tolerated()
    {
        var env = FakeExecutableSearchEnvironment.Standard();
        env.SetVariable("PATH", @";;""C:\Program Files\Some Tool"" ;  ;C:\tools\bin;");
        env.AddFile(@"C:\Program Files\Some Tool\rclone.exe");

        var result = Resolve(env);

        Assert.Equal(@"C:\Program Files\Some Tool\rclone.exe", result.Path);
        Assert.Equal("PATH", result.Source);
    }

    [Fact]
    public void A_configured_name_ending_in_exe_is_not_given_a_second_extension()
    {
        var env = FakeExecutableSearchEnvironment.Standard();
        env.SetVariable("PATH", @"C:\tools\bin");
        env.AddFile(@"C:\tools\bin\rclone.exe");

        Assert.Equal(@"C:\tools\bin\rclone.exe", Resolve(env, "rclone.exe").Path);
    }

    [Fact]
    public void A_missing_PATH_variable_is_not_an_error_and_the_fallbacks_still_run()
    {
        var env = FakeExecutableSearchEnvironment.Standard().SetVariable("PATH", null);
        env.AddFile(FallbacksInOrder[0].Path);

        Assert.Equal(FallbacksInOrder[0].Path, Resolve(env).Path);
    }

    // -- Fallbacks -----------------------------------------------------------------------------

    public static TheoryData<string, string> EachFallback()
    {
        var data = new TheoryData<string, string>();
        foreach (var (source, path) in FallbacksInOrder)
        {
            data.Add(source, path);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EachFallback))]
    public void Each_fallback_location_is_found_when_PATH_misses(string expectedSource, string path)
    {
        var env = FakeExecutableSearchEnvironment.Standard();
        env.AddFile(path);

        var result = Resolve(env);

        Assert.True(result.Found);
        Assert.Equal(path, result.Path);
        Assert.Equal(expectedSource, result.Source);
    }

    [Fact]
    public void The_fallbacks_are_tried_in_the_documented_order()
    {
        var env = FakeExecutableSearchEnvironment.Standard();
        foreach (var (_, path) in FallbacksInOrder)
        {
            env.AddFile(path);
        }

        // With everything present the first wins; remove the winner and the next must take over.
        foreach (var (source, path) in FallbacksInOrder)
        {
            var result = Resolve(env);
            Assert.Equal(source, result.Source);
            Assert.Equal(path, result.Path);
            env.RemoveFile(path);
        }

        Assert.False(Resolve(env).Found);
    }

    [Fact]
    public void Scoop_uses_the_SCOOP_variable_as_its_root_when_it_is_set()
    {
        var env = FakeExecutableSearchEnvironment.Standard().SetVariable("SCOOP", @"D:\apps\scoop");
        env.AddFile(@"D:\apps\scoop\shims\rclone.exe");

        var result = Resolve(env);

        Assert.Equal(@"D:\apps\scoop\shims\rclone.exe", result.Path);
        Assert.Equal("scoop", result.Source);
    }

    [Fact]
    public void Chocolatey_uses_the_ChocolateyInstall_variable_as_its_root_when_it_is_set()
    {
        var env = FakeExecutableSearchEnvironment.Standard().SetVariable("ChocolateyInstall", @"D:\choco");
        env.AddFile(@"D:\choco\bin\rclone.exe");

        var result = Resolve(env);

        Assert.Equal(@"D:\choco\bin\rclone.exe", result.Path);
        Assert.Equal("Chocolatey", result.Source);
    }

    [Fact]
    public void A_location_whose_base_variable_is_not_set_is_skipped_rather_than_guessed()
    {
        var env = FakeExecutableSearchEnvironment.Standard()
            .SetVariable("LOCALAPPDATA", null)
            .SetVariable("USERPROFILE", null);

        var result = Resolve(env);

        Assert.False(result.Found);
        // Nothing is listed under the (unset) local app data folder, and scoop (which needs
        // USERPROFILE) is absent; the locations whose variables ARE set are still searched.
        Assert.DoesNotContain(result.Searched, s => s.StartsWith(Local, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Searched, s => s.Contains("scoop", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Searched, s => s.Contains(@"\chocolatey\bin\", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Searched, s => s.StartsWith(Pf, StringComparison.OrdinalIgnoreCase));
    }

    // -- winget's version/hash wildcard directories ---------------------------------------------

    [Fact]
    public void Winget_is_found_whatever_version_and_hash_its_folders_carry()
    {
        var env = FakeExecutableSearchEnvironment.Standard();
        var path = Local + @"\Microsoft\WinGet\Packages\Rclone.Rclone_SomeOtherSource_abc123\rclone-v9.9.9-windows-arm64\rclone.exe";
        env.AddFile(path);

        Assert.Equal(path, Resolve(env).Path);
    }

    [Fact]
    public void Winget_prefers_the_newest_looking_extract_folder_when_several_have_rclone()
    {
        var env = FakeExecutableSearchEnvironment.Standard();
        var package = Local + @"\Microsoft\WinGet\Packages\Rclone.Rclone_Microsoft.Winget.Source_8wekyb3d8bbwe";
        env.AddFile(package + @"\rclone-v1.74.0-windows-amd64\rclone.exe");
        env.AddFile(package + @"\rclone-v1.75.2-windows-amd64\rclone.exe");

        Assert.Equal(package + @"\rclone-v1.75.2-windows-amd64\rclone.exe", Resolve(env).Path);
    }

    [Fact]
    public void Winget_skips_an_extract_folder_that_has_no_rclone_and_uses_the_one_that_does()
    {
        var env = FakeExecutableSearchEnvironment.Standard();
        var package = Local + @"\Microsoft\WinGet\Packages\Rclone.Rclone_Microsoft.Winget.Source_8wekyb3d8bbwe";
        env.AddFile(package + @"\rclone-v1.75.2-windows-amd64\git-notes.txt");
        env.AddFile(package + @"\rclone-v1.74.0-windows-amd64\rclone.exe");

        Assert.Equal(package + @"\rclone-v1.74.0-windows-amd64\rclone.exe", Resolve(env).Path);
    }

    [Fact]
    public void Winget_ignores_other_packages_and_rclone_files_outside_an_rclone_extract_folder()
    {
        var env = FakeExecutableSearchEnvironment.Standard();
        var packages = Local + @"\Microsoft\WinGet\Packages";
        env.AddFile(packages + @"\Other.Tool_Microsoft.Winget.Source_8wekyb3d8bbwe\rclone-v1.0-windows-amd64\rclone.exe");
        env.AddFile(packages + @"\Rclone.Rclone_Microsoft.Winget.Source_8wekyb3d8bbwe\rclone.exe");
        env.AddFile(packages + @"\Rclone.Rclone_Microsoft.Winget.Source_8wekyb3d8bbwe\tools\rclone.exe");

        Assert.False(Resolve(env).Found);
    }

    [Fact]
    public void A_machine_with_no_winget_packages_folder_simply_does_not_find_rclone_there()
    {
        var env = FakeExecutableSearchEnvironment.Standard();

        var result = Resolve(env);

        Assert.False(result.Found);
    }

    // -- Not found ------------------------------------------------------------------------------

    [Fact]
    public void Nothing_found_lists_every_place_searched_in_order()
    {
        var env = FakeExecutableSearchEnvironment.Standard();

        var result = Resolve(env);

        Assert.False(result.Found);
        Assert.Null(result.Path);
        Assert.Null(result.Source);
        Assert.Equal(
            [
                "PATH",
                Local + @"\rclone\rclone.exe",
                Pf + @"\rclone\rclone.exe",
                Local + @"\Microsoft\WinGet\Links\rclone.exe",
                Local + @"\Microsoft\WinGet\Packages\Rclone.Rclone_*\rclone-*\rclone.exe",
                Pf + @"\WinGet\Packages\Rclone.Rclone_*\rclone-*\rclone.exe",
                FakeExecutableSearchEnvironment.UserProfile + @"\scoop\shims\rclone.exe",
                FakeExecutableSearchEnvironment.ProgramData + @"\chocolatey\bin\rclone.exe",
            ],
            result.Searched);
    }

    [Fact]
    public void The_not_found_exception_message_names_each_searched_location_and_what_to_do()
    {
        var env = FakeExecutableSearchEnvironment.Standard();
        var result = Resolve(env);

        var message = new RcloneExecutableNotFoundException("rclone", result.Searched).Message;

        foreach (var searched in result.Searched)
        {
            Assert.Contains(searched, message, StringComparison.Ordinal);
        }

        Assert.Contains("install it", message, StringComparison.Ordinal);
        Assert.Contains("winget install Rclone.Rclone", message, StringComparison.Ordinal);
        Assert.Contains("restart Bosun", message, StringComparison.Ordinal);
        Assert.Contains("terminal profiles and SSH sessions are unaffected", message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_not_found_message_does_not_address_or_blame_the_reader()
    {
        var message = new RcloneExecutableNotFoundException(
            "rclone", Resolve(FakeExecutableSearchEnvironment.Standard()).Searched).Message;

        Assert.DoesNotContain("you ", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("your ", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("forgot", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("failed to", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_exception_exposes_the_searched_locations()
    {
        var searched = new[] { "PATH", @"C:\x\rclone.exe" };

        var ex = new RcloneExecutableNotFoundException("rclone", searched);

        Assert.Equal(searched, ex.SearchedLocations);
        Assert.Equal("rclone", ex.ExecutablePath);
    }

    [Fact]
    public void The_launcher_form_of_the_exception_has_no_list_but_the_same_advice()
    {
        var ex = new RcloneExecutableNotFoundException("rclone", new InvalidOperationException("os said no"));

        Assert.Empty(ex.SearchedLocations);
        Assert.Contains("could not be found", ex.Message, StringComparison.Ordinal);
        Assert.Contains("restart Bosun", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("looked in", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // -- Explicit paths -------------------------------------------------------------------------

    [Theory]
    [InlineData(@"C:\tools\rclone\rclone.exe")]
    [InlineData(@".\rclone.exe")]
    [InlineData("tools/rclone")]
    public void A_configured_value_that_names_a_directory_is_passed_through_without_searching(string configured)
    {
        var env = FakeExecutableSearchEnvironment.Standard();

        var result = Resolve(env, configured);

        Assert.True(result.Found);
        Assert.Equal(configured, result.Path);
        Assert.Equal("configured path", result.Source);
        Assert.Empty(env.FileExistsCalls);
    }
}
