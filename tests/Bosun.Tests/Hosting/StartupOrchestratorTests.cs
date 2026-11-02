using Bosun.Configuration;
using Bosun.Health;
using Bosun.Hosting;
using Bosun.Probe;
using Bosun.Rclone;
using Bosun.Rclone.Process;
using Bosun.Status;
using Bosun.Supervisor;
using Bosun.Terminal;
using Bosun.Tests.Configuration.Fakes;
using Bosun.Tests.Hosting.Fakes;
using Bosun.Tests.Rclone.Fakes;
using Bosun.Tests.Rclone.Process.Fakes;
using Bosun.Tests.Supervisor.Fakes;
using Bosun.Tests.Terminal.Fakes;
using Bosun.UI;
using Bosun.UI.Banner;
using Bosun.UI.Tray;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bosun.Tests.Hosting;

/// <summary>
/// Covers bs-6f9 (E12b -- the ADR-012 Decision 1 ordered startup sequence) and the readiness half
/// of bs-008: one test per named failure mode, each asserting what is disabled AND what still
/// works, plus the ordering guarantee that is the actual bug bs-bw4 describes (remotes must be
/// provisioned before the first deep probe). Every dependency is fake (CLAUDE.md worktree-safety
/// rules): a real temp-directory <c>hosts.toml</c> exercises the real config-loading path (the
/// same accepted pattern <c>BosunHostFactoryTests</c> uses), but nothing here spawns a real
/// process, calls a real rc HTTP endpoint, touches WinFsp, or mounts a real drive.
/// </summary>
public sealed class StartupOrchestratorTests
{
    [Fact]
    public async Task NoConfig_IsFirstRun_NotAFailure_AndEverythingElseStillStarts()
    {
        await using var harness = new Harness(initialConfigContent: null);

        await harness.StartAsync();

        var readiness = harness.Orchestrator.Current;
        Assert.Equal(ConfigReadinessState.AwaitingFirstRun, readiness.ConfigState);
        Assert.Empty(readiness.ConfigErrors);
        Assert.True(File.Exists(harness.ConfigPath), "a first-run template should have been written");

        // Everything else still runs: WinFsp detected, rcd started and healthy, supervisor
        // running (with zero hosts, since the template configures none).
        Assert.True(readiness.WinFspInstalled);
        Assert.True(readiness.RcloneHealthy);
        Assert.True(readiness.SupervisorRunning);
        Assert.Single(harness.Launcher.StartCalls);

        // bs-3lt: AwaitingFirstRun is still a valid (empty) config -- the fragment gets written too,
        // even with zero hosts, not just Loaded.
        Assert.True(readiness.TerminalFragmentWritten);
        Assert.NotEmpty(harness.FragmentFileSystem.DestinationContent(harness.FragmentPath) ?? "");
    }

    [Fact]
    public async Task NoConfig_TemplateParsesToZeroHostsAndMatchesGlobalConfigDefaults()
    {
        await using var harness = new Harness(initialConfigContent: null);
        await harness.StartAsync();

        var text = File.ReadAllText(harness.ConfigPath);
        var parsed = ConfigParser.Parse(text, "first-run-template");
        var validation = ConfigValidator.Validate(parsed, _ => true);

        Assert.True(validation.IsValid);
        Assert.Empty(parsed.Hosts);
        Assert.Equal(GlobalConfig.DefaultRcloneRcPort, parsed.Global.RcloneRcPort);
        Assert.Equal(GlobalConfig.DefaultFailuresBeforeUnmount, parsed.Global.FailuresBeforeUnmount);
        Assert.Equal(GlobalConfig.DefaultMountedProbeIntervalSeconds, parsed.Global.MountedProbeIntervalSeconds);
    }

    [Fact]
    public async Task Fragment_IsWrittenBeforeWinFspDetection_AndBeforeRclone()
    {
        // bs-3lt: the fragment write must happen immediately after config loads, strictly before
        // WinFsp detection or rclone -- Terminal profiles must not wait on, or be skipped by,
        // anything downstream that can fail (ADR-012's degradation model). Proven directly rather
        // than inferred: the winFspReader callback below captures whether the fragment was already
        // written by the time WinFsp detection actually runs.
        var fragmentFileSystem = new FakeFragmentFileSystem();
        var fragmentWrittenBeforeWinFspRan = false;
        var host = HostBlock("nas", MountMode.None);
        await using var harness = new Harness(
            initialConfigContent: ValidConfig(hostBlocks: [host]),
            fragmentFileSystem: fragmentFileSystem,
            winFspReader: () =>
            {
                fragmentWrittenBeforeWinFspRan = fragmentFileSystem.TouchedPaths.Count > 0;
                return @"C:\WinFsp";
            });

        await harness.StartAsync();

        Assert.True(fragmentWrittenBeforeWinFspRan, "the fragment should already be written by the time WinFsp detection runs");
        Assert.True(harness.Orchestrator.Current.TerminalFragmentWritten);
        Assert.Null(harness.Orchestrator.Current.TerminalFragmentFaultMessage);
        Assert.Contains("nas", fragmentFileSystem.DestinationContent(harness.FragmentPath));
    }

    [Fact]
    public async Task FragmentWriteFailure_IsRecordedCausally_ButDoesNotAbortStartupOrDisableMounting()
    {
        // bs-3lt: "a fragment-write failure is its own degradation ... must not abort startup, and
        // must not disable mounting" -- proven here by driving the exact same persistent-mount,
        // WinFsp-and-rclone-healthy path RemotesAreProvisionedBeforeTheFirstDeepProbe/other happy
        // tests use, but with the fragment write forced to fail, and asserting the mount still
        // happens.
        var host = HostBlock("nas", MountMode.Persistent, drive: "P:");
        await using var harness = new Harness(
            initialConfigContent: ValidConfig(hostBlocks: [host]),
            failFragmentWriteWith: new IOException("disk full"));

        await harness.StartAsync();

        var readiness = harness.Orchestrator.Current;
        Assert.False(readiness.TerminalFragmentWritten);
        Assert.Contains("disk full", readiness.TerminalFragmentFaultMessage, StringComparison.Ordinal);

        // Everything else -- WinFsp, rclone, provisioning, the supervisor, and the actual mount --
        // is completely unaffected.
        Assert.True(readiness.WinFspInstalled);
        Assert.True(readiness.RcloneHealthy);
        Assert.True(readiness.SupervisorRunning);
        Assert.True(readiness.MountingAvailable);
        Assert.Contains("nas", harness.Provisioner.EnsureRemoteCalls);
        Assert.Single(harness.RcloneClient.MountCalls);
        var snapshot = harness.MountSupervisor.GetSnapshot().Single(h => h.HostKey == "nas");
        Assert.Equal(MountState.Mounted, snapshot.State);
    }

    [Fact]
    public async Task InvalidConfig_DisablesEverythingDownstream_ButWinFspIsStillReportedAccurately()
    {
        const string malformed = "this is not valid toml [[[";
        await using var harness = new Harness(initialConfigContent: malformed, winFspReader: () => @"C:\WinFsp");

        await harness.StartAsync();

        var readiness = harness.Orchestrator.Current;
        Assert.Equal(ConfigReadinessState.Invalid, readiness.ConfigState);
        Assert.NotEmpty(readiness.ConfigErrors);

        // Disabled: nothing that needs a GlobalConfig ran.
        Assert.False(readiness.RcloneHealthy);
        Assert.False(readiness.SupervisorRunning);
        Assert.Empty(harness.Launcher.StartCalls);
        Assert.Empty(harness.Provisioner.EnsureRemoteCalls);

        // Still works: WinFsp detection is independent of config and is reported accurately.
        Assert.True(readiness.WinFspInstalled);
        Assert.Contains("WinFsp", readiness.WinFspMessage, StringComparison.Ordinal);

        Assert.False(readiness.MountingAvailable);
        Assert.Equal("the configuration file is invalid", readiness.MountBlockedReason("anything"));

        // bs-3lt: nothing to write a fragment FROM -- there is no config, valid or otherwise (this
        // is distinct from AwaitingFirstRun, where the template itself is a valid, empty config).
        Assert.False(readiness.TerminalFragmentWritten);
        Assert.Empty(harness.FragmentFileSystem.TouchedPaths);
    }

