using System.Threading.Channels;
using Bosun.Configuration;
using Bosun.Probe;
using Bosun.Rclone;
using Microsoft.Extensions.Logging;

namespace Bosun.Supervisor;

/// <summary>
/// Real <see cref="IMountSupervisor"/> (bs-psq): the state machine of docs/ARCHITECTURE.md §4,
/// exactly.
/// </summary>
/// <remarks>
/// <para>
/// <b>How "Mounting is reachable only from Ready" (Invariant I1) is enforced.</b> Every state
/// change in this class -- with the single, explicitly-commented exception of adopting an
/// already-mounted host at startup (see <see cref="AdoptOrClearExistingMountsAsync"/>) -- goes
/// through <see cref="SetState"/>, which consults <see cref="AllowedTransitions"/> and THROWS if
/// the requested transition is not in that table. <see cref="AllowedTransitions"/> maps
/// <see cref="MountState.Ready"/> as the only state whose allowed-set contains
/// <see cref="MountState.Mounting"/>; no other entry does. That makes "transition some other host
/// into Mounting" a compile-time-reachable-but-runtime-throwing bug rather than a silent
/// possibility -- a caller cannot reach <c>Mounting</c> by accident, and a caller that tries
/// (a bug) fails loudly instead of mounting an unverified host. On top of that table check,
/// <see cref="TryBeginMountAsync"/> -- the ONLY method in this file that ever passes
/// <see cref="MountState.Mounting"/> as <see cref="SetState"/>'s target -- additionally guards
/// with an explicit <c>if (host.State != MountState.Ready) return;</c> before doing anything, so
/// the illegal-transition exception is a backstop, not the primary defence.
/// </para>
/// <para>
/// <b>Concurrency model.</b> A single <see cref="Channel{T}"/> of continuations
/// (<c>Func&lt;CancellationToken, Task&gt;</c>) is the "one channel" docs/ARCHITECTURE.md §5
/// describes. <see cref="RunAsync"/> (the production loop, meant to be driven by a future
/// <c>SupervisorService : BackgroundService</c> -- not built by this epic, see the delivery
/// report) reads and fully awaits one continuation at a time: nothing else is dequeued until the
/// current one -- including every <c>await</c> inside it -- has finished. That is a stronger
/// guarantee than "per-host" serialisation; it is GLOBAL serialisation, which trivially implies
/// per-host serialisation as a special case and is a great deal simpler to reason about and test.
/// Every external command (<see cref="RequestMountAsync"/> etc.) and every internally-scheduled
/// timer callback goes through this same channel -- nothing mutates a <c>HostRuntime</c> from
/// outside it. Tests never run <see cref="RunAsync"/> (it would hang the test thread waiting for
/// more work); instead they call the internal <see cref="DrainAsync"/>, which processes whatever
/// is currently queued and returns -- deterministic, no sleeps, no real thread hand-off, matching
/// the <see cref="FakeTimeProvider"/>-driven synchronous-continuation style already used by
/// <c>HostProbe</c>/<c>RcloneProcessService</c> tests.
/// </para>
/// </remarks>
public sealed class MountSupervisor : IMountSupervisor, IAsyncDisposable
{
    // No config field exists for these two (docs/CONFIG-SCHEMA.md has no drain-retry, drain-confirm,
    // or reconciliation-interval setting). Reasonable internal defaults; flagged as discovered work
    // in the delivery report rather than silently treated as tunable-via-config.
    private static readonly TimeSpan DrainRetryInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DrainConfirmTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReconciliationInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The ENTIRE transition table (docs/ARCHITECTURE.md §4). See the class remarks for how this
    /// is what makes "Mounting only from Ready" structural rather than a convention.
    /// </summary>
    private static readonly IReadOnlyDictionary<MountState, MountState[]> AllowedTransitions =
        new Dictionary<MountState, MountState[]>
        {
            [MountState.Disabled] = [MountState.Probing],
            [MountState.Probing] = [MountState.Ready, MountState.Unreachable],
            [MountState.Ready] = [MountState.Mounting, MountState.Unreachable, MountState.Disabled],
            [MountState.Unreachable] = [MountState.Ready, MountState.Disabled],
            [MountState.Mounting] = [MountState.Mounted, MountState.Draining],
            [MountState.Mounted] = [MountState.Draining],
            [MountState.Draining] = [MountState.Disabled],
        };

    private readonly IHostConfigStore configStore;
    private readonly IRcloneClient rcloneClient;
    private readonly IProbe probe;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<MountSupervisor> logger;

    private readonly Channel<Func<CancellationToken, Task>> channel =
        Channel.CreateUnbounded<Func<CancellationToken, Task>>();

    private readonly Dictionary<string, HostRuntime> hosts = new(StringComparer.Ordinal);

    private GlobalConfig global = new();
    private bool started;
    private bool suspended;
    private bool loggedFailuresBeforeUnmountClamp;
    private ITimer? reconciliationTimer;

    /// <summary>Process-wide mounting-availability gate (bs-yvw.1). Defaults to available so every
    /// pre-existing test/behaviour that never touches <see cref="SetMountingAvailabilityAsync"/>
    /// (i.e. everything before <c>StartupOrchestrator</c> started pushing real WinFsp/rclone
    /// health into it) is unaffected. Read directly by <see cref="TryBeginMountAsync"/> and
    /// <see cref="GetSnapshot"/> -- both already only ever run on the single channel-processing
    /// thread/one-caller-at-a-time discipline documented on the class, the same discipline every
    /// other <c>HostRuntime</c> field relies on, except <see cref="GetSnapshot"/> which reads
    /// mutable state from an arbitrary caller thread exactly as every other snapshot field already
    /// does (docs/ARCHITECTURE.md §5: "UI reads a snapshot").</summary>
    private MountingAvailability mountingAvailability = MountingAvailability.Available;

