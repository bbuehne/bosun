using Bosun.Configuration;
using Bosun.Hosting;

namespace Bosun.Tests.Hosting;

/// <summary>
/// Covers bs-008's detection-and-template half of ADR-012 Decision 4:
/// <see cref="FirstRunConfigBootstrapper"/> always targets an injected temp directory here, never
/// the real <c>%LOCALAPPDATA%\Bosun</c> (CLAUDE.md worktree-safety rules) -- this IS the "fresh
/// filesystem root" the acceptance criterion asks for; the root itself is just a real temp
/// directory rather than a mocked filesystem abstraction, matching the pattern
/// <c>BosunHostFactoryTests</c> already uses for real (but sandboxed) file I/O.
/// </summary>
public sealed class FirstRunConfigBootstrapperTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bosun-tests", "first-run", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void EnsureConfigExists_CreatesTheDirectoryAndWritesATemplate_WhenNothingExists()
    {
        Assert.False(Directory.Exists(_root));
        var configPath = Path.Combine(_root, "hosts.toml");
        var bootstrapper = new FirstRunConfigBootstrapper();

        var isFirstRun = bootstrapper.EnsureConfigExists(configPath);

        Assert.True(isFirstRun);
        Assert.True(Directory.Exists(_root));
        Assert.True(File.Exists(configPath));
        Assert.Contains("[global]", File.ReadAllText(configPath), StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureConfigExists_ReturnsFalseAndTouchesNothing_WhenAFileAlreadyExists()
    {
        Directory.CreateDirectory(_root);
        var configPath = Path.Combine(_root, "hosts.toml");
        File.WriteAllText(configPath, "# my own config, do not overwrite me");
        var bootstrapper = new FirstRunConfigBootstrapper();

        var isFirstRun = bootstrapper.EnsureConfigExists(configPath);

        Assert.False(isFirstRun);
        Assert.Equal("# my own config, do not overwrite me", File.ReadAllText(configPath));
    }

    [Fact]
    public void EnsureConfigExists_CreatesTheDirectory_EvenWhenNothingIsWrittenAfterwards()
    {
        // Not first-run-specific: this is what guarantees FileSystemConfigWatcher's constructor
        // (which throws if its directory is missing) never sees a missing directory, regardless
        // of whether a file already existed. Covers the directory half unconditionally.
        var nested = Path.Combine(_root, "nested", "deeper");
        var configPath = Path.Combine(nested, "hosts.toml");
        var bootstrapper = new FirstRunConfigBootstrapper();

        bootstrapper.EnsureConfigExists(configPath);

        Assert.True(Directory.Exists(nested));
    }

    [Fact]
    public void FirstRunTemplate_ParsesAndValidatesToAnEmptyHostSet()
    {
        // ADR-012 Decision 2: "no config at all is not a failure" -- the template it writes must
        // be a genuinely valid, empty config, not something that merely looks plausible.
        var parsed = ConfigParser.Parse(FirstRunConfigTemplateContentForTests(), "first-run-template");
        var validation = ConfigValidator.Validate(parsed, _ => true);

        Assert.True(validation.IsValid, string.Join("; ", validation.Errors.Select(e => e.Message)));
        Assert.Empty(parsed.Hosts);
    }

    [Fact]
    public void FirstRunTemplate_DoesNotEnableAnyExampleHost()
    {
        // ADR-012 Decision 2: shipping a template that tries to mount nas.example.internal on
        // first launch is worse than no config. Every example block must be commented out.
        var text = FirstRunConfigTemplateContentForTests();
        var lines = text.Split('\n');

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('[') && !trimmed.StartsWith("[global]", StringComparison.Ordinal))
            {
                Assert.Fail($"Found an active (non-global, non-commented) TOML table header: '{line}'");
            }
        }
    }

    [Fact]
    public void FirstRunTemplate_GlobalBlockMatchesGlobalConfigDefaultsExactly()
    {
        // Guards against the template silently drifting from what an ABSENT [global] block would
        // already default to -- the whole point of building it from GlobalConfig's own constants.
        var parsed = ConfigParser.Parse(FirstRunConfigTemplateContentForTests(), "first-run-template");

        Assert.Equal(GlobalConfig.DefaultRcloneRcPort, parsed.Global.RcloneRcPort);
        Assert.Equal(GlobalConfig.DefaultRcloneConfigPath, parsed.Global.RcloneConfigPath);
        Assert.Equal(GlobalConfig.DefaultProbeTimeoutSeconds, parsed.Global.ProbeTimeoutSeconds);
        Assert.Equal(GlobalConfig.DefaultFailuresBeforeUnmount, parsed.Global.FailuresBeforeUnmount);
        Assert.Equal(GlobalConfig.DefaultBackoffSeconds, parsed.Global.BackoffSeconds);
        Assert.Equal(GlobalConfig.DefaultMountedProbeIntervalSeconds, parsed.Global.MountedProbeIntervalSeconds);
        Assert.Equal(GlobalConfig.DefaultSuspendUnmountTimeoutSeconds, parsed.Global.SuspendUnmountTimeoutSeconds);
        Assert.Equal(GlobalConfig.DefaultStartWithWindows, parsed.Global.StartWithWindows);
    }

    /// <summary>
    /// <see cref="FirstRunConfigTemplate"/> is <see langword="internal"/> to <c>Bosun</c>, visible
    /// here via <c>InternalsVisibleTo</c> -- an implementation detail of
    /// <see cref="FirstRunConfigBootstrapper"/>, not part of the public seam, but worth testing
    /// directly rather than only indirectly through a written file.
    /// </summary>
    private static string FirstRunConfigTemplateContentForTests() => FirstRunConfigTemplate.Content;
}
