namespace Bosun.Tests.Supervisor.Support;

/// <summary>
/// Pins <see cref="TokenLineage"/> on the runtime in use (bs-s30). The supervisor tests' negative checks
/// ("the suspend did NOT cancel this call") rely on it. If it cannot see a cancelled parent, those
/// checks pass whether or not the supervisor preempted the call.
/// </summary>
/// <remarks>
/// Each case puts a token in the state that made the old checks vacuous: the parent's cancellation
/// has been requested, but the link's callback has not yet run, so the child token itself is not
/// cancelled. The state is made deterministic by registering a callback on the parent before and
/// after the link. Whichever order the runtime runs callbacks in, a gated one runs first and blocks
/// the link's. No sleeps.
/// </remarks>
public sealed class TokenLineageSelfTests
{
    public enum Shape
    {
        /// <summary><c>CreateLinkedTokenSource(parent)</c>.</summary>
        OneParent,

        /// <summary><c>CreateLinkedTokenSource(other, parent)</c>: production's probe wrappers.</summary>
        TwoParents,

        /// <summary><c>CreateLinkedTokenSource(a, b, parent)</c>: production's VFS poll.</summary>
        ManyParents,

        /// <summary>A linked source linked again: the parent is a grandparent.</summary>
        Grandparent,
    }

    /// <summary>
    /// <b>Protects:</b> the negative checks' ability to fail at all.
    /// <b>Catches:</b> TokenLineage missing a cancelled parent for some link shape. The old vacuous pass
    /// would then come back for every check that uses it.
    /// </summary>
    [Theory]
    [InlineData(Shape.OneParent)]
    [InlineData(Shape.TwoParents)]
    [InlineData(Shape.ManyParents)]
    [InlineData(Shape.Grandparent)]
    public async Task Sees_a_parent_cancellation_the_link_has_not_yet_delivered(Shape shape)
    {
        using var parent = new CancellationTokenSource();
        using var other = new CancellationTokenSource();
        using var third = new CancellationTokenSource();
        using var gate = new ManualResetEventSlim();

        using var before = parent.Token.Register(gate.Wait);
        using var middle = shape == Shape.Grandparent ? CancellationTokenSource.CreateLinkedTokenSource(other.Token, parent.Token) : null;
        using var child = shape switch
        {
            Shape.OneParent => CancellationTokenSource.CreateLinkedTokenSource(parent.Token),
            Shape.TwoParents => CancellationTokenSource.CreateLinkedTokenSource(other.Token, parent.Token),
            Shape.ManyParents => CancellationTokenSource.CreateLinkedTokenSource(other.Token, third.Token, parent.Token),
            Shape.Grandparent => CancellationTokenSource.CreateLinkedTokenSource(third.Token, middle!.Token),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
        using var after = parent.Token.Register(gate.Wait);

        Assert.False(TokenLineage.CancellationRequested(child.Token), "nothing has been cancelled yet");

        var cancelling = parent.CancelAsync();
        try
        {
            Assert.False(child.Token.IsCancellationRequested, "setup: the link delivered the cancellation past a blocked callback");
            Assert.True(TokenLineage.CancellationRequested(child.Token), $"TokenLineage missed a requested cancellation through {shape}");
        }
        finally
        {
            gate.Set();
            await cancelling.WaitAsync(TimeSpan.FromSeconds(15));
        }

        Assert.True(child.Token.IsCancellationRequested, "setup: the link never delivered the cancellation");
    }

    /// <summary>
    /// <b>Protects:</b> the negative checks against false alarms.
    /// <b>Catches:</b> TokenLineage reporting a token as cancelled when nothing upstream was, which would
    /// make the negative checks fail with a correct supervisor.
    /// </summary>
    [Fact]
    public void Reports_nothing_for_uncancelled_and_uncancellable_tokens()
    {
        using var a = new CancellationTokenSource();
        using var b = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(a.Token, b.Token);
        using var linkedToNone = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None, a.Token);

        Assert.False(TokenLineage.CancellationRequested(CancellationToken.None));
        Assert.False(TokenLineage.CancellationRequested(a.Token));
        Assert.False(TokenLineage.CancellationRequested(linked.Token));
        Assert.False(TokenLineage.CancellationRequested(linkedToNone.Token));

        b.Cancel();
        Assert.True(TokenLineage.CancellationRequested(linked.Token));
        Assert.False(TokenLineage.CancellationRequested(linkedToNone.Token));
    }
}
