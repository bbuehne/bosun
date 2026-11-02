#if DEBUG
using Bosun.Diagnostics.FaultInjection;
using Bosun.Hosting;
using Bosun.Rclone;
using Bosun.Supervisor;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bosun.Tests.Diagnostics.FaultInjection;

/// <summary>
/// bs-uzk: how the faults are wired into the host. Every host is built with temp paths and with
/// <c>registerStartupOrchestrator: false</c>, so nothing starts rcd, touches a drive letter or reads
/// the environment: the faults come from <see cref="BosunHostOptions.DebugFaults"/>, never from
/// <c>BOSUN_FAULT</c> itself.
/// </summary>
public sealed class DebugFaultHostWiringTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "bosun-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private IHost BuildHost(DebugFaultSettings faults)
    {
        Directory.CreateDirectory(_directory);
        var identityFilePath = Path.Combine(_directory, "id_ed25519_fixture");
        File.WriteAllText(identityFilePath, "not a real key -- existence is all that's checked");
        var configPath = Path.Combine(_directory, "hosts.toml");
        File.WriteAllText(configPath, $$"""
            [hosts.fixture-host]
            display_name  = "Fixture Host"
            hostname      = "fixture.example.internal"
            port          = 22
            user          = "someuser"
            identity_file = '{{identityFilePath}}'

              [hosts.fixture-host.mount]
              mode = "none"

              [hosts.fixture-host.session]
              autostart = false
              reconnect = true
              tmux      = false
              tab_color    = "#2D5F3F"
              color_scheme = "Campbell"

              [hosts.fixture-host.probe]
              interval_seconds = 0
              deep_probe       = false
            """);

        return BosunHostFactory.CreateHost(
            new BosunHostOptions
            {
                LogDirectory = Path.Combine(_directory, "logs"),
                ConfigPath = configPath,
                DebugFaults = faults,
            },
            registerStartupOrchestrator: false);
    }

    [Fact]
    public void DefaultOptions_HaveNoFaultActive()
    {
        // What every test, and every Debug run without the variable, gets.
        var options = new BosunHostOptions { LogDirectory = "x", ConfigPath = "y" };

        Assert.Same(DebugFaultSettings.None, options.DebugFaults);
        Assert.False(options.DebugFaults.Any);
    }

    [Fact]
    public void WithNoFault_TheSupervisorAndTheRcloneClientAreTheRealOnes()
    {
        using var host = BuildHost(DebugFaultSettings.None);

        Assert.IsType<RcloneClient>(host.Services.GetRequiredService<IRcloneClient>());
        Assert.NotNull(host.Services.GetRequiredService<MountSupervisor>());
    }

    [Fact]
    public void SupervisorStartFault_MakesBothWaysOfResolvingTheSupervisorThrow()
    {
        // StartupOrchestrator resolves MountSupervisor; the UI resolves IMountSupervisor. Both must
        // fail, or the tray would show a working supervisor next to a "failed to start" banner.
        using var host = BuildHost(DebugFaultSettings.Parse("supervisor-start", seconds: null));

        var concrete = Assert.Throws<InvalidOperationException>(() => host.Services.GetRequiredService<MountSupervisor>());
        var viaInterface = Assert.Throws<InvalidOperationException>(() => host.Services.GetRequiredService<IMountSupervisor>());

        Assert.Contains("BOSUN_FAULT=supervisor-start", concrete.Message);
        Assert.Contains("BOSUN_FAULT=supervisor-start", viaInterface.Message);
    }

    [Fact]
    public void SupervisorStartFault_DoesNotTouchTheRcloneClient()
    {
        using var host = BuildHost(DebugFaultSettings.Parse("supervisor-start", seconds: null));

        Assert.IsType<RcloneClient>(host.Services.GetRequiredService<IRcloneClient>());
    }

    [Fact]
    public void RcdUnavailableFault_WrapsTheRcloneClient_AndDoesNotAffectTheSupervisor()
    {
        using var host = BuildHost(DebugFaultSettings.Parse("rcd-unavailable-at-resume", "30"));

        Assert.IsType<FaultInjectingRcloneClient>(host.Services.GetRequiredService<IRcloneClient>());
        Assert.NotNull(host.Services.GetRequiredService<MountSupervisor>());
    }

    [Fact]
    public void UiExceptionFault_ChangesNeitherTheSupervisorNorTheRcloneClient()
    {
        // ui-exception is armed by App once the UI is up; the host composition is untouched.
        using var host = BuildHost(DebugFaultSettings.Parse("ui-exception", seconds: null));

        Assert.IsType<RcloneClient>(host.Services.GetRequiredService<IRcloneClient>());
        Assert.NotNull(host.Services.GetRequiredService<MountSupervisor>());
    }

    [Fact]
    public void BuildingTheHost_LogsAWarningNamingEveryActiveFault()
    {
        var host = BuildHost(DebugFaultSettings.Parse("supervisor-start,ui-exception,rcd-unavailable-at-resume,typo", "45"));

        // Serilog is registered with dispose: true, so disposing the host flushes the file sink.
        host.Dispose();

        var logFile = Assert.Single(Directory.GetFiles(Path.Combine(_directory, "logs"), "bosun-*.log"));
        var lines = File.ReadAllText(logFile).Split('\n');
        var faultLines = lines.Where(l => l.Contains("FAULT INJECTION ACTIVE")).ToList();

        Assert.Equal(3, faultLines.Count);
        Assert.Single(faultLines, l => l.Contains("BOSUN_FAULT=supervisor-start"));
        Assert.Single(faultLines, l => l.Contains("BOSUN_FAULT=ui-exception"));
        Assert.Single(faultLines, l => l.Contains("BOSUN_FAULT=rcd-unavailable-at-resume") && l.Contains("45 s"));

        // The log template puts the level on the line before the message.
        foreach (var faultLine in faultLines)
        {
            Assert.Contains("[WRN]", lines[Array.IndexOf(lines, faultLine) - 1]);
        }

        var indexOfTypo = Array.FindIndex(lines, l => l.Contains("'typo'"));
        Assert.True(indexOfTypo > 0, "the unknown value 'typo' should be reported");
        Assert.Contains("[WRN]", lines[indexOfTypo - 1]);
    }

    [Fact]
    public void BuildingTheHost_WithNoFault_LogsNoFaultInjectionLine()
    {
        var host = BuildHost(DebugFaultSettings.None);

        // The file sink creates its file on the first event, so write one: otherwise "no file" and
        // "no fault line" would look the same.
        host.Services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DebugFaultHostWiringTests>>()
            .LogInformation("bs-uzk marker");
        host.Dispose();

        var logFile = Assert.Single(Directory.GetFiles(Path.Combine(_directory, "logs"), "bosun-*.log"));
        var contents = File.ReadAllText(logFile);

        Assert.Contains("bs-uzk marker", contents);
        Assert.DoesNotContain("FAULT INJECTION", contents);
    }
}
#endif
