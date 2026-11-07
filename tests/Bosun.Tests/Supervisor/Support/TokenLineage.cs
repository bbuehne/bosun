using System.Reflection;

namespace Bosun.Tests.Supervisor.Support;

/// <summary>
/// Answers "has anyone asked to cancel this token?" without waiting for the thread pool (bs-s30).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> A held call's token is LINKED to a source the supervisor owns. A suspend
/// calls <c>CancelAsync</c> on that source (ADR-023 decision 2). <c>CancelAsync</c> sets the
/// source's own <c>IsCancellationRequested</c> before it returns, but the linked token is cancelled
/// only when the thread pool runs the link's callback. If the held call is released first, the
/// supervisor disposes the linked source and the callback never runs: the held token is never
/// cancelled. A check like <c>Assert.False(hold.TokenCancelled)</c> can therefore pass when the
/// suspend DID preempt the call. That is a vacuous pass, not a correct one.
/// </para>
/// <para>
/// <b>What it does.</b> It walks from the token to every source it is linked to, at any depth, and
/// reports whether any of them has had cancellation requested. Both <c>Cancel</c> and
/// <c>CancelAsync</c> set that flag on the caller's thread, and it never goes back, so once
/// <c>SuspendAsync</c> has returned, the answer no longer depends on what the thread pool has run.
/// </para>
/// <para>
/// <b>Links first, then the flag (bs-7z8).</b> A source's links do not change while it is alive, but a
/// source linked to three or more tokens DROPS them when it is disposed
/// (<c>LinkedNCancellationTokenSource.Dispose</c> nulls its registration array; the one- and two-token
/// kinds keep theirs). A held call cancelled through such a source can unwind and dispose it on the
/// pool at any moment, including between two reads of this walk. So each level reads the source's
/// links before its token's own flag: if the flag is still clear, the links were read while the source
/// was alive and are complete. If the links are already gone and the flag is clear, the source was
/// disposed without being cancelled, and nothing can say whether a parent was: that FAILS, it is never
/// answered "no".
/// </para>
/// <para>
/// <b>The cost.</b> The runtime does not expose a linked source's parents, so this reads private
/// fields by TYPE, not by name: the token's <see cref="CancellationTokenSource"/>, and the linked
/// source's <see cref="CancellationTokenRegistration"/> fields (each registration's public
/// <see cref="CancellationTokenRegistration.Token"/> is a parent). If a runtime update changes that
/// layout, this fails loudly. It never quietly reports "not cancelled".
/// <see cref="TokenLineageSelfTests"/> pins the behaviour on the runtime in use.
/// </para>
/// </remarks>
internal static class TokenLineage
{
    private const int MaxDepth = 16;

    private static readonly FieldInfo SourceField = typeof(CancellationToken)
        .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
        .Single(f => f.FieldType == typeof(CancellationTokenSource));

    /// <summary>
    /// For <see cref="TokenLineageSelfTests"/> only: runs on the calling thread after a source's links
    /// are read and before its token's own flag is, the window in which the source can be cancelled and
    /// disposed underneath the walk (bs-7z8). Thread-static, so a self-test cannot disturb a walk on
    /// another test's thread.
    /// </summary>
    [ThreadStatic]
    internal static Action? AfterLinksRead;

    /// <summary>True if cancellation has been requested on <paramref name="token"/> or on any
    /// source it is linked to, directly or through other linked sources. Fails (rather than answering
    /// false) when that cannot be told: see the remarks.</summary>
    public static bool CancellationRequested(CancellationToken token) => Walk(token, 0);

    private static bool Walk(CancellationToken token, int depth)
    {
        if (!token.CanBeCanceled)
        {
            return false;
        }

        Assert.True(depth < MaxDepth, $"TokenLineage: linked sources nested deeper than {MaxDepth}; is there a cycle?");
        var source = (CancellationTokenSource)SourceField.GetValue(token)!;

        // Links BEFORE the flag (bs-7z8): see the remarks. The fence keeps the plain read of the links
        // from being reordered after the flag's volatile (acquire) read.
        var parents = Parents(source);
        Interlocked.MemoryBarrier();
        AfterLinksRead?.Invoke();
        if (token.IsCancellationRequested)
        {
            return true;
        }

        Assert.True(
            parents is not null,
            $"TokenLineage: a {source.GetType().Name} was disposed before its token was cancelled, and disposing it " +
            "dropped the sources it was linked to, so whether one of them was cancelled cannot be told. A negative " +
            "cancellation check would pass vacuously. Read it while the call is still in flight: the call's owner " +
            "disposes its linked source when the call ends (bs-7z8).");
        return parents.Any(parent => Walk(parent, depth + 1));
    }

    /// <summary>The tokens <paramref name="source"/> is linked to: none for a plain source (a root),
    /// null if the source has dropped its links on disposal.</summary>
    private static List<CancellationToken>? Parents(CancellationTokenSource source)
    {
        var type = source.GetType();
        if (type == typeof(CancellationTokenSource))
        {
            return []; // A plain source is a root. It has no parents to cancel it.
        }

        var parents = new List<CancellationToken>();
        var recognised = false;
        var dropped = false;
        for (var t = type; t is not null && t != typeof(CancellationTokenSource); t = t.BaseType)
        {
            foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                if (field.FieldType == typeof(CancellationTokenRegistration))
                {
                    recognised = true;
                    parents.Add(((CancellationTokenRegistration)field.GetValue(source)!).Token);
                }
                else if (field.FieldType == typeof(CancellationTokenRegistration[]))
                {
                    recognised = true;
                    if (field.GetValue(source) is CancellationTokenRegistration[] registrations)
                    {
                        parents.AddRange(registrations.Select(r => r.Token));
                    }
                    else
                    {
                        dropped = true; // LinkedNCancellationTokenSource after Dispose
                    }
                }
            }
        }

        Assert.True(
            recognised,
            $"TokenLineage: {type.FullName} derives from CancellationTokenSource but has no CancellationTokenRegistration " +
            "field, so its parents cannot be found. A negative cancellation check would pass vacuously. Update TokenLineage (bs-s30).");
        return dropped ? null : parents;
    }
}
