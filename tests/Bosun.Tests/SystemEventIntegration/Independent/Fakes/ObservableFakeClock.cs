using Bosun.Tests.Configuration.Fakes;

namespace Bosun.Tests.SystemEventIntegration.Independent.Fakes;

/// <summary>
/// <c>FakeTimeProvider</c> plus one signal: <see cref="TimerArmed"/> is set whenever a timer is
/// created. Still a fake clock -- it never moves except when a test says <see cref="Advance"/>.
/// </summary>
/// <remarks>
/// <para>
/// This exists for exactly one scenario, and only that scenario should use it: driving
/// <c>SystemEventSupervisorAdapter</c>'s suspend budget through the REAL synchronous event handler
/// (<c>OnSuspend</c>, which blocks its calling thread by design) rather than through the internal
/// async core. The existing suite does the latter and says so, on the grounds that the thread which
/// would have to call <c>Advance</c> is the thread stuck waiting. That is true of a
/// single-threaded test -- but the blocking wrapper is precisely the part of this delivery whose
/// correctness is least obvious, so a test that never executes it leaves the interesting half
/// unproven.
/// </para>
/// <para>
/// The fix is a rendezvous, not a sleep. The test raises the event on a second thread and then
/// waits on <see cref="TimerArmed"/>, which is set from inside <see cref="CreateTimer"/> -- and the
/// only timer the adapter creates on that path is the one backing
/// <c>Task.Delay(budget, timeProvider)</c>. Once that signal is observed, the budget timer provably
/// exists, so advancing the clock is deterministic rather than racy: there is no ordering in which
/// the advance can be missed. Every <c>Wait</c> in those tests carries a generous timeout purely as
/// a failure guard, so a hang fails the run with a message instead of blocking it forever; no
/// assertion depends on how long anything takes.
/// </para>
/// </remarks>
internal sealed class ObservableFakeClock : TimeProvider
{
    private readonly FakeTimeProvider inner = new();

    /// <summary>Set the first time any timer is created against this clock, and never reset.</summary>
    public ManualResetEventSlim TimerArmed { get; } = new(initialState: false);

    public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = inner.CreateTimer(callback, state, dueTime, period);
        TimerArmed.Set();
        return timer;
    }

    public void Advance(TimeSpan delta) => inner.Advance(delta);
}
