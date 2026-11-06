using System.Diagnostics;

namespace Bosun.Tests.Configuration.Fakes;

/// <summary>
/// The one way a test drives a <see cref="FakeTimeProvider"/> on behalf of code that waits on it
/// from another thread (bs-pfc, generalising bs-3gc).
/// </summary>
/// <remarks>
/// <para>
/// The failure these exist to prevent: a loop that does <c>Advance(step); await Task.Delay(1)</c>
/// advances the clock whether or not the code under test has armed its next delay timer. The
/// continuation that arms it runs on the thread pool, so under CPU load the clock moves past polls
/// that have not happened yet and they are lost -- a 15 s health wait that should poll 61 times
/// polls 57. Every driver here advances only once <see cref="FakeTimeProvider.WhenActiveTimerCountAtLeastAsync"/>
/// says a timer exists, so each step is observed by exactly one wait.
/// </para>
/// <para>
/// <b>Precondition:</b> the code under test must have at most the one timer you are stepping on
/// that <see cref="FakeTimeProvider"/> (the rclone process service, its port guard, the stop
/// timeout). A second, long-lived timer on the same provider (a periodic probe tick, say) satisfies
/// the gate permanently and the driver degenerates into the loop above; gate such a fixture on
/// <see cref="FakeTimeProvider.WhenTimerDueWithinAsync"/> instead.
/// </para>
/// <para>
/// The real-time values here (<see cref="HangGuard"/>, the 1 ms poll) bound how long a test that
/// has already gone wrong may hang, and let a condition that no timer drives be noticed. They never
/// decide how far the fake clock moves.
/// </para>
/// </remarks>
internal static class FakeTimeDriver
{
    /// <summary>Only bounds a test that has already gone wrong; a passing run never waits on it.</summary>
    public static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    /// <summary>Waits until a timer is armed, then advances the clock by <paramref name="delta"/>.</summary>
    public static async Task AdvanceWhenArmedAsync(this FakeTimeProvider time, TimeSpan delta)
    {
        await time.WhenActiveTimerCountAtLeastAsync(1).WaitAsync(HangGuard).ConfigureAwait(false);
        time.Advance(delta);
    }

    /// <summary>
    /// Advances by <paramref name="step"/>, <paramref name="steps"/> times, each only once a timer is
    /// armed. For "it is still waiting after N polls" assertions, where no completion ends the drive.
    /// </summary>
    public static async Task AdvanceStepsAsync(this FakeTimeProvider time, TimeSpan step, int steps)
    {
        for (var i = 0; i < steps; i++)
        {
            await time.AdvanceWhenArmedAsync(step).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Steps the clock by <paramref name="step"/>, each time the code under test has armed a timer,
    /// until <paramref name="task"/> completes. The task may complete without arming another timer
    /// (the last poll can end the wait), so each step waits for either.
    /// </summary>
    public static async Task AdvanceUntilCompleteAsync(this FakeTimeProvider time, Task task, TimeSpan step)
    {
        while (!task.IsCompleted)
        {
            var armed = time.WhenActiveTimerCountAtLeastAsync(1);
            var first = await Task.WhenAny(task, armed).WaitAsync(HangGuard).ConfigureAwait(false);
            if (first == task)
            {
                return;
            }

            time.Advance(step);
        }
    }

    /// <summary>
    /// Steps the clock by <paramref name="step"/>, each time a timer is armed, until
    /// <paramref name="condition"/> holds. For state with no task to await (a detached supervise
    /// loop). The condition is re-checked while waiting, since it can become true with no further
    /// timer ever arriving.
    /// </summary>
    public static async Task AdvanceUntilAsync(this FakeTimeProvider time, Func<bool> condition, TimeSpan step)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            var armed = time.WhenActiveTimerCountAtLeastAsync(1);
            while (!armed.IsCompleted)
            {
                if (condition())
                {
                    return;
                }

                if (clock.Elapsed > HangGuard)
                {
                    throw new TimeoutException("No timer was armed and the condition did not become true.");
                }

                await Task.Delay(1).ConfigureAwait(false);
            }

            if (condition())
            {
                return;
            }

            time.Advance(step);
        }
    }

    /// <summary>
    /// Waits for a timer, advances by <paramref name="delta"/> once, then waits for
    /// <paramref name="condition"/>. The wait is for state the fired timer's continuation sets on
    /// the thread pool; when it expires the caller's own assertions report the failure.
    /// </summary>
    public static async Task AdvanceAndWaitUntilAsync(this FakeTimeProvider time, TimeSpan delta, Func<bool> condition)
    {
        await time.AdvanceWhenArmedAsync(delta).ConfigureAwait(false);

        var clock = Stopwatch.StartNew();
        while (!condition() && clock.Elapsed < HangGuard)
        {
            await Task.Delay(1).ConfigureAwait(false);
        }
    }
}
