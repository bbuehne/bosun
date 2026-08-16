using Bosun.Hosting;

namespace Bosun.Tests.Hosting;

/// <summary>
/// Covers <see cref="StartupReadiness"/>'s causal-message logic directly, independent of
/// <see cref="StartupOrchestrator"/> -- ADR-012 Decision 3 requires a specific reason per host
/// ("P: is not mounted -- WinFsp is not installed"), never a generic "some features unavailable".
/// </summary>
public sealed class StartupReadinessTests
{
    [Fact]
    public void MountBlockedReason_ReturnsConfigMessage_WhenConfigIsInvalid()
    {
        var readiness = Loaded() with { ConfigState = ConfigReadinessState.Invalid };

        Assert.Equal("the configuration file is invalid", readiness.MountBlockedReason("nas"));
        Assert.False(readiness.MountingAvailable);
    }

    [Fact]
    public void MountBlockedReason_ReturnsWinFspMessage_WhenWinFspIsMissing_EvenIfEverythingElseIsHealthy()
    {
        var readiness = Loaded() with { WinFspInstalled = false };

        Assert.Equal("WinFsp is not installed", readiness.MountBlockedReason("nas"));
        Assert.False(readiness.MountingAvailable);
    }

    [Fact]
    public void MountBlockedReason_ReturnsRcloneFaultMessage_WhenRcloneIsUnhealthy()
    {
        var readiness = Loaded() with { RcloneHealthy = false, RcloneFaultMessage = "rclone executable not found" };

        Assert.Equal("rclone executable not found", readiness.MountBlockedReason("nas"));
        Assert.False(readiness.MountingAvailable);
    }

    [Fact]
    public void MountBlockedReason_FallsBackToAGenericMessage_WhenRcloneIsUnhealthyWithNoDetail()
    {
        var readiness = Loaded() with { RcloneHealthy = false, RcloneFaultMessage = null };

        Assert.Equal("rclone is not running", readiness.MountBlockedReason("nas"));
    }

    [Fact]
    public void MountBlockedReason_ReturnsThatHostsOwnProvisioningFailure_ButNotAnotherHosts()
    {
        var readiness = Loaded() with
        {
            ProvisioningFailuresByHost = new Dictionary<string, string> { ["broken-host"] = "config/create failed: boom" },
        };

        Assert.Equal("config/create failed: boom", readiness.MountBlockedReason("broken-host"));
        Assert.Null(readiness.MountBlockedReason("healthy-host"));
    }

    [Fact]
    public void MountBlockedReason_ReturnsNull_WhenEverythingIsHealthyAndNothingFailedForThisHost()
    {
        var readiness = Loaded();

        Assert.Null(readiness.MountBlockedReason("nas"));
        Assert.True(readiness.MountingAvailable);
    }

    [Fact]
    public void MountingAvailable_IsTrue_WhenAwaitingFirstRunWithWinFspAndRcloneHealthy()
    {
        // The empty first-run template is a valid config -- MountingAvailable does not gate on
        // ConfigState == Loaded specifically, only on it being either Loaded or AwaitingFirstRun.
        var readiness = Loaded() with { ConfigState = ConfigReadinessState.AwaitingFirstRun };

        Assert.True(readiness.MountingAvailable);
    }

    [Fact]
    public void Initial_ReportsNothingReadyYet()
    {
        var readiness = StartupReadiness.Initial;

        Assert.Equal(ConfigReadinessState.Invalid, readiness.ConfigState);
        Assert.False(readiness.WinFspInstalled);
        Assert.False(readiness.RcloneHealthy);
        Assert.False(readiness.SupervisorRunning);
        Assert.False(readiness.MountingAvailable);
    }

    private static StartupReadiness Loaded() => new()
    {
        ConfigState = ConfigReadinessState.Loaded,
        WinFspInstalled = true,
        WinFspMessage = "WinFsp found.",
        RcloneHealthy = true,
        SupervisorRunning = true,
    };
}
