namespace Bosun.Hosting;

/// <summary>
/// Cross-process signal used once <see cref="ISingleInstanceGuard"/> has established which
/// process is primary: a secondary launch calls <see cref="RequestActivation"/> to ask the
/// primary instance to show and focus its window, and the primary instance calls
/// <see cref="StartListening"/> once so it can react.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam bs-2wa exists to build but not to consume -- the window itself is bs-ww9.3,
/// built separately. Whatever eventually owns <c>MainWindow</c> subscribes to
/// <see cref="ActivationRequested"/> to bring itself to the foreground; this type has no notion of
/// windows at all.
/// </para>
/// <para>
/// Deliberately not a socket (CLAUDE.md/bs-2wa brief): a socket is another port that can collide
/// the same way the rc port already does. See <see cref="EventWaitHandleActivationChannel"/> for
/// the production implementation, a named <see cref="System.Threading.EventWaitHandle"/>.
/// </para>
/// </remarks>
public interface IInstanceActivationChannel : IDisposable
{
    /// <summary>
    /// Begins listening for activation requests from later launches. Raises
    /// <see cref="ActivationRequested"/> each time one arrives. Call only after this process has
    /// become the primary instance (after a successful <see cref="ISingleInstanceGuard.TryAcquire"/>).
    /// Idempotent -- calling it a second time is a no-op.
    /// </summary>
    void StartListening();

    /// <summary>
    /// Signals whatever instance is currently listening (the primary instance) to activate its
    /// window, then returns immediately without blocking on a reply. Called by a secondary launch
    /// that failed to acquire the guard, before it exits.
    /// </summary>
    void RequestActivation();

    /// <summary>
    /// Raised on the primary instance when a secondary launch calls
    /// <see cref="RequestActivation"/>. Not guaranteed to be raised on any particular thread --
    /// subscribers that touch UI must marshal to the dispatcher themselves.
    /// </summary>
    event EventHandler? ActivationRequested;
}
