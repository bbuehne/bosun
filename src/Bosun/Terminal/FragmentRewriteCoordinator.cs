using Bosun.Configuration;
using Microsoft.Extensions.Logging;

namespace Bosun.Terminal;

/// <summary>
/// Keeps the Windows Terminal fragment in sync with the live config: an explicit initial write,
/// then a rewrite on every subsequent <see cref="IHostConfigStore.ConfigChanged"/> (bs-k41's
/// "rewrite on config change" requirement).
/// </summary>
/// <remarks>
/// <b>Registered in <c>BosunHostFactory</c> as a resolvable singleton, but deliberately NOT an
/// <see cref="Microsoft.Extensions.Hosting.IHostedService"/> and NOT constructor-injected anywhere
/// (bs-3lt).</b> <c>StartupOrchestrator</c>'s own class remarks (ADR-012) are explicit that
/// <c>IHostConfigStore</c> must be resolved from <see cref="IServiceProvider"/> by exactly one
/// place, wrapped in a try/catch, because its registered factory does real synchronous file I/O and
/// throws on an invalid or unreadable first load -- and that an
/// <see cref="Microsoft.Extensions.Hosting.IHostedService"/> whose constructor (via DI activation,
/// which happens before <c>StartAsync</c> even runs and is NOT wrapped by the generic host)
/// resolves it independently would race that guarantee. <c>StartupOrchestrator</c> is therefore the
/// one and only place that ever calls <c>GetRequiredService&lt;FragmentRewriteCoordinator&gt;()</c>
/// -- immediately after confirming config loaded successfully, before WinFsp detection or rclone
/// (ADR-012's degradation model: Terminal profiles are the half of Bosun that works when mounting
/// cannot). This class is fully built and tested in isolation from that wiring (a caller supplies an
/// already-resolved <see cref="IHostConfigStore"/> and <see cref="IFragmentWriter"/> directly), which
/// is what made it a one-line-per-call-site, low-risk wire-up once the ordering decision landed.
/// </remarks>
public sealed class FragmentRewriteCoordinator : IDisposable
{
    private readonly IFragmentWriter _writer;
    private readonly IHostConfigStore _store;
    private readonly ILogger<FragmentRewriteCoordinator> _logger;
    private bool _disposed;

    /// <summary>The most recently started write, so <see cref="WaitForPendingWriteAsync"/> (tests
    /// only -- see its own doc) can observe completion deterministically. <see cref="ConfigChanged"/>
    /// fires synchronously from <see cref="IHostConfigStore"/>'s own reload path, so the handler
    /// below cannot block on it without risking a deadlock in production; this field is how a test
    /// gets determinism back without a sleep-based poll.</summary>
    private Task _pendingWrite = Task.CompletedTask;

    public FragmentRewriteCoordinator(IFragmentWriter writer, IHostConfigStore store, ILogger<FragmentRewriteCoordinator> logger)
    {
        _writer = writer;
        _store = store;
        _logger = logger;
        _store.ConfigChanged += OnConfigChanged;
    }

    /// <summary>
    /// Writes the fragment for whatever config the store is currently serving. Callers invoke this
    /// once, explicitly, to seed the fragment at startup -- construction itself never does I/O
    /// (matches the "constructing a service does nothing" convention <c>BosunHostFactory</c> relies
    /// on for worktree safety).
    /// </summary>
    /// <remarks>
    /// Deliberately calls <see cref="IFragmentWriter.WriteAsync"/> directly rather than going
    /// through <see cref="WriteSafeAsync"/> -- unlike <see cref="OnConfigChanged"/> (a synchronous
    /// event handler that cannot safely block on, or propagate a failure from, an async write), the
    /// caller here explicitly awaits this method BECAUSE it wants to know the outcome (bs-3lt:
    /// <c>StartupOrchestrator</c> records a failure here into <c>StartupReadiness</c> with a causal
    /// reason). Swallowing the exception here, the way <see cref="OnConfigChanged"/> must, would
    /// make that impossible -- the caller would see a completed task regardless of whether anything
    /// was actually written.
    /// </remarks>
    public Task WriteInitialAsync(CancellationToken cancellationToken = default) => _writer.WriteAsync(_store.Current, cancellationToken);

    private void OnConfigChanged(object? sender, ConfigChangedEventArgs e) =>
        _pendingWrite = WriteSafeAsync(e.Config, CancellationToken.None);

    /// <summary>Awaits whatever rewrite is currently in flight (or the last one, if none is).
    /// Test-only seam -- production code never needs to wait for this, since a failed rewrite
    /// already can't take anything else down (see <see cref="WriteSafeAsync"/>).</summary>
    internal Task WaitForPendingWriteAsync() => _pendingWrite;

    private async Task WriteSafeAsync(BosunConfig config, CancellationToken cancellationToken)
    {
        try
        {
            await _writer.WriteAsync(config, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed rewrite must never take anything else down with it -- the previous fragment
            // (if any) is untouched (FragmentWriter writes atomically), so the worst outcome here is
            // a stale fragment, not a corrupt one.
            _logger.LogError(ex, "Failed to rewrite the Windows Terminal fragment after a config change");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _store.ConfigChanged -= OnConfigChanged;
    }
}
