namespace Bosun.Hosting;

/// <summary>
/// Production <see cref="ISingleInstanceGuard"/>: a named <see cref="Mutex"/>, scoped
/// <c>Local\</c> rather than <c>Global\</c>. Bosun is a per-logon-session tool (ADR-004) -- a
/// <c>Global\</c> mutex would serialise across every user session on the machine, including a
/// fast-user-switched second account, which is not the property this guard exists to provide.
/// </summary>
/// <remarks>
/// <para>
/// <b>Abandoned mutex handling.</b> <see cref="Mutex.WaitOne(TimeSpan)"/> still grants ownership
/// when the mutex was abandoned by a process that terminated while holding it -- it signals this
/// by throwing <see cref="AbandonedMutexException"/> rather than returning normally. Per bs-2wa: a
/// crashed previous holder must not permanently lock the user out of running Bosun, so this is
/// caught and treated as a successful acquisition, not an error.
/// </para>
/// <para>
/// <b>Release.</b> <see cref="Dispose"/> calls <see cref="Mutex.ReleaseMutex"/> only if this
/// instance actually owns the mutex (a failed <see cref="TryAcquire"/> must not attempt to release
/// a mutex it never acquired -- that throws <see cref="ApplicationException"/>), then disposes the
/// handle either way.
/// </para>
/// </remarks>
public sealed class MutexSingleInstanceGuard : ISingleInstanceGuard
{
    /// <summary>
    /// <c>Local\</c>-scoped: per-logon-session, not machine-wide (ADR-004). The GUID suffix keeps
    /// this from colliding with any unrelated named object a machine might already have.
    /// </summary>
    public const string DefaultName = @"Local\Bosun.SingleInstance.Guard.9c2e2b0e-2f0a-4a7a-9a2b-9c3f1c9d9e10";

    private readonly string _name;
    private Mutex? _mutex;
    private bool _owned;
    private bool _disposed;

    public MutexSingleInstanceGuard(string? name = null)
    {
        _name = name ?? DefaultName;
    }

    public bool TryAcquire()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_owned)
        {
            // Already the primary instance; re-acquiring is a no-op success rather than an error.
            return true;
        }

        var mutex = new Mutex(initiallyOwned: false, _name, out _);

        try
        {
            _owned = mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            // The previous holder died without releasing it. Per bs-2wa, this is the correct
            // outcome to treat as a successful acquisition, not an error -- ownership was still
            // granted despite the exception (see the class remarks).
            _owned = true;
        }

        if (_owned)
        {
            _mutex = mutex;
        }
        else
        {
            mutex.Dispose();
        }

        return _owned;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_mutex is { } mutex)
        {
            if (_owned)
            {
                try
                {
                    mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // Not held by this thread for some reason -- nothing more we can do here, and
                    // Dispose() below still releases the OS handle either way.
                }
            }

            mutex.Dispose();
            _mutex = null;
        }

        _owned = false;
    }
}
