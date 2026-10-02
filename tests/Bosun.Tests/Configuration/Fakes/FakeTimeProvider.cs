namespace Bosun.Tests.Configuration.Fakes;

/// <summary>
/// A deterministic <see cref="TimeProvider"/> for testing <c>HostConfigStore</c>'s
/// debounce/retry timers without real wall-clock delays. <see cref="Advance"/> moves the clock
/// forward and synchronously fires any timer callback whose due time falls within the advance —
/// including timers a callback itself schedules, as long as their due time still falls within
/// the same advance. No threads, no <c>Thread.Sleep</c> (CLAUDE.md worktree-safety rules).
/// </summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<FakeTimer> _timers = [];
    private DateTimeOffset _now;

    public FakeTimeProvider(DateTimeOffset? start = null) => _now = start ?? DateTimeOffset.UnixEpoch;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        FakeTimer timer;
        List<TaskCompletionSource>? satisfied = null;

        lock (_gate)
        {
            timer = new FakeTimer(this, callback, state, dueTime, period);
            _timers.Add(timer);

            var active = _timers.Count(t => !t.IsDisposed);
            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                if (active >= _waiters[i].Count)
                {
                    (satisfied ??= []).Add(_waiters[i].Tcs);
                    _waiters.RemoveAt(i);
                }
            }
        }

        // Outside the lock; the waiters' continuations are asynchronous (see below), so nothing
        // awaiting one can re-enter this provider on this stack.
        if (satisfied is not null)
        {
            foreach (var tcs in satisfied)
            {
                tcs.TrySetResult();
            }
        }

        return timer;
    }

    private readonly List<(int Count, TaskCompletionSource Tcs)> _waiters = [];

    /// <summary>
    /// Completes once at least <paramref name="count"/> timers are scheduled and not yet fired or
    /// disposed -- immediately, if that is already true. The deterministic answer to "has the code
    /// under test reached its <c>Task.Delay</c>/timer yet?", for code whose continuation hops to
    /// the thread pool (any continuation completed from a thread carrying a
    /// <see cref="SynchronizationContext"/> -- which every xunit test thread does -- is queued
    /// rather than run inline). Calling <see cref="Advance"/> before that point moves the clock
    /// past a timer that does not exist yet, and the timer is then due a full delay later.
    /// </summary>
    public Task WhenActiveTimerCountAtLeastAsync(int count)
    {
        lock (_gate)
        {
            if (_timers.Count(t => !t.IsDisposed) >= count)
            {
                return Task.CompletedTask;
            }

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((count, tcs));
            return tcs.Task;
        }
    }

    /// <summary>Advances the clock by <paramref name="delta"/>, firing every timer due at or
    /// before the new time — in due-time order, including any follow-up timer a fired callback
    /// schedules whose due time still lands within this same advance.</summary>
    public void Advance(TimeSpan delta)
    {
        DateTimeOffset target;
        lock (_gate)
        {
            target = _now + delta;
        }

        while (true)
        {
            FakeTimer? next;
            lock (_gate)
            {
                next = _timers
                    .Where(t => !t.IsDisposed)
                    .OrderBy(t => t.NextDue)
                    .FirstOrDefault();

                if (next is null || next.NextDue > target)
                {
                    _now = target;
                    return;
                }

                _now = next.NextDue;
            }

            next.Fire();
        }
    }

    private void Remove(FakeTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class FakeTimer(FakeTimeProvider owner, TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) : ITimer
    {
        private TimeSpan _period = period;

        public DateTimeOffset NextDue { get; private set; } =
            owner.GetUtcNow() + (dueTime < TimeSpan.Zero ? TimeSpan.Zero : dueTime);

        public bool IsDisposed { get; private set; }

        public void Fire()
        {
            if (IsDisposed)
            {
                return;
            }

            if (_period == Timeout.InfiniteTimeSpan || _period <= TimeSpan.Zero)
            {
                IsDisposed = true;
            }
            else
            {
                NextDue += _period;
            }

            callback(state);
        }

        public bool Change(TimeSpan newDueTime, TimeSpan newPeriod)
        {
            if (IsDisposed)
            {
                return false;
            }

            NextDue = owner.GetUtcNow() + (newDueTime < TimeSpan.Zero ? TimeSpan.Zero : newDueTime);
            _period = newPeriod;
            return true;
        }

        public void Dispose()
        {
            IsDisposed = true;
            owner.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
