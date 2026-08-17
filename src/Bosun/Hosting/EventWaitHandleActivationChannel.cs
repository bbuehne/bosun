namespace Bosun.Hosting;

/// <summary>
/// Production <see cref="IInstanceActivationChannel"/>: a named, auto-reset
/// <see cref="EventWaitHandle"/>, <c>Local\</c>-scoped for the same per-logon-session reason as
/// <see cref="MutexSingleInstanceGuard"/>. Deliberately not a socket -- see the interface remarks.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="StartListening"/> starts a single dedicated background thread that blocks on
/// <see cref="WaitHandle.WaitAny(WaitHandle[])"/> against the named handle and a private stop
/// handle, so <see cref="Dispose"/> can end the loop deterministically instead of leaving a
/// background wait outstanding.
/// </para>
/// <para>
/// <see cref="RequestActivation"/> opens (or creates, if nothing is listening yet -- a harmless
/// race with no observer) the same named handle and pulses it. Auto-reset means the pulse is
/// consumed by exactly one waiter and does not leave the handle signalled for a listener that
/// starts later.
/// </para>
/// </remarks>
public sealed class EventWaitHandleActivationChannel : IInstanceActivationChannel
{
    /// <summary><c>Local\</c>-scoped, distinct from <see cref="MutexSingleInstanceGuard.DefaultName"/>.</summary>
    public const string DefaultName = @"Local\Bosun.SingleInstance.Activate.9c2e2b0e-2f0a-4a7a-9a2b-9c3f1c9d9e10";

    private readonly string _name;
    private readonly object _startGate = new();
    private readonly ManualResetEventSlim _stop = new(initialState: false);

    private EventWaitHandle? _listenHandle;
    private Thread? _listenerThread;
    private bool _disposed;

    public EventWaitHandleActivationChannel(string? name = null)
    {
        _name = name ?? DefaultName;
    }

    public event EventHandler? ActivationRequested;

    public void StartListening()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_startGate)
        {
            if (_listenerThread is not null)
            {
                // Already listening -- idempotent per the interface contract.
                return;
            }

            _listenHandle = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, _name);
            _listenerThread = new Thread(ListenLoop)
            {
                IsBackground = true,
                Name = "Bosun.SingleInstance.ActivationListener",
            };
            _listenerThread.Start();
        }
    }

    public void RequestActivation()
    {
        using var handle = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, _name);
        handle.Set();
    }

    private void ListenLoop()
    {
        var handles = new WaitHandle[] { _listenHandle!, _stop.WaitHandle };

        while (true)
        {
            var signalled = WaitHandle.WaitAny(handles);
            if (signalled == 1)
            {
                // Stop was requested (Dispose).
                return;
            }

            try
            {
                ActivationRequested?.Invoke(this, EventArgs.Empty);
            }
            catch
            {
                // A misbehaving subscriber must not kill the listener loop -- a later activation
                // request would otherwise silently stop working for the rest of the process
                // lifetime.
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _stop.Set();
        _listenerThread?.Join(TimeSpan.FromSeconds(2));
        _listenHandle?.Dispose();
        _stop.Dispose();
    }
}
