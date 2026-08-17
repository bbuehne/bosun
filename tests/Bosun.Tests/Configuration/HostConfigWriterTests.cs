using System.Security.AccessControl;
using System.Security.Principal;
using Bosun.Configuration;
using Bosun.Tests.Configuration.Fakes;

namespace Bosun.Tests.Configuration;

/// <summary>
/// Covers bs-ww9.8 / ADR-019's <see cref="HostConfigWriter"/>: validate-before-write, atomic
/// writes to a real (test-owned, temp) file, and the self-write coordination with
/// <see cref="HostConfigStore"/> that keeps its own atomic write from being reprocessed as an
/// external change.
/// </summary>
/// <remarks>
/// The <see cref="HostConfigStore"/> half of every harness here uses the same
/// <see cref="FakeConfigFileReader"/>/<see cref="FakeConfigFileWatcher"/>/<see cref="FakeTimeProvider"/>
/// combination <c>HostConfigStoreTests</c> established -- fully deterministic, no real timing.
/// <see cref="HostConfigWriter"/>'s OWN file I/O, by contrast, targets a REAL file under a
/// per-test temp directory (never the maintainer's real, gitignored <c>config/hosts.toml</c>) --
/// the pattern <c>BosunHostFactoryTests</c> established for exercising real disk I/O safely from
/// a worktree. The two are deliberately decoupled: the fake reader/watcher drive what the STORE
/// believes is on disk, while the writer's temp-path is where this test suite verifies what
/// actually LANDS on disk. A test that needs both in agreement sets
/// <c>harness.Reader.Content</c> to the real bytes <see cref="HostConfigWriter"/> just wrote,
/// exactly as <c>HostConfigStoreTests</c> already simulates "the file changed" by hand.
/// </remarks>
public sealed class HostConfigWriterTests : IDisposable
{
    private static readonly Func<string, bool> AlwaysExists = _ => true;
    private static readonly Func<string, bool> NeverExists = _ => false;

