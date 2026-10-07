using System.Text.RegularExpressions;
using Bosun.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Bosun.Tests.Hosting;

/// <summary>
/// bs-qcs: production logs at Information (Debug only on request) and the file sink is capped. The
/// level logic is tested on <see cref="BosunLogging"/> directly, with a collecting sink, so no host
/// is built and the machine's real BOSUN_LOG_LEVEL is never read.
/// </summary>
public sealed class BosunLoggingTests : IDisposable
{
    private readonly string logDirectory =
        Path.Combine(Path.GetTempPath(), "bosun-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(logDirectory))
        {
            Directory.Delete(logDirectory, recursive: true);
        }
    }

    // -- the level -------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void With_nothing_set_the_level_is_Information(string? setting)
    {
        Assert.Equal(LogEventLevel.Information, BosunLogging.ResolveMinimumLevel(setting, out var problem));
        Assert.Null(problem);
    }

    [Theory]
    [InlineData("Debug", LogEventLevel.Debug)]
    [InlineData("debug", LogEventLevel.Debug)]
    [InlineData(" DEBUG ", LogEventLevel.Debug)]
    [InlineData("Verbose", LogEventLevel.Verbose)]
    [InlineData("Warning", LogEventLevel.Warning)]
    public void A_level_name_is_honoured_without_regard_to_case(string setting, LogEventLevel expected)
    {
        Assert.Equal(expected, BosunLogging.ResolveMinimumLevel(setting, out var problem));
        Assert.Null(problem);
    }

    [Theory]
    [InlineData("loud")]
    [InlineData("99")]
    [InlineData("Debug,Information")]
    public void An_unusable_value_falls_back_to_Information_and_says_so(string setting)
    {
        Assert.Equal(LogEventLevel.Information, BosunLogging.ResolveMinimumLevel(setting, out var problem));
        Assert.Contains("BOSUN_LOG_LEVEL", problem, StringComparison.Ordinal);
        Assert.Contains(setting, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void By_default_Debug_is_dropped_and_Information_is_kept()
    {
        var events = LogThrough(BosunLogging.ResolveMinimumLevel(null, out _), "Bosun.Anything");

        Assert.Equal(["Information", "Warning", "Error"], events);
    }

    [Fact]
    public void Opted_in_to_Debug_the_debug_lines_appear()
    {
        var events = LogThrough(BosunLogging.ResolveMinimumLevel("Debug", out _), "Bosun.Anything");

        Assert.Equal(["Debug", "Information", "Warning", "Error"], events);
    }

    [Theory]
    [InlineData("Microsoft.Hosting.Lifetime")]
    [InlineData("System.Net.Http.HttpClient")]
    public void Framework_chatter_stays_at_Warning_even_when_Debug_is_on(string sourceContext)
    {
        Assert.Equal(["Warning", "Error"], LogThrough(LogEventLevel.Debug, sourceContext));
        Assert.Equal(["Warning", "Error"], LogThrough(LogEventLevel.Information, sourceContext));
    }

    [Fact]
    public async Task The_built_host_logs_at_Information_unless_asked_for_Debug()
    {
        var quiet = await RunHostAsync(logLevel: null);
        var chatty = await RunHostAsync(logLevel: "Debug");

        Assert.Contains("information-line", quiet, StringComparison.Ordinal);
        Assert.DoesNotContain("debug-line", quiet, StringComparison.Ordinal);
        Assert.Contains("information-line", chatty, StringComparison.Ordinal);
        Assert.Contains("debug-line", chatty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_bad_level_setting_is_reported_in_the_log_and_Information_is_used()
    {
        var text = await RunHostAsync(logLevel: "loud");

        Assert.Contains("BOSUN_LOG_LEVEL='loud' is not a log level", text, StringComparison.Ordinal);
        Assert.DoesNotContain("debug-line", text, StringComparison.Ordinal);
    }

    // -- the file caps ---------------------------------------------------------------------------

    [Fact]
    public void The_default_caps_hold_the_log_directory_to_200_MB()
    {
        Assert.InRange(BosunLogging.FileSizeLimitBytes, 1, 21L * 1024 * 1024);
        Assert.True(
            BosunLogging.FileSizeLimitBytes * BosunLogging.RetainedFileCountLimit <= 200L * 1024 * 1024,
            "file size limit x retained files must stay at or below 200 MB");
    }

    [Fact]
    public void The_file_sink_rolls_at_the_size_limit_keeps_only_the_newest_files_and_names_them_for_the_day()
    {
        const int retained = 3;
        using (var logger = BosunLogging.WriteToCappedFile(
                   new LoggerConfiguration(), logDirectory, "{Message:lj}{NewLine}",
                   fileSizeLimitBytes: 2_000, retainedFileCountLimit: retained).CreateLogger())
        {
            for (var i = 0; i < 100; i++)
            {
                logger.Information("line {Number} {Padding}", i, new string('x', 200));
            }
        }

        var files = Directory.GetFiles(logDirectory, "bosun-*.log").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(retained, files.Count);
        Assert.All(files, f => Assert.Matches(new Regex(@"^bosun-\d{8}(_\d{3})?\.log$"), f!));
        Assert.Contains(files, f => Regex.IsMatch(f!, @"_\d{3}\.log$"));
        Assert.All(Directory.GetFiles(logDirectory), f => Assert.True(new FileInfo(f).Length <= 2_000 + 400, $"{f} exceeded the size limit"));

        // Newest content survives; the oldest was pruned.
        var all = string.Concat(files.Select(f => File.ReadAllText(Path.Combine(logDirectory, f!))));
        Assert.Contains("line 99 ", all, StringComparison.Ordinal);
        Assert.DoesNotContain("line 0 ", all, StringComparison.Ordinal);
    }

    // -- helpers ---------------------------------------------------------------------------------

    private static List<string> LogThrough(LogEventLevel minimum, string sourceContext)
    {
        var sink = new CollectingSink();
        using var logger = BosunLogging.ApplyLevels(new LoggerConfiguration(), minimum).WriteTo.Sink(sink).CreateLogger();
        var scoped = logger.ForContext(Constants.SourceContextPropertyName, sourceContext);

        scoped.Verbose("verbose");
        scoped.Debug("debug");
        scoped.Information("information");
        scoped.Warning("warning");
        scoped.Error("error");

        return sink.Events.Select(e => e.Level.ToString()).ToList();
    }

    private async Task<string> RunHostAsync(string? logLevel)
    {
        var directory = Path.Combine(logDirectory, Guid.NewGuid().ToString("N"));
        var host = BosunHostFactory.CreateHost(
            new BosunHostOptions
            {
                LogDirectory = directory,
                ConfigPath = Path.Combine(directory, "hosts.toml"),
                LogLevel = logLevel,
            },
            registerStartupOrchestrator: false);
        try
        {
            var logger = host.Services.GetRequiredService<ILogger<BosunLoggingTests>>();
            logger.LogDebug("debug-line");
            logger.LogInformation("information-line");
        }
        finally
        {
            host.Dispose(); // disposes the Serilog logger, which flushes the file
        }

        var file = Assert.Single(Directory.GetFiles(directory, "bosun-*.log"));
        return await File.ReadAllTextAsync(file);
    }

    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
