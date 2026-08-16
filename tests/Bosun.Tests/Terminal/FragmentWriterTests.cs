using System.Text.Json;
using Bosun.Terminal;
using Bosun.Tests.Terminal.Fakes;
using Bosun.Tests.Terminal.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bosun.Tests.Terminal;

/// <summary>Covers bs-k41's filesystem half: injected path, atomic write, self-validation, a
/// simulated mid-write failure leaving the previous fragment intact, and Invariant I5 (never a
/// path containing settings.json).</summary>
public sealed class FragmentWriterTests
{
    private const string FragmentPath = @"C:\fake\Fragments\Bosun\bosun.json";

    [Fact]
    public async Task WriteAsync_writes_valid_JSON_to_the_injected_path()
    {
        var fileSystem = new FakeFragmentFileSystem();
        var writer = CreateWriter(fileSystem);
        var config = TerminalHostFixtures.Build(TerminalHostFixtures.Host("example-nas"));

        await writer.WriteAsync(config);

        var content = fileSystem.DestinationContent(FragmentPath);
        Assert.NotNull(content);
        using var parsed = JsonDocument.Parse(content!); // throws if invalid
        Assert.True(parsed.RootElement.TryGetProperty("profiles", out var profiles));
        Assert.Equal(1, profiles.GetArrayLength());
    }

    [Fact]
    public async Task WriteAsync_writes_via_a_temp_file_then_moves_it_into_place()
    {
        var fileSystem = new FakeFragmentFileSystem();
        var writer = CreateWriter(fileSystem);
        var config = TerminalHostFixtures.Build(TerminalHostFixtures.Host("example-nas"));

        await writer.WriteAsync(config);

        // A temp path distinct from the final destination was written to first...
        var tempPath = Assert.Single(fileSystem.TouchedPaths.Distinct(), p => p != FragmentPath && p.Contains("bosun.json"));

        // ...and the move left nothing behind at that temp path -- only the real destination has
        // content, which is what makes the write atomic from an outside observer's perspective.
        Assert.Null(fileSystem.DestinationContent(tempPath));
        Assert.NotNull(fileSystem.DestinationContent(FragmentPath));
    }

    [Fact]
    public async Task WriteAsync_creates_the_containing_directory_if_absent()
    {
        var fileSystem = new FakeFragmentFileSystem();
        var writer = CreateWriter(fileSystem);
        var config = TerminalHostFixtures.Build(TerminalHostFixtures.Host("example-nas"));

        await writer.WriteAsync(config);

        Assert.Contains(fileSystem.TouchedPaths, p => p.EndsWith(@"Fragments\Bosun", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WriteAsync_a_simulated_failure_during_move_leaves_the_previous_fragment_intact()
    {
        var fileSystem = new FakeFragmentFileSystem();
        fileSystem.SeedExistingFragment(FragmentPath, """{"profiles":[]}""");
        fileSystem.FailOnMove = new IOException("simulated failure between temp write and move");
        var writer = CreateWriter(fileSystem);
        var config = TerminalHostFixtures.Build(TerminalHostFixtures.Host("example-nas"));

        await Assert.ThrowsAsync<IOException>(() => writer.WriteAsync(config));

        Assert.Equal("""{"profiles":[]}""", fileSystem.DestinationContent(FragmentPath));
    }

    [Fact]
    public async Task WriteAsync_a_simulated_failure_during_the_temp_write_leaves_the_previous_fragment_intact()
    {
        var fileSystem = new FakeFragmentFileSystem();
        fileSystem.SeedExistingFragment(FragmentPath, """{"profiles":[]}""");
        fileSystem.FailOnWrite = new IOException("simulated disk-full mid-write");
        var writer = CreateWriter(fileSystem);
        var config = TerminalHostFixtures.Build(TerminalHostFixtures.Host("example-nas"));

        await Assert.ThrowsAsync<IOException>(() => writer.WriteAsync(config));

        Assert.Equal("""{"profiles":[]}""", fileSystem.DestinationContent(FragmentPath));
    }

    [Fact]
    public async Task WriteAsync_never_touches_a_path_containing_settingsJson()
    {
        var fileSystem = new FakeFragmentFileSystem();
        var writer = CreateWriter(fileSystem);
        var config = TerminalHostFixtures.Build(
            TerminalHostFixtures.Host("example-nas"),
            TerminalHostFixtures.Host("example-remote", mountMode: Bosun.Configuration.MountMode.None));

        await writer.WriteAsync(config);

        Assert.All(fileSystem.TouchedPaths, p => Assert.DoesNotContain("settings.json", p, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Constructor_rejects_a_fragment_path_containing_settingsJson()
    {
        var fileSystem = new FakeFragmentFileSystem();
        var options = new FragmentWriterOptions { FragmentPath = @"C:\Users\someone\AppData\Local\Microsoft\Windows Terminal\settings.json" };

        Assert.Throws<ArgumentException>(() => new FragmentWriter(options, fileSystem, NullLogger<FragmentWriter>.Instance));
    }

    [Fact]
    public async Task WriteAsync_includes_hosts_with_mount_mode_none()
    {
        var fileSystem = new FakeFragmentFileSystem();
        var writer = CreateWriter(fileSystem);
        var config = TerminalHostFixtures.Build(TerminalHostFixtures.Host("example-jump", mountMode: Bosun.Configuration.MountMode.None));

        await writer.WriteAsync(config);

        var content = fileSystem.DestinationContent(FragmentPath)!;
        Assert.Contains("example-jump", content);
    }

    private static FragmentWriter CreateWriter(IFragmentFileSystem fileSystem) =>
        new(new FragmentWriterOptions { FragmentPath = FragmentPath }, fileSystem, NullLogger<FragmentWriter>.Instance);
}
