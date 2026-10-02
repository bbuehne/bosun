namespace Bosun.Watchdog;

/// <summary>
/// Restarts the Bosun process (bs-6to, ADR-020 Decision 3). Behind an interface so the default
/// test suite never launches or kills a real process: tests use a fake that records the request.
/// </summary>
/// <remarks>
/// The restart is the recovery for a stalled supervisor rather than starting a second loop in this
/// process, for the reason ADR-020 gives: a stalled loop may be mid-action over per-host state, and
/// a second consumer would break the single-consumer guarantee the state machine relies on.
/// </remarks>
public interface IAppRestarter
{
    /// <summary>
    /// Starts a replacement Bosun process and then shuts this one down, boundedly.
    /// </summary>
    /// <param name="reason">Why, for the log.</param>
    /// <returns>
    /// <see langword="true"/> if a replacement was launched and this process has begun shutting down.
    /// <see langword="false"/> if nothing was launched -- the launch failed, or the application is
    /// already shutting down -- in which case this process carries on unchanged. The restarter
    /// reports the cause in the log; the caller decides what to do next.
    /// </returns>
    Task<bool> RestartAsync(string reason, CancellationToken cancellationToken);
}

/// <summary>Whether the application has begun exiting. The watchdog and the restarter consult it so
/// that a deliberate exit is never mistaken for a stall, and never turned into a restart.</summary>
public interface IShutdownState
{
    bool IsShuttingDown { get; }
}
