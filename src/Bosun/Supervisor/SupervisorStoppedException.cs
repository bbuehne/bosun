namespace Bosun.Supervisor;

/// <summary>
/// Thrown to a caller of any <see cref="IMountSupervisor"/> command when the supervisor's loop has
/// stopped (bs-6to / ADR-020 Decision 7): either the command was already waiting when the loop
/// ended, or it was issued after. Before this existed such a caller waited forever, because the
/// only thing that completes a command is the loop -- on 2026-10-01 that froze the UI.
/// </summary>
/// <remarks>
/// An <see cref="InvalidOperationException"/> so existing callers that already treat a refused
/// command as an ordinary failure handle it without knowing about this type. It is not a user
/// mistake: the application is shutting down, or the supervisor died and the watchdog is dealing
/// with it.
/// </remarks>
public sealed class SupervisorStoppedException()
    : InvalidOperationException(
        "The mount supervisor is not running, so this request cannot be processed. " +
        "Bosun is shutting down, or the supervisor has stopped and needs a restart.");
