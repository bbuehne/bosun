using Bosun.Rclone;

namespace Bosun.Tests.Supervisor.Independent.RcTimeout;

/// <summary>
/// The exceptions an <see cref="IRcloneClient"/> call can fail with, named so a theory row says
/// what it is simulating (bs-x57).
/// </summary>
/// <remarks>
/// <para>
/// The incident: a <c>mount/unmount</c> on a dead SSH channel ran into <see cref="HttpClient"/>'s
/// 100-second default timeout. HttpClient reports that timeout as a
/// <see cref="TaskCanceledException"/> -- which IS an <see cref="OperationCanceledException"/> --
/// even though nobody cancelled anything. Every rc <c>catch</c> in the supervisor filtered
/// <c>when (ex is not OperationCanceledException)</c>, so the timeout was read as "the app is
/// shutting down", escaped, and killed the supervisor's only channel consumer.
/// </para>
/// <para>
/// The spec bs-x57 sets: a cancellation means shutdown <b>only</b> when the supervisor's own token
/// is cancelled. Anything else is an ordinary rc failure. Every theory in this folder therefore runs
/// the same scenario with each of these exception kinds:
/// </para>
/// <list type="bullet">
/// <item><see cref="OrdinaryRcFailure"/> -- the control row. This is a failure the supervisor
/// already handles. It must pass on any build. If it fails, the scenario is wrong, not the
/// supervisor.</item>
/// <item><see cref="HttpClientTimeout"/> -- exactly what .NET's HttpClient throws on
/// <c>HttpClient.Timeout</c>: a <see cref="TaskCanceledException"/> wrapping a
/// <see cref="TimeoutException"/>.</item>
/// <item><see cref="BareTaskCanceled"/> -- a <see cref="TaskCanceledException"/> with no inner
/// exception and no token. Catches a fix that recognises only the inner
/// <see cref="TimeoutException"/> shape.</item>
/// <item><see cref="ForeignTokenCanceled"/> -- an <see cref="OperationCanceledException"/> whose
/// <see cref="OperationCanceledException.CancellationToken"/> IS cancelled, but is not the
/// supervisor's token. Catches a fix that tests the exception's own token instead of the
/// supervisor's (for example a linked per-call timeout token that has fired).</item>
/// </list>
/// The values are plain BCL exceptions on purpose. The tests must not depend on whatever
/// wrapper type the fix introduces.
/// </remarks>
internal static class RcFaults
{
    public const string OrdinaryRcFailure = "RcloneRcException (control: an already-handled rc failure)";
    public const string HttpClientTimeout = "TaskCanceledException wrapping TimeoutException (HttpClient.Timeout)";
    public const string BareTaskCanceled = "TaskCanceledException (bare)";
    public const string ForeignTokenCanceled = "OperationCanceledException carrying a cancelled token that is not the supervisor's";

    /// <summary>Every kind: the control row first, then the three cancellation shapes.</summary>
    public static TheoryData<string> All =>
        new() { OrdinaryRcFailure, HttpClientTimeout, BareTaskCanceled, ForeignTokenCanceled };

    /// <summary>The cancellation-shaped kinds only, all of which bs-x57 says must be treated as
    /// ordinary failures.</summary>
    public static TheoryData<string> CancellationShaped =>
        new() { HttpClientTimeout, BareTaskCanceled, ForeignTokenCanceled };

    /// <summary>A factory rather than a shared instance, so every throw is a fresh exception with
    /// its own stack trace, as a real HttpClient call would produce.</summary>
    public static Func<Exception> Factory(string kind) => kind switch
    {
        OrdinaryRcFailure => () => new RcloneRcException(
            "mount/unmount", 500, "rc call failed: simulated ordinary rc error"),
        HttpClientTimeout => () => new TaskCanceledException(
            "The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.",
            new TimeoutException("The operation was canceled.")),
        BareTaskCanceled => () => new TaskCanceledException(),
        ForeignTokenCanceled => () => new OperationCanceledException(
            "A per-call timeout token fired; the supervisor's own token was never cancelled.",
            new CancellationToken(canceled: true)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown rc fault kind."),
    };
}
