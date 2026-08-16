using Bosun.Configuration;
using Bosun.Tests.Configuration.Fakes;

namespace Bosun.Tests.Configuration;

/// <summary>
/// Covers bs-30b: load-and-validate at startup, watch-and-reload, retain-last-valid-on-failure,
/// and the debounce/retry behaviour around an editor's truncate-then-write. Entirely
/// deterministic: a <see cref="FakeTimeProvider"/> stands in for wall-clock timers and a
/// <see cref="FakeConfigFileWatcher"/> stands in for a real <see cref="FileSystemWatcher"/>, so
/// nothing here depends on real timing or touches a real file (CLAUDE.md worktree-safety rules).
/// </summary>
public sealed class HostConfigStoreTests
{
    private const string ValidTomlV1 = """
        [hosts.jump]
        display_name  = "Jump"
        hostname      = "jump-v1.example.com"
        port          = 22
        user          = "barry"
        identity_file = "~/.ssh/id_ed25519"

          [hosts.jump.mount]
          mode = "none"

          [hosts.jump.session]
          autostart = true
          reconnect = true
          tmux      = false
          tab_color    = "#3A4A6B"
          color_scheme = "Campbell"

          [hosts.jump.probe]
          interval_seconds = 0
          deep_probe       = false
        """;

    private const string ValidTomlV2 = """
        [hosts.jump]
        display_name  = "Jump"
        hostname      = "jump-v2.example.com"
        port          = 22
        user          = "barry"
        identity_file = "~/.ssh/id_ed25519"

          [hosts.jump.mount]
          mode = "none"

          [hosts.jump.session]
          autostart = true
          reconnect = true
          tmux      = false
          tab_color    = "#3A4A6B"
          color_scheme = "Campbell"

          [hosts.jump.probe]
          interval_seconds = 0
          deep_probe       = false
        """;

    private const string TomlFailingValidation = """
        [hosts.jump]
        display_name  = "Jump"
        hostname      = "jump-v1.example.com"
        port          = 22
        user          = "barry"
        identity_file = "~/.ssh/id_ed25519"

          [hosts.jump.mount]
          mode = "none"

          [hosts.jump.session]
          autostart = true
          reconnect = true
          tmux      = false
          tab_color    = "#3A4A6B"
          color_scheme = "Campbell"

          [hosts.jump.probe]
          interval_seconds = -1
          deep_probe       = false
        """;

    private const string MalformedToml = """
        [global
        rclone_rc_port = 5572
        """;

    private static readonly Func<string, bool> AlwaysExists = _ => true;

    [Fact]
    public void Load_ValidConfig_ExposesItAsCurrent()
    {
        using var harness = Harness.WithValidConfig();

        Assert.True(harness.Store.Current.Hosts.TryGetValue("jump", out var host));
        Assert.Equal("jump-v1.example.com", host!.Hostname);
    }

    [Fact]
    public void Load_InvalidConfig_ThrowsAndDoesNotConstructAStore()
    {
        var reader = new FakeConfigFileReader { Content = TomlFailingValidation };
        var watcher = new FakeConfigFileWatcher();
        var time = new FakeTimeProvider();

        var ex = Assert.Throws<InvalidConfigException>(() =>
            HostConfigStore.Load("hosts.toml", reader, watcher, time, AlwaysExists));

        Assert.Contains("probe.interval_seconds", ex.Message, StringComparison.Ordinal);
        Assert.NotEmpty(ex.Errors);
    }

    [Fact]
    public void Load_MalformedInitialConfig_Throws()
    {
        var reader = new FakeConfigFileReader { Content = MalformedToml };
        var watcher = new FakeConfigFileWatcher();
        var time = new FakeTimeProvider();

        Assert.Throws<ConfigParseException>(() =>
            HostConfigStore.Load("hosts.toml", reader, watcher, time, AlwaysExists));
    }

