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
/// <c>CancelAsync</c> set that flag on the caller's thread, so once <c>SuspendAsync</c> has returned,
/// the answer no longer depends on what the thread pool has run.
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

    /// <summary>True if cancellation has been requested on <paramref name="token"/> or on any
    /// source it is linked to, directly or through other linked sources.</summary>
    public static bool CancellationRequested(CancellationToken token) => Walk(token, 0);

    private static bool Walk(CancellationToken token, int depth)
    {
        if (token.IsCancellationRequested)
        {
            return true;
        }

        if (!token.CanBeCanceled)
        {
            return false;
        }

        Assert.True(depth < MaxDepth, $"TokenLineage: linked sources nested deeper than {MaxDepth}; is there a cycle?");
        var source = (CancellationTokenSource?)SourceField.GetValue(token);
        return source is not null && Parents(source).Any(parent => Walk(parent, depth + 1));
    }

    private static List<CancellationToken> Parents(CancellationTokenSource source)
    {
        var type = source.GetType();
        if (type == typeof(CancellationTokenSource))
        {
            return []; // A plain source is a root. It has no parents to cancel it.
        }

        var parents = new List<CancellationToken>();
        var recognised = false;
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
                    parents.AddRange(((CancellationTokenRegistration[]?)field.GetValue(source) ?? []).Select(r => r.Token));
                }
            }
        }

        Assert.True(
            recognised,
            $"TokenLineage: {type.FullName} derives from CancellationTokenSource but has no CancellationTokenRegistration " +
            "field, so its parents cannot be found. A negative cancellation check would pass vacuously. Update TokenLineage (bs-s30).");
        return parents;
    }
}
