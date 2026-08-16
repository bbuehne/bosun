using Bosun.Configuration;
using Bosun.Rclone;
using Bosun.Rclone.Process;
using Bosun.Supervisor;
using Bosun.Terminal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bosun.Hosting;

/// <summary>
/// The ONE hosted service that owns ADR-012 Decision 1's ordered startup sequence (bs-6f9):
///
/// <code>
/// load config -&gt; write Terminal fragment -&gt; detect WinFsp -&gt; start rclone rcd + health check
///   -&gt; provision remotes -&gt; adopt-or-clear existing mounts -&gt; run the supervisor
/// </code>
///
/// Also resolves bs-127 (register <see cref="RcloneProcessService"/> as a hosted service),
/// bs-bw4 (nothing was calling <see cref="IRcloneRemoteProvisioner.EnsureRemoteAsync"/>), and
/// bs-3lt (nothing was calling <see cref="FragmentRewriteCoordinator"/>): this class is now the
/// one place that calls all three, in the order that makes them correct.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the Terminal fragment write is early and ungated (bs-3lt).</b> It runs immediately after
/// config loads -- before WinFsp detection, before rclone -- and is never skipped just because a
/// LATER step failed. ADR-012's whole degradation model rests on Terminal profiles being the half
/// of Bosun that keeps working when mounting cannot; putting the write after WinFsp/rclone would
/// mean a machine with neither (the maintainer's own machine, today) gets no working profiles
/// either, which defeats the point. A write failure here is its own degradation -- recorded in
/// <see cref="StartupReadiness.TerminalFragmentWritten"/> /
/// <see cref="StartupReadiness.TerminalFragmentFaultMessage"/> with a causal reason (ADR-012
/// Decision 3) -- and must never abort startup or disable mounting; see the try/catch around it
/// below.
/// </para>
/// <para>
/// <b>Why one hosted service and not several independent <c>AddHostedService</c> registrations.</b>
/// The order above is load-bearing (see the class-level doc on the individual steps below), and
/// ADR-012 is explicit that scattering it across independent hosted-service registrations "encodes
/// the sequence somewhere invisible and fragile". <see cref="RcloneProcessService"/> and
/// <see cref="MountSupervisor"/> are therefore resolved here as plain singletons and driven
/// directly -- <c>StartAsync</c>/<c>StopAsync</c> calls issued by this class, in this order --
/// rather than each being independently registered via <c>AddHostedService</c>, which would hand
/// their relative ordering back to DI registration order (exactly the failure mode this ADR exists
/// to avoid).
/// </para>
/// <para>
/// <b>Fail-soft, never fail-fast.</b> Every step below is wrapped so a failure updates
/// <see cref="Current"/> rather than throwing out of <see cref="StartAsync"/> -- an
/// <see cref="IHostedService.StartAsync"/> that throws aborts the ENTIRE host, which is exactly
/// the uniform-fail-fast policy ADR-012 rejects for every condition except "cannot construct the
/// host at all" (that one case is <see cref="BootstrapOrchestrator"/>'s job, a phase earlier than
/// this class even exists).
/// </para>
/// <para>
/// <b>Why <see cref="IHostConfigStore"/> is resolved via <see cref="IServiceProvider"/> rather
/// than constructor-injected.</b> Its registered factory (<c>BosunHostFactory</c>) does real,
/// synchronous file I/O and throws on an invalid or unreadable first load. This class must be the
/// FIRST and ONLY thing that ever attempts that resolution, exactly once, inside a
/// <see langword="try"/>/<see langword="catch"/> -- constructor injection would let the DI
/// container attempt it implicitly (and uncatchably, from this class's point of view) the moment
/// something else asked for an <see cref="IHostConfigStore"/>. The same reasoning applies to
/// <see cref="FragmentRewriteCoordinator"/>, <see cref="RcloneProcessService"/>,
/// <see cref="IRcloneRemoteProvisioner"/>, and <see cref="MountSupervisor"/>: each depends on
/// <see cref="IHostConfigStore"/> transitively, so none of them may be resolved before this class
/// has confirmed config loaded successfully. This is also why <see cref="FragmentRewriteCoordinator"/>
/// is registered in <c>BosunHostFactory</c> but never independently resolved or
/// constructor-injected anywhere else -- its own class remarks record the same constraint from the
/// other side.
/// </para>
/// <para>
/// <b>Why <see cref="MountSupervisor"/> (the concrete type) and not <see cref="IMountSupervisor"/>.</b>
/// <see cref="MountSupervisor.RunAsync"/> -- the loop that actually pumps its internal command
/// channel -- is deliberately not on the interface (every other <see cref="IMountSupervisor"/>
/// method uses <c>EnqueueAndWait</c>, which posts to that same channel and deadlocks forever if
/// nothing is dequeuing it). This class is the first thing in the codebase to run that loop in
/// production (previously, nothing did -- see <c>bs-7ck</c>), which is why it must start the loop
/// BEFORE issuing the very first command (<see cref="IMountSupervisor.StartAsync"/>) rather than
/// after.
/// </para>
/// </remarks>
public sealed class StartupOrchestrator : IHostedService, IAsyncDisposable
{
    private readonly IServiceProvider services;
    private readonly BosunHostOptions options;
    private readonly IFirstRunConfigBootstrapper firstRunBootstrapper;
    private readonly IWinFspDetector winFspDetector;
    private readonly ILogger<StartupOrchestrator> logger;

