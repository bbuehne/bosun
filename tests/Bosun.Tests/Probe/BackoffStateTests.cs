using Bosun.Probe;

namespace Bosun.Tests.Probe;

/// <summary>
/// Covers bs-fhp: <see cref="BackoffState"/> is pure and deterministic — a function of
/// (consecutive-failure count, ladder) plus explicit reset, no timers, no wall clock. The default
/// ladder used throughout mirrors <c>GlobalConfig.DefaultBackoffSeconds</c>
/// (<c>[5, 15, 30, 60, 300]</c>).
/// </summary>
public sealed class BackoffStateTests
{
    private static readonly int[] DefaultLadder = [5, 15, 30, 60, 300];

    [Fact]
    public void A_fresh_state_has_zero_consecutive_failures_and_zero_delay()
    {
        var state = BackoffState.Initial;

        Assert.Equal(0, state.ConsecutiveFailures);
        Assert.Equal(0, state.NextDelaySeconds(DefaultLadder));
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 15)]
    [InlineData(3, 30)]
    [InlineData(4, 60)]
    [InlineData(5, 300)]
    public void Each_rung_of_the_ladder_is_used_in_order(int failures, int expectedSeconds)
    {
        var state = BackoffState.Initial;
        for (var i = 0; i < failures; i++)
        {
            state = state.RecordFailure();
        }

        Assert.Equal(failures, state.ConsecutiveFailures);
        Assert.Equal(expectedSeconds, state.NextDelaySeconds(DefaultLadder));
    }

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(100)]
    public void Beyond_the_ladders_length_it_holds_at_the_last_rung_rather_than_wrapping_or_growing(int failures)
    {
        var state = BackoffState.Initial;
        for (var i = 0; i < failures; i++)
        {
            state = state.RecordFailure();
        }

        Assert.Equal(300, state.NextDelaySeconds(DefaultLadder));
    }

    [Fact]
    public void Resetting_after_many_failures_returns_to_zero_delay_immediately()
    {
        // docs/ARCHITECTURE.md §4: reset is what makes dock/undock feel immediate. A laptop that
        // failed five times at the office must not sit on the 300s rung after arriving home.
        var state = BackoffState.Initial;
        for (var i = 0; i < 5; i++)
        {
            state = state.RecordFailure();
        }
        Assert.Equal(300, state.NextDelaySeconds(DefaultLadder));

        state = BackoffState.Initial; // network change / resume / explicit retry

        Assert.Equal(0, state.ConsecutiveFailures);
        Assert.Equal(0, state.NextDelaySeconds(DefaultLadder));
    }

    [Fact]
    public void A_single_rung_ladder_holds_at_that_rung_for_every_failure_count()
    {
        var state = BackoffState.Initial.RecordFailure().RecordFailure().RecordFailure();

        Assert.Equal(42, state.NextDelaySeconds([42]));
    }

    [Fact]
    public void An_empty_ladder_is_rejected_rather_than_silently_returning_zero()
    {
        var state = BackoffState.Initial.RecordFailure();

        Assert.Throws<ArgumentException>(() => state.NextDelaySeconds([]));
    }

    [Fact]
    public void RecordFailure_does_not_mutate_the_original_state()
    {
        var original = BackoffState.Initial;
        var afterOneFailure = original.RecordFailure();

        Assert.Equal(0, original.ConsecutiveFailures);
        Assert.Equal(1, afterOneFailure.ConsecutiveFailures);
    }
}
