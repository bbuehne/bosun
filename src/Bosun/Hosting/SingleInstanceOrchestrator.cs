namespace Bosun.Hosting;

/// <summary>
/// Enforces single-instance startup (bs-2wa / ADR-018 Decision 6). Composes
/// <see cref="ISingleInstanceGuard"/> (the named lock) and <see cref="IInstanceActivationChannel"/>
/// (the cross-process "come to the foreground" signal) behind one call, <see cref="TryBecomePrimary"/>,
/// so <c>App.xaml.cs</c> has exactly one thing to call and exactly one thing to check.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ordering is the entire point of this type.</b> <see cref="TryBecomePrimary"/> must be the
/// very first thing <c>App.OnStartup</c> does with side effects -- before
/// <c>BootstrapOrchestrator.TryCreateHost</c>, which is where <c>BosunHostFactory.CreateHost</c>
/// calls <c>Directory.CreateDirectory</c> on the log directory as its first line. A second launch
/// that fails to acquire the guard must return from <see cref="TryBecomePrimary"/> having created
/// no log directory, started no <c>rclone rcd</c>, written no Terminal fragment, and mounted
/// nothing -- see bs-2wa's acceptance criterion and CLAUDE.md's mount-safety invariants. This type
/// itself does no I/O beyond the guard/channel it is given, so it is safe to construct and call
/// from a worktree per CLAUDE.md's worktree-safety rules -- it never spawns a process, mounts
/// anything, or touches a real drive letter or fragment path.
/// </para>
/// <para>
/// <b>Why this does not build or return an <see cref="Microsoft.Extensions.Hosting.IHost"/>
/// itself.</b> Deliberately decoupled from <c>BootstrapOrchestrator</c>/<c>BosunHostFactory</c>:
/// this type knows nothing about hosts, only about "is this process allowed to proceed". That
/// keeps it testable with a bare <c>Func&lt;bool&gt;</c>-shaped assertion -- construct with a
/// guard that fails to acquire, call <see cref="TryBecomePrimary"/>, and assert the caller's own
/// host-factory delegate was never invoked (bs-2wa's own acceptance criterion, phrased exactly
/// this way) -- without this class needing a reference to <c>Microsoft.Extensions.Hosting</c> at
/// all.
/// </para>
/// <para>
/// <b>The second launch's experience.</b> A failed <see cref="TryBecomePrimary"/> call has already
/// signalled the running instance (via <see cref="IInstanceActivationChannel.RequestActivation"/>)
/// by the time it returns. The caller's job is then just to exit with a success code -- no error
/// dialog, no scary log line. The user asked to see Bosun; bringing the existing window forward
/// *is* success, not a degraded path (see bs-2wa's brief and ADR-018's "Why one instance becomes
/// load-bearing now").
/// </para>
/// </remarks>
public sealed class SingleInstanceOrchestrator : IDisposable
{
    private readonly ISingleInstanceGuard _guard;
    private readonly IInstanceActivationChannel _activationChannel;

    public SingleInstanceOrchestrator(ISingleInstanceGuard guard, IInstanceActivationChannel activationChannel)
    {
        _guard = guard;
        _activationChannel = activationChannel;
    }

    /// <summary>
    /// Raised when a later launch asks this (the primary) instance to come to the foreground.
    /// The window layer (bs-ww9.3, not yet built) is the intended subscriber -- this class has no
    /// notion of windows and never will; see the class remarks.
    /// </summary>
    public event EventHandler? ActivationRequested
    {
        add => _activationChannel.ActivationRequested += value;
        remove => _activationChannel.ActivationRequested -= value;
    }

    /// <summary>
    /// Attempts to become the primary Bosun instance for this logon session.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if this process is now the primary instance: the guard is held and
    /// this instance is listening for later launches asking it to activate. The caller should
    /// proceed with the normal startup sequence.
    /// <see langword="false"/> if another instance is already running. The running instance has
    /// already been signalled to activate its window. The caller must not build a host or perform
    /// any other startup side effect, and should exit immediately with a success code.
    /// </returns>
    public bool TryBecomePrimary()
    {
        if (!_guard.TryAcquire())
        {
            _activationChannel.RequestActivation();
            return false;
        }

        _activationChannel.StartListening();
        return true;
    }

    public void Dispose()
    {
        _activationChannel.Dispose();
        _guard.Dispose();
    }
}