    private readonly object readinessGate = new();

    private IHostConfigStore? configStore;
    private FragmentRewriteCoordinator? fragmentRewriteCoordinator;
    private RcloneProcessService? rcloneProcessService;
    private IRcloneRemoteProvisioner? remoteProvisioner;
    private MountSupervisor? mountSupervisor;
    private CancellationTokenSource? supervisorLoopCts;
    private Task? supervisorLoopTask;
    private StartupReadiness current = StartupReadiness.Initial;
    private bool stopped;

    /// <summary>Set once, in step 2 of <see cref="RunSequenceAsync"/>, and read afterwards by
    /// every later push of <see cref="MountingAvailability"/> (bs-yvw.1) -- WinFsp presence is
    /// detected once at startup and does not change at runtime, unlike rclone health.</summary>
    private bool winFspInstalled;

    public StartupOrchestrator(
        IServiceProvider services,
        BosunHostOptions options,
        IFirstRunConfigBootstrapper firstRunBootstrapper,
        IWinFspDetector winFspDetector,
        ILogger<StartupOrchestrator> logger)
    {
        this.services = services;
        this.options = options;
        this.firstRunBootstrapper = firstRunBootstrapper;
        this.winFspDetector = winFspDetector;
        this.logger = logger;
    }

    public StartupReadiness Current
    {
        get
        {
            lock (readinessGate)
            {
                return current;
            }
        }
        private set
        {
            lock (readinessGate)
            {
                current = value;
            }
        }
    }

    /// <summary>Raised every time <see cref="Current"/> changes -- E9 (not built yet) is the
    /// intended subscriber, for the tray icon and status window.</summary>
    public event EventHandler<StartupReadinessChangedEventArgs>? ReadinessChanged;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RunSequenceAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // See the class remarks: StartAsync must never throw. Whatever readiness had already
            // been published for earlier, successful steps is left standing.
            logger.LogCritical(ex, "Unexpected failure in the startup sequence; continuing with whatever succeeded so far");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Idempotency guard: .NET's generic host calls IHostedService.StopAsync exactly once per
        // lifecycle, so this should never fire in production -- it exists because a SECOND call
        // (found while testing this class: a test harness calling StopAsync both explicitly and
        // from its own teardown) would otherwise hang forever. The second call's
        // mountSupervisor.StopAsync/rcloneProcessService.StopAsync would post to a channel/await a
        // loop task that the FIRST call already tore down, and nothing would ever be there to
        // dequeue or complete it -- a real, reachable deadlock, not a theoretical one.
        if (stopped)
        {
            logger.LogDebug("StopAsync called again after already stopping; ignoring");
            return;
        }

        stopped = true;

        // Unsubscribes from IHostConfigStore.ConfigChanged -- harmless to skip (the process is
        // exiting either way) but cheap and symmetric with every other subscription torn down here.
        fragmentRewriteCoordinator?.Dispose();

        if (rcloneProcessService is not null)
        {
            rcloneProcessService.StatusChanged -= OnRcloneProcessStatusChanged;
        }

