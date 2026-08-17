using Bosun.Hosting;

namespace Bosun.Tests.Hosting;

/// <summary>
/// Exercises the REAL <see cref="MutexSingleInstanceGuard"/> acquire/release cycle against a
/// real named <see cref="Mutex"/> -- deliberately not a fake, per bs-2wa's brief ("write at least
/// one test that exercises the real implementation's acquire/release cycle if you can do so
/// without leaking a named handle across test runs").
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is safe in the default suite, not <c>Category=Integration</c>.</b> CLAUDE.md's
/// worktree-safety rules forbid a default-suite test touching WinFsp, a real SFTP host, a real
/// drive letter, or the real Terminal fragment path -- a named <see cref="Mutex"/> is none of
/// those; it is a pure OS synchronisation primitive with no filesystem, network, or mount
/// footprint. Nothing here can wedge Explorer or touch a live host.
/// </para>
/// <para>
/// <b>Why this cannot leak a named handle across test runs.</b> Every test generates its own
/// GUID-suffixed <c>Local\</c> name, distinct from <see cref="MutexSingleInstanceGuard.DefaultName"/>
/// and from every other test's name, so nothing here can collide with the real production mutex or
/// with a concurrently-running test. Each guard is wrapped in <see langword="using"/> (directly or
/// via <see cref="BackgroundHolder"/>), and a <c>Local\</c>-scoped named kernel object is destroyed
/// by the OS once its last handle closes -- there is nothing left over for a later test run to
/// find.
/// </para>
/// <para>
/// <b>Why "second holder" contention is simulated on a background thread, not just a second
/// <see cref="MutexSingleInstanceGuard"/> instance on the test thread.</b> Windows named-mutex
/// ownership is per-THREAD, not per-handle or per-object: a thread that already owns a mutex can
/// acquire a second handle to the very same kernel object recursively and successfully. Two real
/// Bosun processes are always on different threads (different processes entirely), so contention
/// must be modelled that way here too, via <see cref="BackgroundHolder"/> -- otherwise these tests
/// would pass even for an implementation with no real locking effect at all, which is exactly the
/// failure mode this remark exists to prevent silently reappearing.
/// </para>
/// </remarks>
public sealed class MutexSingleInstanceGuardTests
{
    private static string UniqueName([System.Runtime.CompilerServices.CallerMemberName] string? testName = null) =>
        $@"Local\bosun-tests-{testName}-{Guid.NewGuid():N}";

    [Fact]
    public void TryAcquire_SucceedsForTheFirstHolder_AndFailsForASecondWhileTheFirstHoldsIt()
    {
        var name = UniqueName();
        using var holder = new BackgroundHolder(name);
        Assert.True(holder.Acquired);

        using var second = new MutexSingleInstanceGuard(name);
        Assert.False(second.TryAcquire());
    }

    [Fact]
    public void TryAcquire_SucceedsForASecondHolder_AfterTheFirstReleases()
    {
        var name = UniqueName();
        var holder = new BackgroundHolder(name);
        Assert.True(holder.Acquired);

        using var second = new MutexSingleInstanceGuard(name);
        Assert.False(second.TryAcquire());

        holder.ReleaseAndJoin();

        Assert.True(second.TryAcquire());
    }

    [Fact]
    public void TryAcquire_IsIdempotent_ForTheInstanceThatAlreadyOwnsIt()
    {
        var name = UniqueName();
        using var guard = new MutexSingleInstanceGuard(name);

        Assert.True(guard.TryAcquire());
        Assert.True(guard.TryAcquire());
    }

    [Fact]
    public void TryAcquire_TreatsAnAbandonedMutex_AsASuccessfulAcquisition()
    {
        var name = UniqueName();

        // A named kernel object is destroyed as soon as its LAST open handle closes, regardless
        // of abandonment status. If nothing here held a handle open across the background thread
        // below closing its own, the object would simply cease to exist the moment that thread's
        // `using` disposed it -- and MutexSingleInstanceGuard.TryAcquire's own
        // `new Mutex(false, _name, out _)` a few lines down would then silently create a BRAND
        // NEW, unowned object instead of ever observing the abandonment, so no
        // AbandonedMutexException would fire and this test would pass for the wrong reason (which
        // is exactly what an earlier draft of this test did -- confirmed via a standalone repro:
        // WaitOne returned true normally, never throwing). Keeping this handle open for the whole
        // test is what makes the abandonment actually observable.
        using var keepAlive = new Mutex(initiallyOwned: false, name, out _);

        // Simulate a previous Bosun process that died while holding the mutex: acquire it (via
        // its OWN separate handle, opened by name -- not initiallyOwned, since keepAlive above
        // already created the object) on a background OS thread, and let that thread exit
        // WITHOUT calling ReleaseMutex. Mutex ownership in Windows is per-thread, so the thread
        // terminating abandons it -- exactly what happens when the owning process crashes.
        var thread = new Thread(() =>
        {
            using var abandoned = new Mutex(initiallyOwned: false, name, out _);
            abandoned.WaitOne();
            // Deliberately no ReleaseMutex() here.
        });
        thread.Start();
        thread.Join();

        using var guard = new MutexSingleInstanceGuard(name);

        // Per bs-2wa: acquiring an abandoned mutex is the correct outcome, not an error -- a
        // crashed previous holder must not permanently lock the user out of running Bosun.
        Assert.True(guard.TryAcquire());
    }