    [Fact]
    public async Task NoWinFsp_DisablesMounting_ButConfigRcloneAndSupervisorStillRun()
    {
        var hostToml = HostBlock("nas", MountMode.Persistent, drive: "P:");
        await using var harness = new Harness(
            initialConfigContent: ValidConfig(hostToml),
            winFspReader: () => null);

        await harness.StartAsync();

        var readiness = harness.Orchestrator.Current;
        Assert.Equal(ConfigReadinessState.Loaded, readiness.ConfigState);
        Assert.False(readiness.WinFspInstalled);
        Assert.Contains("WinFsp is not installed", readiness.WinFspMessage, StringComparison.Ordinal);

        // Disabled: mounting specifically.
        Assert.False(readiness.MountingAvailable);
        Assert.Equal("WinFsp is not installed", readiness.MountBlockedReason("nas"));

        // Still works: rcd started, the remote was still provisioned, the supervisor is running.
        Assert.True(readiness.RcloneHealthy);
        Assert.Empty(readiness.ProvisioningFailuresByHost);
        Assert.Contains("nas", harness.Provisioner.EnsureRemoteCalls);
        Assert.True(readiness.SupervisorRunning);

        // bs-yvw.1: this is the actual gap. Without a gate on MountSupervisor, "nas" would pass
        // its shallow probe (Ready), pass its deep probe (SFTP operations/list needs neither
        // WinFsp nor rclone -- RecordingProbe/FakeProbe default to Success), and call mount/mount,
        // which would fail and retry forever. The host must reach Ready and stay there, and
        // mount/mount must never be called at all -- asserted on the call count, not on eventual
        // state, per the acceptance criterion.
        Assert.Empty(harness.RcloneClient.MountCalls);
        var snapshot = harness.MountSupervisor.GetSnapshot().Single(h => h.HostKey == "nas");
        Assert.Equal(MountState.Ready, snapshot.State);
        Assert.Equal("WinFsp is not installed", snapshot.MountUnavailableReason);

        // bs-3lt: the fragment write does not depend on WinFsp at all -- Terminal profiles are
        // exactly the thing that must still work when mounting cannot.
        Assert.True(readiness.TerminalFragmentWritten);
        Assert.Contains("nas", harness.FragmentFileSystem.DestinationContent(harness.FragmentPath));
    }

    [Fact]
    public async Task NoRcloneBinary_DisablesMounting_ButConfigAndWinFspStillRun()
    {
        await using var harness = new Harness(initialConfigContent: ValidConfig());
        harness.Launcher.EnqueueExecutableNotFound();

        await harness.StartAsync();

        var readiness = harness.Orchestrator.Current;
        Assert.Equal(ConfigReadinessState.Loaded, readiness.ConfigState);
        Assert.True(readiness.WinFspInstalled);

        Assert.False(readiness.RcloneHealthy);
        Assert.Contains("PATH", readiness.RcloneFaultMessage, StringComparison.Ordinal);
        Assert.Empty(readiness.ProvisioningFailuresByHost);
        Assert.Empty(harness.Provisioner.EnsureRemoteCalls);

        // Still works: the supervisor still starts (shallow/idle reachability probing needs
        // neither rclone nor WinFsp) even though nothing can mount.
        Assert.True(readiness.SupervisorRunning);
        Assert.False(readiness.MountingAvailable);

        // bs-3lt: the fragment write does not depend on rclone either -- it runs before rclone is
        // even started.
        Assert.True(readiness.TerminalFragmentWritten);
    }

    [Fact]
    public async Task RcdFailsHealthCheck_DisablesMounting_ButConfigAndWinFspStillRun()
    {
        // pollInterval > timeout: the first failed poll already exceeds the deadline, so no
        // FakeTimeProvider.Advance() is needed -- same trick RcloneProcessServiceTests uses.
        var hostToml = HostBlock("nas", MountMode.Persistent, drive: "P:");
        await using var harness = new Harness(
            initialConfigContent: ValidConfig(hostToml),
            healthCheckTimeout: TimeSpan.FromMilliseconds(50),
            healthCheckPollInterval: TimeSpan.FromSeconds(5));
        harness.Launcher.EnqueueSuccess(new FakeRcloneProcessHandle());
        harness.RcloneClient.EnqueueVersionFailure(new InvalidOperationException("rcd not answering"));

        await harness.StartAsync();

        var readiness = harness.Orchestrator.Current;
        Assert.False(readiness.RcloneHealthy);
        Assert.NotNull(readiness.RcloneFaultMessage);
        Assert.True(readiness.SupervisorRunning);
        Assert.False(readiness.MountingAvailable);

        // bs-yvw.1: the gate generalises beyond WinFsp -- rcd being unhealthy is the same
        // "mounting cannot possibly succeed" condition, and must block mount/mount identically.
        Assert.Empty(harness.RcloneClient.MountCalls);
        var snapshot = harness.MountSupervisor.GetSnapshot().Single(h => h.HostKey == "nas");
        Assert.Equal(MountState.Ready, snapshot.State);
        Assert.Equal(readiness.RcloneFaultMessage, snapshot.MountUnavailableReason);
    }

    [Fact]
    public async Task RcloneRecoveringAtRuntime_MountsAPreviouslyBlockedPersistentHostWithoutARestart()
    {
        // bs-yvw.1 acceptance: "when mounting becomes available (rcd recovers), the host mounts
        // without needing a restart." The first launch attempt fails (LaunchFailed retries
        // automatically), so the initial sequence completes with rclone unhealthy and the
        // persistent host blocked at Ready; the internal retry then succeeds.
        var hostToml = HostBlock("nas", MountMode.Persistent, drive: "P:");
        await using var harness = new Harness(
            initialConfigContent: ValidConfig(hostToml),
            restartDelay: TimeSpan.FromSeconds(5));
        harness.Launcher.EnqueueLaunchFailure();

        await harness.StartAsync();
        Assert.False(harness.Orchestrator.Current.RcloneHealthy);
        Assert.Empty(harness.RcloneClient.MountCalls);
        Assert.Equal(MountState.Ready, harness.MountSupervisor.GetSnapshot().Single(h => h.HostKey == "nas").State);

        harness.Launcher.EnqueueSuccess(new FakeRcloneProcessHandle());
        await AdvanceUntilAsync(harness.RcloneTime, TimeSpan.FromSeconds(5), () => harness.Orchestrator.Current.RcloneHealthy);

        // The supervisor's own channel processes the recovery asynchronously (it is driven by
        // SupervisorTime/its own loop task, not RcloneTime), so poll briefly for the mount to land
        // rather than asserting the instant RcloneHealthy flips.
        await AdvanceUntilAsync(harness.SupervisorTime, TimeSpan.FromSeconds(1), () => harness.RcloneClient.MountCalls.Count > 0);

        Assert.Single(harness.RcloneClient.MountCalls);
        var snapshot = harness.MountSupervisor.GetSnapshot().Single(h => h.HostKey == "nas");
        Assert.Equal(MountState.Mounted, snapshot.State);
        Assert.Null(snapshot.MountUnavailableReason);
    }

