using Bosun.Configuration;
using Bosun.Tests.Configuration.Fakes;

namespace Bosun.Tests.Configuration;

/// <summary>
/// The store's failure semantics, from docs/ARCHITECTURE.md §6 ("Config file invalid → keep
/// running on the last valid config; surface the parse error; never apply a partial config") and
/// §3 ("refuses to apply an invalid config rather than partially applying it"). Independently
/// authored: the existing suite proves an invalid reload does not change one hostname, which a
/// store that swapped <c>[global]</c> and kept the hosts would also pass. These tests assert on
/// the whole retained content, on object identity, and on what happens <em>after</em> a failure —
/// a store that latches into a failed state keeps serving a stale config forever while the user
/// stares at a corrected file.
///
/// Fully deterministic: <see cref="FakeTimeProvider"/> for every timer, <see
/// cref="FakeConfigFileWatcher"/> in place of a real <see cref="FileSystemWatcher"/>, and an
/// injected identity-file check. No sleeps, no wall clock, no disk.
/// </summary>
public sealed class HostConfigStoreRetentionTests
{
    private static readonly Func<string, bool> AlwaysExists = _ => true;

    /// <summary>Two hosts and a deliberately non-default <c>[global]</c>, so that a store which
    /// partially applies a rejected config has something to get wrong.</summary>
    private const string ValidV1 = """
        [global]
        rclone_rc_port                  = 5599
        probe_timeout_seconds           = 7
        failures_before_unmount         = 4
        backoff_seconds                 = [3, 9, 27]
        mounted_probe_interval_seconds  = 45
        suspend_unmount_timeout_seconds = 6
        start_with_windows              = false

        [hosts.nas]
        display_name  = "NAS v1"
        hostname      = "nas-v1.example.internal"
        port          = 22
        user          = "someuser"
        identity_file = "~/.ssh/id_ed25519"

          [hosts.nas.mount]
          mode                 = "persistent"
          drive                = "N:"
          remote_path          = "/volume1/share"
          vfs_cache_mode       = "writes"
          network_mode         = true
          idle_unmount_seconds = 0

          [hosts.nas.session]
          autostart    = false
          reconnect    = true
          tmux         = true
          tmux_session = "main"
          tab_color    = "#2D5F3F"
          color_scheme = "Campbell"

          [hosts.nas.probe]
          interval_seconds = 60
          deep_probe       = true

        [hosts.jump]
        display_name  = "Jump v1"
        hostname      = "jump-v1.example.com"
        port          = 22
        user          = "someuser"
        identity_file = "~/.ssh/id_ed25519"

          [hosts.jump.mount]
          mode = "none"

          [hosts.jump.session]
          autostart    = true
          reconnect    = true
          tmux         = false
          tab_color    = "#3A4A6B"
          color_scheme = "Campbell"

          [hosts.jump.probe]
          interval_seconds = 0
          deep_probe       = false
        """;

    /// <summary>A single-host rewrite: applying it changes the host set, the global block and the
    /// surviving host's fields all at once.</summary>
    private const string ValidV2 = """
        [global]
        rclone_rc_port = 5572

        [hosts.nas]
        display_name  = "NAS v2"
        hostname      = "nas-v2.example.internal"
        port          = 2222
        user          = "someuser"
        identity_file = "~/.ssh/id_ed25519"

          [hosts.nas.mount]
          mode                 = "persistent"
          drive                = "P:"
          remote_path          = "/volume2/share"
          vfs_cache_mode       = "full"
          network_mode         = true
          idle_unmount_seconds = 0

          [hosts.nas.session]
          autostart    = false
          reconnect    = true
          tmux         = true
          tmux_session = "main"
          tab_color    = "#2D5F3F"
          color_scheme = "Campbell"

          [hosts.nas.probe]
          interval_seconds = 30
          deep_probe       = true
        """;

