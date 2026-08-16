using Bosun.Configuration;
using Bosun.Terminal;
using Bosun.Tests.Terminal.Fakes;
using Bosun.Tests.Terminal.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bosun.Tests.Terminal;

/// <summary>Covers the "rewrite on IHostConfigStore.ConfigChanged" behaviour bs-k41 describes,
/// exercised against a fake store that fires ConfigChanged manually -- no real config file, no
/// real fragment path.</summary>
public sealed class FragmentRewriteCoordinatorTests
{
    private const string FragmentPath = @"C:\fake\Fragments\Bosun\bosun.json";

    [Fact]
    public async Task WriteInitialAsync_writes_the_stores_current_config()
    {
        var fileSystem = new FakeFragmentFileSystem();
        var writer = new FragmentWriter(new FragmentWriterOptions { FragmentPath = FragmentPath }, fileSystem, NullLogger<FragmentWriter>.Instance);
        var config = TerminalHostFixtures.Build(TerminalHostFixtures.Host("example-nas"));
        var store = new MutableFakeHostConfigStore(config);
        using var coordinator = new FragmentRewriteCoordinator(writer, store, NullLogger<FragmentRewriteCoordinator>.Instance);

        await coordinator.WriteInitialAsync();

        Assert.Contains("example-nas", fileSystem.DestinationContent(FragmentPath));
    }

    [Fact]
    public async Task ConfigChanged_triggers_a_rewrite_with_the_new_config()
    {
        var fileSystem = new FakeFragmentFileSystem();
        var writer = new FragmentWriter(new FragmentWriterOptions { FragmentPath = FragmentPath }, fileSystem, NullLogger<FragmentWriter>.Instance);
        var initial = TerminalHostFixtures.Build(TerminalHostFixtures.Host("example-nas"));
        var store = new MutableFakeHostConfigStore(initial);
        using var coordinator = new FragmentRewriteCoordinator(writer, store, NullLogger<FragmentRewriteCoordinator>.Instance);
        await coordinator.WriteInitialAsync();

        var updated = TerminalHostFixtures.Build(TerminalHostFixtures.Host("example-remote"));
        store.RaiseConfigChanged(updated);
        await coordinator.WaitForPendingWriteAsync();

        var content = fileSystem.DestinationContent(FragmentPath)!;
        Assert.Contains("example-remote", content);
        Assert.DoesNotContain("example-nas", content);
    }

    [Fact]
    public async Task Dispose_unsubscribes_so_a_later_ConfigChanged_does_not_rewrite()
    {
        var fileSystem = new FakeFragmentFileSystem();
        var writer = new FragmentWriter(new FragmentWriterOptions { FragmentPath = FragmentPath }, fileSystem, NullLogger<FragmentWriter>.Instance);
        var initial = TerminalHostFixtures.Build(TerminalHostFixtures.Host("example-nas"));
        var store = new MutableFakeHostConfigStore(initial);
        var coordinator = new FragmentRewriteCoordinator(writer, store, NullLogger<FragmentRewriteCoordinator>.Instance);
        await coordinator.WriteInitialAsync();
        coordinator.Dispose();

        var writeCountBefore = fileSystem.TouchedPaths.Count;
        store.RaiseConfigChanged(TerminalHostFixtures.Build(TerminalHostFixtures.Host("example-remote")));

        // Dispose unsubscribed before this raise, so -- unlike the other tests -- there is no
        // pending write to await: OnConfigChanged (and therefore the assignment to the pending-write
        // field) never runs at all. Asserting immediately is deterministic precisely because nothing
        // was scheduled, not despite it.
        Assert.Equal(writeCountBefore, fileSystem.TouchedPaths.Count);
        Assert.Contains("example-nas", fileSystem.DestinationContent(FragmentPath));
    }

    /// <summary>Local, mutable fake -- SessionMonitor's FakeHostConfigStore is internal to its own
    /// namespace's test needs (immutable Current, no reload support), so this coordinator's tests
    /// bring their own that can actually raise ConfigChanged.</summary>
    private sealed class MutableFakeHostConfigStore(BosunConfig initial) : IHostConfigStore
    {
        public BosunConfig Current { get; private set; } = initial;

        public event EventHandler<ConfigChangedEventArgs>? ConfigChanged;
#pragma warning disable CS0067 // never raised -- these tests never exercise a reload failure
        public event EventHandler<ConfigReloadFailedEventArgs>? ConfigReloadFailed;
#pragma warning restore CS0067

        public void RaiseConfigChanged(BosunConfig config)
        {
            Current = config;
            ConfigChanged?.Invoke(this, new ConfigChangedEventArgs(config));
        }
    }
}
