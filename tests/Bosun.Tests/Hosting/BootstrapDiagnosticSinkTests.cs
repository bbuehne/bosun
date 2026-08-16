using Bosun.Hosting;
using Bosun.Tests.Hosting.Fakes;

namespace Bosun.Tests.Hosting;

/// <summary>
/// Covers the production <see cref="IBootstrapDiagnosticSink"/>: it must attempt the Windows
/// Event Log, must always also attempt its fixed fallback file, and must never throw regardless
/// of which of those two attempts fail. Every test injects <see cref="FakeEventLogWriter"/> and a
/// fallback path under a temp directory the test owns -- never the real Windows Event Log and
/// never the real <c>%TEMP%</c> (CLAUDE.md worktree-safety rules).
/// </summary>
public sealed class BootstrapDiagnosticSinkTests : IDisposable
{
    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), "bosun-tests", Guid.NewGuid().ToString("N"));

    private string FallbackFilePath => Path.Combine(_tempDirectory, "bootstrap-failures.log");

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void Record_WritesToBothTheEventLogAndTheFallbackFile_WhenBothSucceed()
    {
        var eventLogWriter = new FakeEventLogWriter();
        var sink = new BootstrapDiagnosticSink(eventLogWriter, FallbackFilePath);

        sink.Record("something catastrophic happened", new InvalidOperationException("boom"));

        Assert.Equal(1, eventLogWriter.CallCount);
        Assert.Contains("something catastrophic happened", eventLogWriter.LastMessage);
        Assert.Contains("boom", eventLogWriter.LastMessage);

        Assert.True(File.Exists(FallbackFilePath));
        var contents = File.ReadAllText(FallbackFilePath);
        Assert.Contains("something catastrophic happened", contents);
        Assert.Contains("boom", contents);
    }

    [Fact]
    public void Record_StillWritesTheFallbackFile_WhenTheEventLogWriterThrows()
    {
        // The realistic case: the "Bosun" event source is not registered and the process is not
        // elevated (Win32EventLogWriter's doc comment / ADR-010). The fallback file is what
        // actually carries the record in that case.
        var throwingEventLogWriter = new FakeEventLogWriter(throwOnWrite: new UnauthorizedAccessException("no admin rights"));
        var sink = new BootstrapDiagnosticSink(throwingEventLogWriter, FallbackFilePath);

        var exception = Record.Exception(() =>
            sink.Record("host failed to construct", new IOException("disk full")));

        Assert.Null(exception);
        Assert.True(File.Exists(FallbackFilePath));
        var contents = File.ReadAllText(FallbackFilePath);
        Assert.Contains("host failed to construct", contents);
        Assert.Contains("disk full", contents);
    }

    [Fact]
    public void Record_DoesNotThrow_WhenBothTheEventLogAndTheFallbackFileFail()
    {
        var throwingEventLogWriter = new FakeEventLogWriter(throwOnWrite: new UnauthorizedAccessException("no admin rights"));

        // An unwritable fallback path: a FILE occupies where the sink wants to create the
        // fallback file's parent directory, so Directory.CreateDirectory inside the sink throws
        // too. This is the "sink can itself fail" scenario the brief calls out explicitly, and it
        // proves Record() survives even total failure of both paths.
        var occupiedParent = Path.Combine(_tempDirectory, "occupied");
        Directory.CreateDirectory(_tempDirectory);
        File.WriteAllText(occupiedParent, "occupies the path the sink wants as a directory");
        var unwritableFallbackPath = Path.Combine(occupiedParent, "bootstrap-failures.log");

        var sink = new BootstrapDiagnosticSink(throwingEventLogWriter, unwritableFallbackPath);

        var exception = Record.Exception(() =>
            sink.Record("host failed to construct", new IOException("disk full")));

        Assert.Null(exception);
        Assert.Equal(1, throwingEventLogWriter.CallCount);
        Assert.False(File.Exists(unwritableFallbackPath));
    }

    [Fact]
    public void Record_HandlesANullException()
    {
        var eventLogWriter = new FakeEventLogWriter();
        var sink = new BootstrapDiagnosticSink(eventLogWriter, FallbackFilePath);

        var exception = Record.Exception(() => sink.Record("reason with no exception object", exception: null));

        Assert.Null(exception);
        Assert.Contains("reason with no exception object", eventLogWriter.LastMessage);
        Assert.Contains("reason with no exception object", File.ReadAllText(FallbackFilePath));
    }
}
