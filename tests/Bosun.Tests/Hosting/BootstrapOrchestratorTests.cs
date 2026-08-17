using Bosun.Hosting;
using Bosun.Tests.Hosting.Fakes;

namespace Bosun.Tests.Hosting;

/// <summary>
/// Covers bs-ipq's acceptance criterion: force <c>BosunHostFactory.CreateHost()</c> to throw
/// (an unwritable log directory is the realistic case) and assert the failure is both presented
/// through the injected notifier and durably recorded through the diagnostic channel -- plus that
/// a broken diagnostic channel does not itself prevent the user-facing presentation.
/// </summary>
/// <remarks>
/// Every test here uses <see cref="FakeCatastrophicStartupNotifier"/> and
/// <see cref="FakeBootstrapDiagnosticSink"/> -- never <see cref="MessageBoxCatastrophicStartupNotifier"/>
/// or <see cref="BootstrapDiagnosticSink"/>, so nothing here can pop a real dialog, write to the
/// real Windows Event Log, or write to the real <c>%TEMP%</c>/<c>%LOCALAPPDATA%</c> (CLAUDE.md
/// worktree-safety rules). Every test also passes an <see cref="ISingleInstanceGuard"/> --
/// <c>AlreadyPrimary</c> unless the test is specifically about bs-2wa's primacy precondition,
/// which <see cref="TryCreateHost_Throws_WhenSingleInstancePrimacyWasNotEstablished"/> covers.
/// </remarks>
public sealed class BootstrapOrchestratorTests : IDisposable
{
    private static FakeSingleInstanceGuard AlreadyPrimary() => new(isOwned: true);

    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), "bosun-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }

        if (File.Exists(_tempDirectory))
        {
            File.Delete(_tempDirectory);
        }
    }

    [Fact]
    public void TryCreateHost_ReturnsTheHost_WhenTheFactorySucceeds()
    {
        var expectedHost = Bosun.Hosting.BosunHostFactory.CreateHost(new BosunHostOptions
        {
            LogDirectory = _tempDirectory,
            ConfigPath = Path.Combine(_tempDirectory, "hosts.toml"),
        });
        var notifier = new FakeCatastrophicStartupNotifier();
        var sink = new FakeBootstrapDiagnosticSink();
        var orchestrator = new BootstrapOrchestrator(() => expectedHost, notifier, sink, AlreadyPrimary());

        var host = orchestrator.TryCreateHost();

        Assert.Same(expectedHost, host);
        Assert.Equal(0, notifier.CallCount);
        Assert.Equal(0, sink.CallCount);

        expectedHost.Dispose();
    }

    [Fact]
    public void TryCreateHost_Throws_WhenSingleInstancePrimacyWasNotEstablished()
    {
        // bs-2wa: TryCreateHost must not silently proceed if App.OnStartup were ever reordered to
        // call this before SingleInstanceOrchestrator.TryBecomePrimary() succeeds -- a second
        // launch would otherwise create the log directory, spawn rclone rcd, and race the first
        // instance for the same drive letters before anyone noticed. This is a startup-ordering
        // bug in the CALLER, not a "host failed to construct" runtime failure, so it must be a
        // loud, immediate exception -- never routed through the catastrophic notifier/diagnostic
        // sink, which would misreport it to the user as an environment problem.
        var hostFactoryCallCount = 0;
        var notifier = new FakeCatastrophicStartupNotifier();
        var sink = new FakeBootstrapDiagnosticSink();
        var notPrimary = new FakeSingleInstanceGuard(); // IsOwned defaults to false
        var orchestrator = new BootstrapOrchestrator(
            () =>
            {
                hostFactoryCallCount++;
                throw new InvalidOperationException("hostFactory must never be called in this test");
            },
            notifier,
            sink,
            notPrimary);

        var exception = Assert.Throws<InvalidOperationException>(() => orchestrator.TryCreateHost());

        Assert.Contains("bs-2wa", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, hostFactoryCallCount);
        Assert.Equal(0, notifier.CallCount);
        Assert.Equal(0, sink.CallCount);
    }

    [Fact]
    public void TryCreateHost_ReportsAndRecords_WhenCreateHostThrowsOnAnUnwritableLogDirectory()
    {
        // The realistic case named in bs-ipq's acceptance criterion: BosunHostFactory.CreateHost
        // calls Directory.CreateDirectory(options.LogDirectory) first thing, and that throws when
        // the path already exists as a FILE rather than a directory -- a deterministic,
        // environment-independent way to force the exact defect (Directory.CreateDirectory
        // failing) without depending on ACL manipulation, which would be flaky and might not even
        // fail for a test process running elevated.
        Directory.CreateDirectory(Path.GetDirectoryName(_tempDirectory)!);
        File.WriteAllText(_tempDirectory, "this occupies the path CreateHost wants as a directory");

        var notifier = new FakeCatastrophicStartupNotifier();
        var sink = new FakeBootstrapDiagnosticSink();
        var orchestrator = new BootstrapOrchestrator(
            () => Bosun.Hosting.BosunHostFactory.CreateHost(new BosunHostOptions
            {
                LogDirectory = _tempDirectory,
                ConfigPath = Path.Combine(Path.GetTempPath(), "hosts.toml"),
            }),
            notifier,
            sink,
            AlreadyPrimary());

        var host = orchestrator.TryCreateHost();

        Assert.Null(host);

        Assert.Equal(1, notifier.CallCount);
        Assert.NotNull(notifier.LastReason);
        Assert.NotNull(notifier.LastException);

        Assert.Equal(1, sink.CallCount);
        Assert.NotNull(sink.LastReason);
        Assert.NotNull(sink.LastException);

        // Both channels report the same failure.
        Assert.Equal(notifier.LastException, sink.LastException);
    }

    [Fact]
    public void TryCreateHost_StillNotifies_WhenTheDiagnosticSinkItselfThrows()
    {
        // Acceptance criterion: a broken durable channel must not prevent the user-facing
        // presentation. The sink here throws unconditionally from Record().
        var notifier = new FakeCatastrophicStartupNotifier();
        var brokenSink = new FakeBootstrapDiagnosticSink(throwOnRecord: new InvalidOperationException("sink is broken"));
        var orchestrator = new BootstrapOrchestrator(
            () => throw new IOException("simulated CreateHost failure"),
            notifier,
            brokenSink,
            AlreadyPrimary());

        // Must not throw -- TryCreateHost itself must survive a sink that violates its "never
        // throw" contract.
        var host = orchestrator.TryCreateHost();

        Assert.Null(host);
        Assert.Equal(1, brokenSink.CallCount);
        Assert.Equal(1, notifier.CallCount);
    }

    [Fact]
    public void RecordPreLoggerFailure_SwallowsASinkThatThrows()
    {
        var brokenSink = new FakeBootstrapDiagnosticSink(throwOnRecord: new InvalidOperationException("sink is broken"));
        var orchestrator = new BootstrapOrchestrator(
            () => throw new InvalidOperationException("never called in this test"),
            new FakeCatastrophicStartupNotifier(),
            brokenSink,
            AlreadyPrimary());

        var exception = Record.Exception(() =>
            orchestrator.RecordPreLoggerFailure("some pre-logger failure", new Exception("boom")));

        Assert.Null(exception);
        Assert.Equal(1, brokenSink.CallCount);
    }

    [Fact]
    public void ReportTerminalPreLoggerFailure_BothRecordsAndPresents()
    {
        var notifier = new FakeCatastrophicStartupNotifier();
        var sink = new FakeBootstrapDiagnosticSink();
        var orchestrator = new BootstrapOrchestrator(
            () => throw new InvalidOperationException("unused"), notifier, sink, AlreadyPrimary());

        var boom = new InvalidOperationException("fatal during startup");
        orchestrator.ReportTerminalPreLoggerFailure("Bosun could not start.", boom);

        // Recording alone is not enough. Pre-logger AND terminating means no logger, no tray icon,
        // and in a moment no process -- a durable record nobody reads is not communication
        // (ADR-012). This is the invisible death the whole contract exists to eliminate.
        Assert.Equal(1, sink.CallCount);
        Assert.Equal(1, notifier.CallCount);
        Assert.Same(boom, notifier.LastException);
    }

    [Fact]
    public void ReportTerminalPreLoggerFailure_StillPresents_WhenTheDurableSinkThrows()
    {
        var notifier = new FakeCatastrophicStartupNotifier();
        var sink = new FakeBootstrapDiagnosticSink(new IOException("diagnostic sink is broken too"));
        var orchestrator = new BootstrapOrchestrator(
            () => throw new InvalidOperationException("unused"), notifier, sink, AlreadyPrimary());

        orchestrator.ReportTerminalPreLoggerFailure("Bosun could not start.", new IOException("disk"));

        // The user-facing channel must not depend on the durable one succeeding: on a machine where
        // the profile tree is broken, the sink is exactly what is most likely to fail too.
        Assert.Equal(1, notifier.CallCount);
    }

    [Fact]
    public void ReportTerminalPreLoggerFailure_DoesNotThrow_WhenTheNotifierItselfThrows()
    {
        var notifier = new ThrowingCatastrophicStartupNotifier();
        var sink = new FakeBootstrapDiagnosticSink();
        var orchestrator = new BootstrapOrchestrator(
            () => throw new InvalidOperationException("unused"), notifier, sink, AlreadyPrimary());

        // The process is already terminating; failing to display must not add a second exception on
        // the way down, which would replace a legible message with a crash dump.
        orchestrator.ReportTerminalPreLoggerFailure("Bosun could not start.", new IOException("disk"));

        Assert.Equal(1, sink.CallCount);
    }

    private sealed class ThrowingCatastrophicStartupNotifier : ICatastrophicStartupNotifier
    {
        public void NotifyAndAwaitDismissal(string reason, Exception? exception) =>
            throw new InvalidOperationException("no window station");
    }
}

