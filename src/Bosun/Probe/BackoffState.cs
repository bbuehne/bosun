namespace Bosun.Probe;

/// <summary>
/// Immutable backoff-ladder state (bs-fhp, docs/ARCHITECTURE.md §4 "Backoff"). Pure and
/// deterministic: a function of consecutive-failure count plus an explicit ladder, plus an
/// explicit reset. No timers, no wall clock, no knowledge of scheduling — E5 owns turning
/// <see cref="NextDelaySeconds"/> into an actual wait via <see cref="TimeProvider"/>. This type
/// only ever answers "how many consecutive failures so far" and "given that, and a ladder, how
/// long before the next attempt".
/// </summary>
public readonly record struct BackoffState
{
    /// <summary>Zero consecutive failures — the state after an explicit reset (network address
    /// change, power resume, or user "retry now"; docs/ARCHITECTURE.md §4) and the state a fresh
    /// host starts in.</summary>
    public static readonly BackoffState Initial = default;

    private BackoffState(int consecutiveFailures) => ConsecutiveFailures = consecutiveFailures;

    public int ConsecutiveFailures { get; }

    /// <summary>One more consecutive failure. Unbounded in principle (an integer counter), but
    /// <see cref="NextDelaySeconds"/> holds at the ladder's last rung regardless of how large this
    /// grows — it never wraps and the wait never keeps increasing past the ladder's max.</summary>
    public BackoffState RecordFailure() => new(ConsecutiveFailures + 1);

    /// <summary>
    /// Seconds to wait before the next probe attempt, given <paramref name="ladderSeconds"/>
    /// (docs/CONFIG-SCHEMA.md <c>global.backoff_seconds</c>, default <c>[5, 15, 30, 60, 300]</c>).
    /// The Nth consecutive failure uses <c>ladderSeconds[N-1]</c>, holding at the last rung for
    /// any failure count beyond the ladder's length rather than wrapping or growing unbounded.
    /// Zero when there have been no failures yet — nothing to back off from.
    /// </summary>
    public int NextDelaySeconds(IReadOnlyList<int> ladderSeconds)
    {
        ArgumentNullException.ThrowIfNull(ladderSeconds);
        if (ladderSeconds.Count == 0)
        {
            throw new ArgumentException("Backoff ladder must have at least one rung.", nameof(ladderSeconds));
        }

        if (ConsecutiveFailures <= 0)
        {
            return 0;
        }

        var index = Math.Min(ConsecutiveFailures, ladderSeconds.Count) - 1;
        return ladderSeconds[index];
    }
}
