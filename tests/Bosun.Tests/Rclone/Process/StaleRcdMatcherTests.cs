using Bosun.Rclone.Process;

namespace Bosun.Tests.Rclone.Process;

/// <summary>
/// The kill decision (ADR-020 §2, bs-772). A false positive kills someone's process, so the
/// table leans on cases that must NOT match. Pure functions, no process.
/// </summary>
public sealed class StaleRcdMatcherTests
{
    private const int Port = 5572;
    private const string Config = @"C:\Users\Barry\AppData\Roaming\rclone\rclone.conf";
    private const string User = @"DESKTOP\Barry";

    // -- command-line table ------------------------------------------------------------------

    [Theory]
    // The exact line Bosun's launcher produces (ArgumentList, nothing needs quoting).
    [InlineData(@"C:\Users\Barry\AppData\Local\rclone\rclone.exe rcd --rc-addr 127.0.0.1:5572 --config C:\Users\Barry\AppData\Roaming\rclone\rclone.conf")]
    // Quoted executable and quoted config.
    [InlineData("\"C:\\Users\\Barry\\AppData\\Local\\rclone\\rclone.exe\" rcd --rc-addr 127.0.0.1:5572 --config \"C:\\Users\\Barry\\AppData\\Roaming\\rclone\\rclone.conf\"")]
    // Quoted executable with a space in its path.
    [InlineData("\"C:\\Program Files\\rclone\\rclone.exe\" rcd --rc-addr 127.0.0.1:5572 --config C:\\Users\\Barry\\AppData\\Roaming\\rclone\\rclone.conf")]
    // Argument order does not matter.
    [InlineData(@"rclone.exe --config C:\Users\Barry\AppData\Roaming\rclone\rclone.conf --rc-addr 127.0.0.1:5572 rcd")]
    [InlineData(@"rclone.exe --rc-addr 127.0.0.1:5572 --config C:\Users\Barry\AppData\Roaming\rclone\rclone.conf rcd")]
    // --flag=value spelling.
    [InlineData(@"rclone.exe rcd --rc-addr=127.0.0.1:5572 --config=C:\Users\Barry\AppData\Roaming\rclone\rclone.conf")]
    // Quoting around the value of an = flag.
    [InlineData("rclone.exe rcd --rc-addr=127.0.0.1:5572 \"--config=C:\\Users\\Barry\\AppData\\Roaming\\rclone\\rclone.conf\"")]
    // Path case and separators are normalised.
    [InlineData(@"rclone.exe rcd --rc-addr 127.0.0.1:5572 --config c:\users\barry\appdata\roaming\RCLONE\rclone.conf")]
    [InlineData("rclone.exe rcd --rc-addr 127.0.0.1:5572 --config C:/Users/Barry/AppData/Roaming/rclone/rclone.conf")]
    // Extra whitespace; bare 'rclone' with no extension in argv[0].
    [InlineData("  rclone   rcd   --rc-addr   127.0.0.1:5572   --config   C:\\Users\\Barry\\AppData\\Roaming\\rclone\\rclone.conf  ")]
    public void CommandLine_that_is_exactly_Bosuns_own_matches(string commandLine)
    {
        Assert.True(StaleRcdMatcher.CommandLineMatches(commandLine, Port, Config));
    }