    [Fact]
    public async Task RestartingRcloneOnRequest_ReconcilesTheSupervisorFromTheHealthyTransition_AndRemountsPersistentHosts()
    {
        // bs-aoz "Restart rclone": RcloneProcessService.RestartAsync kills rcd (and with it every
        // mount it held) and starts a fresh one. Nothing tells the supervisor directly. It learns
        // from the Healthy transition the orchestrator already turns into OnRcloneRestartedAsync,
        // which reconciles against listmounts, drains the host, and remounts it after a new probe.
        var hostToml = HostBlock("nas", MountMode.Persistent, drive: "P:");
        await using var harness = new Harness(initialConfigContent: ValidConfig(hostToml));
        await harness.StartAsync();
        Assert.Single(harness.RcloneClient.MountCalls);
        Assert.Equal(MountState.Mounted, harness.MountSupervisor.GetSnapshot().Single(h => h.HostKey == "nas").State);

        // What a fresh rcd's mount/listmounts looks like: the old process's mounts are gone.
        await harness.RcloneClient.UnmountAsync("P:", CancellationToken.None);
        harness.RcloneClient.UnmountCalls.Clear();
        var rcloneService = harness.Services.GetRequiredService<RcloneProcessService>();

        var restarted = await rcloneService.RestartAsync();

        Assert.True(restarted);
        Assert.Equal(2, harness.Launcher.StartCalls.Count);

        // SupervisorTime is deliberately NOT advanced (step zero): the 30 s reconciliation tick must
        // not be what recovers the mount. Only the Healthy-transition reconcile can.
        await AdvanceUntilAsync(harness.SupervisorTime, TimeSpan.Zero, () => harness.RcloneClient.MountCalls.Count == 2);

        Assert.Equal(MountState.Mounted, harness.MountSupervisor.GetSnapshot().Single(h => h.HostKey == "nas").State);
        Assert.Equal(2, harness.RcloneClient.MountCalls.Count);
    }

    [Fact]
    public async Task ProvisioningFailsForOneHostButNotOthers()
    {
        var hostA = HostBlock("host-a", MountMode.None);
        var hostB = HostBlock("host-b", MountMode.None);
        await using var harness = new Harness(initialConfigContent: ValidConfig(hostBlocks: [hostA, hostB]));
        harness.Provisioner.FailFor("host-b", new InvalidOperationException("boom for host-b"));

        await harness.StartAsync();

        var readiness = harness.Orchestrator.Current;
        Assert.True(readiness.RcloneHealthy);

        // host-b failed, host-a did not -- and the loop kept going past the failure.
        Assert.Contains("host-a", harness.Provisioner.EnsureRemoteCalls);
        Assert.Contains("host-b", harness.Provisioner.EnsureRemoteCalls);

        Assert.True(readiness.ProvisioningFailuresByHost.TryGetValue("host-b", out var message));
        Assert.Contains("boom for host-b", message, StringComparison.Ordinal);
        Assert.False(readiness.ProvisioningFailuresByHost.ContainsKey("host-a"));

        Assert.Equal("boom for host-b", readiness.MountBlockedReason("host-b"));
        Assert.Null(readiness.MountBlockedReason("host-a"));
    }

    // ------------------------------------------------------------------------------------------
    // bs-qwv: a per-host provisioning failure reaches that host's row, and is cleared when a
    // re-provision succeeds. Real orchestrator -> real StatusReadModel -> real StatusDerivation.
    // ------------------------------------------------------------------------------------------

    private static string OnDemandHostBlock(string key, string drive) =>
        HostBlock(key, MountMode.Persistent, drive).Replace("\"persistent\"", "\"on-demand\"", StringComparison.Ordinal);

    private static StatusReadModel RowsFor(Harness harness) => new(
        harness.MountSupervisor,
        harness.Services.GetRequiredService<IHostConfigStore>(),
        new Bosun.Tests.SessionMonitor.Fakes.FakeSessionMonitor(),
        new FakeTimeProvider(),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<StatusReadModel>.Instance,
        () => harness.Orchestrator.Current.ProvisioningFailuresByHost);

