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
        lock (_gate)
        {
            var timer = new FakeTimer(this, callback, state, dueTime, period);
            _timers.Add(timer);
            return timer;
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