        if (mountSupervisor is not null)
        {
            // Must happen BEFORE cancelling the loop below: IMountSupervisor.StopAsync posts to
            // the same command channel RunAsync pumps, and deadlocks forever if nothing is still
            // dequeuing it.
            try
            {
                await mountSupervisor.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "MountSupervisor.StopAsync failed during shutdown");
            }
        }

        supervisorLoopCts?.Cancel();
        if (supervisorLoopTask is not null)
        {
            await supervisorLoopTask.ConfigureAwait(false);
        }

        if (rcloneProcessService is not null)
        {
            try
            {
                await rcloneProcessService.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "RcloneProcessService.StopAsync failed during shutdown");
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        supervisorLoopCts?.Dispose();
        return ValueTask.CompletedTask;
    }

    // ------------------------------------------------------------------------------------------
    // The ordered sequence (ADR-012 Decision 1)
    // ------------------------------------------------------------------------------------------

    private async Task RunSequenceAsync(CancellationToken ct)
    {
        // Step 1: load config. Guarantee the directory exists (and, on first run, write a valid
        // empty template into it) BEFORE ever resolving IHostConfigStore -- that resolution
        // constructs a FileSystemConfigWatcher, whose constructor throws if its directory is
        // missing (bs-008).
        var configState = ConfigReadinessState.Invalid;
        IReadOnlyList<string> configErrors = [];
        var isFirstRun = false;

        try
        {
            isFirstRun = firstRunBootstrapper.EnsureConfigExists(options.ConfigPath);
            configStore = services.GetRequiredService<IHostConfigStore>();
            configState = isFirstRun ? ConfigReadinessState.AwaitingFirstRun : ConfigReadinessState.Loaded;

            if (isFirstRun)
            {
                logger.LogInformation(
                    "No configuration found at {ConfigPath}; wrote a first-run template. Bosun will not " +
                    "reach or mount anything until hosts are added and Bosun is restarted.", options.ConfigPath);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            configErrors = ex is InvalidConfigException invalidConfig ? invalidConfig.Errors : [ex.Message];
            logger.LogError(
                ex, "hosts.toml at {ConfigPath} is invalid, or could not be prepared; mounting and remote " +
                "provisioning are disabled until this is fixed and Bosun is restarted", options.ConfigPath);
        }

        // Step 1b: write the initial Windows Terminal fragment (bs-3lt). Deliberately immediately
        // after config loads and BEFORE WinFsp/rclone -- see the class remarks on why this must be
        // early and ungated. Skipped only when there is no config to write from at all (ConfigState
        // == Invalid); a failure here is its own degradation, never fatal to the rest of startup.
        var fragmentWritten = false;
        string? fragmentFaultMessage = null;
        if (configState != ConfigReadinessState.Invalid)
        {
            try
            {
                fragmentRewriteCoordinator = services.GetRequiredService<FragmentRewriteCoordinator>();
                await fragmentRewriteCoordinator.WriteInitialAsync(ct).ConfigureAwait(false);
                fragmentWritten = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(
                    ex, "Failed to write the initial Windows Terminal fragment at {ConfigPath}; Terminal " +
                    "profiles may be missing or stale until this is fixed, but mounting is unaffected", options.ConfigPath);
                fragmentFaultMessage = ex.Message;
            }
        }

        // Step 2: detect WinFsp. Independent of config -- always runs, always reported accurately,
        // regardless of what step 1 produced (ADR-012's fail-soft principle: a failed step
        // disables what depends on it and leaves everything else running). Wrapped for the same
        // reason every other step is: a real IWinFspDetector reads the registry and, while it does
        // not document throwing, nothing here should be the one step that turns an unexpected
        // exception into a readiness snapshot that never gets published at all.
        WinFspDetectionResult winFsp;
        try
        {
            winFsp = winFspDetector.Detect();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "WinFsp detection failed unexpectedly; treating it as not installed");
            winFsp = new WinFspDetectionResult { IsInstalled = false, Message = $"WinFsp detection failed: {ex.Message}" };
        }

        winFspInstalled = winFsp.IsInstalled;

        PublishReadiness(new StartupReadiness
        {
            ConfigState = configState,
            ConfigErrors = configErrors,
            TerminalFragmentWritten = fragmentWritten,
            TerminalFragmentFaultMessage = fragmentFaultMessage,
            WinFspInstalled = winFsp.IsInstalled,
            WinFspMessage = winFsp.Message,
            RcloneHealthy = false,
            RcloneFaultMessage = configState == ConfigReadinessState.Invalid
                ? "The configuration is invalid, so rclone's RC port and config path are unknown."
                : "rclone rcd has not been started yet.",
            SupervisorRunning = false,
        });

        if (configState == ConfigReadinessState.Invalid)
        {
            // Nothing past this point can run without a GlobalConfig: rcd's port and config path,
            // remote provisioning, and the mount supervisor's host set all come from it.
            return;
        }

        // Step 3: start rclone rcd + health check.
        var rcloneHealthy = await StartRcloneAsync(ct).ConfigureAwait(false);

        // Step 4: provision remotes. Only meaningful once rcd is actually answering -- every
        // config/create call would otherwise just fail with a connection error, one per host, for
        // no benefit (a host whose remote was never created fails its own deep probe naturally,
        // which is reported via ProvisioningFailuresByHost / MountBlockedReason instead).
        var provisioningFailures = new Dictionary<string, string>(StringComparer.Ordinal);
        if (rcloneHealthy)
        {
            remoteProvisioner = services.GetRequiredService<IRcloneRemoteProvisioner>();
            provisioningFailures = await ProvisionAllRemotesAsync(configStore!.Current, ct).ConfigureAwait(false);
            PublishReadiness(Current with { ProvisioningFailuresByHost = provisioningFailures });
        }

        // Steps 5 + 6: adopt-or-clear (inside IMountSupervisor.StartAsync, per its own contract --
        // "before doing anything else") then run the supervisor. The supervisor always starts,
        // whether or not rclone/WinFsp are healthy: its shallow (TCP) idle probing needs neither,
        // so hosts still show live reachability even when nothing can actually mount yet (deep
        // probes and mount/mount calls simply keep failing gracefully -- MountSupervisor already
        // treats every rc call as fallible).
        var supervisorStarted = await StartMountSupervisorAsync(rcloneHealthy, Current.RcloneFaultMessage, ct).ConfigureAwait(false);
        PublishReadiness(Current with { SupervisorRunning = supervisorStarted });

        // Only now -- after the initial sequence has fully finished provisioning once and started
        // the supervisor once -- start reacting to LATER rcd health transitions (a restart after
        // this point). Subscribing any earlier risks the event firing concurrently with the
        // explicit provisioning call above, which would double-provision and could race the
        // "remotes exist before the first deep probe" guarantee this whole ordering exists for.
        if (rcloneProcessService is not null)
        {
            rcloneProcessService.StatusChanged += OnRcloneProcessStatusChanged;
        }
    }

    private async Task<bool> StartRcloneAsync(CancellationToken ct)
    {
        try
        {
            rcloneProcessService = services.GetRequiredService<RcloneProcessService>();
            await rcloneProcessService.StartAsync(ct).ConfigureAwait(false);

            var healthy = rcloneProcessService.Status == RcloneProcessStatus.Healthy;
            PublishReadiness(Current with
            {
                RcloneHealthy = healthy,
                RcloneFaultMessage = healthy ? null : rcloneProcessService.LastFaultMessage,
            });
            return healthy;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to start rclone rcd; mounting is disabled until this is fixed");
            PublishReadiness(Current with { RcloneHealthy = false, RcloneFaultMessage = ex.Message });
            return false;
        }
    }

    private async Task<bool> StartMountSupervisorAsync(bool rcloneHealthy, string? rcloneFaultMessage, CancellationToken ct)
    {
        try
        {
            mountSupervisor = services.GetRequiredService<MountSupervisor>();

            // The loop MUST already be pumping the command channel before the first
            // EnqueueAndWait-based call (StartAsync included) is issued below, or that call
            // deadlocks forever waiting for work nothing is dequeuing -- see the class remarks.
            supervisorLoopCts = new CancellationTokenSource();
            supervisorLoopTask = RunSupervisorLoopAsync(mountSupervisor, supervisorLoopCts.Token);

            // bs-yvw.1: the mounting-availability gate MUST be in effect before StartAsync below
            // runs, not after. StartAsync enables every host and, for a persistent host whose
            // initial shallow probe already succeeds, attempts its very first mount SYNCHRONOUSLY
            // inside that same call (EnableHostAsync -> OnEnteredReadyAsync -> TryBeginMountAsync).
            // MountSupervisor's own default is "available", so pushing this afterwards would let
            // that first real-world attempt slip through ungated on the exact first run this fix
            // targets (WinFsp missing, persistent host, first launch).
            await mountSupervisor.SetMountingAvailabilityAsync(
                ComputeMountingAvailability(winFspInstalled, rcloneHealthy, rcloneFaultMessage), ct).ConfigureAwait(false);

            await mountSupervisor.StartAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to start the mount supervisor; no host will be probed or mounted this session");
            return false;
        }
    }

    /// <summary>
    /// Composes the process-wide mounting-availability gate (bs-yvw.1) from the two independent
    /// signals that feed it, in the same priority order <see cref="StartupReadiness.MountBlockedReason"/>
    /// already establishes: WinFsp first (it is checked once, at startup, and does not change),
    /// then rclone health (which can flip at any time via <see cref="RcloneProcessService.StatusChanged"/>).
    /// The reason strings are deliberately the exact ones ADR-012 Decision 3's worked example and
    /// <see cref="StartupReadiness.MountBlockedReason"/> already use -- "WinFsp is not installed" --
    /// rather than anything generic like "mounting unavailable".
    /// </summary>
    private static MountingAvailability ComputeMountingAvailability(bool winFspInstalled, bool rcloneHealthy, string? rcloneFaultMessage)
    {
        if (!winFspInstalled)
        {
            return MountingAvailability.Unavailable(MountingUnavailableCause.WinFspMissing, "WinFsp is not installed");
        }

        if (!rcloneHealthy)
        {
            return MountingAvailability.Unavailable(
                MountingUnavailableCause.RcloneUnhealthy, rcloneFaultMessage ?? "rclone is not running");
        }

        return MountingAvailability.Available;
    }

    private Task PushMountingAvailabilityAsync(bool rcloneHealthy, string? rcloneFaultMessage, CancellationToken ct) =>
        mountSupervisor is null
            ? Task.CompletedTask
            : mountSupervisor.SetMountingAvailabilityAsync(
                ComputeMountingAvailability(winFspInstalled, rcloneHealthy, rcloneFaultMessage), ct);

    private async Task RunSupervisorLoopAsync(MountSupervisor supervisor, CancellationToken ct)
    {
        try
        {
            await supervisor.RunAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path (StopAsync cancelled this token).
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "The mount supervisor's processing loop crashed; mounts are no longer managed until Bosun restarts");
        }
    }

    private async Task<Dictionary<string, string>> ProvisionAllRemotesAsync(BosunConfig config, CancellationToken ct)
    {
        var failures = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var host in config.Hosts.Values)
        {
            try
            {
                await remoteProvisioner!.EnsureRemoteAsync(host, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(
                    ex, "Failed to provision the rclone remote for host {HostKey}; that host cannot mount " +
                    "until this is fixed, but every other host is unaffected", host.Key);
                failures[host.Key] = ex.Message;
            }
        }

        return failures;
    }

    // ------------------------------------------------------------------------------------------
    // Recovery: RcloneProcessService.StatusChanged -> Healthy, after the initial sequence
    // ------------------------------------------------------------------------------------------

    private void OnRcloneProcessStatusChanged(object? sender, RcloneProcessStatusChangedEventArgs e)
    {
        if (e.Status != RcloneProcessStatus.Healthy)
        {
            PublishReadiness(Current with { RcloneHealthy = false, RcloneFaultMessage = e.Detail });

            // bs-yvw.1: rclone rcd going unhealthy at runtime (crash, restart in progress) is the
            // same "mounting cannot possibly succeed" condition WinFsp-missing is, generalised --
            // close the gate immediately rather than waiting for the next host to independently
            // discover it via a failing deep probe or mount/mount call.
            _ = PushMountingAvailabilityAsync(rcloneHealthy: false, e.Detail, CancellationToken.None);
            return;
        }

        _ = HandleRcloneBecameHealthyAsync(CancellationToken.None);
    }

    /// <summary>
    /// A restarted <c>rclone rcd</c> knows nothing about mounts this process believes are up
    /// (<c>mount/listmounts</c> on a fresh process returns empty -- docs/ARCHITECTURE.md §6), and
    /// it also knows nothing about any remote Bosun had previously provisioned into its OWN
    /// in-memory config, so remotes are re-provisioned here too, not just reconciled.
    /// </summary>
    private async Task HandleRcloneBecameHealthyAsync(CancellationToken ct)
    {
        try
        {
            remoteProvisioner ??= services.GetRequiredService<IRcloneRemoteProvisioner>();
            var failures = configStore is not null
                ? await ProvisionAllRemotesAsync(configStore.Current, ct).ConfigureAwait(false)
                : new Dictionary<string, string>(StringComparer.Ordinal);

            PublishReadiness(Current with
            {
                RcloneHealthy = true,
                RcloneFaultMessage = null,
                ProvisioningFailuresByHost = failures,
            });

            if (mountSupervisor is not null)
            {
                // bs-yvw.1: reopen the gate before reconciling. If WinFsp is still missing this is
                // a no-op (ComputeMountingAvailability keeps it closed on that ground instead), so
                // rclone recovering alone never mounts anything WinFsp still blocks.
                await PushMountingAvailabilityAsync(rcloneHealthy: true, rcloneFaultMessage: null, ct).ConfigureAwait(false);
                await mountSupervisor.OnRcloneRestartedAsync(ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to reconcile after rclone rcd became healthy again");
        }
    }

    private void PublishReadiness(StartupReadiness readiness)
    {
        Current = readiness;
        ReadinessChanged?.Invoke(this, new StartupReadinessChangedEventArgs(readiness));
    }
}

public sealed class StartupReadinessChangedEventArgs(StartupReadiness readiness) : EventArgs
{
    public StartupReadiness Readiness { get; } = readiness;
}
