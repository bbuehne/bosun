namespace Bosun.Hosting;

/// <summary>
/// Outcome of the "load config" step of the ADR-012 Decision 1 ordered sequence (bs-6f9, bs-008).
/// </summary>
public enum ConfigReadinessState
{
    /// <summary>
    /// No <c>hosts.toml</c> existed at the configured path when Bosun started this time.
    /// <see cref="Hosting.IFirstRunConfigBootstrapper"/> has already created the directory and
    /// written a first-run template that IS a valid, empty (zero-host) config -- this is
    /// deliberately NOT the same value as <see cref="Loaded"/>, purely so a first-run window
    /// (bs-ww9.1, E9) can be shown instead of Bosun starting up silently with nothing configured.
    /// ADR-012 Decision 2 is explicit: "No config at all is not a failure."
    /// </summary>
    AwaitingFirstRun,

    /// <summary><c>hosts.toml</c> existed and parsed and validated successfully.</summary>
    Loaded,

    /// <summary>
    /// <c>hosts.toml</c> existed but could not be parsed, or failed validation, on the very first
    /// load -- there is no previous valid config to keep serving instead (contrast
    /// <see cref="Configuration.IHostConfigStore.ConfigReloadFailed"/>, which always has one).
    /// Everything downstream that needs a <c>GlobalConfig</c> (rclone rcd's port and config path,
    /// remote provisioning, the mount supervisor's host set) is disabled. Everything that does not
    /// -- WinFsp detection -- still runs and is still reported accurately.
    /// </summary>
    Invalid,
}

/// <summary>
/// Typed, causal readiness snapshot for the whole ADR-012 startup sequence (bs-6f9). Populated by
/// <see cref="StartupOrchestrator"/> as each step of
/// <c>load config -&gt; detect WinFsp -&gt; start rclone rcd + health check -&gt; provision remotes
/// -&gt; adopt-or-clear existing mounts -&gt; run the supervisor</c>
/// completes -- never by throwing. A failed step disables what depends on it (see
/// <see cref="MountBlockedReason"/>) and leaves everything else in this record describing
/// whatever the still-running steps produced. E9 (not built yet) reads this to drive the tray
/// icon and status window; ADR-012 Decision 3 forbids "some features unavailable" as a message,
/// so <see cref="MountBlockedReason"/> exists to give E9 a specific, causal sentence per host
/// instead -- e.g. "P: is not mounted -- WinFsp is not installed".
/// </summary>
public sealed record StartupReadiness
{
    /// <summary>Before <see cref="StartupOrchestrator"/> has run its sequence even once.</summary>
    public static readonly StartupReadiness Initial = new()
    {
        ConfigState = ConfigReadinessState.Invalid,
        ConfigErrors = ["Bosun has not finished starting yet."],
        WinFspInstalled = false,
        WinFspMessage = "Not checked yet.",
        RcloneHealthy = false,
        RcloneFaultMessage = "rclone rcd has not been started yet.",
        SupervisorRunning = false,
        TerminalFragmentWritten = false,
    };

    public required ConfigReadinessState ConfigState { get; init; }

    /// <summary>Populated only when <see cref="ConfigState"/> is <see cref="ConfigReadinessState.Invalid"/>.</summary>
    public IReadOnlyList<string> ConfigErrors { get; init; } = [];

    /// <summary>
    /// True once the initial Windows Terminal fragment write (bs-3lt) has completed successfully
    /// for whatever config loaded this run -- including the empty first-run template. Written
    /// immediately after config loads, before WinFsp detection or rclone: Terminal profiles are the
    /// half of Bosun that works even when mounting cannot (ADR-012's degradation model), so they
    /// must not wait on, or be skipped by, anything downstream that can fail. Does not reflect
    /// LATER rewrites triggered by <see cref="Configuration.IHostConfigStore.ConfigChanged"/> --
    /// those are fire-and-forget by design (see <see cref="Terminal.FragmentRewriteCoordinator"/>)
    /// and are not re-published here.
    /// </summary>
    public required bool TerminalFragmentWritten { get; init; }

    /// <summary>Populated only when <see cref="TerminalFragmentWritten"/> is <see langword="false"/>
    /// and a config was actually loaded (i.e. not populated merely because config was invalid --
    /// there both fields describe the same root cause, "no config to write from", so this is left
    /// null rather than reported redundantly against <see cref="ConfigErrors"/>).</summary>
    public string? TerminalFragmentFaultMessage { get; init; }

    public required bool WinFspInstalled { get; init; }

    /// <summary>Always populated, on both outcomes -- mirrors <c>WinFspDetectionResult.Message</c>.</summary>
    public required string WinFspMessage { get; init; }

    public required bool RcloneHealthy { get; init; }

    /// <summary>Populated only when <see cref="RcloneHealthy"/> is <see langword="false"/>.</summary>
    public string? RcloneFaultMessage { get; init; }

    /// <summary>
    /// Host key -&gt; why <c>IRcloneRemoteProvisioner.EnsureRemoteAsync</c> failed for that host
    /// specifically. A host absent from this dictionary either provisioned cleanly or was never
    /// attempted (check <see cref="RcloneHealthy"/> first -- provisioning does not run at all
    /// while rclone itself is unhealthy). One host's entry here does not affect any other host --
    /// this is what makes "provisioning fails for one host but not others" leave the others alone.
    /// </summary>
    public IReadOnlyDictionary<string, string> ProvisioningFailuresByHost { get; init; } =
        new Dictionary<string, string>();

    /// <summary>
    /// True once <c>IMountSupervisor.StartAsync</c> has been called (adopt-or-clear has run, and
    /// idle probing has begun for every administratively-enabled host). Independent of
    /// <see cref="RcloneHealthy"/>/<see cref="WinFspInstalled"/> -- the supervisor's shallow
    /// (TCP) idle probing needs neither, so it still runs and shows live reachability even when
    /// mounting itself cannot succeed. See <see cref="MountBlockedReason"/> for why a host is not
    /// actually mounting despite this being <see langword="true"/>.
    /// </summary>
    public required bool SupervisorRunning { get; init; }

    /// <summary>
    /// True only when mounting is possible in principle: a config is in service (including the
    /// empty first-run template) and WinFsp and rclone are both healthy. Does not account for a
    /// PARTICULAR host's own provisioning failure -- see <see cref="MountBlockedReason"/> for the
    /// per-host answer.
    /// </summary>
    public bool MountingAvailable =>
        ConfigState is ConfigReadinessState.Loaded or ConfigReadinessState.AwaitingFirstRun
        && WinFspInstalled
        && RcloneHealthy;

    /// <summary>
    /// The single, specific, startup-level reason <paramref name="hostKey"/> cannot mount right
    /// now, or <see langword="null"/> if nothing at the startup level is blocking it (it may
    /// still simply be unreachable, or mid-probe -- that is <c>IMountSupervisor</c>'s live state,
    /// not this snapshot). ADR-012 Decision 3 requires the caller render this causally -- e.g.
    /// "P: is not mounted -- WinFsp is not installed" -- and never fall back to a generic "some
    /// features unavailable".
    /// </summary>
    public string? MountBlockedReason(string hostKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(hostKey);

        if (ConfigState == ConfigReadinessState.Invalid)
        {
            return "the configuration file is invalid";
        }

        if (!WinFspInstalled)
        {
            return "WinFsp is not installed";
        }

        if (!RcloneHealthy)
        {
            return RcloneFaultMessage ?? "rclone is not running";
        }

        return ProvisioningFailuresByHost.TryGetValue(hostKey, out var reason) ? reason : null;
    }
}