    [Fact]
    public void IsOwned_IsFalse_BeforeAnyTryAcquireCall()
    {
        var name = UniqueName();
        using var guard = new MutexSingleInstanceGuard(name);

        Assert.False(guard.IsOwned);
    }

    [Fact]
    public void IsOwned_IsTrue_AfterASuccessfulTryAcquire()
    {
        var name = UniqueName();
        using var guard = new MutexSingleInstanceGuard(name);

        Assert.True(guard.TryAcquire());
        Assert.True(guard.IsOwned);
    }

    [Fact]
    public void IsOwned_IsFalse_OnAnInstanceWhoseTryAcquireFailed()
    {
        // bs-2wa: BootstrapOrchestrator.TryCreateHost() asserts IsOwned to decide whether the
        // startup-ordering precondition held -- if IsOwned ever reported true for an instance
        // that never actually won the mutex, that assertion would silently stop protecting
        // anything. See BootstrapOrchestratorTests.TryCreateHost_Throws_WhenSingleInstancePrimacyWasNotEstablished.
        var name = UniqueName();
        using var holder = new BackgroundHolder(name);
        Assert.True(holder.Acquired);

        using var second = new MutexSingleInstanceGuard(name);
        Assert.False(second.TryAcquire());

        Assert.False(second.IsOwned);
    }

    [Fact]
    public void IsOwned_IsFalse_AfterDispose()
    {
        var name = UniqueName();
        var guard = new MutexSingleInstanceGuard(name);
        Assert.True(guard.TryAcquire());
        Assert.True(guard.IsOwned);

        guard.Dispose();

        Assert.False(guard.IsOwned);
    }

    [Fact]
    public void Dispose_ReleasesWithoutThrowing_WhenTryAcquireWasNeverCalled()
    {
        var name = UniqueName();
        var guard = new MutexSingleInstanceGuard(name);

        var exception = Record.Exception(guard.Dispose);

        Assert.Null(exception);
    }

    [Fact]
    public void Dispose_ReleasesWithoutThrowing_WhenTryAcquireFailed()
    {
        var name = UniqueName();
        using var holder = new BackgroundHolder(name);
        Assert.True(holder.Acquired);

        var second = new MutexSingleInstanceGuard(name);
        Assert.False(second.TryAcquire());

        var exception = Record.Exception(second.Dispose);

        Assert.Null(exception);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var name = UniqueName();
        var guard = new MutexSingleInstanceGuard(name);
        guard.TryAcquire();

        guard.Dispose();
        var exception = Record.Exception(guard.Dispose);

        Assert.Null(exception);
    }

    /// <summary>
    /// Acquires a <see cref="MutexSingleInstanceGuard"/> on a dedicated background thread and
    /// holds it until <see cref="ReleaseAndJoin"/> (or <see cref="Dispose"/>) is called --
    /// standing in for "a second real Bosun process already holds the guard", which requires a
    /// genuinely different OS thread to model correctly (see the class remarks). Synchronised with
    /// <see cref="ManualResetEventSlim"/>s rather than sleeps or polling, so acquisition is
    /// observed deterministically.
    /// </summary>
    private sealed class BackgroundHolder : IDisposable
    {
        private readonly ManualResetEventSlim _acquired = new(initialState: false);
        private readonly ManualResetEventSlim _release = new(initialState: false);
        private readonly Thread _thread;

        public BackgroundHolder(string name)
        {
            _thread = new Thread(() => Run(name)) { IsBackground = true };
            _thread.Start();
            if (!_acquired.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Background holder thread did not acquire the guard in time.");
            }
        }

        public bool Acquired { get; private set; }

        private void Run(string name)
        {
            using var guard = new MutexSingleInstanceGuard(name);
            Acquired = guard.TryAcquire();
            _acquired.Set();
            _release.Wait();
        }

        public void ReleaseAndJoin()
        {
            _release.Set();
            _thread.Join(TimeSpan.FromSeconds(5));
        }

        public void Dispose()
        {
            if (!_release.IsSet)
            {
                _release.Set();
            }

            _thread.Join(TimeSpan.FromSeconds(5));
            _acquired.Dispose();
            _release.Dispose();
        }
    }
}