    [Theory]
    // Different port, including one that merely has our port as a prefix.
    [InlineData(@"rclone.exe rcd --rc-addr 127.0.0.1:5573 --config C:\Users\Barry\AppData\Roaming\rclone\rclone.conf")]
    [InlineData(@"rclone.exe rcd --rc-addr 127.0.0.1:55720 --config C:\Users\Barry\AppData\Roaming\rclone\rclone.conf")]
    // Not the loopback address we bind.
    [InlineData(@"rclone.exe rcd --rc-addr localhost:5572 --config C:\Users\Barry\AppData\Roaming\rclone\rclone.conf")]
    [InlineData(@"rclone.exe rcd --rc-addr 0.0.0.0:5572 --config C:\Users\Barry\AppData\Roaming\rclone\rclone.conf")]
    [InlineData(@"rclone.exe rcd --rc-addr :5572 --config C:\Users\Barry\AppData\Roaming\rclone\rclone.conf")]
    // Different config.
    [InlineData(@"rclone.exe rcd --rc-addr 127.0.0.1:5572 --config C:\Users\Barry\other\rclone.conf")]
    [InlineData(@"rclone.exe rcd --rc-addr 127.0.0.1:5572 --config C:\Users\Barry\AppData\Roaming\rclone\rclone.conf.bak")]
    // Relative config: relative to a working directory we cannot see, so never a match.
    [InlineData(@"rclone.exe rcd --rc-addr 127.0.0.1:5572 --config rclone.conf")]
    // Missing pieces.
    [InlineData(@"rclone.exe rcd --rc-addr 127.0.0.1:5572")]
    [InlineData(@"rclone.exe rcd --config C:\Users\Barry\AppData\Roaming\rclone\rclone.conf")]
    [InlineData(@"rclone.exe --rc-addr 127.0.0.1:5572 --config C:\Users\Barry\AppData\Roaming\rclone\rclone.conf")]
    [InlineData(@"rclone.exe rcd --rc-addr")]
    [InlineData(@"rclone.exe rcd --rc-addr 127.0.0.1:5572 --config")]
    [InlineData("rclone.exe rcd --rc-addr= --config=")]
    // A different subcommand.
    [InlineData(@"rclone.exe serve sftp --rc-addr 127.0.0.1:5572 --config C:\Users\Barry\AppData\Roaming\rclone\rclone.conf")]
    [InlineData(@"rclone.exe mount remote: X: --rc-addr 127.0.0.1:5572 --config C:\Users\Barry\AppData\Roaming\rclone\rclone.conf")]
    // Anything beyond the three things Bosun passes is somebody else's invocation.
    [InlineData(@"rclone.exe rcd --rc-addr 127.0.0.1:5572 --config C:\Users\Barry\AppData\Roaming\rclone\rclone.conf --rc-no-auth")]
    [InlineData(@"rclone.exe rcd --rc-web-gui --rc-addr 127.0.0.1:5572 --config C:\Users\Barry\AppData\Roaming\rclone\rclone.conf")]
    [InlineData(@"rclone.exe rcd --rc-addr 127.0.0.1:5572 --rc-addr 127.0.0.1:5572 --config C:\Users\Barry\AppData\Roaming\rclone\rclone.conf")]
    [InlineData(@"rclone.exe rcd rcd --rc-addr 127.0.0.1:5572 --config C:\Users\Barry\AppData\Roaming\rclone\rclone.conf")]
    // Not rclone at all, or a renamed lookalike.
    [InlineData(@"notrclone.exe rcd --rc-addr 127.0.0.1:5572 --config C:\Users\Barry\AppData\Roaming\rclone\rclone.conf")]
    [InlineData(@"C:\tools\rclone.exe.bat rcd --rc-addr 127.0.0.1:5572 --config C:\Users\Barry\AppData\Roaming\rclone\rclone.conf")]
    [InlineData(@"python.exe -c rcd --rc-addr 127.0.0.1:5572 --config C:\Users\Barry\AppData\Roaming\rclone\rclone.conf")]
    // Empty and degenerate.
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("rclone.exe")]
    public void CommandLine_that_is_not_exactly_Bosuns_own_does_not_match(string commandLine)
    {
        Assert.False(StaleRcdMatcher.CommandLineMatches(commandLine, Port, Config));
    }

    // -- splitter, so the table above stands on a known tokeniser ------------------------------

    [Theory]
    [InlineData("a.exe b c", new[] { "a.exe", "b", "c" })]
    [InlineData("\"a b.exe\" \"c d\" e", new[] { "a b.exe", "c d", "e" })]
    [InlineData("a.exe \"x\\\"y\"", new[] { "a.exe", "x\"y" })] // \" is a literal quote
    [InlineData("a.exe x\\\\y", new[] { "a.exe", "x\\\\y" })] // backslashes not before a quote stay
    [InlineData("a.exe \"x\\\\\" y", new[] { "a.exe", "x\\", "y" })] // 2n backslashes then quote: n + toggle
    [InlineData("a.exe \"\"", new[] { "a.exe", "" })]
    [InlineData("\"C:\\dir\\\" a", new[] { "C:\\dir\\", "a" })] // program name takes no escapes
    public void WindowsCommandLine_splits_like_CommandLineToArgv(string commandLine, string[] expected)
    {
        Assert.Equal(expected, WindowsCommandLine.Split(commandLine));
    }

    // -- the whole decision: image, owner, and every "unknown" field ---------------------------

    [Fact]
    public void A_fully_matching_process_is_Bosuns_own_rcd()
    {
        Assert.True(StaleRcdMatcher.IsBosunsOwnRcd(Matching(), Port, Config, User));
    }

    [Fact]
    public void Owner_comparison_ignores_case()
    {
        Assert.True(StaleRcdMatcher.IsBosunsOwnRcd(Matching() with { Owner = @"desktop\BARRY" }, Port, Config, User));
    }

    [Fact]
    public void A_different_user_is_never_killed()
    {
        Assert.False(StaleRcdMatcher.IsBosunsOwnRcd(Matching() with { Owner = @"DESKTOP\SomeoneElse" }, Port, Config, User));
        Assert.False(StaleRcdMatcher.IsBosunsOwnRcd(Matching() with { Owner = @"NT AUTHORITY\SYSTEM" }, Port, Config, User));
    }

    [Theory]
    [InlineData("rclone.exe.old")]
    [InlineData("notrclone.exe")]
    [InlineData("python.exe")]
    [InlineData("")]
    public void A_process_that_is_not_rclone_exe_is_never_killed(string name)
    {
        Assert.False(StaleRcdMatcher.IsBosunsOwnRcd(Matching() with { Name = name }, Port, Config, User));
    }

    [Fact]
    public void A_different_port_or_config_for_the_inspected_process_is_never_killed()
    {
        Assert.False(StaleRcdMatcher.IsBosunsOwnRcd(Matching(), 5999, Config, User));
        Assert.False(StaleRcdMatcher.IsBosunsOwnRcd(Matching(), Port, @"C:\elsewhere\rclone.conf", User));
    }

    [Theory]
    [InlineData("Name")]
    [InlineData("ImagePath")]
    [InlineData("CommandLine")]
    [InlineData("Owner")]
    [InlineData("StartTime")]
    public void Any_missing_or_unreadable_field_is_never_killed(string missingField)
    {
        var process = missingField switch
        {
            "Name" => Matching() with { Name = null },
            "ImagePath" => Matching() with { ImagePath = null },
            "CommandLine" => Matching() with { CommandLine = null },
            "Owner" => Matching() with { Owner = null },
            "StartTime" => Matching() with { StartTime = null },
            _ => throw new ArgumentOutOfRangeException(nameof(missingField)),
        };

        Assert.False(StaleRcdMatcher.IsBosunsOwnRcd(process, Port, Config, User));
    }

    internal static ProcessDescription Matching(int pid = 31032) => new()
    {
        ProcessId = pid,
        Name = "rclone.exe",
        ImagePath = @"C:\Users\Barry\AppData\Local\rclone\rclone.exe",
        CommandLine = $@"C:\Users\Barry\AppData\Local\rclone\rclone.exe rcd --rc-addr 127.0.0.1:{Port} --config {Config}",
        Owner = User,
        StartTime = new DateTimeOffset(2026, 9, 16, 16, 0, 55, TimeSpan.Zero),
    };
}
