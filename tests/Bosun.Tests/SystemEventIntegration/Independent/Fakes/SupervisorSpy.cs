using Bosun.Supervisor;

namespace Bosun.Tests.SystemEventIntegration.Independent.Fakes;

/// <summary>
/// An <see cref="IMountSupervisor"/> spy written independently of the implementer's
/// <c>FakeMountSupervisor</c>. Two deliberate differences, each required by a test in this folder:
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing throws.</b> The implementer's fake throws <see cref="NotSupportedException"/> from
/// every member the adapter is not expected to call. That is a reasonable trip-wire, but it cannot
/// distinguish "was never called" from "was called and the throw was swallowed somewhere up the
/// stack" -- and the suspend path in particular runs inside a synchronous event handler whose
/// exceptions have nowhere obvious to go. Every member here records its name into
/// <see cref="Calls"/> and returns normally, so "SessionLock does nothing" can be asserted as a
/// literal call count of zero across the whole interface (the brief's phrasing), not as an absence
/// of an exception.
/// </para>
/// <para>
/// <b>Cancellation tokens are captured.</b> <see cref="SuspendTokens"/> exists so a test can prove
/// the adapter never cancels the drain it started when its budget elapses. docs/ARCHITECTURE.md §3
/// says the suspend handler must return quickly and that "a forced unmount may still be needed on
/// resume" -- returning quickly is about the HANDLER, not about the unmount. Cancelling an in-flight
/// <c>mount/unmount</c> midway is not the same thing, and would leave the drive in exactly the
/// half-removed state Invariant I2 exists to prevent.
/// </para>
/// <para>
/// Nothing here can reach a real drive letter, a real <c>rclone rcd</c>, WinFsp, or a real SFTP
/// host: it is a list of strings.
/// </para>
/// </remarks>
internal sealed class SupervisorSpy : IMountSupervisor
{
    /// <summary>Every member call, in order, by name. The full-interface ledger the SessionLock
    /// test asserts is empty.</summary>
    public List<string> Calls { get; } = [];

    /// <summary>The token passed to each <see cref="SuspendAsync"/> call, in order.</summary>
    public List<CancellationToken> SuspendTokens { get; } = [];

    public int SuspendCalls => Calls.Count(c => c == nameof(SuspendAsync));

    public int ResumeCalls => Calls.Count(c => c == nameof(ResumeAsync));

    public int NetworkChangedCalls => Calls.Count(c => c == nameof(NetworkChangedAsync));

    /// <summary>When set, the n-th <see cref="SuspendAsync"/> returns the n-th task queued here
    /// instead of a completed one -- a supervisor command channel that is busy with some other
    /// host's drain, seen from the adapter's side. A null entry (or running off the end) means
    /// "completes immediately".</summary>
    public Queue<TaskCompletionSource?> PendingSuspends { get; } = new();

    public Task SuspendAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(SuspendAsync));
        SuspendTokens.Add(cancellationToken);

        if (PendingSuspends.Count > 0 && PendingSuspends.Dequeue() is { } pending)
        {
            return pending.Task;
        }

        return Task.CompletedTask;
    }

    public Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(ResumeAsync));
        return Task.CompletedTask;
    }

    public Task NetworkChangedAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(NetworkChangedAsync));
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(StartAsync));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(StopAsync));
        return Task.CompletedTask;
    }

    public IReadOnlyList<HostMountSnapshot> GetSnapshot()
    {
        Calls.Add(nameof(GetSnapshot));
        return [];
    }

    public Task RequestMountAsync(string hostKey, CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(RequestMountAsync));
        return Task.CompletedTask;
    }

    public Task RequestUnmountAsync(string hostKey, CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(RequestUnmountAsync));
        return Task.CompletedTask;
    }

    public Task RequestRetryNowAsync(string hostKey, CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(RequestRetryNowAsync));
        return Task.CompletedTask;
    }

    public Task RecordActivityAsync(string hostKey, CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(RecordActivityAsync));
        return Task.CompletedTask;
    }

    public Task OnRcloneRestartedAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(OnRcloneRestartedAsync));
        return Task.CompletedTask;
    }

    public Task SetMountingAvailabilityAsync(MountingAvailability availability, CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(SetMountingAvailabilityAsync));
        return Task.CompletedTask;
    }
}
