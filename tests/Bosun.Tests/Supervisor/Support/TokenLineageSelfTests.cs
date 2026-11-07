using Xunit.Sdk;

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

        /// <summary>A source linked to three tokens, linked again: the kind that drops its links on
        /// disposal, one level up (bs-7z8).</summary>
        GrandparentThroughMany,
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
    [InlineData(Shape.GrandparentThroughMany)]
    public async Task Sees_a_parent_cancellation_the_link_has_not_yet_delivered(Shape shape)
    {
        using var chain = new Chain(shape);

        Assert.False(TokenLineage.CancellationRequested(chain.ChildToken), "nothing has been cancelled yet");

        var cancelling = chain.Parent.CancelAsync();
        try
        {
            Assert.False(chain.ChildToken.IsCancellationRequested, "setup: the link delivered the cancellation past a blocked callback");
            Assert.True(TokenLineage.CancellationRequested(chain.ChildToken), $"TokenLineage missed a requested cancellation through {shape}");
        }
        finally
        {
            chain.Gate.Set();
            await cancelling.WaitAsync(TimeSpan.FromSeconds(15));
        }

        Assert.True(chain.ChildToken.IsCancellationRequested, "setup: the link never delivered the cancellation");
    }

    /// <summary>
    /// <b>Protects:</b> the checks on <c>vfs/*</c> calls (bs-7z8). Their token comes from a source linked to
    /// three tokens, which drops its links when disposed, and a call that source cancels unwinds and disposes
    /// it on the thread pool: the observed case is the link delivering and the source being disposed between
    /// the walk's two reads of one level (the token's own flag and its links). Forced here at that point.
    /// <b>Catches:</b> the walk reading the flag before the links, which then finds the links gone and the flag
    /// it already read clear, and answers "not requested" for a token whose parent was cancelled.
    /// </summary>
    [Theory]
    [InlineData(Shape.OneParent)]
    [InlineData(Shape.TwoParents)]
    [InlineData(Shape.ManyParents)]
    [InlineData(Shape.Grandparent)]
    [InlineData(Shape.GrandparentThroughMany)]
    public async Task Sees_a_cancellation_delivered_and_the_link_disposed_in_the_middle_of_the_walk(Shape shape)
    {
        using var chain = new Chain(shape);
        var cancelling = chain.Parent.CancelAsync();
        Assert.False(chain.ChildToken.IsCancellationRequested, "setup: the link delivered the cancellation past a blocked callback");

        var windows = 0;
        TokenLineage.AfterLinksRead = () =>
        {
            if (windows++ > 0)
            {
                return;
            }

            chain.Gate.Set();
            Assert.True(
                SpinWait.SpinUntil(() => chain.ChildToken.IsCancellationRequested, TimeSpan.FromSeconds(15)),
                "setup: the link never delivered the cancellation");
            chain.DisposeLinked(); // the held call unwound and its owner disposed the linked source
        };
        try
        {
            Assert.True(TokenLineage.CancellationRequested(chain.ChildToken), $"TokenLineage missed a requested cancellation through {shape}");
        }
        finally
        {
            TokenLineage.AfterLinksRead = null;
            chain.Gate.Set();
            await cancelling.WaitAsync(TimeSpan.FromSeconds(15));
        }

        Assert.True(windows > 0, "setup: the walk never reached the window");
    }

    /// <summary>
    /// <b>Protects:</b> "it never quietly reports not cancelled" (bs-7z8). A linked source disposed while the
    /// parent's cancellation is still undelivered is never cancelled itself. The one- and two-token kinds keep
    /// their links after disposal, so the answer is still found. The three-token kind drops them, so nothing can
    /// say whether a parent was cancelled, and that must fail, not answer "no".
    /// <b>Catches:</b> dropped links read as "no parents", which turned a requested cancellation into a negative
    /// check's pass.
    /// </summary>
    [Theory]
    [InlineData(Shape.OneParent, true)]
    [InlineData(Shape.TwoParents, true)]
    [InlineData(Shape.ManyParents, false)]
    [InlineData(Shape.Grandparent, true)]
    [InlineData(Shape.GrandparentThroughMany, false)]
    public async Task A_link_disposed_before_it_delivered_is_seen_or_fails_and_is_never_reported_as_not_cancelled(Shape shape, bool linksSurvive)
    {
        using var chain = new Chain(shape);
        var cancelling = chain.Parent.CancelAsync();
        try
        {
            chain.DisposeLinked();
            Assert.False(chain.ChildToken.IsCancellationRequested, "setup: the link delivered the cancellation past a blocked callback");

            if (linksSurvive)
            {
                Assert.True(TokenLineage.CancellationRequested(chain.ChildToken), $"TokenLineage missed a requested cancellation through a disposed {shape}");
            }
            else
            {
                var failure = Assert.ThrowsAny<XunitException>(() => TokenLineage.CancellationRequested(chain.ChildToken));
                Assert.Contains("bs-7z8", failure.Message, StringComparison.Ordinal);
            }
        }
        finally
        {
            chain.Gate.Set();
            await cancelling.WaitAsync(TimeSpan.FromSeconds(15));
        }
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

    /// <summary>
    /// A parent with a gated callback registered before and after the link (see the class remarks), and the
    /// child token for a <see cref="Shape"/>. The child's token is captured at once: a source's
    /// <c>Token</c> throws after disposal, and a held call keeps the token, not the source.
    /// </summary>
    private sealed class Chain : IDisposable
    {
        private readonly CancellationTokenSource other = new();
        private readonly CancellationTokenSource third = new();
        private readonly CancellationTokenSource? middle;
        private readonly CancellationTokenSource child;
        private readonly CancellationTokenRegistration before;
        private readonly CancellationTokenRegistration after;

        public Chain(Shape shape)
        {
            before = Parent.Token.Register(Gate.Wait);
            middle = shape switch
            {
                Shape.Grandparent => CancellationTokenSource.CreateLinkedTokenSource(other.Token, Parent.Token),
                Shape.GrandparentThroughMany => CancellationTokenSource.CreateLinkedTokenSource(other.Token, third.Token, Parent.Token),
                _ => null,
            };
            child = shape switch
            {
                Shape.OneParent => CancellationTokenSource.CreateLinkedTokenSource(Parent.Token),
                Shape.TwoParents => CancellationTokenSource.CreateLinkedTokenSource(other.Token, Parent.Token),
                Shape.ManyParents => CancellationTokenSource.CreateLinkedTokenSource(other.Token, third.Token, Parent.Token),
                Shape.Grandparent => CancellationTokenSource.CreateLinkedTokenSource(third.Token, middle!.Token),
                Shape.GrandparentThroughMany => CancellationTokenSource.CreateLinkedTokenSource(middle!.Token),
                _ => throw new ArgumentOutOfRangeException(nameof(shape)),
            };
            ChildToken = child.Token;
            after = Parent.Token.Register(Gate.Wait);
        }

        public CancellationTokenSource Parent { get; } = new();

        public ManualResetEventSlim Gate { get; } = new();

        public CancellationToken ChildToken { get; }

        /// <summary>What the supervisor does when a call ends: disposes the linked sources it made for it.</summary>
        public void DisposeLinked()
        {
            child.Dispose();
            middle?.Dispose();
        }

        public void Dispose()
        {
            Gate.Set();
            before.Dispose();
            after.Dispose();
            DisposeLinked();
            Parent.Dispose();
            other.Dispose();
            third.Dispose();
            Gate.Dispose();
        }
    }
}
