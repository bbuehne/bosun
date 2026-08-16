namespace Bosun.Hosting;

/// <summary>
/// Durably records a catastrophic bootstrap failure -- one that happened before
/// <c>BosunHostFactory.CreateHost()</c> returned, meaning no Serilog pipeline exists yet to do it
/// (bs-ipq / ADR-012 Decision 2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Must never throw.</b> This runs in the one place in the application where nothing else is
/// available to catch a failure -- if <see cref="Record"/> itself throws, the caller
/// (<c>BootstrapOrchestrator</c>) swallows it defensively, but a correct implementation should not
/// rely on that safety net; it should be a dead code path, not a load-bearing one.
/// </para>
/// <para>
/// See <see cref="BootstrapDiagnosticSink"/> for the production implementation and its choice of
/// durable store.
/// </para>
/// </remarks>
public interface IBootstrapDiagnosticSink
{
    /// <summary>
    /// Records <paramref name="reason"/> and <paramref name="exception"/>. Best-effort: swallows
    /// its own failures rather than propagating them.
    /// </summary>
    void Record(string reason, Exception? exception);
}
