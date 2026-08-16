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
