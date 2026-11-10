using Bosun.Rclone.Process;

namespace Bosun.Tests.Rclone.Process;

/// <summary>
/// Exercises <see cref="Win32RcloneProcessLauncher"/> against a REAL process -- excluded from
/// the default suite (CLAUDE.md worktree-safety rules: "no test in the default suite may... start
/// a real process"). Deliberately uses <c>cmd.exe</c>, not <c>rclone</c> -- rclone is not
/// installed on the maintainer's machine as of this epic, and these tests only need to prove the
/// launcher's process mechanics (start, detect exit, kill), which any real executable
/// demonstrates equally well without depending on rclone being present. Run deliberately with
/// <c>dotnet test --settings tests/Bosun.Tests/integration.runsettings</c>.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
public sealed class Win32RcloneProcessLauncherIntegrationTests
{
    [Fact]
    public async Task Start_launches_the_process_and_Exited_fires_when_it_exits_on_its_own()
    {
        var launcher = new Win32RcloneProcessLauncher();
        using var handle = launcher.Start(new RcloneProcessStartInfo
        {
            ExecutablePath = "cmd.exe",
            Arguments = ["/c", "exit 0"],
        });

        var exitedTcs = new TaskCompletionSource();
        handle.Exited += (_, _) => exitedTcs.TrySetResult();

        await exitedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(handle.HasExited);
        Assert.Equal(0, handle.ExitCode);
    }

    [Fact]
    public async Task Start_reports_a_nonzero_exit_code()
    {
        var launcher = new Win32RcloneProcessLauncher();
        using var handle = launcher.Start(new RcloneProcessStartInfo
        {
            ExecutablePath = "cmd.exe",
            Arguments = ["/c", "exit 7"],
        });

        var exitedTcs = new TaskCompletionSource();
        handle.Exited += (_, _) => exitedTcs.TrySetResult();
        await exitedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(7, handle.ExitCode);
    }

    [Fact]
    public void Kill_terminates_a_still_running_process()
    {
        var launcher = new Win32RcloneProcessLauncher();
        using var handle = launcher.Start(new RcloneProcessStartInfo
        {
            ExecutablePath = "cmd.exe",
            // /c "waits" via ping to localhost -- long enough that Kill (not natural exit) is
            // what ends it.
            Arguments = ["/c", "ping", "-n", "30", "127.0.0.1", ">nul"],
        });

        Assert.False(handle.HasExited);

        handle.Kill();

        // Kill is not guaranteed synchronous end-to-end (TerminateProcess is asynchronous at the
        // OS level), but should be observable almost immediately.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!handle.HasExited && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(50);
        }

        Assert.True(handle.HasExited);
    }

    [Fact]
    public void Start_throws_RcloneExecutableNotFoundException_for_an_executable_that_does_not_exist()
    {
        var launcher = new Win32RcloneProcessLauncher();

        var ex = Assert.Throws<RcloneExecutableNotFoundException>(() => launcher.Start(new RcloneProcessStartInfo
        {
            ExecutablePath = "this-executable-definitely-does-not-exist-bosun-e3.exe",
            Arguments = [],
        }));

        Assert.Contains("PATH", ex.Message);
    }

    // bs-0lq. cmd.exe stands in for rclone: a child that writes far more than a pipe buffer holds
    // to BOTH redirected streams, then exits. Before the launcher drained its pipes this child
    // blocked in its first write that did not fit and never exited; with the drain it finishes. Only
    // cmd.exe is started, with a hard timeout and a Kill in the finally. It is in this (excluded)
    // class because the project's rule is that the default suite starts no real process.
    private const string FloodBothStreams =
        "for /L %i in (1,1,3000) do @echo stdout-line-%i-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx" +
        " & echo stderr-line-%i-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx 1>&2";

    private static string CommandInterpreter() =>
        Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";

    [Fact]
    public async Task A_child_that_writes_far_more_than_a_pipe_holds_to_stdout_and_stderr_still_exits()
    {
        var launcher = new Win32RcloneProcessLauncher();
        using var handle = launcher.Start(new RcloneProcessStartInfo
        {
            ExecutablePath = CommandInterpreter(),
            Arguments = ["/c", FloodBothStreams],
        });

        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handle.Exited += (_, _) => exited.TrySetResult();
        try
        {
            // About 190 KB on each stream; a pipe buffer is a few KB.
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.True(handle.HasExited);
            Assert.Equal(0, handle.ExitCode);
        }
        finally
        {
            handle.Kill(); // never leave the test's own child behind, whatever happened
        }
    }

    [Fact]
    public async Task The_launchers_pump_logs_what_a_child_writes_to_stderr_and_stdout()
    {
        var logger = new CapturingLogger();
        var launcher = new Win32RcloneProcessLauncher(
            new Fakes.FakeProcessJob(), logger, TimeProvider.System);
        var handle = launcher.Start(new RcloneProcessStartInfo
        {
            ExecutablePath = CommandInterpreter(),
            Arguments = ["/c", "echo hello-from-stdout & echo NOTICE-from-stderr 1>&2"],
        });

        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handle.Exited += (_, _) => exited.TrySetResult();
        try
        {
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            handle.Kill();
            handle.Dispose(); // waits (bounded) for the pump to finish the final lines
        }

        Assert.Contains(logger.Messages, m => m.Contains("NOTICE-from-stderr", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, m => m.Contains("hello-from-stdout", StringComparison.Ordinal));
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<Win32RcloneProcessLauncher>
    {
        private readonly object _gate = new();
        private readonly List<string> _messages = [];

        public List<string> Messages
        {
            get
            {
                lock (_gate)
                {
                    return [.. _messages];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
            {
                _messages.Add(formatter(state, exception));
            }
        }
    }
}
