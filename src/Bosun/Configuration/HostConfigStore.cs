using System.IO;

namespace Bosun.Configuration;

/// <summary>
/// Default <see cref="IHostConfigStore"/> (bs-30b). Loads and validates <c>hosts.toml</c> at
/// startup, then watches it for changes: debounces the burst of raw
/// <see cref="IConfigFileWatcher.Changed"/> events one save produces into a single reload
/// attempt, and retries briefly on a transient read (an editor's truncate-then-write can be
/// observed as a zero-byte intermediate state). An invalid reload — parse failure or a
/// <see cref="ConfigValidator"/> violation — never touches <see cref="Current"/>; only a
/// transient read failure that exhausts its retries is also left alone, for the same reason
/// (docs/ARCHITECTURE.md §6).
/// </summary>
/// <remarks>
/// Takes ownership of the <see cref="IConfigFileWatcher"/> passed to <see cref="Load"/>:
/// disposing the store disposes the watcher too, so callers should not dispose it separately.
/// </remarks>
public sealed class HostConfigStore : IHostConfigStore, IDisposable
{
    private readonly string _path;
    private readonly string _sourceName;
    private readonly IConfigFileReader _reader;
    private readonly IConfigFileWatcher _watcher;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string, bool> _identityFileExists;
    private readonly HostConfigStoreOptions _options;
    private readonly object _gate = new();

    private BosunConfig _current;
    private ITimer? _pendingTimer;
    private bool _disposed;

    // Set by AdoptSelfWrite immediately after HostConfigWriter atomically writes this store's
    // file; consumed (and cleared) by the first AttemptReload whose freshly-read text matches it
    // exactly. See AdoptSelfWrite's remarks -- this is what makes the self-write hazard (ADR-019)
    // a text comparison, never a timing assumption.
    private string? _pendingSelfWriteText;

    public event EventHandler<ConfigChangedEventArgs>? ConfigChanged;
    public event EventHandler<ConfigReloadFailedEventArgs>? ConfigReloadFailed;

    public BosunConfig Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    private HostConfigStore(
        string path,
        string sourceName,
        IConfigFileReader reader,
        IConfigFileWatcher watcher,
        TimeProvider timeProvider,
        Func<string, bool> identityFileExists,
        HostConfigStoreOptions options,
        BosunConfig initial)
    {
        _path = path;
        _sourceName = sourceName;
        _reader = reader;
        _watcher = watcher;
        _timeProvider = timeProvider;
        _identityFileExists = identityFileExists;
        _options = options;
        _current = initial;

        _watcher.Changed += OnFileChanged;
    }

    /// <summary>
    /// Loads and validates <paramref name="path"/> synchronously, then starts watching it for
    /// further changes via <paramref name="watcher"/>. The returned store takes ownership of
    /// <paramref name="watcher"/> — disposing the store disposes it too.
    /// </summary>
    /// <exception cref="InvalidConfigException">
    /// The initial load is invalid. There is no previous config to serve instead, so this is
    /// fatal to startup (docs/ARCHITECTURE.md §3: "refuses to apply an invalid config rather
    /// than partially applying it" applies to the very first load too).
    /// </exception>
    public static HostConfigStore Load(
        string path,
        IConfigFileReader reader,
        IConfigFileWatcher watcher,
        TimeProvider timeProvider,
        Func<string, bool>? identityFileExists = null,
        HostConfigStoreOptions? options = null,
        string? sourceName = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(watcher);
        ArgumentNullException.ThrowIfNull(timeProvider);

        sourceName ??= path;
        options ??= new HostConfigStoreOptions();
        identityFileExists ??= DefaultIdentityFileExists;

        try
        {
            var text = reader.ReadAllText(path);
            var config = ConfigParser.Parse(text, sourceName);
            var validation = ConfigValidator.Validate(config, identityFileExists);
            if (!validation.IsValid)
            {
                throw new InvalidConfigException(sourceName, validation.Errors.Select(e => e.Message).ToArray());
            }

            return new HostConfigStore(path, sourceName, reader, watcher, timeProvider, identityFileExists, options, config);
        }
        catch
        {
            // watcher is constructed by the caller and starts watching immediately (see
            // FileSystemConfigWatcher's constructor) -- before this method's own read/parse/
            // validate has run. On any failure here, no HostConfigStore is ever returned to take
            // ownership of it, so nothing would otherwise dispose it. That is a real, live
            // FileSystemWatcher leaked on every failed initial load -- harmless in the previously
            // untested/lazy path, but bs-6f9's StartupOrchestrator now deliberately exercises this
            // exact branch (an invalid or unreadable first-run config), so it is no longer a
            // theoretical concern.
            watcher.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _watcher.Changed -= OnFileChanged;
            _watcher.Dispose();
            _pendingTimer?.Dispose();
            _pendingTimer = null;
        }
    }

