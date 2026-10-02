using Bosun.Tests.Configuration.Fakes;
using Bosun.Watchdog;

namespace Bosun.Tests.Watchdog;

internal sealed class FakeLiveness : ISupervisorLiveness
{
    public bool IsStarted { get; set; } = true;

    public bool IsLoopRunning { get; set; } = true;

    public DateTimeOffset LastLoopActivityUtc { get; set; }
}

/// <summary>Records restart requests; launches nothing. <see cref="Result"/> is what it reports.</summary>
internal sealed class FakeRestarter : IAppRestarter
{
    public List<string> Reasons { get; } = [];

    public bool Result { get; set; } = true;

    public Exception? Throws { get; set; }

    /// <summary>Runs at the moment of each request -- lets a test inspect state as the restarter sees it.</summary>
    public Action? OnRequest { get; set; }

    public Task<bool> RestartAsync(string reason, CancellationToken cancellationToken)
    {
        Reasons.Add(reason);
        OnRequest?.Invoke();

        return Throws is not null ? Task.FromException<bool>(Throws) : Task.FromResult(Result);
    }
}

internal sealed class InMemoryRestartHistory : IRestartHistoryStore
{
    public List<DateTimeOffset> Stored { get; set; } = [];

    public Exception? SaveThrows { get; set; }

    public int SaveCount { get; private set; }

    public IReadOnlyList<DateTimeOffset> Load() => [.. Stored];

    public void Save(IReadOnlyList<DateTimeOffset> restarts)
    {
        if (SaveThrows is not null)
        {
            throw SaveThrows;
        }

        SaveCount++;
        Stored = [.. restarts];
    }
}

internal sealed class FakeShutdownState : IShutdownState
{
    public bool IsShuttingDown { get; set; }
}

internal sealed class FakeProcessExiter : IProcessExiter
{
    public List<int> ExitCodes { get; } = [];

    public void Exit(int exitCode) => ExitCodes.Add(exitCode);
}

/// <summary>Reports a process as running until <see cref="ExitsAt"/> (by the supplied clock), or forever.</summary>
internal sealed class FakeProcessExitProbe(TimeProvider time) : IProcessExitProbe
{
    public DateTimeOffset? ExitsAt { get; set; }

    public int Polls { get; private set; }

    public bool IsRunning(int processId)
    {
        Polls++;
        return ExitsAt is null || time.GetUtcNow() < ExitsAt;
    }
}

internal sealed class FakeLauncher : IBosunProcessLauncher
{
    public List<(string Exe, IReadOnlyList<string> Args)> Launches { get; } = [];

    public Exception? Throws { get; set; }

    public void Start(string exePath, IReadOnlyList<string> arguments)
    {
        if (Throws is not null)
        {
            throw Throws;
        }

        Launches.Add((exePath, arguments));
    }
}

internal sealed class FakeApplicationShutdown : IApplicationShutdown
{
    public int Requests { get; private set; }

    public void RequestShutdown() => Requests++;
}

/// <summary>
/// A clock whose timers are the wrapped fake's, but whose <see cref="GetUtcNow"/> can be pushed
/// forward without any timer firing -- which is what a machine sleeping looks like to a process.
/// </summary>
internal sealed class SleepableTimeProvider(FakeTimeProvider inner) : TimeProvider
{
    public TimeSpan Slept { get; private set; }

    public void Sleep(TimeSpan duration) => Slept += duration;

    public void Advance(TimeSpan delta) => inner.Advance(delta);

    public override DateTimeOffset GetUtcNow() => inner.GetUtcNow() + Slept;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        inner.CreateTimer(callback, state, dueTime, period);
}
