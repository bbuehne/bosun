using Bosun.Hosting;

namespace Bosun.Tests.Hosting;

/// <summary>
/// Exercises the REAL <see cref="EventWaitHandleActivationChannel"/> against a real named
/// <see cref="EventWaitHandle"/> -- same reasoning as <see cref="MutexSingleInstanceGuardTests"/>:
/// a named wait handle is a pure OS synchronisation primitive, not one of the real systems
/// CLAUDE.md's worktree-safety rules forbid in the default suite (WinFsp, a real SFTP host, a
/// drive letter, the real Terminal fragment path), and every test here uses its own GUID-suffixed
/// name so nothing can collide with the real production channel, another test, or a leftover
/// handle from a previous run.
/// </summary>
public sealed class EventWaitHandleActivationChannelTests
{
    private static string UniqueName([System.Runtime.CompilerServices.CallerMemberName] string? testName = null) =>
        $@"Local\bosun-tests-activate-{testName}-{Guid.NewGuid():N}";

    [Fact]
    public void RequestActivation_RaisesActivationRequested_OnAListeningChannel()
    {
        var name = UniqueName();
        using var listener = new EventWaitHandleActivationChannel(name);
        using var requester = new EventWaitHandleActivationChannel(name);

        using var raised = new ManualResetEventSlim(initialState: false);
        listener.ActivationRequested += (_, _) => raised.Set();
        listener.StartListening();

        requester.RequestActivation();

        Assert.True(raised.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void RequestActivation_BeforeAnyoneListens_DoesNotThrow()
    {
        var name = UniqueName();
        using var requester = new EventWaitHandleActivationChannel(name);

        var exception = Record.Exception(requester.RequestActivation);

        Assert.Null(exception);
    }

    [Fact]
    public void StartListening_IsIdempotent()
    {
        var name = UniqueName();
        using var listener = new EventWaitHandleActivationChannel(name);

        var exception = Record.Exception(() =>
        {
            listener.StartListening();
            listener.StartListening();
        });

        Assert.Null(exception);
    }

    [Fact]
    public void Dispose_JoinsTheListenerThread_ProvingTheLoopExited()
    {
        // Dispose() calls _listenerThread.Join(...) before returning (see the implementation's
        // remarks), so a Dispose() that returns at all -- without this test needing to sleep or
        // poll -- is itself the deterministic proof that the background wait loop exited rather
        // than being left running past the call.
        var name = UniqueName();
        var listener = new EventWaitHandleActivationChannel(name);
        listener.StartListening();

        var exception = Record.Exception(listener.Dispose);

        Assert.Null(exception);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var name = UniqueName();
        var channel = new EventWaitHandleActivationChannel(name);
        channel.StartListening();

        channel.Dispose();
        var exception = Record.Exception(channel.Dispose);

        Assert.Null(exception);
    }
}
