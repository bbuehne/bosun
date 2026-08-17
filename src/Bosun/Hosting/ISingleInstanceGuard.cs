namespace Bosun.Hosting;

/// <summary>
/// A named, cross-process lock used to enforce that at most one Bosun process runs per logon
/// session (bs-2wa / ADR-018 Decision 6). Behind an interface so the default test suite can
/// exercise the acquire/fail-to-acquire logic with a fake, never a real named
/// <see cref="System.Threading.Mutex"/> racing against a concurrently-running test or a
/// concurrently-running real Bosun instance.
/// </summary>
/// <remarks>
/// See <see cref="MutexSingleInstanceGuard"/> for the production implementation (a
/// <c>Local\</c>-scoped named <see cref="System.Threading.Mutex"/> -- per-logon-session, matching
/// ADR-004: this is not a machine-wide, cross-user lock).
/// </remarks>
public interface ISingleInstanceGuard : IDisposable
{
    /// <summary>
    /// Attempts to become the sole owner of the guard. Returns <see langword="true"/> if this
    /// call acquired it (this process should proceed as the primary instance) or
    /// <see langword="false"/> if another live process already holds it (this process must not
    /// build a host or touch anything else -- it should signal the running instance and exit).
    /// </summary>
    /// <remarks>
    /// An implementation that finds the lock abandoned by a process that died while holding it
    /// must treat that as a successful acquisition, not an error -- see
    /// <see cref="MutexSingleInstanceGuard"/>'s remarks on <see cref="System.Threading.AbandonedMutexException"/>.
    /// A crashed previous holder must never permanently lock the user out of running Bosun.
    /// </remarks>
    bool TryAcquire();
}