    public MountSupervisor(
        IHostConfigStore configStore,
        IRcloneClient rcloneClient,
        IProbe probe,
        TimeProvider timeProvider,
        ILogger<MountSupervisor> logger)
    {
        this.configStore = configStore;
        this.rcloneClient = rcloneClient;
        this.probe = probe;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <summary>
    /// Why a drain was begun (bs-fix-e5, see the delivery report). Drives two things in
    /// <see cref="CompleteDrainAsync"/>: whether the host is parked rather than auto re-enabled
    /// (<see cref="UserUnmount"/>), and whether the post-drain re-enable is paced through
    /// <see cref="ArmMountRetryTimer"/> instead of firing immediately (<see cref="MountFailure"/>).
    /// Also drives which timeout <see cref="AttemptDrainStepAsync"/> escalates against
    /// (<see cref="Suspend"/> uses <c>global.suspend_unmount_timeout_seconds</c>; everything else
    /// uses the fixed ordinary <see cref="DrainConfirmTimeout"/>).
    /// </summary>
    private enum DrainCause
    {
        /// <summary>Reconciliation drift, a mounted-probe-failure threshold, or an idle-unmount
        /// timeout -- the host should come back exactly as it did before this fix: re-enabled
        /// immediately once the drain completes.</summary>
        Automatic,

        /// <summary>System suspend (Invariant I8). Escalates on the suspend budget, not the
        /// ordinary drain timeout; never re-enabled while <c>suspended</c> is true (that is
        /// <see cref="ResumeAsync"/>'s job).</summary>
        Suspend,

        /// <summary>An explicit <see cref="RequestUnmountAsync"/>. Parks the host
        /// (<see cref="HostRuntime.UserParked"/>) so it does not auto-remount itself the moment the
        /// drain completes -- see the defect-3 discussion in the delivery report.</summary>
        UserUnmount,

        /// <summary>A deep-probe or <c>mount/mount</c> failure while entering <c>Mounting</c>
        /// (<see cref="TryBeginMountAsync"/>). Paced via <see cref="ArmMountRetryTimer"/> instead of
        /// an immediate re-enable -- see the defect-1 discussion in the delivery report (ADR-005 /
        /// docs/ARCHITECTURE.md §6 "drive letter already in use: refuse the mount... do not retry
        /// forever").</summary>
        MountFailure,
    }

    /// <summary>Mutable per-host runtime state. Deliberately a private nested class: nothing
    /// outside <see cref="MountSupervisor"/> can even see this type, let alone mutate
    /// <see cref="State"/> directly -- the only mutations happen through <see cref="SetState"/>
    /// (or the one documented bootstrap exception). This is part of the same "structurally hard
    /// to violate" story as the transition table itself.</summary>
    private sealed class HostRuntime
    {
        public required string Key;
        public required HostConfig Config;
        public MountState State;
        public bool AdministrativelyEnabled;
        public BackoffState Backoff = BackoffState.Initial;

        /// <summary>Separate backoff ladder for MOUNT-ATTEMPT failures (deep probe fail or
        /// mount/mount fail while entering <c>Mounting</c>) -- deliberately distinct from
        /// <see cref="Backoff"/> (idle-probe pacing), which <see cref="EnableHostAsync"/> resets to
        /// <see cref="BackoffState.Initial"/> on every successful shallow probe. Sharing the field
        /// would erase this counter the moment the paced re-enable's own probe succeeds, right
        /// before the very auto-mount attempt it exists to pace. Reset on a successful mount; see
        /// <see cref="TryBeginMountAsync"/>.</summary>
        public BackoffState MountRetryBackoff = BackoffState.Initial;

        /// <summary>True once an explicit user unmount (<see cref="RequestUnmountAsync"/>) has
        /// completed its drain. Suppresses the persistent tier's auto-mount-on-Ready
        /// (<see cref="OnEnteredReadyAsync"/>) until an explicit user mount request clears it
        /// (<see cref="RequestMountAsync"/>), a config reload rebuilds the host set, or the app
        /// restarts. Deliberately does NOT stop the host from being probed while idle -- it still
        /// shows live reachability, it just never auto-mounts. See the defect-3 discussion in the
        /// delivery report and <see cref="HostMountSnapshot.UserParked"/>.</summary>
        public bool UserParked;

        public int ConsecutiveMountedFailures;
        public ITimer? ProbeTimer;
        public ITimer? IdleUnmountTimer;
        public DateTimeOffset? DrainStartedUtc;
        public bool DrainEscalated;
        public DrainCause DrainCause;
        public string? DrainReason;
        public DateTimeOffset? LastTransitionUtc;
        public string? LastTransitionTrigger;
    }

    // ------------------------------------------------------------------------------------------
    // IMountSupervisor
    // ------------------------------------------------------------------------------------------

    public Task StartAsync(CancellationToken cancellationToken = default) => EnqueueAndWait(async ct =>
    {
        if (started)
        {
            return;
        }

        started = true;
        global = configStore.Current.Global;

        foreach (var (key, hostConfig) in configStore.Current.Hosts)
        {
            hosts[key] = new HostRuntime
            {
                Key = key,
                Config = hostConfig,
                State = MountState.Disabled,
                AdministrativelyEnabled = hostConfig.Mount.Mode != MountMode.None,
            };
        }

        // Crash recovery: adopt-or-clear BEFORE anything else touches a host's state
        // (docs/ARCHITECTURE.md §6).
        await AdoptOrClearExistingMountsAsync(ct).ConfigureAwait(false);

        foreach (var host in hosts.Values)
        {
            if (!host.AdministrativelyEnabled || host.State == MountState.Mounted)
            {
                // Mode == none, or already adopted as Mounted above -- nothing further to do at
                // startup for either case.
                continue;
            }

            await EnableHostAsync(host, "startup", ct).ConfigureAwait(false);
        }

        ArmReconciliationTimer();
    }, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default) => EnqueueAndWait(_ =>
    {
        reconciliationTimer?.Dispose();
        reconciliationTimer = null;

        foreach (var host in hosts.Values)
        {
            host.ProbeTimer?.Dispose();
            host.ProbeTimer = null;
            host.IdleUnmountTimer?.Dispose();
            host.IdleUnmountTimer = null;
        }

        started = false;
        return Task.CompletedTask;
    }, cancellationToken);

    public IReadOnlyList<HostMountSnapshot> GetSnapshot() =>
        hosts.Values
            .Select(h => new HostMountSnapshot
            {
                HostKey = h.Key,
                State = h.State,
                Drive = h.Config.Mount.Drive,
                AdministrativelyEnabled = h.AdministrativelyEnabled,
                ConsecutiveMountedFailures = h.ConsecutiveMountedFailures,
                ConsecutiveIdleFailures = h.Backoff.ConsecutiveFailures,
                LastTransitionUtc = h.LastTransitionUtc,
                LastTransitionTrigger = h.LastTransitionTrigger,
                UserParked = h.UserParked,
                MountUnavailableReason = mountingAvailability.IsAvailable ? null : mountingAvailability.Reason,
            })
            .ToList();

    public Task RequestMountAsync(string hostKey, CancellationToken cancellationToken = default) =>
        EnqueueAndWait(async ct =>
        {
            var host = RequireHost(hostKey);

            // bs-yvw.1 / ADR-014 rule 8: an explicit mount request while mounting is unavailable
            // must fail loudly with the causal reason, not silently do nothing -- the same
            // "no silent no-op" standard rule 8 already set for a request against an Unreachable
            // host. Checked here (before the Ready-only TryBeginMountAsync guard) precisely so it
            // still fires for a host that IS Ready and would otherwise mount right now.
            if (!mountingAvailability.IsAvailable)
            {
                throw new MountingUnavailableException(hostKey, mountingAvailability.Cause!.Value, mountingAvailability.Reason!);
            }

            // An explicit mount request is exactly the "explicit user remount" that un-parks a host
            // a previous RequestUnmountAsync parked (bs-fix-e5 defect 3). Harmless when the host was
            // not parked, and harmless for on-demand hosts (UserParked never gates their mounting --
            // only the persistent-tier auto-mount in OnEnteredReadyAsync checks it).
            host.UserParked = false;

            if (host.State == MountState.Unreachable)
            {
                // ADR-014 rule 8: a mount request against an Unreachable host is never a silent
                // no-op -- without this, the on-demand tier-split above ("Unreachable is not
                // polled at all") would strand an on-demand host in Unreachable forever, since rule
                // 1 forbids Mounting from anywhere but Ready and nothing else would ever move it
                // there again. The click IS the trigger a suppressed timer no longer supplies.
                //
                // Refuses first, rather than probing, if the system is currently suspended --
                // Invariant I8 in reverse: bringing reachability information (or a mount) back up
                // in the moments around a suspend is exactly the mount this invariant exists to
                // prevent.
                if (suspended)
                {
                    throw new MountRequestRefusedException(hostKey, "the system is suspended");
                }

                // Reuses RequestRetryNowAsync's own reset-and-probe path: a user asking to mount is
                // at least as strong a signal as a user asking to retry, and the Backoff section
                // already lists "explicit user retry now" as a reset trigger. This does not weaken
                // Invariant I1 -- the click causes a *shallow* probe; Mounting is still reached only
                // from Ready, after its own deep probe (TryBeginMountAsync, below).
                host.Backoff = BackoffState.Initial;
                await ForceImmediateIdleProbeAsync(host, "user mount request: immediate probe (ADR-014 rule 8)", ct)
                    .ConfigureAwait(false);

                if (host.State == MountState.Unreachable)
                {
                    // The probe rule 8 itself triggered came back negative. Surface why, causally,
                    // exactly as MountingUnavailableException already does for the process-wide
                    // gate -- a quiet return here would be the same silent-no-op problem rule 8
                    // exists to close, just in a smaller costume.
                    throw new MountRequestRefusedException(
                        hostKey, "the host did not respond to an immediate reachability check");
                }

                // Any other outcome -- Ready (on-demand, or a parked/gate-blocked persistent host),
                // or Mounted/Draining (a persistent host's own auto-mount already ran inside
                // OnEnteredReadyAsync, above) -- falls through to the ordinary attempt below, which
                // is a safe no-op for the states that already resolved themselves.
            }

            await TryBeginMountAsync(host, "user/persistent mount request", ct).ConfigureAwait(false);
        }, cancellationToken);

    public Task RequestUnmountAsync(string hostKey, CancellationToken cancellationToken = default) =>
        EnqueueAndWait(async ct =>
        {
            var host = RequireHost(hostKey);
            if (host.State is not (MountState.Mounting or MountState.Mounted))
            {
                logger.LogDebug(
                    "Ignoring unmount request for {HostKey}: not Mounting/Mounted (currently {State})", host.Key, host.State);
                return;
            }

            await BeginDrainAsync(host, "user unmount", DrainCause.UserUnmount, ct).ConfigureAwait(false);
        }, cancellationToken);

    public Task RequestRetryNowAsync(string hostKey, CancellationToken cancellationToken = default) =>
        EnqueueAndWait(async ct =>
        {
            var host = RequireHost(hostKey);
            if (host.State is not (MountState.Ready or MountState.Unreachable))
            {
                logger.LogDebug(
                    "Ignoring retry-now for {HostKey}: not Ready/Unreachable (currently {State})", host.Key, host.State);
                return;
            }

            host.Backoff = BackoffState.Initial;
            await ForceImmediateIdleProbeAsync(host, "user retry now", ct).ConfigureAwait(false);
        }, cancellationToken);

    public Task RecordActivityAsync(string hostKey, CancellationToken cancellationToken = default) =>
        EnqueueAndWait(ct =>
        {
            var host = RequireHost(hostKey);
            if (host.State == MountState.Mounted)
            {
                ArmIdleUnmountTimer(host);
            }

            return Task.CompletedTask;
        }, cancellationToken);

    public Task SuspendAsync(CancellationToken cancellationToken = default) => EnqueueAndWait(async ct =>
    {
        suspended = true;

        foreach (var host in hosts.Values.Where(h => h.State is MountState.Mounting or MountState.Mounted).ToList())
        {
            await BeginDrainAsync(host, "system suspend", DrainCause.Suspend, ct).ConfigureAwait(false);
        }
    }, cancellationToken);

    public Task ResumeAsync(CancellationToken cancellationToken = default) => EnqueueAndWait(async ct =>
    {
        suspended = false;

        foreach (var host in hosts.Values.Where(h => h.AdministrativelyEnabled).ToList())
        {
            switch (host.State)
            {
                case MountState.Disabled:
                    host.Backoff = BackoffState.Initial;
                    await EnableHostAsync(host, "resume: previously enabled", ct).ConfigureAwait(false);
                    break;
                case MountState.Ready:
                    host.Backoff = BackoffState.Initial;
                    await ForceImmediateIdleProbeAsync(host, "resume: bypass backoff", ct).ConfigureAwait(false);
                    break;
                case MountState.Unreachable:
                    // NO tier split here, deliberately. ADR-014 splits the RECURRING ladder only.
                    // Its stated cost -- "makes the UI slow and the auth logs noisy" -- is an
                    // argument about continuous traffic, every rung, forever; a resume is a rare,
                    // bounded event producing one probe per host. Suppressing it would leave an
                    // on-demand host displaying a stale Unreachable after the machine has moved
                    // networks, which is exactly the sluggishness §4's Backoff section says the
                    // reset exists to eliminate, and OPERATIONS.md T2 is the acceptance test for it.

                    host.Backoff = BackoffState.Initial;
                    await ForceImmediateIdleProbeAsync(host, "resume: bypass backoff", ct).ConfigureAwait(false);
                    break;
                default:
                    // Mounting/Mounted should not exist here (suspend already drained them);
                    // Draining is left alone to keep retrying on its own schedule.
                    break;
            }
        }
    }, cancellationToken);

    public Task NetworkChangedAsync(CancellationToken cancellationToken = default) => EnqueueAndWait(async ct =>
    {
        foreach (var host in hosts.Values.Where(h => h.State is MountState.Ready or MountState.Unreachable).ToList())
        {
            // No tier split -- same reasoning as ResumeAsync above. ADR-014 splits the recurring
            // ladder; a network change is one bounded probe per host, and it is the event T2 turns
            // on. An on-demand row left showing a stale Unreachable after a dock is worse than
            // ADR-008's "unknown until acted on", because it is confidently wrong rather than blank.
            host.Backoff = BackoffState.Initial;
            await ForceImmediateIdleProbeAsync(host, "network change: bypass backoff", ct).ConfigureAwait(false);
        }

        foreach (var host in hosts.Values.Where(h => h.State == MountState.Mounted).ToList())
        {
            host.ProbeTimer?.Dispose();
            host.ProbeTimer = null;
            await HandleMountedProbeDueAsync(host, "network change: force-probe mounted host", ct).ConfigureAwait(false);
        }
    }, cancellationToken);

    public Task OnRcloneRestartedAsync(CancellationToken cancellationToken = default) =>
        EnqueueAndWait(ct => ReconcileAsync(ct), cancellationToken);

    public Task SetMountingAvailabilityAsync(MountingAvailability availability, CancellationToken cancellationToken = default) =>
        EnqueueAndWait(async ct =>
        {
            var wasAvailable = mountingAvailability.IsAvailable;
            mountingAvailability = availability;

            if (availability.IsAvailable && !wasAvailable)
            {
                logger.LogInformation("Mounting became available again");

                // Recovery (bs-yvw.1): every persistent host sitting in Ready only because the
                // gate refused it gets its mount attempt now, rather than waiting for a restart.
                // On-demand hosts need no equivalent sweep -- RequestMountAsync stops throwing
                // MountingUnavailableException the instant mountingAvailability above is updated,
                // so the user's next click (or the tray re-enabling the menu item) just works.
                foreach (var host in hosts.Values
                    .Where(h => h.State == MountState.Ready
                        && h.Config.Mount.Mode == MountMode.Persistent
                        && h.AdministrativelyEnabled
                        && !h.UserParked)
                    .ToList())
                {
                    await TryBeginMountAsync(host, "mounting became available", ct).ConfigureAwait(false);
                }
            }
            else if (!availability.IsAvailable && wasAvailable)
            {
                logger.LogWarning("Mounting became unavailable: {Reason}", availability.Reason);
            }
        }, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        reconciliationTimer?.Dispose();
        foreach (var host in hosts.Values)
        {
            host.ProbeTimer?.Dispose();
            host.IdleUnmountTimer?.Dispose();
        }

        channel.Writer.TryComplete();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    // ------------------------------------------------------------------------------------------
    // The channel loop
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// The production loop: reads and fully awaits one continuation at a time, forever, until
    /// <paramref name="cancellationToken"/> fires. Not exercised by any test in this delivery
    /// (there is no hosted-service integration test -- CLAUDE.md forbids running the app from a
    /// worktree); the state-machine LOGIC every continuation runs is fully covered via
    /// <see cref="DrainAsync"/> instead. See the delivery report.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (channel.Reader.TryRead(out var action))
            {
                await action(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Test-only synchronous pump: processes whatever is currently queued (including any
    /// follow-up work a processed item itself enqueues) and returns once the channel is empty.
    /// Never used in production -- see <see cref="RunAsync"/>.</summary>
    internal async Task DrainAsync(CancellationToken cancellationToken = default)
    {
        while (channel.Reader.TryRead(out var action))
        {
            await action(cancellationToken).ConfigureAwait(false);
        }
    }

    private void Enqueue(Func<CancellationToken, Task> action)
    {
        channel.Writer.TryWrite(async ct =>
        {
            try
            {
                await action(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Unhandled exception processing a supervisor action");
            }
        });
    }

    private Task EnqueueAndWait(Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.Writer.TryWrite(async ct =>
        {
            try
            {
                await action(ct).ConfigureAwait(false);
                completion.TrySetResult();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });
        return completion.Task;
    }

    private HostRuntime RequireHost(string hostKey) =>
        hosts.TryGetValue(hostKey, out var host)
            ? host
            : throw new KeyNotFoundException($"No configured host '{hostKey}'.");

    // ------------------------------------------------------------------------------------------
    // Transition enforcement (see class remarks)
    // ------------------------------------------------------------------------------------------

    private void SetState(HostRuntime host, MountState to, string trigger)
    {
        var from = host.State;
        if (!AllowedTransitions.TryGetValue(from, out var allowed) || Array.IndexOf(allowed, to) < 0)
        {
            throw new InvalidOperationException(
                $"Illegal mount-state transition for host '{host.Key}': {from} -> {to} (trigger: {trigger}). " +
                "This violates the transition table in docs/ARCHITECTURE.md §4 and indicates a bug in " +
                "MountSupervisor, not a recoverable runtime condition.");
        }

        host.State = to;
        host.LastTransitionUtc = timeProvider.GetUtcNow();
        host.LastTransitionTrigger = trigger;

        logger.LogInformation(
            "Host {HostKey} transitioned {From} -> {To} (trigger: {Trigger})", host.Key, from, to, trigger);
    }

    // ------------------------------------------------------------------------------------------
    // Disabled -> Probing -> Ready | Unreachable
    // ------------------------------------------------------------------------------------------

    private async Task EnableHostAsync(HostRuntime host, string trigger, CancellationToken ct)
    {
        if (host.State != MountState.Disabled)
        {
            logger.LogDebug("Ignoring enable for {HostKey}: not Disabled (currently {State})", host.Key, host.State);
            return;
        }

        SetState(host, MountState.Probing, trigger);

        var result = await probe.ProbeShallowAsync(
            host.Config.Hostname, host.Config.Port, ProbeTimeout(), ct).ConfigureAwait(false);

        if (result.Outcome == ShallowProbeOutcome.Success)
        {
            host.Backoff = BackoffState.Initial;
            SetState(host, MountState.Ready, $"initial probe succeeded ({trigger})");
            await OnEnteredReadyAsync(host, ct).ConfigureAwait(false);
        }
        else
        {
            host.Backoff = host.Backoff.RecordFailure();
            SetState(host, MountState.Unreachable, $"initial probe failed: {result.Outcome} ({trigger})");
            ArmIdleProbeTimer(host);
        }
    }

    /// <summary>Shared by every path that lands a host in <see cref="MountState.Ready"/>: a
    /// persistent-tier host auto-mounts (docs/ARCHITECTURE.md §4 rule 6); an on-demand host just
    /// rests. Idle probing (if configured) is armed either way.</summary>
    /// <remarks>
    /// The <see cref="HostRuntime.UserParked"/> check is the defect-3 fix: rule 6's auto-mount
    /// fires "on becoming Ready", literally read as every arrival, which is what let a user-issued
    /// unmount blink back on. <see cref="HostRuntime.UserParked"/> is set only by a completed
    /// <see cref="DrainCause.UserUnmount"/> drain (<see cref="CompleteDrainAsync"/>) and cleared
    /// only by an explicit follow-up <see cref="RequestMountAsync"/> -- so this still auto-mounts on
    /// every OTHER arrival at Ready (recovering from <see cref="MountState.Unreachable"/>, a resume,
    /// a network change), exactly as rule 6 intends; it only skips the one arrival immediately
    /// following the user's own "take it down" instruction.
    /// </remarks>
    private async Task OnEnteredReadyAsync(HostRuntime host, CancellationToken ct)
    {
        if (host.Config.Mount.Mode == MountMode.Persistent && !host.UserParked)
        {
            await TryBeginMountAsync(host, "persistent tier: auto-mount on becoming Ready", ct).ConfigureAwait(false);
            if (host.State != MountState.Ready)
            {
                // Moved on to Mounting/Mounted/Draining -- that path owns its own timers now.
                return;
            }
        }

        ArmIdleProbeTimer(host);
    }

    /// <summary>The recurring shallow-probe check while idle (docs/ARCHITECTURE.md §3: "the
    /// recurring liveness check while a host is Ready/Unreachable/Mounted"). Deliberately does
    /// NOT revisit <see cref="MountState.Probing"/> -- that state is reserved for the one-shot
    /// initial check on "enable" (see <see cref="EnableHostAsync"/> and the delivery report's note
    /// on this reading of docs/ARCHITECTURE.md §4).</summary>
    private async Task HandleIdleProbeDueAsync(HostRuntime host, string trigger, CancellationToken ct)
    {
        if (host.State is not (MountState.Ready or MountState.Unreachable))
        {
            // Stale timer: state moved on (e.g. into Mounting) before this fired.
            return;
        }

        var result = await probe.ProbeShallowAsync(
            host.Config.Hostname, host.Config.Port, ProbeTimeout(), ct).ConfigureAwait(false);

        if (result.Outcome == ShallowProbeOutcome.Success)
        {
            var wasUnreachable = host.State == MountState.Unreachable;
            host.Backoff = BackoffState.Initial;
            if (wasUnreachable)
            {
                SetState(host, MountState.Ready, $"probe succeeded ({trigger})");
                await OnEnteredReadyAsync(host, ct).ConfigureAwait(false);
                return;
            }
        }
        else
        {
            if (host.State == MountState.Ready)
            {
                host.Backoff = host.Backoff.RecordFailure();
                SetState(host, MountState.Unreachable, $"probe failed: {result.Outcome} ({trigger})");
            }
            else
            {
                host.Backoff = host.Backoff.RecordFailure();
                logger.LogInformation(
                    "Host {HostKey} still unreachable ({Outcome}); backoff now at {Failures} consecutive failure(s)",
                    host.Key, result.Outcome, host.Backoff.ConsecutiveFailures);
            }
        }

        ArmIdleProbeTimer(host);
    }

    private Task ForceImmediateIdleProbeAsync(HostRuntime host, string trigger, CancellationToken ct)
    {
        host.ProbeTimer?.Dispose();
        host.ProbeTimer = null;
        return HandleIdleProbeDueAsync(host, trigger, ct);
    }

    private void ArmIdleProbeTimer(HostRuntime host)
    {
        host.ProbeTimer?.Dispose();
        host.ProbeTimer = null;

        int delaySeconds;
        if (host.State == MountState.Ready)
        {
            var interval = host.Config.Probe.IntervalSeconds;
            if (interval <= 0)
            {
                // ADR-008: 0 means never poll while idle. No timer armed.
                return;
            }

            delaySeconds = interval;
        }
        else if (host.State == MountState.Unreachable)
        {
            if (host.Config.Mount.Mode != MountMode.Persistent)
            {
                // ADR-014 decision 1/2: the backoff ladder runs for persistent hosts only. An
                // on-demand host in Unreachable is not polled at all -- it stays dark until the
                // user acts (RequestMountAsync, rule 8, or an explicit RequestRetryNowAsync). No
                // timer armed.
                return;
            }

            delaySeconds = host.Backoff.NextDelaySeconds(global.BackoffSeconds);
        }
        else
        {
            return;
        }

        host.ProbeTimer = CreateOneShotTimer(
            TimeSpan.FromSeconds(Math.Max(delaySeconds, 0)),
            () => Enqueue(ct => HandleIdleProbeDueAsync(host, "periodic idle probe", ct)));
    }

    // ------------------------------------------------------------------------------------------
    // Ready -> Mounting -> Mounted | Draining (Invariant I1)
    // ------------------------------------------------------------------------------------------

    private async Task TryBeginMountAsync(HostRuntime host, string trigger, CancellationToken ct)
    {
        if (host.State != MountState.Ready)
        {
            logger.LogDebug("Ignoring mount request for {HostKey}: not Ready (currently {State})", host.Key, host.State);
            return;
        }

        if (!mountingAvailability.IsAvailable)
        {
            // bs-yvw.1: the second, structural condition on entering Mounting, alongside "must be
            // Ready" above -- checked BEFORE SetState/the deep probe, not after, so a host whose
            // mount cannot possibly succeed (WinFsp missing, rcd down) does not keep hitting the
            // remote host's SFTP subsystem with a deep probe every retry for no reason (the exact
            // "endless and noisy in the target server's auth log" shape this fix exists to close).
            // The host stays Ready and keeps shallow-probing; SetMountingAvailabilityAsync sweeps
            // it back in the moment the gate reopens.
            logger.LogInformation(
                "Refusing to mount {HostKey}: mounting is unavailable ({Reason}); staying Ready -- will be " +
                "attempted automatically once mounting becomes available again",
                host.Key, mountingAvailability.Reason);
            return;
        }

        SetState(host, MountState.Mounting, trigger);
        host.ProbeTimer?.Dispose();
        host.ProbeTimer = null;

        var deep = await probe.ProbeDeepAsync(host.Key, ProbeTimeout(), ct).ConfigureAwait(false);
        if (deep.Outcome != DeepProbeOutcome.Success)
        {
            logger.LogWarning(
                "Deep probe failed for {HostKey} while entering Mounting ({Outcome}: {Detail}); draining rather " +
                "than mounting (Invariant I1 -- deep probe failure never reaches Mounted)",
                host.Key, deep.Outcome, deep.Detail);
            host.MountRetryBackoff = host.MountRetryBackoff.RecordFailure();
            await BeginDrainAsync(host, $"deep probe failed entering Mounting: {deep.Outcome}", DrainCause.MountFailure, ct)
                .ConfigureAwait(false);
            return;
        }

        var drive = host.Config.Mount.Drive!;
        var fs = RcloneRemoteNaming.RemoteFsPath(host.Key, host.Config.Mount.RemotePath!);

        try
        {
            await rcloneClient.MountAsync(
                new RcloneMountRequest { Fs = fs, MountPoint = drive, VfsCacheMode = host.Config.Mount.VfsCacheMode },
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "mount/mount failed for {HostKey} at {Drive}", host.Key, drive);
            host.MountRetryBackoff = host.MountRetryBackoff.RecordFailure();
            await BeginDrainAsync(host, $"mount/mount failed: {ex.Message}", DrainCause.MountFailure, ct).ConfigureAwait(false);
            return;
        }

        host.ConsecutiveMountedFailures = 0;
        host.MountRetryBackoff = BackoffState.Initial;
        SetState(host, MountState.Mounted, "mount/mount succeeded");
        ArmMountedProbeTimer(host);
        ArmIdleUnmountTimer(host);
    }

    // ------------------------------------------------------------------------------------------
    // Mounted: always probed (ADR-011)
    // ------------------------------------------------------------------------------------------

    private async Task HandleMountedProbeDueAsync(HostRuntime host, string trigger, CancellationToken ct)
    {
        if (host.State != MountState.Mounted)
        {
            return;
        }

        var result = await probe.ProbeShallowAsync(
            host.Config.Hostname, host.Config.Port, ProbeTimeout(), ct).ConfigureAwait(false);

        if (result.Outcome == ShallowProbeOutcome.Success)
        {
            host.ConsecutiveMountedFailures = 0;
        }
        else
        {
            host.ConsecutiveMountedFailures++;
            var threshold = EffectiveFailuresBeforeUnmount();
            logger.LogWarning(
                "Mounted probe failed for {HostKey} ({Outcome}): {Failures}/{Threshold} consecutive failures",
                host.Key, result.Outcome, host.ConsecutiveMountedFailures, threshold);

            if (host.ConsecutiveMountedFailures >= threshold)
            {
                await BeginDrainAsync(
                    host,
                    $"{host.ConsecutiveMountedFailures} consecutive mounted-probe failures (threshold {threshold})",
                    DrainCause.Automatic,
                    ct).ConfigureAwait(false);
                return;
            }
        }

        ArmMountedProbeTimer(host);
    }

    private void ArmMountedProbeTimer(HostRuntime host)
    {
        host.ProbeTimer?.Dispose();
        var seconds = EffectiveMountedProbeIntervalSeconds(host.Config, global);
        host.ProbeTimer = CreateOneShotTimer(
            TimeSpan.FromSeconds(seconds),
            () => Enqueue(ct => HandleMountedProbeDueAsync(host, "periodic mounted probe", ct)));
    }

    /// <summary>ADR-011's formula, exactly. <c>interval_seconds == 0</c> -- the documented
    /// on-demand default that <c>hosts.example.toml</c> ships -- must never mean "never probe
    /// while mounted"; that is the one-line change that would reopen the wedged-Explorer hole
    /// ADR-011 exists to close. This is the single call site every mounted-probe schedule goes
    /// through, and <c>AdrElevenProbeSchedulingTests</c> asserts each branch directly.</summary>
    private static int EffectiveMountedProbeIntervalSeconds(HostConfig hostConfig, GlobalConfig globalConfig)
    {
        var interval = hostConfig.Probe.IntervalSeconds;
        return interval > 0 ? Math.Min(interval, globalConfig.MountedProbeIntervalSeconds) : globalConfig.MountedProbeIntervalSeconds;
    }

    /// <summary>
    /// Defensive clamp, kept as defence-in-depth after bs-z3y closed the underlying gap:
    /// <see cref="ConfigValidator"/> now rejects <c>global.failures_before_unmount &lt; 1</c>
    /// outright ("invalid-failures-before-unmount"), so this clamp should be unreachable in
    /// practice -- every config that reaches the supervisor has already passed validation. It
    /// stays rather than being deleted because a value could still arrive here some other way
    /// (a future direct construction of <see cref="GlobalConfig"/> that bypasses
    /// <see cref="ConfigValidator"/>, a test double, etc.), and if that ever happens, erring
    /// toward unmounting-on-the-next-failure is the only safe direction -- erring toward "never
    /// unmounts" would be a direct Invariant I2 violation. Logs once per process, loudly, the
    /// first time a clamp is needed, precisely because it should never fire.
    /// </summary>
    private int EffectiveFailuresBeforeUnmount()
    {
        var configured = global.FailuresBeforeUnmount;
        if (configured >= 1)
        {
            return configured;
        }

        if (!loggedFailuresBeforeUnmountClamp)
        {
            loggedFailuresBeforeUnmountClamp = true;
            logger.LogWarning(
                "global.failures_before_unmount is {Configured}, which ConfigValidator should have rejected " +
                "(rule invalid-failures-before-unmount, bs-z3y) -- this clamp should be unreachable. " +
                "Clamping to 1 so a mounted host is unmounted on its very next probe failure rather than never " +
                "(Invariant I2: erring toward unmounting, never toward leaving a drive letter up indefinitely).",
                configured);
        }

        return 1;
    }

    private void ArmIdleUnmountTimer(HostRuntime host)
    {
        host.IdleUnmountTimer?.Dispose();
        host.IdleUnmountTimer = null;

        if (host.State != MountState.Mounted)
        {
            return;
        }

        var idleSeconds = host.Config.Mount.IdleUnmountSeconds ?? 0;
        if (host.Config.Mount.Mode != MountMode.OnDemand || idleSeconds <= 0)
        {
            return;
        }

        host.IdleUnmountTimer = CreateOneShotTimer(
            TimeSpan.FromSeconds(idleSeconds),
            () => Enqueue(ct => HandleIdleUnmountDueAsync(host, ct)));
    }

    private Task HandleIdleUnmountDueAsync(HostRuntime host, CancellationToken ct)
    {
        if (host.State != MountState.Mounted)
        {
            return Task.CompletedTask;
        }

        return BeginDrainAsync(
            host, $"idle timeout ({host.Config.Mount.IdleUnmountSeconds}s without activity)", DrainCause.Automatic, ct);
    }

    // ------------------------------------------------------------------------------------------
    // Draining, with forced-unmount escalation verified against listmounts
    // ------------------------------------------------------------------------------------------

    private async Task BeginDrainAsync(HostRuntime host, string reason, DrainCause cause, CancellationToken ct)
    {
        if (host.State is not (MountState.Mounting or MountState.Mounted))
        {
            logger.LogDebug(
                "Ignoring drain request for {HostKey}: not Mounting/Mounted (currently {State}); reason was: {Reason}",
                host.Key, host.State, reason);
            return;
        }

        SetState(host, MountState.Draining, reason);
        host.DrainReason = reason;
        host.DrainStartedUtc = timeProvider.GetUtcNow();
        host.DrainEscalated = false;
        host.DrainCause = cause;

        host.ProbeTimer?.Dispose();
        host.ProbeTimer = null;
        host.IdleUnmountTimer?.Dispose();
        host.IdleUnmountTimer = null;

        await AttemptDrainStepAsync(host, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// One drain attempt: unmount, then verify against <c>mount/listmounts</c> -- never trust the
    /// unmount call's own success/failure as proof the drive is actually gone (docs/ARCHITECTURE.md
    /// §4 rule 4: "Never leave the state machine believing a drive is gone when it is not"). If the
    /// drain has been running longer than its timeout and has not yet escalated, re-issues the
    /// unmount call (the only escalation lever <see cref="IRcloneClient"/> exposes -- it has no
    /// distinct "force" endpoint) and re-verifies. If it STILL cannot be confirmed gone, this does
    /// NOT declare victory: it stays in <see cref="MountState.Draining"/> and schedules another
    /// attempt, forever, rather than ever transitioning to <see cref="MountState.Disabled"/> on an
    /// unconfirmed drain.
    /// </summary>
    private async Task AttemptDrainStepAsync(HostRuntime host, CancellationToken ct)
    {
        if (host.State != MountState.Draining)
        {
            return;
        }

        var mountPoint = host.Config.Mount.Drive!;

        try
        {
            await rcloneClient.UnmountAsync(mountPoint, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex, "mount/unmount call failed for {HostKey} at {MountPoint}; will re-verify against listmounts",
                host.Key, mountPoint);
        }

        if (await IsStillMountedAsync(mountPoint, ct).ConfigureAwait(false) is false)
        {
            logger.LogInformation(
                "Unmount confirmed for {HostKey} at {MountPoint} (reason: {Reason})", host.Key, mountPoint, host.DrainReason);
            await CompleteDrainAsync(host, ct).ConfigureAwait(false);
            return;
        }

        var elapsed = timeProvider.GetUtcNow() - host.DrainStartedUtc!.Value;
        var timeout = host.DrainCause == DrainCause.Suspend
            ? TimeSpan.FromSeconds(global.SuspendUnmountTimeoutSeconds)
            : DrainConfirmTimeout;

        if (!host.DrainEscalated && elapsed >= timeout)
        {
            host.DrainEscalated = true;
            logger.LogWarning(
                "Unmount for {HostKey} at {MountPoint} did not confirm within {Timeout}; escalating to forced unmount",
                host.Key, mountPoint, timeout);

            try
            {
                await rcloneClient.UnmountAsync(mountPoint, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Forced unmount re-attempt failed for {HostKey} at {MountPoint}", host.Key, mountPoint);
            }

            if (await IsStillMountedAsync(mountPoint, ct).ConfigureAwait(false) is false)
            {
                logger.LogInformation("Forced unmount confirmed for {HostKey} at {MountPoint}", host.Key, mountPoint);
                await CompleteDrainAsync(host, ct).ConfigureAwait(false);
                return;
            }

            logger.LogError(
                "Forced unmount for {HostKey} at {MountPoint} STILL not confirmed by mount/listmounts; the drive " +
                "letter may still be present. Retrying rather than declaring the drain complete.",
                host.Key, mountPoint);
        }

        // bs-fix-e5 defect 4: before escalation, the next check must land AT the escalation
        // deadline, not be rounded up to the next multiple of DrainRetryInterval. Without this, a
        // suspendUnmountTimeoutSeconds anywhere in (0, DrainRetryInterval) x N still only gets
        // checked on the fixed 5s/10s/15s.. grid, so e.g. 6s and 9s (both < the 10s ordinary
        // timeout) escalate at the identical wall-clock moment as each other and as the ordinary
        // path -- the setting has no observable effect for most of its plausible range. Once
        // escalated there is no further deadline to aim for, so post-escalation retries fall back
        // to the fixed interval (rule 4: keep retrying, forever, on a steady cadence).
        var nextDelay = DrainRetryInterval;
        if (!host.DrainEscalated)
        {
            var remaining = timeout - elapsed;
            if (remaining < nextDelay)
            {
                nextDelay = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }

        host.ProbeTimer = CreateOneShotTimer(nextDelay, () => Enqueue(ct2 => AttemptDrainStepAsync(host, ct2)));
    }

    private async Task<bool> IsStillMountedAsync(string mountPoint, CancellationToken ct)
    {
        IReadOnlyList<RcloneMountInfo> mounts;
        try
        {
            mounts = await rcloneClient.ListMountsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Per docs/ARCHITECTURE.md §4 rule 4: never assume a drive is gone. If we cannot even
            // ask, assume the worst (still mounted) so the caller keeps retrying rather than
            // declaring victory on missing information.
            logger.LogWarning(ex, "mount/listmounts failed while verifying unmount of {MountPoint}; assuming still mounted", mountPoint);
            return true;
        }

        return mounts.Any(m => string.Equals(m.MountPoint, mountPoint, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Not <c>async</c> -- deliberately. bs-fix-e5 defect 1: the old body directly <c>await</c>-ed
    /// straight back into <see cref="EnableHostAsync"/>, which (for a persistent host whose deep
    /// probe or <c>mount/mount</c> keeps failing) could immediately re-enter
    /// <see cref="TryBeginMountAsync"/> -&gt; <see cref="BeginDrainAsync"/> -&gt;
    /// <see cref="AttemptDrainStepAsync"/> -&gt; here again, all on the SAME call stack, with no
    /// clock advance anywhere in the cycle -- unbounded synchronous recursion terminating in a
    /// <see cref="StackOverflowException"/> that no <c>catch</c> can stop. Every re-enable this
    /// method triggers now goes through <see cref="Enqueue"/> instead: the continuation is posted to
    /// the channel and this call returns immediately, so the next iteration runs from
    /// <see cref="RunAsync"/>'s (or <see cref="DrainAsync"/>'s) loop -- a NEW stack frame, not a
    /// nested one -- regardless of whether the re-enable is paced (see
    /// <see cref="ArmMountRetryTimer"/>) or immediate. Tests that pump via <see cref="DrainAsync"/>
    /// still observe the full chain complete within one "tick", because that pump loops until the
    /// channel is empty; only the call-stack shape changes, not the test-visible timing.
    /// </summary>
    private Task CompleteDrainAsync(HostRuntime host, CancellationToken ct)
    {
        host.ProbeTimer?.Dispose();
        host.ProbeTimer = null;
        host.ConsecutiveMountedFailures = 0;

        if (host.DrainCause == DrainCause.UserUnmount)
        {
            // bs-fix-e5 defect 3: park rather than let rule 6's "persistent auto-mounts on becoming
            // Ready" fire on the very next arrival, which is what turned the tray's Unmount command
            // into a blink-and-return. See HostRuntime.UserParked and OnEnteredReadyAsync.
            host.UserParked = true;
            logger.LogInformation(
                "Host {HostKey} parked at the user's request (explicit unmount); will not auto-remount until the " +
                "user requests it again, config reloads, or Bosun restarts", host.Key);
        }

        SetState(host, MountState.Disabled, $"drain completed: {host.DrainReason}");

        if (host.AdministrativelyEnabled && !suspended)
        {
            if (host.DrainCause == DrainCause.MountFailure)
            {
                // bs-fix-e5 defect 1: a failed mount attempt must not immediately re-arm into
                // another one (ADR-005 / docs/ARCHITECTURE.md §6 "drive letter already in use:
                // refuse the mount... do not retry forever"). Pace the re-enable through the same
                // BackoffState machinery §4's Backoff section already establishes, rather than
                // firing it straight back onto the channel.
                ArmMountRetryTimer(host);
            }
            else
            {
                Enqueue(ct2 => EnableHostAsync(host, "auto re-enable after drain", ct2));
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>Paces the re-enable that follows a <see cref="DrainCause.MountFailure"/> drain using
    /// <see cref="HostRuntime.MountRetryBackoff"/> (already incremented by the failing attempt in
    /// <see cref="TryBeginMountAsync"/>, so this is always at least the ladder's first rung -- never
    /// an immediate retry). Reuses <see cref="HostRuntime.ProbeTimer"/>: safe because every path that
    /// arms a different timer into that field (<see cref="ArmIdleProbeTimer"/>,
    /// <see cref="ArmMountedProbeTimer"/>, <see cref="TryBeginMountAsync"/>) disposes whatever is
    /// there first, so a still-pending mount-retry timer is cleanly superseded the moment the host
    /// moves on for any other reason (e.g. <see cref="ResumeAsync"/> re-enabling it directly).</summary>
    private void ArmMountRetryTimer(HostRuntime host)
    {
        host.ProbeTimer?.Dispose();

        var delaySeconds = host.MountRetryBackoff.NextDelaySeconds(global.BackoffSeconds);
        host.ProbeTimer = CreateOneShotTimer(
            TimeSpan.FromSeconds(Math.Max(delaySeconds, 0)),
            () => Enqueue(ct => EnableHostAsync(host, "mount-retry backoff elapsed", ct)));
    }

    // ------------------------------------------------------------------------------------------
    // Crash recovery (startup adopt-or-clear) and per-tick reconciliation
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Runs once, at the very start of <see cref="StartAsync"/>, before any host begins probing
    /// (docs/ARCHITECTURE.md §6: "on next start, enumerate existing mounts... and adopt or clear
    /// them before doing anything else"). For each mount <c>mount/listmounts</c> reports:
    /// recognised host at the matching drive letter WHOSE REPORTED <c>Fs</c> IS THIS HOST'S OWN
    /// REMOTE -> ADOPT (trust it, bring it under the same continuous probing every other Mounted
    /// host gets -- ADR-011 -- so a genuinely dead adopted mount is still caught and drained within
    /// the normal failure window, just starting warm instead of re-probing cold first). Anything
    /// else -- no configured host at that drive letter, a host now configured with
    /// <c>mount.mode = "none"</c>, OR a recognised drive letter whose <c>Fs</c> belongs to a
    /// DIFFERENT remote (bs-fix-e5 defect 2: a drive letter reassigned between runs, or between one
    /// host's config and another's, is ordinary config editing and coincides easily with the
    /// exact moment a crash leaves a stale mount behind) -> CLEAR (force unmount; nothing can adopt
    /// a mount that does not correspond to an actively-mounting host's own remote). Adopting on
    /// mount-point match alone silently hands the user a drive letter connected to whatever server
    /// happened to be there before -- worse than a wedge, because nothing hangs and nothing is
    /// logged as wrong.
    /// </summary>
    /// <remarks>
    /// This is the ONE deliberate exception to "every state change goes through
    /// <see cref="SetState"/>": there is no meaningful "from" state for a host whose in-process
    /// runtime was just constructed a moment ago (its <see cref="HostRuntime.State"/> field is
    /// still its default, <see cref="MountState.Disabled"/>, but that default never actually
    /// "happened" as an observed transition -- it is bootstrap initialisation, not a transition).
    /// Setting <see cref="HostRuntime.State"/> directly here, rather than adding a
    /// <c>Disabled -&gt; Mounted</c> table entry that would ALSO silently permit that transition
    /// at any other, non-bootstrap moment, is what keeps <see cref="AllowedTransitions"/> an
    /// accurate description of real runtime transitions rather than widened for one bootstrap
    /// corner case.
    /// </remarks>
    private async Task AdoptOrClearExistingMountsAsync(CancellationToken ct)
    {
        IReadOnlyList<RcloneMountInfo> mounts;
        try
        {
            mounts = await rcloneClient.ListMountsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "mount/listmounts failed during startup crash recovery; assuming no existing mounts");
            return;
        }

        foreach (var mount in mounts)
        {
            var host = hosts.Values.FirstOrDefault(
                h => h.Config.Mount.Drive is not null &&
                     string.Equals(h.Config.Mount.Drive, mount.MountPoint, StringComparison.OrdinalIgnoreCase));

            // bs-fix-e5 defect 2: adoption must be authorised by the REMOTE the mount actually
            // serves, not merely by the drive letter it happens to occupy. RemotePath is expected
            // to be set whenever Drive is (mode != none); if it is somehow absent, expectedFs is
            // null and the string comparison below can never match, so the mount safely falls
            // through to the clear branch rather than being adopted on a null/empty fs.
            var expectedFs = host?.Config.Mount.RemotePath is { } remotePath
                ? RcloneRemoteNaming.RemoteFsPath(host.Key, remotePath)
                : null;
            var servesItsOwnRemote = expectedFs is not null &&
                string.Equals(expectedFs, mount.Fs, StringComparison.Ordinal);

            if (host is not null && host.Config.Mount.Mode != MountMode.None && servesItsOwnRemote)
            {
                logger.LogInformation(
                    "Host {HostKey} transitioned Disabled -> Mounted (trigger: startup: adopted existing mount " +
                    "at {MountPoint} serving its own remote {Fs}, crash recovery)",
                    host.Key, mount.MountPoint, mount.Fs);

                host.State = MountState.Mounted; // see remarks: the one documented SetState bypass
                host.LastTransitionUtc = timeProvider.GetUtcNow();
                host.LastTransitionTrigger = "startup: adopted existing mount";
                host.ConsecutiveMountedFailures = 0;
                ArmMountedProbeTimer(host);
                ArmIdleUnmountTimer(host);
            }
            else
            {
                if (host is not null && host.Config.Mount.Mode != MountMode.None)
                {
                    logger.LogWarning(
                        "Clearing mount at {MountPoint} found on startup: it claims to serve {ActualFs}, but host " +
                        "{HostKey} (which owns that drive letter) expects {ExpectedFs} -- refusing to adopt a " +
                        "mount for the wrong remote rather than handing the user someone else's files",
                        mount.MountPoint, mount.Fs, host.Key, expectedFs);
                }
                else
                {
                    logger.LogWarning(
                        "Clearing orphaned/unrecognised mount at {MountPoint} found on startup (no configured host " +
                        "claims it, or its host is now mount.mode = none)",
                        mount.MountPoint);
                }

                try
                {
                    await rcloneClient.UnmountAsync(mount.MountPoint, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Failed to clear orphaned mount at {MountPoint} during startup", mount.MountPoint);
                }
            }
        }
    }

    private void ArmReconciliationTimer()
    {
        reconciliationTimer?.Dispose();
        reconciliationTimer = timeProvider.CreateTimer(
            _ => Enqueue(ReconcileAsync), null, ReconciliationInterval, ReconciliationInterval);
    }

    /// <summary>
    /// Every supervisor tick (and immediately on demand via <see cref="OnRcloneRestartedAsync"/>):
    /// compares intended state against <c>mount/listmounts</c> and corrects drift, logging every
    /// correction (docs/ARCHITECTURE.md §4 "Reconciliation"). A host believed <c>Mounted</c> whose
    /// mount rclone no longer reports is drained (reusing the same idempotent drain path used
    /// everywhere else -- <c>mount/unmount</c> on an already-gone mount is expected to be a
    /// harmless no-op/error, tolerated the same way <see cref="AttemptDrainStepAsync"/> tolerates
    /// any unmount-call failure). A host NOT believed <c>Mounted</c> (and not mid-transition) whose
    /// drive rclone DOES report is an orphan and is force-unmounted directly.
    /// </summary>
    private async Task ReconcileAsync(CancellationToken ct)
    {
        IReadOnlyList<RcloneMountInfo> mounts;
        try
        {
            mounts = await rcloneClient.ListMountsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Reconciliation: mount/listmounts failed; skipping this tick");
            return;
        }

        var actual = new HashSet<string>(mounts.Select(m => m.MountPoint), StringComparer.OrdinalIgnoreCase);

        foreach (var host in hosts.Values.ToList())
        {
            var drive = host.Config.Mount.Drive;
            if (drive is null)
            {
                continue;
            }

            var actuallyMounted = actual.Contains(drive);

            if (host.State == MountState.Mounted && !actuallyMounted)
            {
                logger.LogWarning(
                    "Reconciliation: {HostKey} believed Mounted at {Drive} but rclone reports no such mount; " +
                    "correcting", host.Key, drive);
                await BeginDrainAsync(host, "reconciliation: mount missing from listmounts", DrainCause.Automatic, ct)
                    .ConfigureAwait(false);
            }
            else if (host.State is not (MountState.Mounted or MountState.Mounting or MountState.Draining) && actuallyMounted)
            {
                logger.LogWarning(
                    "Reconciliation: {HostKey} is {State} but rclone reports an active mount at {Drive}; clearing " +
                    "orphaned mount", host.Key, host.State, drive);
                try
                {
                    await rcloneClient.UnmountAsync(drive, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Reconciliation: failed to clear orphaned mount for {HostKey}", host.Key);
                }
            }
        }
    }

    // ------------------------------------------------------------------------------------------
    // Shared helpers
    // ------------------------------------------------------------------------------------------

    private TimeSpan ProbeTimeout() => TimeSpan.FromSeconds(global.ProbeTimeoutSeconds);

    private ITimer CreateOneShotTimer(TimeSpan due, Action callback) =>
        timeProvider.CreateTimer(_ => callback(), null, due, Timeout.InfiniteTimeSpan);
}
