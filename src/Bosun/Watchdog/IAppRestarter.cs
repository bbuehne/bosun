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
    /// <param name="kind">Who asked. The replacement is told (see <see cref="RestartKind"/>), because the
    /// two behave differently on arrival: a watchdog restart starts hidden and says what happened; a
    /// restart the user asked for shows the window.</param>
    /// <returns>
    /// <see langword="true"/> if a replacement was launched and this process has begun shutting down.
    /// <see langword="false"/> if nothing was launched -- the launch failed, or the application is
    /// already shutting down -- in which case this process carries on unchanged. The restarter
    /// reports the cause in the log; the caller decides what to do next.
    /// </returns>
    Task<bool> RestartAsync(string reason, RestartKind kind, CancellationToken cancellationToken);
}

/// <summary>
/// Who asked for a restart (bs-aoz). Carried to the new instance on its command line
/// (<see cref="RestartHandoffArguments"/>).
/// </summary>
public enum RestartKind
{
    /// <summary>The supervisor watchdog (ADR-020 Decision 3). Counts against its hourly limit, which is the
    /// watchdog's own bookkeeping, not the restarter's. The new instance starts with no window, however
    /// it was originally launched: a 3 a.m. recovery must not pop a window up.</summary>
    Watchdog,

    /// <summary>The user chose "Restart Bosun" (bs-aoz). Never recorded in the watchdog's history, so a
    /// person restarting Bosun cannot use up the allowance that protects them from a restart loop. The
    /// new instance shows its window, because the user just asked for it.</summary>
    Manual,
}

/// <summary>Whether the application has begun exiting. The watchdog and the restarter consult it so
/// that a deliberate exit is never mistaken for a stall, and never turned into a restart.</summary>
public interface IShutdownState
{
    bool IsShuttingDown { get; }
}