    /// <summary>Well-formed TOML violating two rules at once: a reserved drive letter and a
    /// <c>vfs_cache_mode</c> below the Invariant I6 floor.</summary>
    private const string InvalidTwoRules = """
        [hosts.nas]
        display_name  = "NAS bad"
        hostname      = "nas-bad.example.internal"
        port          = 22
        user          = "someuser"
        identity_file = "~/.ssh/id_ed25519"

          [hosts.nas.mount]
          mode                 = "persistent"
          drive                = "C:"
          remote_path          = "/volume1/share"
          vfs_cache_mode       = "off"
          network_mode         = true
          idle_unmount_seconds = 0

          [hosts.nas.session]
          autostart    = false
          reconnect    = true
          tmux         = false
          tab_color    = "#2D5F3F"
          color_scheme = "Campbell"

          [hosts.nas.probe]
          interval_seconds = 60
          deep_probe       = true
        """;

    /// <summary>
    /// The core §6 requirement, asserted on content rather than on a single field: after a
    /// rejected reload, <c>Current</c> must be the previous config in its entirety — same
    /// instance, same host set, same per-host fields, same <c>[global]</c>. Catches a store that
    /// applies the new <c>[global]</c> while rejecting the hosts (or vice versa), and catches a
    /// rebuild-from-the-new-text that happens to preserve the one field an existing test looks
    /// at. A partially applied config is worse than a rejected one: it can point a live drive
    /// letter at settings the user never approved.
    /// </summary>
    [Fact]
    public void InvalidReload_LeavesCurrentEntirelyUnchanged()
    {
        using var harness = Harness.WithV1();
        var before = harness.Store.Current;

        harness.Reader.Content = InvalidTwoRules;
        harness.Watcher.RaiseChanged();
        harness.Time.Advance(harness.Options.DebounceDelay);

        var after = harness.Store.Current;
        Assert.Same(before, after);

        Assert.Equal(["jump", "nas"], after.Hosts.Keys.Order());
        Assert.Equal("nas-v1.example.internal", after.Hosts["nas"].Hostname);
        Assert.Equal("N:", after.Hosts["nas"].Mount.Drive);
        Assert.Equal("writes", after.Hosts["nas"].Mount.VfsCacheMode);
        Assert.Equal(60, after.Hosts["nas"].Probe.IntervalSeconds);
        Assert.Equal(MountMode.None, after.Hosts["jump"].Mount.Mode);

        Assert.Equal(5599, after.Global.RcloneRcPort);
        Assert.Equal(7, after.Global.ProbeTimeoutSeconds);
        Assert.Equal(4, after.Global.FailuresBeforeUnmount);
        Assert.Equal([3, 9, 27], after.Global.BackoffSeconds);
        Assert.Equal(45, after.Global.MountedProbeIntervalSeconds);
        Assert.Equal(6, after.Global.SuspendUnmountTimeoutSeconds);
        Assert.False(after.Global.StartWithWindows);
    }

    /// <summary>
    /// <see cref="IHostConfigStore.Current"/> is documented as a snapshot that "never mutates
    /// underneath" a consumer. The supervisor reads it once per tick and makes mount decisions
    /// from it. Catches a store that edits the live config in place on reload, which would let a
    /// host's drive letter or mount mode change halfway through a decision that was made about
    /// the old value.
    /// </summary>
    [Fact]
    public void ValidReload_DoesNotMutateAPreviouslyHandedOutSnapshot()
    {
        using var harness = Harness.WithV1();
        var snapshot = harness.Store.Current;

        harness.Reader.Content = ValidV2;
        harness.Watcher.RaiseChanged();
        harness.Time.Advance(harness.Options.DebounceDelay);

        Assert.Equal(["jump", "nas"], snapshot.Hosts.Keys.Order());
        Assert.Equal("nas-v1.example.internal", snapshot.Hosts["nas"].Hostname);
        Assert.Equal("N:", snapshot.Hosts["nas"].Mount.Drive);
        Assert.Equal(5599, snapshot.Global.RcloneRcPort);

        Assert.NotSame(snapshot, harness.Store.Current);
        Assert.Equal("nas-v2.example.internal", harness.Store.Current.Hosts["nas"].Hostname);
    }

