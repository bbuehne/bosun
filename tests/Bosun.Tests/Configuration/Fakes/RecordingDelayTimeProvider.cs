namespace Bosun.Tests.Configuration.Fakes;

/// <summary>
/// A <see cref="TimeProvider"/> whose delays complete at once, without real waiting, and which
/// records every delay that was requested -- so a test can assert "the code backed off N times"
/// and can run a hook at the exact moment a backoff happens (<see cref="BeforeDelayCompletes"/>),
/// deterministically, with no clock to advance and no polling.
/// </summary>
/// <remarks>
/// <see cref="BeforeDelayCompletes"/> runs synchronously inside <see cref="CreateTimer"/>, i.e.
/// before the delayed code can resume. The timer's callback is then queued to the thread pool
/// rather than invoked inline, so the awaiting code never resumes inside its own
/// <c>Task.Delay</c> setup. Ordering is therefore fixed: hook first, resumption second.
/// </remarks>
internal sealed class RecordingDelayTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<TimeSpan> _delays = [];

    /// <summary>Every delay requested so far, in order.</summary>
    public IReadOnlyList<TimeSpan> Delays
    {
        get
        {
            lock (_gate)
            {
                return _delays.ToArray();
            }
        }
    }

    /// <summary>Called with the zero-based index of each delay, before that delay completes.</summary>
    public Action<int>? BeforeDelayCompletes { get; set; }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        int index;
        lock (_gate)
        {
            index = _delays.Count;
            _delays.Add(dueTime);
        }

        BeforeDelayCompletes?.Invoke(index);
        ThreadPool.QueueUserWorkItem(_ => callback(state));
        return new CompletedTimer();
    }

    private sealed class CompletedTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => false;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