    private const string ValidTwoHostToml = """
        [global]
        rclone_rc_port                  = 5599
        probe_timeout_seconds           = 7
        failures_before_unmount         = 4
        backoff_seconds                 = [3, 9, 27]
        mounted_probe_interval_seconds  = 45
        suspend_unmount_timeout_seconds = 6
        start_with_windows              = false

        [hosts.nas]
        display_name  = "NAS"
        hostname      = "nas.example.internal"
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
        display_name  = "Jump"
        hostname      = "jump.example.com"
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

    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), "bosun-tests", "config-writer", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private static HostConfig ValidNewHost(string key = "new-host", string drive = "P:") => new()
    {
        Key = key,
        DisplayName = "New Host",
        Hostname = "new-host.example.internal",
        Port = 22,
        User = "someuser",
        IdentityFile = "~/.ssh/id_ed25519",
        Mount = new MountConfig
        {
            Mode = MountMode.OnDemand,
            Drive = drive,
            RemotePath = "/srv/data",
            VfsCacheMode = "writes",
            NetworkMode = true,
            IdleUnmountSeconds = 0,
        },
        Session = new SessionConfig
        {
            Autostart = false,
            Reconnect = true,
            Tmux = false,
            TabColor = "#445566",
            ColorScheme = "Campbell",
        },
        Probe = new ProbeConfig { IntervalSeconds = 30, DeepProbe = true },
    };

    // -- validation runs before the write ------------------------------------------------------

    [Fact]
    public async Task SaveHostAsync_HostThatWouldFailValidation_ReturnsInvalidAndWritesNothing()
    {
        using var harness = Harness.WithTwoHosts(_tempDirectory);

        // C: is reserved -- guaranteed rejected by ConfigValidator regardless of any other rule.
        var badHost = ValidNewHost(drive: "C:");

        var result = await harness.Writer.SaveHostAsync(badHost);

        Assert.False(result.Succeeded);
        Assert.NotEmpty(result.ValidationErrors);
        Assert.Contains(result.ValidationErrors, e => e.Rule == "invalid-drive-letter");
        // The file already on disk (pre-seeded by the harness, exactly as a real hosts.toml
        // would already exist before any save) must be byte-for-byte untouched -- and no temp
        // file must have been left behind either, since validation must refuse before ANY I/O.
        Assert.Equal(ValidTwoHostToml, File.ReadAllText(harness.ConfigPath));
        Assert.Single(Directory.GetFiles(_tempDirectory));
        Assert.False(harness.Store.Current.Hosts.ContainsKey("new-host"));
    }

    [Fact]
    public async Task SaveHostAsync_DriveCollisionWithAnExistingHost_ReturnsInvalid()
    {
        using var harness = Harness.WithTwoHosts(_tempDirectory);

        var badHost = ValidNewHost(drive: "N:"); // already claimed by "nas"

        var result = await harness.Writer.SaveHostAsync(badHost);

        Assert.False(result.Succeeded);
        Assert.Contains(result.ValidationErrors, e => e.Rule == "drive-collision");
    }

    [Fact]
    public async Task SaveHostAsync_IdentityFileThatDoesNotExistPerTheInjectedCheck_ReturnsInvalid()
    {
        using var harness = Harness.WithTwoHosts(_tempDirectory, identityFileExists: NeverExists);

        var result = await harness.Writer.SaveHostAsync(ValidNewHost());

        Assert.False(result.Succeeded);
        Assert.Contains(result.ValidationErrors, e => e.Rule == "identity-file-not-found");
    }

    // -- the write itself is atomic and lands real bytes ---------------------------------------

    [Fact]
    public async Task SaveHostAsync_ValidHost_WritesAFileThatParsesAndValidatesCleanly()
    {
        using var harness = Harness.WithTwoHosts(_tempDirectory);

        var result = await harness.Writer.SaveHostAsync(ValidNewHost());

        Assert.True(result.Succeeded);
        Assert.True(File.Exists(harness.ConfigPath));

        var written = File.ReadAllText(harness.ConfigPath);
        var reparsed = ConfigParser.Parse(written, "written");
        Assert.True(reparsed.Hosts.ContainsKey("new-host"));
        Assert.True(reparsed.Hosts.ContainsKey("nas"));
        Assert.True(reparsed.Hosts.ContainsKey("jump"));
        Assert.Empty(ConfigValidator.Validate(reparsed, AlwaysExists).Errors);
    }

    [Fact]
    public async Task SaveHostAsync_SuccessfulWrite_LeavesNoTempFileBehind()
    {
        using var harness = Harness.WithTwoHosts(_tempDirectory);

        await harness.Writer.SaveHostAsync(ValidNewHost());

        var entries = Directory.GetFiles(_tempDirectory);
        Assert.Single(entries);
        Assert.Equal(harness.ConfigPath, entries[0]);
    }

    /// <summary>
    /// The atomicity contract: if the final rename cannot complete for any reason, the file at
    /// <c>hosts.toml</c>'s path must be found EXACTLY as it was before the attempt -- never
    /// truncated, never partially overwritten. Forces that failure by holding the target file
    /// open with a share mode that blocks a rename onto it, which fails
    /// <c>File.Move(..., overwrite: true)</c> with an <see cref="IOException"/> -- proving the
    /// write only ever touches a separate temp path until the swap actually succeeds.
    /// </summary>
    [Fact]
    public async Task SaveHostAsync_DestinationLockedSoTheRenameCannotComplete_LeavesOriginalFileIntact()
    {
        using var harness = Harness.WithTwoHosts(_tempDirectory);
        var originalBytes = File.ReadAllText(harness.ConfigPath);

        using (new FileStream(harness.ConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = await harness.Writer.SaveHostAsync(ValidNewHost());

            Assert.False(result.Succeeded);
            Assert.NotNull(result.Error);
        }

        Assert.Equal(originalBytes, File.ReadAllText(harness.ConfigPath));
        // The failed attempt's temp file must not litter the directory either.
        Assert.DoesNotContain(Directory.GetFiles(_tempDirectory), f => f != harness.ConfigPath);
    }

    /// <summary>
    /// A stronger, discriminating proof of atomicity than the locked-destination test above. That
    /// test forces a failure mode (destination locked) that a NAIVE direct-overwrite
    /// implementation happens to fail identically to a correct one, since the initial open for
    /// write is refused either way -- it cannot tell "writes to a temp file first" apart from
    /// "writes the target directly". This test can: <c>FileSystemRights.CreateFiles</c> denied on
    /// the directory blocks creating a NEW file there (the temp file a correct implementation
    /// needs) while leaving <c>WriteData</c> on the ALREADY-EXISTING target file untouched (a
    /// naive direct overwrite would sail right through). A correct temp-file-then-rename
    /// implementation must therefore fail here and leave <c>hosts.toml</c> completely untouched;
    /// an implementation that writes the target directly would not.
    /// </summary>
    [Fact]
    public async Task SaveHostAsync_DirectoryDeniesCreatingNewFiles_FailsWithoutTouchingTheExistingTarget()
    {
        using var harness = Harness.WithTwoHosts(_tempDirectory);
        var originalBytes = File.ReadAllText(harness.ConfigPath);

        var sid = WindowsIdentity.GetCurrent().User!;
        var dirInfo = new DirectoryInfo(_tempDirectory);
        var security = dirInfo.GetAccessControl();
        var denyCreateFiles = new FileSystemAccessRule(
            sid,
            FileSystemRights.CreateFiles,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Deny);
        security.AddAccessRule(denyCreateFiles);
        dirInfo.SetAccessControl(security);

        try
        {
            var result = await harness.Writer.SaveHostAsync(ValidNewHost());

            Assert.False(result.Succeeded);
            Assert.NotNull(result.Error);
            Assert.Equal(originalBytes, File.ReadAllText(harness.ConfigPath));
        }
        finally
        {
            // Must be lifted before the harness/temp-directory teardown, or Directory.Delete in
            // Dispose() fails the same way the write above was made to.
            security.RemoveAccessRule(denyCreateFiles);
            dirInfo.SetAccessControl(security);
        }
    }

    [Fact]
    public async Task SaveHostAsync_EditingAnExistingHost_ReplacesRatherThanDuplicates()
    {
        using var harness = Harness.WithTwoHosts(_tempDirectory);

        var edited = harness.Store.Current.Hosts["nas"] with { Hostname = "nas-v2.example.internal" };
        var result = await harness.Writer.SaveHostAsync(edited);

        Assert.True(result.Succeeded);
        Assert.Equal(2, harness.Store.Current.Hosts.Count);
        Assert.Equal("nas-v2.example.internal", harness.Store.Current.Hosts["nas"].Hostname);
    }

    [Fact]
    public async Task SaveHostAsync_PreservesGlobalExactlyAsLoaded()
    {
        using var harness = Harness.WithTwoHosts(_tempDirectory);
        var globalBefore = harness.Store.Current.Global;

        await harness.Writer.SaveHostAsync(ValidNewHost());

        var globalAfter = harness.Store.Current.Global;
        Assert.Equal(globalBefore.RcloneRcPort, globalAfter.RcloneRcPort);
        Assert.Equal(globalBefore.ProbeTimeoutSeconds, globalAfter.ProbeTimeoutSeconds);
        Assert.Equal(globalBefore.FailuresBeforeUnmount, globalAfter.FailuresBeforeUnmount);
        Assert.Equal(globalBefore.BackoffSeconds, globalAfter.BackoffSeconds);
        Assert.Equal(globalBefore.MountedProbeIntervalSeconds, globalAfter.MountedProbeIntervalSeconds);
        Assert.Equal(globalBefore.SuspendUnmountTimeoutSeconds, globalAfter.SuspendUnmountTimeoutSeconds);
        Assert.Equal(globalBefore.StartWithWindows, globalAfter.StartWithWindows);
    }

    // -- self-write coordination with HostConfigStore -------------------------------------------

    [Fact]
    public async Task SaveHostAsync_UpdatesStoreCurrentImmediately_WithoutWaitingForTheDebounce()
    {
        using var harness = Harness.WithTwoHosts(_tempDirectory);

        var result = await harness.Writer.SaveHostAsync(ValidNewHost());

        Assert.True(result.Succeeded);
        // No harness.Time.Advance(...) call anywhere -- if this passes, Current updated
        // synchronously from the write itself, not from the debounced file-watcher path.
        Assert.True(harness.Store.Current.Hosts.ContainsKey("new-host"));
    }

    [Fact]
    public async Task SaveHostAsync_FiresConfigChangedExactlyOnceForTheWrite()
    {
        using var harness = Harness.WithTwoHosts(_tempDirectory);
        var changes = new List<BosunConfig>();
        harness.Store.ConfigChanged += (_, e) => changes.Add(e.Config);

        await harness.Writer.SaveHostAsync(ValidNewHost());

        Assert.Single(changes);
        Assert.True(changes[0].Hosts.ContainsKey("new-host"));
    }

    /// <summary>
    /// The core hazard ADR-019 names: after <see cref="HostConfigWriter"/> writes, the file
    /// watcher WILL eventually notice (simulated here by pointing the fake reader/watcher at the
    /// exact bytes the writer just put on real disk and raising <c>Changed</c>). That must not be
    /// reprocessed as a second, independent change -- no second <c>ConfigChanged</c>, and
    /// <see cref="IHostConfigStore.Current"/> must stay the very same instance the write already
    /// produced rather than being replaced by a new, merely content-equal one.
    /// </summary>
    [Fact]
    public async Task WatcherCatchingUpToTheWriterSOwnWrite_IsSuppressedNotReprocessed()
    {
        using var harness = Harness.WithTwoHosts(_tempDirectory);
        var changes = new List<BosunConfig>();
        harness.Store.ConfigChanged += (_, e) => changes.Add(e.Config);

        await harness.Writer.SaveHostAsync(ValidNewHost());
        Assert.Single(changes);
        var currentAfterWrite = harness.Store.Current;

        // Simulate the watcher catching up: the fake reader now serves exactly what landed on
        // real disk, and a Changed event fires, exactly as FileSystemConfigWatcher would in
        // production once it observes the writer's own rename.
        harness.Reader.Content = File.ReadAllText(harness.ConfigPath);
        harness.Watcher.RaiseChanged();
        harness.Time.Advance(harness.Options.DebounceDelay);

        Assert.Single(changes); // still just the one from the write itself -- no second firing
        Assert.Same(currentAfterWrite, harness.Store.Current); // same instance, not a new equal one
    }

    /// <summary>
    /// The suppression must be scoped to the exact write, not a blanket "ignore the next
    /// change" -- a genuine external edit that lands instead of (or after) our own write must
    /// still be processed normally.
    /// </summary>
    [Fact]
    public async Task GenuineExternalEditAfterOurWrite_IsStillProcessedNormally()
    {
        using var harness = Harness.WithTwoHosts(_tempDirectory);
        var changes = new List<BosunConfig>();
        harness.Store.ConfigChanged += (_, e) => changes.Add(e.Config);

        await harness.Writer.SaveHostAsync(ValidNewHost());
        Assert.Single(changes);

        // A real external edit -- different content from what the writer produced.
        harness.Reader.Content = ValidTwoHostToml.Replace(
            "hostname      = \"nas.example.internal\"",
            "hostname      = \"nas-edited-externally.example.internal\"",
            StringComparison.Ordinal);
        harness.Watcher.RaiseChanged();
        harness.Time.Advance(harness.Options.DebounceDelay);

        Assert.Equal(2, changes.Count);
        Assert.Equal("nas-edited-externally.example.internal", harness.Store.Current.Hosts["nas"].Hostname);
    }

    // -- delete ---------------------------------------------------------------------------------

    [Fact]
    public async Task DeleteHostAsync_ExistingHost_RemovesItAndWritesTheRest()
    {
        using var harness = Harness.WithTwoHosts(_tempDirectory);

        var result = await harness.Writer.DeleteHostAsync("jump");

        Assert.True(result.Succeeded);
        Assert.False(harness.Store.Current.Hosts.ContainsKey("jump"));
        Assert.True(harness.Store.Current.Hosts.ContainsKey("nas"));

        var written = ConfigParser.Parse(File.ReadAllText(harness.ConfigPath), "written");
        Assert.False(written.Hosts.ContainsKey("jump"));
        Assert.True(written.Hosts.ContainsKey("nas"));
    }

    [Fact]
    public async Task DeleteHostAsync_UnknownHostKey_FailsWithAClearErrorAndWritesNothing()
    {
        using var harness = Harness.WithTwoHosts(_tempDirectory);

        var result = await harness.Writer.DeleteHostAsync("does-not-exist");

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
        Assert.Contains("does-not-exist", result.Error, StringComparison.Ordinal);
        Assert.Equal(2, harness.Store.Current.Hosts.Count);
    }

    private sealed class Harness : IDisposable
    {
        public required HostConfigStore Store { get; init; }
        public required HostConfigWriter Writer { get; init; }
        public required FakeConfigFileReader Reader { get; init; }
        public required FakeConfigFileWatcher Watcher { get; init; }
        public required FakeTimeProvider Time { get; init; }
        public required HostConfigStoreOptions Options { get; init; }
        public required string ConfigPath { get; init; }

        // identityFileExists governs only the WRITER's validation of a NEW/EDITED host -- the
        // store's own initial Load always uses AlwaysExists so the two pre-seeded fixture hosts
        // (whose identity_file is a placeholder path) load cleanly regardless of what a given
        // test wants the writer to see.
        public static Harness WithTwoHosts(string tempDirectory, Func<string, bool>? identityFileExists = null)
        {
            Directory.CreateDirectory(tempDirectory);
            var configPath = Path.Combine(tempDirectory, "hosts.toml");
            File.WriteAllText(configPath, ValidTwoHostToml);

            var reader = new FakeConfigFileReader { Content = ValidTwoHostToml };
            var watcher = new FakeConfigFileWatcher();
            var time = new FakeTimeProvider();
            var options = new HostConfigStoreOptions();

            var store = HostConfigStore.Load("hosts.toml", reader, watcher, time, AlwaysExists, options);
            var writer = new HostConfigWriter(configPath, store, identityFileExists ?? AlwaysExists);

            return new Harness
            {
                Store = store,
                Writer = writer,
                Reader = reader,
                Watcher = watcher,
                Time = time,
                Options = options,
                ConfigPath = configPath,
            };
        }

        public void Dispose() => Store.Dispose();
    }
}