    /// <summary>
    /// The recovery path: valid → invalid → valid must end up applied. Catches a store that
    /// latches after a rejection — a disposed-and-never-rearmed debounce timer, or a "failed"
    /// flag that suppresses later reloads. The symptom in the field is silent: the user fixes
    /// the file, saves, sees no error, and Bosun keeps running the old config indefinitely.
    /// </summary>
    [Fact]
    public void InvalidThenCorrectedReload_AppliesTheCorrectedConfig()
    {
        using var harness = Harness.WithV1();
        var changes = new List<BosunConfig>();
        var failures = new List<ConfigReloadFailedEventArgs>();
        harness.Store.ConfigChanged += (_, e) => changes.Add(e.Config);
        harness.Store.ConfigReloadFailed += (_, e) => failures.Add(e);

        harness.Reader.Content = InvalidTwoRules;
        harness.Watcher.RaiseChanged();
        harness.Time.Advance(harness.Options.DebounceDelay);

        Assert.Empty(changes);
        Assert.Equal(ConfigReloadFailureReason.ValidationFailed, Assert.Single(failures).Reason);

        harness.Reader.Content = ValidV2;
        harness.Watcher.RaiseChanged();
        harness.Time.Advance(harness.Options.DebounceDelay);

        Assert.Equal("nas-v2.example.internal", harness.Store.Current.Hosts["nas"].Hostname);
        Assert.Single(changes);
        Assert.Single(failures);
    }

    /// <summary>
    /// Same latch risk on the transient-read path, which unlike a validation failure runs a
    /// retry chain: after the retry budget is exhausted and <c>ReadFailed</c> is surfaced, a
    /// later save must still be picked up. Catches a retry chain that leaves the debounce timer
    /// slot occupied, so subsequent <c>Changed</c> events are swallowed — and confirms the
    /// exhausted read never evicted the last valid config in the first place.
    /// </summary>
    [Fact]
    public void ExhaustedTransientReadFailure_KeepsCurrentAndDoesNotBlockALaterSuccessfulReload()
    {
        using var harness = Harness.WithV1();
        var failures = new List<ConfigReloadFailedEventArgs>();
        harness.Store.ConfigReloadFailed += (_, e) => failures.Add(e);

        harness.Reader.Content = string.Empty;
        harness.Watcher.RaiseChanged();
        harness.Time.Advance(harness.Options.DebounceDelay + (harness.Options.ReadRetryDelay * harness.Options.MaxReadAttempts));

        Assert.Equal(ConfigReloadFailureReason.ReadFailed, Assert.Single(failures).Reason);
        Assert.Equal("nas-v1.example.internal", harness.Store.Current.Hosts["nas"].Hostname);
        Assert.Equal(2, harness.Store.Current.Hosts.Count);

        harness.Reader.Content = ValidV2;
        harness.Watcher.RaiseChanged();
        harness.Time.Advance(harness.Options.DebounceDelay);

        Assert.Equal("nas-v2.example.internal", harness.Store.Current.Hosts["nas"].Hostname);
        Assert.Single(failures);
    }

    /// <summary>
    /// A rejected reload must surface every reason it was rejected, not the first — same
    /// requirement as <see cref="ConfigValidator"/>'s, but across the store boundary where the
    /// error list is re-projected. Catches a store that reports <c>Errors[0]</c> only, sending
    /// the user round the edit-save-reject loop once per violation.
    /// </summary>
    [Fact]
    public void RejectedReload_SurfacesEveryValidationErrorNotJustTheFirst()
    {
        using var harness = Harness.WithV1();
        ConfigReloadFailedEventArgs? failure = null;
        harness.Store.ConfigReloadFailed += (_, e) => failure = e;

        harness.Reader.Content = InvalidTwoRules;
        harness.Watcher.RaiseChanged();
        harness.Time.Advance(harness.Options.DebounceDelay);

        Assert.NotNull(failure);
        Assert.Equal(2, failure!.Errors.Count);
        Assert.Contains(failure.Errors, m => m.Contains("C:", StringComparison.Ordinal));
        Assert.Contains(failure.Errors, m => m.Contains("off", StringComparison.Ordinal));
    }