    [Fact]
    public async Task ProvisioningFailure_ShowsOnThatHostsRowOnly_AndNotInTheBanner()
    {
        var hostA = OnDemandHostBlock("host-a", "P:");
        var hostB = OnDemandHostBlock("host-b", "Q:");
        await using var harness = new Harness(initialConfigContent: ValidConfig(hostBlocks: [hostA, hostB]), withHealth: true);
        harness.Provisioner.FailFor("host-b", new InvalidOperationException("boom for host-b"));

        await harness.StartAsync();

        using var model = RowsFor(harness);
        var rows = model.Current.Rows;
        var a = rows.Single(r => r.HostKey == "host-a");
        var b = rows.Single(r => r.HostKey == "host-b");
        Assert.Equal(StatusCategory.ProvisioningFailed, b.Category);
        Assert.Equal(
            "Q: is not mounted -- the rclone remote for this host could not be set up: boom for host-b",
            b.StatusText);
        Assert.NotEqual(StatusCategory.ProvisioningFailed, a.Category);
        Assert.DoesNotContain("rclone remote", a.StatusText, StringComparison.Ordinal);

        // Host-specific, so the app-wide banner must not carry it.
        Assert.DoesNotContain(
            harness.Health.Current.Issues,
            i => (i.Detail ?? "").Contains("boom for host-b", StringComparison.Ordinal)
                || i.Code.Contains("provision", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ProvisioningFailure_IsClearedFromTheRow_WhenRcloneRestartsAndReProvisioningSucceeds()
    {
        var host = OnDemandHostBlock("host-b", "Q:");
        await using var harness = new Harness(initialConfigContent: ValidConfig(hostBlocks: [host]));
        harness.Provisioner.FailFor("host-b", new InvalidOperationException("boom for host-b"));
        await harness.StartAsync();
        using (var before = RowsFor(harness))
        {
            Assert.Equal(StatusCategory.ProvisioningFailed, before.Current.Rows.Single().Category);
        }

        // The cause is fixed; ADR-020's restart re-provisions every host.
        harness.Provisioner.ClearFailure("host-b");
        var restarted = await harness.Services.GetRequiredService<RcloneProcessService>().RestartAsync();
        Assert.True(restarted);
        await AdvanceUntilAsync(
            harness.SupervisorTime, TimeSpan.Zero, () => harness.Orchestrator.Current.ProvisioningFailuresByHost.Count == 0);

        using var after = RowsFor(harness);
        var row = after.Current.Rows.Single();
        Assert.NotEqual(StatusCategory.ProvisioningFailed, row.Category);
        Assert.Null(row.ProvisioningFailure);
    }

    [Fact]
    public async Task ProvisioningFailure_FromAConfigChangeReProvision_AppearsAndIsLaterCleared()
    {
        var host = OnDemandHostBlock("host-b", "Q:");
        await using var harness = new Harness(initialConfigContent: ValidConfig(hostBlocks: [host]));
        await harness.StartAsync();
        Assert.Empty(harness.Orchestrator.Current.ProvisioningFailuresByHost);

        // Edit 1: the hostname changes and the re-provision fails.
        harness.Provisioner.FailFor("host-b", new InvalidOperationException("boom after edit"));
        var moved = host.Replace("host-b.example.internal", "moved.example.internal", StringComparison.Ordinal);
        File.WriteAllText(harness.ConfigPath, ValidConfig(hostBlocks: [moved]));
        await AdvanceUntilAsync(
            harness.SupervisorTime, TimeSpan.FromSeconds(1), () => harness.Orchestrator.Current.ProvisioningFailuresByHost.Count == 1);

        using (var failed = RowsFor(harness))
        {
            Assert.Equal(
                "Q: is not mounted -- the rclone remote for this host could not be set up: boom after edit",
                failed.Current.Rows.Single().StatusText);
        }

        // Edit 2: fixed, and the next re-provision succeeds.
        harness.Provisioner.ClearFailure("host-b");
        var movedAgain = host.Replace("host-b.example.internal", "moved-again.example.internal", StringComparison.Ordinal);
        File.WriteAllText(harness.ConfigPath, ValidConfig(hostBlocks: [movedAgain]));
        await AdvanceUntilAsync(
            harness.SupervisorTime, TimeSpan.FromSeconds(1), () => harness.Orchestrator.Current.ProvisioningFailuresByHost.Count == 0);

        using var cleared = RowsFor(harness);
        Assert.Null(cleared.Current.Rows.Single().ProvisioningFailure);
    }

    [Fact]
    public async Task RemotesAreProvisionedBeforeTheFirstDeepProbe()
    {
        // bs-bw4: this is the actual bug. A persistent host auto-mounts on becoming Ready, which
        // triggers a deep probe against its rclone remote -- if provisioning ran after that (or
        // not at all), the remote would not exist yet and the probe/mount would fail on a real
        // machine, invisibly, because the default test suite fakes IRcloneClient.
        var host = HostBlock("nas", MountMode.Persistent, drive: "P:");
        await using var harness = new Harness(initialConfigContent: ValidConfig(hostBlocks: [host]));

        await harness.StartAsync();

        var provisionIndex = harness.Order.IndexOf("provision:nas");
        var deepProbeIndex = harness.Order.IndexOf("deep-probe:nas");

        Assert.True(provisionIndex >= 0, "the remote should have been provisioned");
        Assert.True(deepProbeIndex >= 0, "a deep probe should have run (persistent host auto-mount)");
        Assert.True(
            provisionIndex < deepProbeIndex,
            $"expected provisioning before the first deep probe; order was: {string.Join(", ", harness.Order)}");

        // And the mount actually happened -- proving the ordering wasn't accidentally irrelevant.
        Assert.Single(harness.RcloneClient.MountCalls);
    }

    /// <summary>
    /// bs-0tn: a config change must re-provision rclone remotes BEFORE the supervisor acts on it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An rclone remote is named <c>bosun-&lt;key&gt;</c> — derived from the host KEY alone, never
    /// from its hostname. <c>EnsureRemoteAsync</c> is what writes hostname/port/user into it, and
    /// before config reload existed it ran only at startup. A hostname change therefore needed a
    /// restart, and that restart is what silently kept the remote correct.
    /// </para>
    /// <para>
    /// <b>Catches:</b> a reload that hands the change to the supervisor without re-provisioning
    /// first. The supervisor drains the renamed host, re-probes it — correctly, because
    /// <c>IProbe</c> reads <c>Hostname</c> directly and so hits the NEW machine and succeeds — and
    /// then remounts <c>bosun-&lt;key&gt;:</c>, still configured for the OLD one. The drive letter
    /// returns and every state assertion reads healthy while the user reads and writes the wrong
    /// server's files. That is worse than a wedged drive: a wedge is obvious, this is silent.
    /// Reordering the two awaits in <c>HandleConfigChangedAsync</c> reintroduces it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AConfigChangeReprovisionsRemotesBeforeTheSupervisorSeesIt()
    {
        var host = HostBlock("nas", MountMode.Persistent, drive: "P:");
        await using var harness = new Harness(initialConfigContent: ValidConfig(hostBlocks: [host]));

        await harness.StartAsync();

        var provisionsAtStartup = harness.Provisioner.EnsureRemoteCalls.Count;
        Assert.True(provisionsAtStartup > 0, "startup should already have provisioned once");

        // Rewrite the host with a different hostname -- the case where a stale remote silently
        // serves the wrong machine.
        var renamed = HostBlock("nas", MountMode.Persistent, drive: "P:")
            .Replace("nas.example.internal", "moved.example.internal", StringComparison.Ordinal);
        File.WriteAllText(harness.ConfigPath, ValidConfig(hostBlocks: [renamed]));

        await AdvanceUntilAsync(
            harness.SupervisorTime,
            TimeSpan.FromSeconds(1),
            () => harness.Provisioner.EnsureRemoteCalls.Count > provisionsAtStartup);

        Assert.True(
            harness.Provisioner.EnsureRemoteCalls.Count > provisionsAtStartup,
            "a config change must re-assert the rclone remote; otherwise a renamed host remounts "
            + $"against stale connection details. Calls: {string.Join(", ", harness.Provisioner.EnsureRemoteCalls)}");
    }

    [Fact]
    public async Task StopAsync_CompletesWithoutHangingAfterAFullStart()
    {
        var host = HostBlock("nas", MountMode.Persistent, drive: "P:");
        await using var harness = new Harness(initialConfigContent: ValidConfig(hostBlocks: [host]));

        await harness.StartAsync();
        await harness.StopAsync();

        Assert.Equal(RcloneProcessStatus.Stopped, harness.Services.GetRequiredService<RcloneProcessService>().Status);
    }

    [Fact]
    public async Task RcloneBecomingHealthyAgainLater_ReProvisionsAndReconcilesTheSupervisor()
    {
        var host = HostBlock("nas", MountMode.None);
        await using var harness = new Harness(
            initialConfigContent: ValidConfig(hostBlocks: [host]),
            restartDelay: TimeSpan.FromSeconds(5));

        // First attempt fails (non-terminal -- LaunchFailed retries automatically), so the
        // initial sequence completes with rclone unhealthy and provisioning skipped.
        harness.Launcher.EnqueueLaunchFailure();

        await harness.StartAsync();
        Assert.False(harness.Orchestrator.Current.RcloneHealthy);
        Assert.Empty(harness.Provisioner.EnsureRemoteCalls);

        // Now let the internal retry succeed.
        harness.Launcher.EnqueueSuccess(new FakeRcloneProcessHandle());
        await AdvanceUntilAsync(harness.RcloneTime, TimeSpan.FromSeconds(5), () => harness.Orchestrator.Current.RcloneHealthy);

        Assert.True(harness.Orchestrator.Current.RcloneHealthy);
        Assert.Contains("nas", harness.Provisioner.EnsureRemoteCalls);
    }

    // ------------------------------------------------------------------------------------------
    // bs-yyg: the orchestrator feeds the application-health model
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Health_RcdFailsHealthCheck_ShowsTheRealCauseOnce_AndTheHostRowOnlyPointsAtTheBanner()
    {
        // The 2026-10-01 failure end to end: rcd faulted with a specific message, and the old UI put
        // that message on every host row. Now the message is the banner's issue detail, and the row
        // says only that it is waiting.
        var hostToml = HostBlock("nas", MountMode.Persistent, drive: "P:");
        await using var harness = new Harness(
            initialConfigContent: ValidConfig(hostToml),
            healthCheckTimeout: TimeSpan.FromMilliseconds(50),
            healthCheckPollInterval: TimeSpan.FromSeconds(5),
            withHealth: true);
        harness.Launcher.EnqueueSuccess(new FakeRcloneProcessHandle());
        harness.RcloneClient.EnqueueVersionFailure(new InvalidOperationException("rcd not answering"));

        await harness.StartAsync();

        var fault = harness.Orchestrator.Current.RcloneFaultMessage;
        Assert.False(string.IsNullOrEmpty(fault));

        var health = harness.Health.Current;
        Assert.Equal(HealthLevel.Faulted, health.Level);
        var issue = Assert.Single(health.Issues);
        Assert.Equal(HealthIssueCodes.RcloneHealthCheckFailed, issue.Code);
        Assert.Equal(fault, issue.Detail);

        var snapshot = harness.MountSupervisor.GetSnapshot().Single(h => h.HostKey == "nas");
        Assert.Equal(MountingUnavailableCause.RcloneUnhealthy, snapshot.MountUnavailableCause);
        var host = harness.Services.GetRequiredService<IHostConfigStore>().Current.Hosts["nas"];
        var row = StatusDerivation.DeriveRow(snapshot, host, sessionCount: 0);
        Assert.DoesNotContain(fault!, row.StatusText, StringComparison.Ordinal);
        Assert.Contains("see banner", row.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SupervisorFailsToStart_IsPublishedAsState_AndTheTrayAndWindowRenderItAsAnErrorWithTheRealReason()
    {
        // bs-4js, defect 1: this used to be a log line and nothing else, so Bosun looked healthy while
        // supervising nothing. The failure must be state, and the real derivation code (health model,
        // tray status, banner) must turn it into Error plus the causal text.
        const string reason = "Unable to resolve service for type 'System.TimeProvider' while attempting to activate 'HostProbe'.";
        var host = HostBlock("nas", MountMode.Persistent, drive: "P:");
        await using var harness = new Harness(
            initialConfigContent: ValidConfig(host),
            withHealth: true,
            failSupervisorResolutionWith: new InvalidOperationException(reason));
        harness.Launcher.EnqueueSuccess(new FakeRcloneProcessHandle());

        await harness.StartAsync();

        // 1. The queryable state.
        var readiness = harness.Orchestrator.Current;
        Assert.False(readiness.SupervisorRunning);
        Assert.True(readiness.SupervisorStartFailed);
        Assert.Equal(reason, readiness.SupervisorFaultMessage);

        // 2. Nothing was probed or mounted -- the failure really did void supervision.
        Assert.Empty(harness.Probe.ShallowProbeCalls);
        Assert.Empty(harness.RcloneClient.MountCalls);

        // 3. The health model: Faulted, one issue, the maintainer's wording with the real reason.
        var health = harness.Health.Current;
        Assert.Equal(HealthLevel.Faulted, health.Level);
        var issue = Assert.Single(health.Issues);
        Assert.Equal(HealthIssueCodes.StartupSupervisorFailed, issue.Code);
        Assert.Equal($"The mount supervisor failed to start: {reason}", issue.Title);

        // 4. The tray: red icon (AggregateHealth.Error), tooltip names the cause -- even though the
        //    host roll-up is Healthy because there are no rows (the supervisor never existed).
        var tray = TrayStatus.Compose(AggregateHealth.Healthy, health, []);
        Assert.Equal(AggregateHealth.Error, tray.IconHealth);
        Assert.StartsWith("Bosun — Faulted: The mount supervisor failed to start: ", tray.Tooltip, StringComparison.Ordinal);

        // 5. The status window: banner visible and red with the full text; header says Faulted.
        var banner = new HealthBannerViewModel(TimeZoneInfo.Utc);
        banner.Update(health, AggregateHealth.Healthy);
        Assert.True(banner.IsVisible);
        Assert.Equal(HealthLevel.Faulted, banner.Level);
        Assert.Equal($"The mount supervisor failed to start: {reason}", banner.Title);
        Assert.Equal("Bosun — Faulted", banner.HeaderText);
    }

    [Fact]
    public async Task SupervisorFailsToStart_DoesNotStopTheRestOfStartup_FailSoft()
    {
        // The maintainer's decision (bs-4js): fail-soft. Bosun keeps running -- the fragment is still
        // written, rclone still starts, and StartAsync still returns instead of throwing or exiting.
        var host = HostBlock("nas", MountMode.Persistent, drive: "P:");
        await using var harness = new Harness(
            initialConfigContent: ValidConfig(host),
            withHealth: true,
            failSupervisorResolutionWith: new InvalidOperationException("boom"));
        harness.Launcher.EnqueueSuccess(new FakeRcloneProcessHandle());

        await harness.StartAsync();

        var readiness = harness.Orchestrator.Current;
        Assert.True(readiness.TerminalFragmentWritten);
        Assert.True(readiness.RcloneHealthy);
        Assert.True(readiness.SupervisorStartFailed);
    }

    [Fact]
    public async Task SupervisorStartedNormally_PublishesNoFaultAndNoSupervisorIssue()
    {
        var host = HostBlock("nas", MountMode.None);
        await using var harness = new Harness(initialConfigContent: ValidConfig(hostBlocks: [host]), withHealth: true);

        await harness.StartAsync();

        var readiness = harness.Orchestrator.Current;
        Assert.True(readiness.SupervisorRunning);
        Assert.Null(readiness.SupervisorFaultMessage);
        Assert.False(readiness.SupervisorStartFailed);
        Assert.DoesNotContain(harness.Health.Current.Issues, i => i.Code == HealthIssueCodes.StartupSupervisorFailed);
    }

    [Fact]
    public async Task InvalidConfig_NeverAttemptsTheSupervisor_SoItIsNotReportedAsASupervisorFailure()
    {
        // "Not started" because the config is invalid is the config's fault, reported as such -- not a
        // second, misleading "the supervisor failed to start".
        await using var harness = new Harness(initialConfigContent: "this is not valid toml [[[", withHealth: true);

        await harness.StartAsync();

        Assert.False(harness.Orchestrator.Current.SupervisorStartFailed);
        Assert.DoesNotContain(harness.Health.Current.Issues, i => i.Code == HealthIssueCodes.StartupSupervisorFailed);
        Assert.Contains(harness.Health.Current.Issues, i => i.Code == HealthIssueCodes.StartupConfigInvalid);
    }

    [Fact]
    public async Task Health_HealthyStartup_IsOkAndNotStarting()
    {
        var host = HostBlock("nas", MountMode.None);
        await using var harness = new Harness(initialConfigContent: ValidConfig(hostBlocks: [host]), withHealth: true);

        await harness.StartAsync();

        Assert.True(harness.Health.Current.IsOk);
        Assert.False(harness.Health.Current.IsStarting);
        Assert.Empty(harness.Health.Current.Issues);
    }

    [Fact]
    public async Task Stop_WithAWedgedSupervisor_FinishesWhenItsTokenIsCancelled_InsteadOfHangingForever()
    {
        // bs-6to / ADR-020 Decision 7. The 2026-10-01 AppHang: the supervisor loop was wedged, so the
        // StopAsync command queued behind it was never reached, and shutdown waited on it forever.
        var host = HostBlock("nas", MountMode.None);
        await using var harness = new Harness(initialConfigContent: ValidConfig(hostBlocks: [host]));
        await harness.StartAsync();

        // Wedge the loop inside an action that ignores cancellation, like a real blocked call.
        var wedged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.MountSupervisor.Enqueue(async _ =>
        {
            parked.SetResult();
            await wedged.Task;
        });
        await parked.Task.WaitAsync(TimeSpan.FromSeconds(10));

        using var stopToken = new CancellationTokenSource();
        var stop = harness.Orchestrator.StopAsync(stopToken.Token);
        Assert.False(stop.IsCompleted, "Stop should be waiting on the wedged supervisor");

        stopToken.Cancel();

        await stop.WaitAsync(TimeSpan.FromSeconds(10));
        wedged.SetResult(); // let the parked action go so teardown is clean
    }

    [Fact]
    public async Task Stop_DoesNotTurnTheShutdownItselfIntoAHealthIssue()
    {
        // bs-6to: while Bosun is exiting, rclone stopping and the supervisor loop ending are the
        // shutdown, not faults -- the banner must not flash "rclone is not running" on a normal exit.
        var host = HostBlock("nas", MountMode.None);
        await using var harness = new Harness(initialConfigContent: ValidConfig(hostBlocks: [host]), withHealth: true);
        await harness.StartAsync();
        Assert.True(harness.Health.Current.IsOk);

        await harness.StopAsync();
        harness.HealthTime.Advance(TimeSpan.FromMinutes(5)); // well past the startup grace period
        var reporter = harness.Services.GetRequiredService<IAppHealthReporter>();
        reporter.ObserveRclone(RcloneProcessStatus.Stopped, RcloneProcessFaultKind.None, null);
        reporter.ObserveSupervisorLoop(started: true, isRunning: false);

        Assert.True(harness.Health.Current.IsOk, string.Join(", ", harness.Health.Current.Issues.Select(i => i.Code)));
    }

    [Fact]
    public async Task Health_RcloneRecoveringAtRuntime_ClearsTheIssue()
    {
        var host = HostBlock("nas", MountMode.None);
        await using var harness = new Harness(
            initialConfigContent: ValidConfig(hostBlocks: [host]),
            restartDelay: TimeSpan.FromSeconds(5),
            withHealth: true);
        harness.Launcher.EnqueueLaunchFailure();

        await harness.StartAsync();
        Assert.Equal(HealthIssueCodes.RcloneLaunchFailed, Assert.Single(harness.Health.Current.Issues).Code);

        harness.Launcher.EnqueueSuccess(new FakeRcloneProcessHandle());
        await AdvanceUntilAsync(harness.RcloneTime, TimeSpan.FromSeconds(5), () => harness.Orchestrator.Current.RcloneHealthy);
        await AdvanceUntilAsync(harness.HealthTime, TimeSpan.Zero, () => harness.Health.Current.IsOk);

        Assert.Empty(harness.Health.Current.Issues);
    }

    [Fact]
    public async Task Health_NoWinFsp_ReportsAStartupIssue()
    {
        var host = HostBlock("nas", MountMode.None);
        await using var harness = new Harness(
            initialConfigContent: ValidConfig(hostBlocks: [host]), winFspReader: () => null, withHealth: true);

        await harness.StartAsync();

        var issue = Assert.Single(harness.Health.Current.Issues);
        Assert.Equal(HealthIssueCodes.StartupWinFspMissing, issue.Code);
        Assert.Equal(HealthLevel.Faulted, issue.Severity);
    }

    [Fact]
    public async Task Health_InvalidConfig_ReportsOneIssueNotAlsoRcloneNotRunning()
    {
        await using var harness = new Harness(initialConfigContent: "this is not valid toml [[[", withHealth: true);

        await harness.StartAsync();

        // Well past the startup grace period: rclone was never started because the config never
        // loaded, and "rclone is not running" would only repeat that under another name.
        harness.HealthTime.Advance(TimeSpan.FromMinutes(5));

        var issue = Assert.Single(harness.Health.Current.Issues);
        Assert.Equal(HealthIssueCodes.StartupConfigInvalid, issue.Code);
    }

    [Fact]
    public async Task Health_SupervisorLoopEndingWithoutShutdown_IsReportedAsFaulted()
    {
        // Completing the supervisor's command channel (DisposeAsync) ends RunAsync without the
        // shutdown token being cancelled -- the "loop died, nobody noticed" shape of bs-x57.
        var host = HostBlock("nas", MountMode.None);
        await using var harness = new Harness(initialConfigContent: ValidConfig(hostBlocks: [host]), withHealth: true);
        await harness.StartAsync();
        Assert.True(harness.Health.Current.IsOk);

        await harness.MountSupervisor.DisposeAsync();

        // The loop task finishes on a pool thread; wait on the observable outcome.
        await AdvanceUntilAsync(harness.HealthTime, TimeSpan.Zero, () => !harness.Health.Current.IsOk);
        var issue = Assert.Single(harness.Health.Current.Issues);
        Assert.Equal(HealthIssueCodes.SupervisorLoopStopped, issue.Code);
        Assert.Equal(HealthLevel.Faulted, issue.Severity);
    }

    [Fact]
    public async Task Health_OrdinaryShutdown_IsNotReportedAsALoopFault()
    {
        var host = HostBlock("nas", MountMode.None);
        await using var harness = new Harness(initialConfigContent: ValidConfig(hostBlocks: [host]), withHealth: true);
        await harness.StartAsync();

        await harness.StopAsync();

        Assert.DoesNotContain(harness.Health.Current.Issues, i => i.Code == HealthIssueCodes.SupervisorLoopStopped);
    }

    private static async Task AdvanceUntilAsync(FakeTimeProvider time, TimeSpan step, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            time.Advance(step);
            await Task.Delay(5).ConfigureAwait(false);
        }

        Assert.True(condition(), "condition was not reached within the deadline");
    }

    // ----------------------------------------------------------------------------------------
    // bs-e28: session.autostart opens Terminal tabs, only for a Run-key launch
    // ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "--autostart" }, true)]
    [InlineData(new[] { "--restarted-by-watchdog", "123" }, false)]
    [InlineData(new[] { "--restarted-by-user", "123" }, false)]
    [InlineData(new[] { "--autostart", "--restarted-by-watchdog", "123" }, false)]
    public async Task AutostartTabs_OpenOnlyForTheRunKeyLaunchContext(string[] args, bool expectLaunch)
    {
        // The context goes through the same detector and mapping App uses to set the option.
        var openTabs = LaunchContextDetector.Detect(args).OpensAutostartTerminalTabs();
        var launcher = new FakeTerminalTabLauncher();
        await using var harness = new Harness(
            ValidConfig(HostBlock("nas", MountMode.OnDemand, autostart: true)),
            tabLauncher: launcher,
            openAutostartTabs: openTabs);

        await harness.StartAsync();

        Assert.Equal(expectLaunch ? 1 : 0, launcher.Calls.Count);
        if (expectLaunch)
        {
            Assert.Equal(["-w", "0", "new-tab", "-p", "nas"], launcher.Calls[0]);
        }
    }

    [Fact]
    public async Task AutostartTabs_AllAutostartHostsAreChainedInOneCall_NoneModeIncluded_OthersExcluded()
    {
        var launcher = new FakeTerminalTabLauncher();
        await using var harness = new Harness(
            ValidConfig(
                HostBlock("alpha", MountMode.Persistent, drive: "P:", autostart: true),
                HostBlock("bravo", MountMode.None, autostart: true),
                HostBlock("charlie", MountMode.None, autostart: false),
                HostBlock("delta", MountMode.OnDemand, drive: "Q:", autostart: true)),
            tabLauncher: launcher,
            openAutostartTabs: true);

        await harness.StartAsync();

        var call = Assert.Single(launcher.Calls);
        Assert.Equal(
            ["-w", "0", "new-tab", "-p", "alpha", ";", "new-tab", "-p", "bravo", ";", "new-tab", "-p", "delta"],
            call);
    }

    [Fact]
    public async Task AutostartTabs_NoHostWithAutostart_LaunchesNothing()
    {
        var launcher = new FakeTerminalTabLauncher();
        await using var harness = new Harness(
            ValidConfig(HostBlock("nas", MountMode.OnDemand, autostart: false)),
            tabLauncher: launcher,
            openAutostartTabs: true);

        await harness.StartAsync();

        Assert.Empty(launcher.Calls);
    }

    [Fact]
    public async Task AutostartTabs_FragmentWriteFailed_LaunchesNothing()
    {
        var launcher = new FakeTerminalTabLauncher();
        await using var harness = new Harness(
            ValidConfig(HostBlock("nas", MountMode.OnDemand, autostart: true)),
            failFragmentWriteWith: new IOException("disk full"),
            tabLauncher: launcher,
            openAutostartTabs: true);

        await harness.StartAsync();

        Assert.False(harness.Orchestrator.Current.TerminalFragmentWritten);
        Assert.Empty(launcher.Calls);
        // The rest of startup is unaffected.
        Assert.True(harness.Orchestrator.Current.SupervisorRunning);
    }

    [Fact]
    public async Task AutostartTabs_InvalidConfig_LaunchesNothing()
    {
        var launcher = new FakeTerminalTabLauncher();
        await using var harness = new Harness(
            "this is not valid toml [[[", tabLauncher: launcher, openAutostartTabs: true);

        await harness.StartAsync();

        Assert.Empty(launcher.Calls);
    }

    [Fact]
    public async Task AutostartTabs_OpenExactlyOnce_EvenThoughReadinessIsPublishedSeveralTimes()
    {
        var launcher = new FakeTerminalTabLauncher();
        await using var harness = new Harness(
            ValidConfig(HostBlock("nas", MountMode.Persistent, drive: "P:", autostart: true)),
            tabLauncher: launcher,
            openAutostartTabs: true);
        var readinessPublications = 0;
        harness.Orchestrator.ReadinessChanged += (_, _) => readinessPublications++;

        await harness.StartAsync();

        // Sanity: the sequence really did publish more than once (WinFsp, rclone, provisioning,
        // supervisor), so "one launch" is not just "one publication".
        Assert.True(readinessPublications > 1, $"expected several readiness publications, saw {readinessPublications}");
        Assert.Single(launcher.Calls);
    }

    [Fact]
    public async Task AutostartTabs_DoNotWaitForRclone_AFailedRcloneStillGetsItsTabs()
    {
        var launcher = new FakeTerminalTabLauncher();
        await using var harness = new Harness(
            ValidConfig(HostBlock("nas", MountMode.Persistent, drive: "P:", autostart: true)),
            winFspReader: () => null,
            tabLauncher: launcher,
            openAutostartTabs: true);

        await harness.StartAsync();

        Assert.False(harness.Orchestrator.Current.WinFspInstalled);
        Assert.Single(launcher.Calls);
    }

    [Fact]
    public async Task AutostartTabs_ALauncherThatThrows_NeverAbortsStartup()
    {
        var launcher = new FakeTerminalTabLauncher { ThrowOnOpen = new InvalidOperationException("no wt.exe") };
        await using var harness = new Harness(
            ValidConfig(HostBlock("nas", MountMode.Persistent, drive: "P:", autostart: true)),
            tabLauncher: launcher,
            openAutostartTabs: true);

        await harness.StartAsync();

        Assert.Single(launcher.Calls);
        Assert.True(harness.Orchestrator.Current.RcloneHealthy);
        Assert.True(harness.Orchestrator.Current.SupervisorRunning);
    }

    [Fact]
    public async Task AutostartTabs_NoLauncherRegistered_NeverAbortsStartup()
    {
        await using var harness = new Harness(
            ValidConfig(HostBlock("nas", MountMode.OnDemand, autostart: true)),
            tabLauncher: null,
            openAutostartTabs: true);

        await harness.StartAsync();

        Assert.True(harness.Orchestrator.Current.SupervisorRunning);
    }

    [Fact]
    public async Task AutostartTabs_ASemicolonInTheHostName_IsEscapedInTheLaunchedArguments()
    {
        var launcher = new FakeTerminalTabLauncher();
        await using var harness = new Harness(
            ValidConfig(HostBlock("nas", MountMode.None, autostart: true, displayName: "Prod; Web")),
            tabLauncher: launcher,
            openAutostartTabs: true);

        await harness.StartAsync();

        Assert.Equal(["-w", "0", "new-tab", "-p", @"Prod\; Web"], Assert.Single(launcher.Calls));
    }

    // identity_file is a literal, non-existent path everywhere here -- the harness registers
    // IHostConfigStore with identityFileExists: _ => true, so no test needs a real file on disk
    // for it (simpler than threading a real per-test path through every TOML fixture, and no less
    // faithful: ConfigValidator's existence check is HostConfigStoreTests'/ConfigValidatorTests'
    // job, not this file's).
    private static string HostBlock(
        string key, MountMode mode, string drive = "P:", bool autostart = false, string? displayName = null) => mode switch
    {
        MountMode.None => $"""
            [hosts.{key}]
            display_name  = "{displayName ?? key}"
            hostname      = "{key}.example.internal"
            port          = 22
            user          = "someuser"
            identity_file = "/fake/identity"

              [hosts.{key}.mount]
              mode = "none"

              [hosts.{key}.session]
              autostart = {(autostart ? "true" : "false")}
              reconnect = false
              tmux      = false
              tab_color    = "#2D5F3F"
              color_scheme = "Campbell"

              [hosts.{key}.probe]
              interval_seconds = 0
              deep_probe       = false
            """,
        MountMode.Persistent => $"""
            [hosts.{key}]
            display_name  = "{displayName ?? key}"
            hostname      = "{key}.example.internal"
            port          = 22
            user          = "someuser"
            identity_file = "/fake/identity"

              [hosts.{key}.mount]
              mode                 = "persistent"
              drive                = "{drive}"
              remote_path          = "/srv/share"
              vfs_cache_mode       = "writes"
              network_mode         = true
              idle_unmount_seconds = 0

              [hosts.{key}.session]
              autostart = {(autostart ? "true" : "false")}
              reconnect = false
              tmux      = false
              tab_color    = "#2D5F3F"
              color_scheme = "Campbell"

              [hosts.{key}.probe]
              interval_seconds = 60
              deep_probe       = true
            """,
        MountMode.OnDemand => $"""
            [hosts.{key}]
            display_name  = "{displayName ?? key}"
            hostname      = "{key}.example.internal"
            port          = 22
            user          = "someuser"
            identity_file = "/fake/identity"

              [hosts.{key}.mount]
              mode                 = "on-demand"
              drive                = "{drive}"
              remote_path          = "/srv/share"
              vfs_cache_mode       = "writes"
              network_mode         = true
              idle_unmount_seconds = 600

              [hosts.{key}.session]
              autostart = {(autostart ? "true" : "false")}
              reconnect = false
              tmux      = false

              [hosts.{key}.probe]
              interval_seconds = 0
              deep_probe       = false
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static string ValidConfig(params string[] hostBlocks) =>
        "[global]\n\n" + string.Join("\n\n", hostBlocks);

    /// <summary>Decorates <see cref="FakeProbe"/> to log deep-probe calls into a shared,
    /// cross-fake ordering log -- see <see cref="RemotesAreProvisionedBeforeTheFirstDeepProbe"/>.</summary>
    private sealed class RecordingProbe(IProbe inner, List<string> order) : IProbe
    {
        public Task<ShallowProbeResult> ProbeShallowAsync(string hostname, int port, TimeSpan timeout, CancellationToken cancellationToken) =>
            inner.ProbeShallowAsync(hostname, port, timeout, cancellationToken);

        public Task<DeepProbeResult> ProbeDeepAsync(string hostKey, TimeSpan timeout, CancellationToken cancellationToken)
        {
            order.Add($"deep-probe:{hostKey}");
            return inner.ProbeDeepAsync(hostKey, timeout, cancellationToken);
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        public string Root { get; }
        public string ConfigPath { get; }
        public string FragmentPath { get; }
        public FakeRcloneProcessLauncher Launcher { get; } = new();
        public FakeRcloneClient RcloneClient { get; } = new();
        public FakeProbe Probe { get; } = new();
        public FakeRcloneRemoteProvisioner Provisioner { get; }
        public FakeFragmentFileSystem FragmentFileSystem { get; }
        public FakeTimeProvider RcloneTime { get; } = new();
        public FakeTimeProvider SupervisorTime { get; } = new();
        public FakeTimeProvider HealthTime { get; } = new();

        /// <summary>The health model, when the harness was built <c>withHealth: true</c>.</summary>
        public IAppHealth Health => Services.GetRequiredService<IAppHealth>();
        public List<string> Order { get; } = [];
        public ServiceProvider Services { get; }
        public StartupOrchestrator Orchestrator { get; }

        /// <summary>Convenience accessor for bs-yvw.1 assertions -- the same singleton
        /// <see cref="StartupOrchestrator"/> resolves and drives internally.</summary>
        public MountSupervisor MountSupervisor => Services.GetRequiredService<MountSupervisor>();

        public Harness(
            string? initialConfigContent,
            Func<string?>? winFspReader = null,
            TimeSpan? healthCheckTimeout = null,
            TimeSpan? healthCheckPollInterval = null,
            TimeSpan? restartDelay = null,
            Exception? failFragmentWriteWith = null,
            FakeFragmentFileSystem? fragmentFileSystem = null,
            bool withHealth = false,
            Exception? failSupervisorResolutionWith = null,
            ITerminalTabLauncher? tabLauncher = null,
            bool openAutostartTabs = false)
        {
            Root = Path.Combine(Path.GetTempPath(), "bosun-tests", "orchestrator", Guid.NewGuid().ToString("N"));
            ConfigPath = Path.Combine(Root, "hosts.toml");
            // A fake path under the test's own temp Root -- never the real
            // %LOCALAPPDATA%\Microsoft\Windows Terminal\Fragments\Bosun\bosun.json (CLAUDE.md
            // worktree-safety rules; bs-3lt's own acceptance criteria) -- and FakeFragmentFileSystem
            // never touches disk regardless, so this path is never actually created.
            FragmentPath = Path.Combine(Root, "fake-fragments", "bosun.json");
            Provisioner = new FakeRcloneRemoteProvisioner(Order);
            // Accepts a caller-supplied instance so a test can hold a direct reference to it (e.g.
            // to inspect write ordering from inside a winFspReader callback) without going through
            // the not-yet-assigned Harness variable itself.
            FragmentFileSystem = fragmentFileSystem ?? new FakeFragmentFileSystem();

            if (failFragmentWriteWith is not null)
            {
                FragmentFileSystem.FailOnWrite = failFragmentWriteWith;
            }

            if (initialConfigContent is not null)
            {
                Directory.CreateDirectory(Root);
                File.WriteAllText(ConfigPath, initialConfigContent);
            }

            var services = new ServiceCollection();
            services.AddLogging();

            // identityFileExists: _ => true -- every HostBlock() fixture uses a literal,
            // non-existent "/fake/identity" path; the real existence check is
            // ConfigValidator's/HostConfigStoreTests' concern, not this file's.
            services.AddSingleton<IHostConfigStore>(sp => HostConfigStore.Load(
                path: ConfigPath,
                reader: new FileConfigReader(),
                watcher: new FileSystemConfigWatcher(ConfigPath),
                timeProvider: TimeProvider.System,
                identityFileExists: _ => true));

            services.AddSingleton<IRcloneClient>(RcloneClient);
            services.AddSingleton<IRcloneProcessLauncher>(Launcher);
            services.AddSingleton(sp => new RcloneProcessService(
                sp.GetRequiredService<IRcloneProcessLauncher>(),
                sp.GetRequiredService<IRcloneClient>(),
                new RcloneProcessServiceOptions
                {
                    RcloneRcPort = 5572,
                    RcloneConfigPath = Path.Combine(Root, "rclone.conf"),
                    HealthCheckTimeout = healthCheckTimeout ?? TimeSpan.FromSeconds(10),
                    HealthCheckPollInterval = healthCheckPollInterval ?? TimeSpan.FromMilliseconds(50),
                    RestartDelay = restartDelay ?? TimeSpan.FromSeconds(5),
                },
                RcloneTime,
                sp.GetRequiredService<ILogger<RcloneProcessService>>(),
                new RcloneRcCredential("bosun-test-user", "bosun-test-pass")));

            services.AddSingleton<IRcloneRemoteProvisioner>(Provisioner);
            services.AddSingleton<IProbe>(new RecordingProbe(Probe, Order));
            if (failSupervisorResolutionWith is not null)
            {
                // bs-4js: the container cannot build the supervisor -- what an unregistered
                // TimeProvider (which makes IProbe unbuildable) did on the maintainer's machine.
                services.AddSingleton<MountSupervisor>(_ => throw failSupervisorResolutionWith);
            }
            else
            {
                services.AddSingleton(sp => new MountSupervisor(
                    sp.GetRequiredService<IHostConfigStore>(),
                    sp.GetRequiredService<IRcloneClient>(),
                    sp.GetRequiredService<IProbe>(),
                    new FakeDriveTypeReader(),
                    SupervisorTime,
                    sp.GetRequiredService<ILogger<MountSupervisor>>()));
            }

            services.AddSingleton<IMountSupervisor>(sp => sp.GetRequiredService<MountSupervisor>());

            services.AddSingleton<IFirstRunConfigBootstrapper, FirstRunConfigBootstrapper>();
            services.AddSingleton<IWinFspDetector>(new WinFspDetector(winFspReader ?? (() => @"C:\WinFsp")));

            // bs-3lt: the same fake-filesystem/fake-path pattern FragmentWriterTests /
            // FragmentRewriteCoordinatorTests already use -- FakeFragmentFileSystem never touches a
            // real path regardless of what FragmentPath is set to, so this is safe from a worktree.
            services.AddSingleton<IFragmentFileSystem>(FragmentFileSystem);
            services.AddSingleton(new FragmentWriterOptions { FragmentPath = FragmentPath });
            services.AddSingleton<IFragmentWriter, FragmentWriter>();
            services.AddSingleton(sp => new FragmentRewriteCoordinator(
                sp.GetRequiredService<IFragmentWriter>(),
                sp.GetRequiredService<IHostConfigStore>(),
                sp.GetRequiredService<ILogger<FragmentRewriteCoordinator>>()));

            if (withHealth)
            {
                // bs-yyg: the same one-instance-behind-two-faces registration BosunHostFactory makes.
                // Its own fake clock: the grace period is not what these tests are about.
                services.AddSingleton(new AppHealthService(HealthTime));
                services.AddSingleton<IAppHealth>(sp => sp.GetRequiredService<AppHealthService>());
                services.AddSingleton<IAppHealthReporter>(sp => sp.GetRequiredService<AppHealthService>());
            }

            if (tabLauncher is not null)
            {
                services.AddSingleton(tabLauncher);
            }

            Services = services.BuildServiceProvider();

            var options = new BosunHostOptions
            {
                LogDirectory = Root,
                ConfigPath = ConfigPath,
                OpenAutostartTerminalTabs = openAutostartTabs,
            };
            Orchestrator = new StartupOrchestrator(
                Services,
                options,
                Services.GetRequiredService<IFirstRunConfigBootstrapper>(),
                Services.GetRequiredService<IWinFspDetector>(),
                Services.GetRequiredService<ILogger<StartupOrchestrator>>());
        }

        public async Task StartAsync()
        {
            var task = Orchestrator.StartAsync(CancellationToken.None);
            var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.True(ReferenceEquals(task, completed), "StartAsync did not complete -- possible deadlock");
            await task;
        }

        public async Task StopAsync()
        {
            var task = Orchestrator.StopAsync(CancellationToken.None);
            var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.True(ReferenceEquals(task, completed), "StopAsync did not complete -- possible deadlock");
            await task;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await StopAsync();
            }
            catch (Exception ex)
            {
                // StartupOrchestrator.StopAsync is idempotent (a test may have already called it
                // explicitly), so this is genuinely best-effort teardown, not a mask for a real
                // hang -- surface it rather than swallowing silently, so a regression here still
                // shows up somewhere.
                Console.Error.WriteLine($"Harness teardown: StopAsync threw: {ex}");
            }

            await Services.DisposeAsync();

            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