    [Fact]
    public void FileChanged_ValidRewrite_UpdatesCurrentAndRaisesConfigChanged()
    {
        using var harness = Harness.WithValidConfig();
        BosunConfig? raised = null;
        harness.Store.ConfigChanged += (_, e) => raised = e.Config;

        harness.Reader.Content = ValidTomlV2;
        harness.Watcher.RaiseChanged();
        harness.Time.Advance(harness.Options.DebounceDelay);

        Assert.Equal("jump-v2.example.com", harness.Store.Current.Hosts["jump"].Hostname);
        Assert.NotNull(raised);
        Assert.Equal("jump-v2.example.com", raised!.Hosts["jump"].Hostname);
    }

    [Fact]
    public void FileChanged_InvalidRewrite_KeepsPreviousConfigAndSurfacesValidationFailure()
    {
        using var harness = Harness.WithValidConfig();
        var changedFired = false;
        ConfigReloadFailedEventArgs? failure = null;
        harness.Store.ConfigChanged += (_, _) => changedFired = true;
        harness.Store.ConfigReloadFailed += (_, e) => failure = e;

        harness.Reader.Content = TomlFailingValidation;
        harness.Watcher.RaiseChanged();
        harness.Time.Advance(harness.Options.DebounceDelay);

        Assert.False(changedFired);
        Assert.Equal("jump-v1.example.com", harness.Store.Current.Hosts["jump"].Hostname);
        Assert.NotNull(failure);
        Assert.Equal(ConfigReloadFailureReason.ValidationFailed, failure!.Reason);
        Assert.NotEmpty(failure.Errors);
    }

    [Fact]
    public void FileChanged_MalformedRewrite_KeepsPreviousConfigAndSurfacesParseFailure()
    {
        using var harness = Harness.WithValidConfig();
        var changedFired = false;
        ConfigReloadFailedEventArgs? failure = null;
        harness.Store.ConfigChanged += (_, _) => changedFired = true;
        harness.Store.ConfigReloadFailed += (_, e) => failure = e;

        harness.Reader.Content = MalformedToml;
        harness.Watcher.RaiseChanged();
        harness.Time.Advance(harness.Options.DebounceDelay);

        Assert.False(changedFired);
        Assert.Equal("jump-v1.example.com", harness.Store.Current.Hosts["jump"].Hostname);
        Assert.NotNull(failure);
        Assert.Equal(ConfigReloadFailureReason.ParseFailed, failure!.Reason);
    }

    [Fact]
    public void FileChanged_TruncatedMidWriteReadThenValidContent_RetriesAndSucceedsWithoutSurfacingFailure()
    {
        using var harness = Harness.WithValidConfig();
        var failures = new List<ConfigReloadFailedEventArgs>();
        harness.Store.ConfigReloadFailed += (_, e) => failures.Add(e);

        // Simulate an editor's truncate-then-write: the very next read (the one the debounce
        // timer triggers) lands in the empty gap. Content thereafter (the fallback once the
        // scripted queue drains) is the real, valid rewrite.
        harness.Reader.EnqueueResult(string.Empty);
        harness.Reader.Content = ValidTomlV2;

        harness.Watcher.RaiseChanged();
        harness.Time.Advance(harness.Options.DebounceDelay);

        // Not yet retried -- still serving the old config, and no failure was surfaced for a
        // transient blip still within its retry budget.
        Assert.Equal("jump-v1.example.com", harness.Store.Current.Hosts["jump"].Hostname);
        Assert.Empty(failures);

        harness.Time.Advance(harness.Options.ReadRetryDelay);

        Assert.Equal("jump-v2.example.com", harness.Store.Current.Hosts["jump"].Hostname);
        Assert.Empty(failures);
    }

    [Fact]
    public void FileChanged_ReadStaysEmptyPastRetryBudget_KeepsPreviousConfigAndSurfacesReadFailed()
    {
        using var harness = Harness.WithValidConfig();
        var changedFired = false;
        ConfigReloadFailedEventArgs? failure = null;
        harness.Store.ConfigChanged += (_, _) => changedFired = true;
        harness.Store.ConfigReloadFailed += (_, e) => failure = e;

        // Every read -- initial attempt and every retry -- lands empty, as if the file were
        // deleted or perpetually mid-write. This must NOT be treated as a validation error and
        // must NOT evict the last valid config.
        harness.Reader.Content = string.Empty;

        harness.Watcher.RaiseChanged();
        // One advance covering the debounce plus every retry delay lets the fake timer chain
        // through all MaxReadAttempts attempts.
        harness.Time.Advance(harness.Options.DebounceDelay + (harness.Options.ReadRetryDelay * harness.Options.MaxReadAttempts));

        Assert.False(changedFired);
        Assert.Equal("jump-v1.example.com", harness.Store.Current.Hosts["jump"].Hostname);
        Assert.NotNull(failure);
        Assert.Equal(ConfigReloadFailureReason.ReadFailed, failure!.Reason);
        Assert.Equal(harness.Options.MaxReadAttempts, harness.Reader.ReadCount - 1); // -1 for the initial Load read
    }