    /// <summary>
    /// The config the event announces must be the config the store is serving. Catches an event
    /// raised with a differently-bound instance (or with the pre-reload config), which would set
    /// a consumer's cached view against what every later <c>Current</c> read returns — the kind
    /// of divergence that ends with the supervisor unmounting a host that is no longer
    /// configured the way it thinks.
    /// </summary>
    [Fact]
    public void ConfigChanged_CarriesExactlyTheInstanceThatBecameCurrent()
    {
        using var harness = Harness.WithV1();
        BosunConfig? announced = null;
        harness.Store.ConfigChanged += (_, e) => announced = e.Config;

        harness.Reader.Content = ValidV2;
        harness.Watcher.RaiseChanged();
        harness.Time.Advance(harness.Options.DebounceDelay);

        Assert.NotNull(announced);
        Assert.Same(harness.Store.Current, announced);
    }

    /// <summary>
    /// The identity-file check injected at <c>Load</c> must also be the one used on every
    /// reload. Catches a reload path that falls back to a real <c>File.Exists</c>: functionally
    /// it would reject configs on machines where the key lives elsewhere, and in the test suite
    /// it would mean tests silently reading the maintainer's real <c>~/.ssh</c> — precisely what
    /// CLAUDE.md's worktree-safety rules forbid. The reload here names a path that cannot exist,
    /// so a real check would fail it.
    /// </summary>
    [Fact]
    public void Reload_UsesTheInjectedIdentityFileCheck_NotTheRealFilesystem()
    {
        var probed = new List<string>();
        var reader = new FakeConfigFileReader { Content = ValidV1 };
        var watcher = new FakeConfigFileWatcher();
        var time = new FakeTimeProvider();
        var options = new HostConfigStoreOptions();

        using var store = HostConfigStore.Load(
            "hosts.toml",
            reader,
            watcher,
            time,
            path =>
            {
                probed.Add(path);
                return true;
            },
            options);

        const string unreachableKeyPath = "Q:/no-such-directory-bosun-test/id_ed25519";
        reader.Content = ValidV2.Replace(
            "identity_file = \"~/.ssh/id_ed25519\"",
            $"identity_file = \"{unreachableKeyPath}\"",
            StringComparison.Ordinal);

        watcher.RaiseChanged();
        time.Advance(options.DebounceDelay);

        Assert.Equal("nas-v2.example.internal", store.Current.Hosts["nas"].Hostname);
        Assert.Contains(unreachableKeyPath, probed);
    }

    private sealed class Harness : IDisposable
    {
        public required HostConfigStore Store { get; init; }
        public required FakeConfigFileReader Reader { get; init; }
        public required FakeConfigFileWatcher Watcher { get; init; }
        public required FakeTimeProvider Time { get; init; }
        public required HostConfigStoreOptions Options { get; init; }

        public static Harness WithV1()
        {
            var reader = new FakeConfigFileReader { Content = ValidV1 };
            var watcher = new FakeConfigFileWatcher();
            var time = new FakeTimeProvider();
            var options = new HostConfigStoreOptions();

            return new Harness
            {
                Store = HostConfigStore.Load("hosts.toml", reader, watcher, time, AlwaysExists, options),
                Reader = reader,
                Watcher = watcher,
                Time = time,
                Options = options,
            };
        }

        public void Dispose() => Store.Dispose();
    }
}