    /// <summary>
    /// Called by <see cref="HostConfigWriter"/> immediately after it atomically writes
    /// <paramref name="writtenText"/> to the file this store watches (bs-ww9.8, ADR-019: "Bosun's
    /// own write must not be reprocessed as an external change"). Two things happen here,
    /// together and synchronously, so neither depends on the debounce timer's timing:
    /// </summary>
    /// <remarks>
    /// <list type="number">
    /// <item><see cref="Current"/> becomes <paramref name="config"/> immediately, and
    /// <see cref="ConfigChanged"/> fires exactly once, right here. A caller that just awaited
    /// <c>IHostConfigWriter.SaveHostAsync</c>/<c>DeleteHostAsync</c> and re-reads
    /// <see cref="Current"/> sees the change without waiting out
    /// <see cref="HostConfigStoreOptions.DebounceDelay"/> for the watcher to catch up.</item>
    /// <item>The store is armed to recognize the watcher's own eventual notice of this exact
    /// write. <see cref="AttemptReload"/> compares the raw text it reads against
    /// <paramref name="writtenText"/> -- a TEXT comparison, not a timing one: whichever reload
    /// next reads content equal to <paramref name="writtenText"/> (on any real
    /// <see cref="FileSystemWatcher"/>, this could be the very next event or, if the OS coalesces
    /// notifications, several events later) is treated as already-applied and silently skipped --
    /// no re-parse, no re-validate, no second <see cref="ConfigChanged"/>. Content that differs (a
    /// genuine external edit racing this write, or a later legitimate change) is processed
    /// normally, exactly as any other reload. The armed text is consumed on its first match, so it
    /// never suppresses a later, unrelated change that happens to be re-armed by a subsequent
    /// self-write.</item>
    /// </list>
    /// <paramref name="config"/> is trusted to already be <see cref="ConfigValidator"/>-clean --
    /// <see cref="HostConfigWriter"/> validates before ever writing a byte -- so this method does
    /// not validate again.
    /// </remarks>
    internal void AdoptSelfWrite(BosunConfig config, string writtenText)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(writtenText);

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _current = config;
            _pendingSelfWriteText = writtenText;
        }

        ConfigChanged?.Invoke(this, new ConfigChangedEventArgs(config));
    }

    private void OnFileChanged()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // Debounce: a burst of raw Changed events from one save collapses into a single
            // reload attempt, DebounceDelay after the LAST event, by cancelling and replacing
            // any timer already pending.
            _pendingTimer?.Dispose();
            _pendingTimer = _timeProvider.CreateTimer(
                _ => AttemptReload(1),
                null,
                _options.DebounceDelay,
                Timeout.InfiniteTimeSpan);
        }
    }

    private void AttemptReload(int attempt)
    {
        string text;
        try
        {
            text = _reader.ReadAllText(_path);
        }
        catch (IOException)
        {
            RetryOrSurface(attempt, ["file could not be read (locked or mid-write)"]);
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // AdoptSelfWrite armed this with the exact text HostConfigWriter just wrote. An exact
            // match means the watcher is only now catching up to a write this store already
            // adopted synchronously -- consume the arming and stop, rather than re-parsing,
            // re-validating and re-firing ConfigChanged for a change that already happened. Text
            // that does NOT match (an external edit) falls through to the normal path below,
            // exactly like any other reload.
            if (_pendingSelfWriteText is not null && string.Equals(text, _pendingSelfWriteText, StringComparison.Ordinal))
            {
                _pendingSelfWriteText = null;
                return;
            }
        }

        // A editor's truncate-then-write can be observed as an empty file mid-save. That is
        // neither a valid nor an invalid config -- it isn't a config yet. Treat it exactly like
        // a transient read failure: retry briefly, never evict the last valid config over it.
        if (string.IsNullOrWhiteSpace(text))
        {
            RetryOrSurface(attempt, ["file was empty when read (likely mid-write)"]);
            return;
        }

        BosunConfig parsed;
        try
        {
            parsed = ConfigParser.Parse(text, _sourceName);
        }
        catch (ConfigParseException ex)
        {
            Surface(ConfigReloadFailureReason.ParseFailed, ex.Diagnostics);
            return;
        }

        var validation = ConfigValidator.Validate(parsed, _identityFileExists);
        if (!validation.IsValid)
        {
            Surface(ConfigReloadFailureReason.ValidationFailed, validation.Errors.Select(e => e.Message).ToArray());
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _current = parsed;
        }

        ConfigChanged?.Invoke(this, new ConfigChangedEventArgs(parsed));
    }

    private void RetryOrSurface(int attempt, IReadOnlyList<string> errors)
    {
        if (attempt >= _options.MaxReadAttempts)
        {
            // Retries exhausted. Still not a validation failure -- the last valid config
            // survives. Surface it so it is visible, then wait for the next real Changed event.
            Surface(ConfigReloadFailureReason.ReadFailed, errors);
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pendingTimer?.Dispose();
            _pendingTimer = _timeProvider.CreateTimer(
                _ => AttemptReload(attempt + 1),
                null,
                _options.ReadRetryDelay,
                Timeout.InfiniteTimeSpan);
        }
    }

    private void Surface(ConfigReloadFailureReason reason, IReadOnlyList<string> errors) =>
        ConfigReloadFailed?.Invoke(this, new ConfigReloadFailedEventArgs(reason, errors));

    private static bool DefaultIdentityFileExists(string path) => File.Exists(path);
}