    [Fact]
    public void FileChanged_IoExceptionOnRead_IsTreatedAsTransientNotValidationFailure()
    {
        using var harness = Harness.WithValidConfig();
        ConfigReloadFailedEventArgs? failure = null;
        harness.Store.ConfigReloadFailed += (_, e) => failure = e;

        harness.Reader.EnqueueThrow(new IOException("file is in use by another process"));
        harness.Reader.Content = ValidTomlV2;

        harness.Watcher.RaiseChanged();
        harness.Time.Advance(harness.Options.DebounceDelay);

        // Still on the old config immediately after the failed read; not yet retried.
        Assert.Equal("jump-v1.example.com", harness.Store.Current.Hosts["jump"].Hostname);
        Assert.Null(failure);

        harness.Time.Advance(harness.Options.ReadRetryDelay);

        Assert.Equal("jump-v2.example.com", harness.Store.Current.Hosts["jump"].Hostname);
        Assert.Null(failure);
    }

    [Fact]
    public void FileChanged_BurstOfRawEvents_CoalescesIntoASingleReloadAttempt()
    {
        using var harness = Harness.WithValidConfig();
        var changedCount = 0;
        harness.Store.ConfigChanged += (_, _) => changedCount++;

        harness.Reader.Content = ValidTomlV2;

        // An editor's save can fire several raw filesystem events for one logical write.
        harness.Watcher.RaiseChanged();
        harness.Watcher.RaiseChanged();
        harness.Watcher.RaiseChanged();

        harness.Time.Advance(harness.Options.DebounceDelay);

        Assert.Equal(1, changedCount);
        Assert.Equal(2, harness.Reader.ReadCount); // 1 for Load, 1 for the single coalesced reload
    }

    [Fact]
    public void Dispose_StopsReactingToFurtherFileChanges()
    {
        var harness = Harness.WithValidConfig();
        harness.Store.Dispose();

        Assert.Equal(1, harness.Watcher.DisposeCallCount); // HostConfigStore owns the watcher it was handed

        harness.Reader.Content = ValidTomlV2;
        harness.Watcher.RaiseChanged();
        harness.Time.Advance(harness.Options.DebounceDelay + harness.Options.ReadRetryDelay * harness.Options.MaxReadAttempts);

        Assert.Equal(1, harness.Reader.ReadCount); // only the initial Load read
        Assert.Equal("jump-v1.example.com", harness.Store.Current.Hosts["jump"].Hostname);
    }

    private sealed class Harness : IDisposable
    {
        public required HostConfigStore Store { get; init; }
        public required FakeConfigFileReader Reader { get; init; }
        public required FakeConfigFileWatcher Watcher { get; init; }
        public required FakeTimeProvider Time { get; init; }
        public required HostConfigStoreOptions Options { get; init; }

        public static Harness WithValidConfig()
        {
            var reader = new FakeConfigFileReader { Content = ValidTomlV1 };
            var watcher = new FakeConfigFileWatcher();
            var time = new FakeTimeProvider();
            var options = new HostConfigStoreOptions
            {
                DebounceDelay = TimeSpan.FromMilliseconds(250),
                ReadRetryDelay = TimeSpan.FromMilliseconds(100),
                MaxReadAttempts = 3,
            };

            var store = HostConfigStore.Load("hosts.toml", reader, watcher, time, AlwaysExists, options);

            return new Harness
            {
                Store = store,
                Reader = reader,
                Watcher = watcher,
                Time = time,
                Options = options,
            };
        }

        public void Dispose() => Store.Dispose();
    }
}
