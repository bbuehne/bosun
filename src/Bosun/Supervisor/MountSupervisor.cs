using System.IO;
using System.Collections.Concurrent;
using System.Threading.Channels;
using Bosun.Configuration;
using Bosun.Logging;
using Bosun.Probe;
using Bosun.Rclone;
using Bosun.Watchdog;
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
/// outside it. Almost all tests do not run <see cref="RunAsync"/> (it waits forever for more
/// work); instead they call the internal <see cref="DrainAsync"/>, which processes whatever
/// is currently queued and returns -- deterministic, no sleeps, no real thread hand-off, matching
/// the <see cref="FakeTimeProvider"/>-driven synchronous-continuation style already used by
/// <c>HostProbe</c>/<c>RcloneProcessService</c> tests.
/// </para>
/// <para>
/// <b>The one thing that is NOT on the channel: the prologue of <see cref="SuspendAsync"/></b>
/// (ADR-023, bs-4in). A suspend queued behind a hung routine check would miss its three-second
/// budget (Invariant I8), so before queuing itself it runs on the caller's thread: it increments
/// <c>pendingSuspends</c> (the I8 gate, <see cref="MountingSuspended"/>, closes at once) and calls
/// <see cref="CancellationTokenSource.CancelAsync"/> on the recurring tick in flight. That is ALL it
/// does -- it touches no host, no timer, no counter and no state, so the single-writer discipline
/// above still holds. Only the five recurring timer callbacks are preemptible (see
/// <see cref="RunTickAsync"/>); commands, drains and the probe of an enable are not.
/// </para>
/// </remarks>
public sealed class MountSupervisor : IMountSupervisor, ISupervisorLiveness, IAsyncDisposable
{
    // No config field exists for these two (docs/CONFIG-SCHEMA.md has no drain-retry, drain-confirm,
    // or reconciliation-interval setting). Reasonable internal defaults; flagged as discovered work
    // in the delivery report rather than silently treated as tunable-via-config.
    private static readonly TimeSpan DrainRetryInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DrainConfirmTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReconciliationInterval = TimeSpan.FromSeconds(30);

    /// <summary>ADR-022's second amendment (2026-11-01, bs-ane): how long after a resume a remount
    /// record may keep waiting on a local obstacle (mounting unavailable, rcd failing the core/version
    /// gate) before it is dropped. A named constant, not a config option: it is the maintainer's
    /// decision about what "waking up" means, not a tuning knob. Counts from the LATEST resume.</summary>
    internal static readonly TimeSpan RemountRecordWaitLimit = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Ring-buffer capacity for the bounded in-memory transition history (bs-ww9.2, ADR-018). Same
    /// category as <see cref="ReconciliationInterval"/> and
    /// <see cref="MountedDeepProbeFailureThreshold"/> below: a deliberately hardcoded constant, not
    /// config-driven (docs/CONFIG-SCHEMA.md has no setting for it, and this is not a value that
    /// feeds a safety decision -- it only bounds a diagnostic convenience). "A few hundred" is the
    /// right order of magnitude for a personal tool with a handful of hosts: a full sleep/resume
    /// cycle plus a stretch of probe failures produces at most a few dozen transitions per host, so
    /// this comfortably covers "what happened overnight" (the scenario ADR-018 names) while staying
    /// nowhere near a memory concern in a process that runs for weeks. <c>internal</c> (rather than
    /// <c>private</c>) purely so tests can assert boundedness against the real constant instead of a
    /// hardcoded duplicate of it.
    /// </summary>
    internal const int TransitionHistoryCapacity = 300;

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
    private readonly IDriveTypeReader driveTypeReader;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<MountSupervisor> logger;

    private readonly Channel<Func<CancellationToken, Task>> channel =
        Channel.CreateUnbounded<Func<CancellationToken, Task>>();

    private readonly Dictionary<string, HostRuntime> hosts = new(StringComparer.Ordinal);

    private GlobalConfig global = new();
    private bool started;

    /// <summary>True from the moment the suspend ACTION runs until <see cref="ResumeAsync"/>. Channel
    /// thread only. Every I8 gate reads <see cref="MountingSuspended"/> instead, never this field
    /// alone: it is set when the suspend is DEQUEUED, and work queued ahead of the suspend would
    /// otherwise still mount in the meantime (ADR-023).</summary>
    private bool suspended;

    /// <summary>Suspends that have been called (<see cref="SuspendAsync"/>, on the CALLER's thread)
    /// but whose action has not yet started. Incremented before the suspend is queued and
    /// decremented as the first statement of the action, so the I8 gate closes the instant a suspend
    /// arrives rather than when the channel gets round to it (ADR-023). A counter, not a flag: two
    /// suspends can be pending at once (suspend, resume, suspend).</summary>
    private int pendingSuspends;

    /// <summary>True while a suspend has been called and not yet begun. Safe from any thread.</summary>
    private bool SuspendPending => Volatile.Read(ref pendingSuspends) > 0;

    /// <summary>The Invariant I8 gate: no host may enter <see cref="MountState.Mounting"/> and no
    /// host may be probed towards a mount while the system is suspended OR a suspend is pending
    /// (ADR-023). Read by <see cref="TryBeginMountAsync"/>, <see cref="RequestMountAsync"/>,
    /// <see cref="RepairAllAsync"/>, <see cref="OnMountRetryElapsedAsync"/>,
    /// <see cref="CompleteDrainAsync"/> and the remount-record resolution. <b>Do not read
    /// <see cref="suspended"/> directly as a gate.</b></summary>
    private bool MountingSuspended => suspended || SuspendPending;

    /// <summary>The cancellation source of the recurring tick that is running right now, or null
    /// between ticks (ADR-023). Published by <see cref="RunTickAsync"/> on the channel thread, read
    /// by <see cref="PreemptRunningTick"/> on whichever thread called <see cref="SuspendAsync"/>.
    /// Accessed only through <see cref="Interlocked"/>/<see cref="Volatile"/>: the full barrier on
    /// both sides is what guarantees that, of a tick starting and a suspend arriving at the same
    /// moment, at least one of them sees the other.</summary>
    private CancellationTokenSource? runningTick;

    private bool loggedFailuresBeforeUnmountClamp;

    /// <summary>bs-qcs: rc failures that recur on a timer (reconciliation every 30 s, a stuck drain
    /// every 5 s) are logged once, then as a reminder every ten minutes, not once per tick.</summary>
    private readonly RepeatingFaultLogger repeatingFaults;
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

    /// <summary>ADR-022 amendment (bs-hrg): whether <c>rclone rcd</c> last answered
    /// <c>core/version</c>. False only after ADR-017's re-derivation gate failed
    /// (<see cref="ReDeriveAfterTransitionAsync"/>); set true again by the next successful gate, by
    /// <see cref="OnRcloneRestartedAsync"/> and by the reconciliation tick
    /// (<see cref="ReconcileTickAsync"/>). It gates ONLY the deferred remount
    /// (<see cref="ResolveRemountRecordAsync"/>): a remount is not attempted against an rcd that
    /// has just failed to answer, because the attempt could only fail and the record would be lost.
    /// Starts true so everything that never fails the gate is unaffected.</summary>
    private bool rcdAnswering = true;

    /// <summary>
    /// Bounded transition history backing <see cref="GetTransitionHistory"/> (bs-ww9.2). Every
    /// entry is written by <see cref="RecordTransition"/>, called only from
    /// <see cref="SetState"/> and the one documented <c>SetState</c> bypass in
    /// <see cref="AdoptOrClearExistingMountsAsync"/> -- i.e. only ever from the single
    /// channel-processing thread, the same single-writer discipline every other piece of mutable
    /// supervisor state relies on (see the class remarks).
    /// </summary>
    /// <remarks>
    /// <see cref="ConcurrentQueue{T}"/> rather than a plain <see cref="Queue{T}"/>, deliberately.
    /// <see cref="GetSnapshot"/> gets away with reading plain fields directly from an arbitrary UI
    /// thread because each individual read is a single field access -- a benign, already-accepted
    /// race (see that method's remarks). Trimming this collection on every write is a *structural*
    /// mutation (grow, then possibly dequeue), not a single field write: enumerating a plain
    /// <see cref="Queue{T}"/> on one thread while another thread is concurrently
    /// enqueuing/dequeuing it risks "Collection was modified; enumeration operation may not
    /// execute" -- an actual exception reaching the UI thread, not just a stale read.
    /// <see cref="ConcurrentQueue{T}"/> makes <see cref="GetTransitionHistory"/>'s
    /// <c>ToArray()</c> call a genuine moment-in-time snapshot with no lock and no possibility of
    /// that exception, while the write side (here) still only ever runs on the channel thread --
    /// exactly the same "single writer, direct unlocked read from elsewhere" shape as everything
    /// else in this class, just with a collection type that makes that shape safe for a
    /// continuously-mutated buffer instead of only for scalar fields.
    /// </remarks>
    private readonly ConcurrentQueue<MountTransitionEntry> transitionHistory = new();

    public MountSupervisor(
        IHostConfigStore configStore,
        IRcloneClient rcloneClient,
        IProbe probe,
        IDriveTypeReader driveTypeReader,
        TimeProvider timeProvider,
        ILogger<MountSupervisor> logger)
    {
        this.configStore = configStore;

        // bs-6to: every rc call stamps loop activity when it returns, so a legitimately long
        // composite action (StartAsync or ResumeAsync walking many hosts, one rc call after
        // another) keeps proving it is alive. Without this, the only stamps are an action's begin
        // and end, and a 20-host StartAsync could look "stalled" to the watchdog while working.
        this.rcloneClient = new ActivityStampingRcloneClient(rcloneClient, TouchLoopActivity);
        this.probe = probe;
        this.driveTypeReader = driveTypeReader;
        this.timeProvider = timeProvider;
        this.logger = logger;
        repeatingFaults = new RepeatingFaultLogger(timeProvider);
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

        /// <summary>Consecutive mount-ATTEMPT failures (bs-ww9.4) -- incremented alongside
        /// <see cref="MountRetryBackoff"/>.<c>RecordFailure()</c> at both failure sites in
        /// <see cref="TryBeginMountAsync"/> (deep probe failure, <c>mount/mount</c> failure), and
        /// reset to zero in the exact same place <see cref="MountRetryBackoff"/> resets to
        /// <see cref="BackoffState.Initial"/> -- a successful mount. Surfaced on
        /// <see cref="HostMountSnapshot.ConsecutiveMountFailures"/> so a host that answers TCP fine
        /// but cannot be mounted is no longer invisible to the UI (see that property's remarks).</summary>
        public int ConsecutiveMountFailures;

        /// <summary>The reason the most recent mount attempt failed (bs-ww9.4), set alongside
        /// <see cref="ConsecutiveMountFailures"/> at the same two failure sites, and cleared back to
        /// <see langword="null"/> at the same successful-mount reset. Surfaced verbatim on
        /// <see cref="HostMountSnapshot.LastMountFailureReason"/>.</summary>
        public string? LastMountFailureReason;

        /// <summary>True once an explicit user unmount (<see cref="RequestUnmountAsync"/>) has
        /// completed its drain. Suppresses the persistent tier's auto-mount-on-Ready
        /// (<see cref="OnEnteredReadyAsync"/>) until an explicit user mount request clears it
        /// (<see cref="RequestMountAsync"/>), a config reload rebuilds the host set, or the app
        /// restarts. Deliberately does NOT stop the host from being probed while idle -- it still
        /// shows live reachability, it just never auto-mounts. See the defect-3 discussion in the
        /// delivery report and <see cref="HostMountSnapshot.UserParked"/>.</summary>
        public bool UserParked;

        /// <summary>ADR-022 (bs-hrg): set by <see cref="SuspendAsync"/> on an ON-DEMAND host that was
        /// <see cref="MountState.Mounted"/> or <see cref="MountState.Mounting"/> when suspend arrived,
        /// i.e. a drive the user mounted that the suspend drain is about to take down. Resolved by the
        /// FIRST <see cref="EnableHostAsync"/> after resume: if that probe reaches
        /// <see cref="MountState.Ready"/> and nothing LOCAL is in the way, the host is mounted again
        /// through <c>Ready -&gt; Mounting</c> exactly as a click would
        /// (<see cref="ResolveRemountRecordAsync"/>); if the probe fails, the record is dropped and the
        /// host stays unmounted.
        /// <para>The amendment (2026-11-01, bs-hrg): when something process-wide and local is in the way
        /// -- mounting unavailable (<see cref="MountingAvailability"/>), <c>rclone rcd</c> not answering
        /// <c>core/version</c> at the resume re-derivation (<see cref="rcdAnswering"/>), or the system
        /// suspended again -- the record is KEPT and the host rests in <c>Ready</c>.
        /// <see cref="SweepDeferredRemountsAsync"/> re-probes it once those clear; Ready then mounts
        /// (<c>TryBeginMountAsync</c>, deep probe first) and anything else drops the record.</para>
        /// <para>Deliberately in memory only, and deliberately not part of
        /// <see cref="HostMountSnapshot"/> (nothing in the UI needs it). Scope is suspend only: no
        /// other drain cause sets it. Cleared by: the remount attempt (it is cleared as the host enters
        /// <c>Mounting</c>, so a remount whose own deep probe or <c>mount/mount</c> fails is not
        /// retried, and neither is a user click that mounts the host first), a failed or
        /// unreachable resolving probe, a user unmount (<see cref="RequestUnmountAsync"/>), the host
        /// being removed or changing mode (<see cref="ApplyHostConfigChangeAsync"/>), a WAITING record
        /// passing <see cref="RemountRecordWaitLimit"/> after the latest resume (bs-ane,
        /// <see cref="RemountDeadlineUtc"/>), and the app restarting (a new process starts with every record false). A host removed from config needs
        /// no clearing of its own: its runtime is evicted (<see cref="RemoveFromSupervision"/>),
        /// record and all, and a host later added under the same key gets a fresh one.</para></summary>
        public bool RemountAfterResume;

        /// <summary>ADR-022's second amendment (2026-11-01, bs-ane): the instant at which a WAITING
        /// <see cref="RemountAfterResume"/> record expires and is dropped
        /// (<see cref="RemountRecordWaitLimit"/> after the latest resume). Null while the system is
        /// suspended, and meaningless whenever <see cref="RemountAfterResume"/> is false, so none of
        /// the places that clear the record clear this too: it is cleared by <see cref="SuspendAsync"/>
        /// and set by <see cref="ResumeAsync"/>, the only two writers.</summary>
        public DateTimeOffset? RemountDeadlineUtc;

        public int ConsecutiveMountedFailures;

        /// <summary>Consecutive DEEP-probe failures while <see cref="MountState.Mounted"/> (bs-2eg
        /// / ADR-016). Deliberately a separate counter from <see cref="ConsecutiveMountedFailures"/>
        /// -- see the remarks on <see cref="HandleMountedDeepProbeDueAsync"/> for why sharing one
        /// counter between the two independently-timed probes is an order-dependent race, not a
        /// simplification. Reset to zero by a deep-probe success, and whenever the host leaves
        /// <c>Mounted</c> (<see cref="CompleteDrainAsync"/>, mirroring
        /// <see cref="ConsecutiveMountedFailures"/>'s own reset) so a fresh mount never starts life
        /// partway toward the threshold.</summary>
        public int ConsecutiveDeepProbeFailures;
        public ITimer? ProbeTimer;

        /// <summary>The recurring DEEP probe while <see cref="MountState.Mounted"/> (bs-2eg /
        /// ADR-016). Deliberately a SEPARATE timer field from <see cref="ProbeTimer"/> -- unlike
        /// every other use of <see cref="ProbeTimer"/> (idle probe, mounted shallow probe, drain
        /// retry, mount retry), which are mutually exclusive across states and safely share one
        /// field, the mounted deep probe runs CONCURRENTLY with the mounted shallow probe on its
        /// own independent cadence (<see cref="GlobalConfig.MountedDeepProbeIntervalSeconds"/>).
        /// Armed alongside <see cref="ProbeTimer"/> everywhere a host lands in <c>Mounted</c>
        /// (<see cref="TryBeginMountAsync"/>, <see cref="AdoptOrClearExistingMountsAsync"/>);
        /// disposed alongside it everywhere a host leaves <c>Mounted</c>
        /// (<see cref="BeginDrainAsync"/>, <see cref="StopAsync"/>, <see cref="DisposeAsync"/>).</summary>
        public ITimer? DeepProbeTimer;
        public ITimer? IdleUnmountTimer;

        /// <summary>Bumped by every <see cref="ArmIdleUnmountTimer"/>. A fire captures the value its
        /// timer was armed with and is ignored if it no longer matches (bs-obf): disposing a timer
        /// does not retract a callback that already ran and is queued on the channel, and an activity
        /// reset that lands between a timer firing and its continuation running must win.</summary>
        public int IdleTimerGeneration;

        /// <summary>The previous <c>vfs/stats</c> sample while Mounted (bs-obf): the six counters whose
        /// change counts as activity. Null until the first sample after a mount, which becomes the
        /// baseline; cleared on every mount, adoption and drain (<see cref="ResetVfsActivityTracking"/>).</summary>
        public VfsCounters? VfsBaseline;

        /// <summary>Files rclone reports as having failed to upload, from the latest successful
        /// <c>vfs/stats</c> poll (bs-obf). Surfaced on <see cref="HostMountSnapshot.UploadFailureCount"/>.
        /// Last known value: a failed poll leaves it alone. Zero outside Mounted.</summary>
        public long UploadFailureCount;

        /// <summary>Last reason an idle unmount was skipped, and whether a failed activity sample has
        /// been logged since the last good one -- both only so a recurring condition is logged when it
        /// starts, not on every tick.</summary>
        public string? LastIdleBlockReason;
        public bool VfsPollFaultLogged;
        public DateTimeOffset? DrainStartedUtc;

        /// <summary>Set by <see cref="SuspendAsync"/> on a host that was ALREADY
        /// <see cref="MountState.Draining"/> for an ordinary cause when suspend arrived (bs-u4a item
        /// 4): the moment its forced-unmount escalation must happen by, namely the suspend arrival
        /// plus <c>global.suspend_unmount_timeout_seconds</c>. It only ever SHORTENS the deadline the
        /// drain's own cause gives it (<see cref="EffectiveDrainTimeout"/> takes the minimum); it is
        /// cleared by <see cref="BeginDrainAsync"/> so it cannot leak into a later drain.</summary>
        public DateTimeOffset? SuspendEscalationDeadlineUtc;
        public bool DrainEscalated;
        public DrainCause DrainCause;
        public string? DrainReason;
        public DateTimeOffset? LastTransitionUtc;
        public string? LastTransitionTrigger;

        /// <summary>A newer <see cref="Config"/> queued by a mount-affecting
        /// <see cref="ConfigChangedAsync"/> diff while this host is Mounting/Mounted/Draining
        /// (bs-7ck). Deliberately NOT applied to <see cref="Config"/> immediately: every drain-retry
        /// read of <c>Config.Mount.Drive</c> (<see cref="AttemptDrainStepAsync"/>) must keep
        /// targeting the mount point that is ACTUALLY live until the drain confirms -- applying a
        /// new drive letter or remote path mid-drain would make a retry try to unmount the WRONG
        /// mount point. Applied by <see cref="CompleteDrainAsync"/> the instant the drain confirms,
        /// before whatever comes next (a paced remount, an immediate re-enable, or eviction), so
        /// that next step already sees the new definition.</summary>
        public HostConfig? PendingConfig;
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
        Volatile.Write(ref startedFlag, 1);
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
            host.DeepProbeTimer?.Dispose();
            host.DeepProbeTimer = null;
            host.IdleUnmountTimer?.Dispose();
            host.IdleUnmountTimer = null;
        }

        started = false;
        Volatile.Write(ref startedFlag, 0);
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
                ConsecutiveDeepProbeFailures = h.ConsecutiveDeepProbeFailures,
                ConsecutiveIdleFailures = h.Backoff.ConsecutiveFailures,
                LastTransitionUtc = h.LastTransitionUtc,
                LastTransitionTrigger = h.LastTransitionTrigger,
                UserParked = h.UserParked,
                MountUnavailableReason = mountingAvailability.IsAvailable ? null : mountingAvailability.Reason,
                MountUnavailableCause = mountingAvailability.IsAvailable ? null : mountingAvailability.Cause,
                ConsecutiveMountFailures = h.ConsecutiveMountFailures,
                LastMountFailureReason = h.LastMountFailureReason,
                UploadFailureCount = h.State == MountState.Mounted ? (int)Math.Min(h.UploadFailureCount, int.MaxValue) : 0,
            })
            .ToList();

    /// <inheritdoc/>
    public IReadOnlyList<MountTransitionEntry> GetTransitionHistory() =>
        // ToArray() is ConcurrentQueue<T>'s documented moment-in-time snapshot (see
        // transitionHistory's remarks) -- safe to call from any thread, no lock needed. The queue
        // itself is oldest-first (FIFO); reverse into newest-first per this method's documented
        // contract on IMountSupervisor.
        transitionHistory.ToArray().Reverse().ToList();

    public Task RequestMountAsync(string hostKey, CancellationToken cancellationToken = default) =>
        EnqueueAndWait(async ct =>
        {
            var host = RequireHost(hostKey);

            // bs-oly (maintainer decision 2026-11-01): while the system is suspended EVERY mount
            // request is refused, whatever the host's state -- Ready, Unreachable, Disabled, parked.
            // The TryBeginMountAsync gate (bs-bv0, Invariant I8) already stops the mount itself, but
            // it does so with only a log line, which is the silent no-op ADR-014 rule 8 forbids for a
            // user click. Checked FIRST, before anything below can change host state: the refusal
            // must not clear UserParked (the user did not get the remount they asked for, so their
            // earlier "take it down" still stands) and must not issue a probe. It precedes the
            // mounting-availability check because suspend is the more specific, self-clearing cause.
            // MountingSuspended, not the flag alone (ADR-023): a click that was queued BEFORE a suspend
            // reaches this line while the suspend is still waiting its turn, and mounting then would be
            // the very thing the pending suspend is about to undo.
            if (MountingSuspended)
            {
                throw new MountRequestRefusedException(hostKey, "the system is suspended");
            }

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
                // The suspended refusal (bs-oly) has already happened above, before UserParked was
                // touched, so this path never probes around a suspend (Invariant I8 in reverse).

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

            // ADR-022 decision 5: a user unmount clears the remount record, whatever state the host is
            // in. The case that matters is a click while the suspend drain is already running (the
            // host is Draining, so the request below is ignored): the user said "take it down", and
            // the drain taking it down must not be undone by a remount at wake.
            host.RemountAfterResume = false;

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

    /// <summary>
    /// ADR-023 (bs-4in, bs-7d7): the PROLOGUE runs on the caller's thread -- in production
    /// <c>Microsoft.Win32.SystemEvents</c>' single notification thread -- and does exactly three
    /// things, in this order: it captures the arrival time, it closes the I8 gate
    /// (<see cref="pendingSuspends"/>), and it cancels the recurring tick that is running
    /// (<see cref="PreemptRunningTick"/>). Only then is the suspend queued. It never throws and
    /// never blocks: a fault in it is logged, because an exception on that thread kills the process.
    /// </summary>
    /// <remarks>
    /// The order is load-bearing. Count before cancel, so a tick that starts after the cancel finds
    /// the counter and cancels itself; cancel before queue, so the tick is already stopping while the
    /// suspend waits for the channel. The arrival time is taken first so the suspend budget counts
    /// from the moment suspend ARRIVES (rule 5), not from when the channel dequeues it.
    /// <para>If the supervisor loop is dead the counter is never decremented. That is harmless:
    /// nothing mounts without the loop, and a closed gate on a dead loop is the right answer.</para>
    /// </remarks>
    public Task SuspendAsync(CancellationToken cancellationToken = default)
    {
        var arrivalUtc = timeProvider.GetUtcNow();
        Interlocked.Increment(ref pendingSuspends);
        PreemptRunningTick();
        return EnqueueAndWait(ct => RunSuspendAsync(arrivalUtc, ct), cancellationToken);
    }

    /// <summary>
    /// Cancels the recurring tick that is in flight, if any (ADR-023). Runs on the caller's thread
    /// of <see cref="SuspendAsync"/>, so it must not throw and must not run callbacks inline:
    /// <see cref="CancellationTokenSource.CancelAsync"/>, never <c>Cancel</c> (which executes the
    /// registered callbacks -- here, the linked sources that release a hung probe or rc call -- on
    /// the calling thread, i.e. on <c>SystemEvents</c>' thread). <c>CancelAsync</c> flips
    /// <c>IsCancellationRequested</c> synchronously and queues the callbacks to the thread pool.
    /// The returned task is observed, not awaited: a throwing callback faults it, and that is logged.
    /// </summary>
    private void PreemptRunningTick()
    {
        try
        {
            var tick = Volatile.Read(ref runningTick);
            if (tick is null)
            {
                return;
            }

            var cancellation = tick.CancelAsync();
            _ = cancellation.ContinueWith(
                faulted => logger.LogWarning(
                    faulted.Exception?.GetBaseException(),
                    "Cancelling the running tick for a pending suspend faulted in a cancellation callback; " +
                    "the suspend proceeds regardless (ADR-023)"),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch (ObjectDisposedException)
        {
            // The tick finished and disposed its source between the read and the cancel: it is
            // already over, which is what the cancel was for.
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "Preempting the running tick for a pending suspend failed; the suspend proceeds regardless (ADR-023)");
        }
    }

    private async Task RunSuspendAsync(DateTimeOffset arrivalUtc, CancellationToken ct)
    {
        // First thing, before anything can yield: from here on `suspended` carries the gate
        // (set a few lines below, with no await in between), so the gate never opens in the gap.
        Interlocked.Decrement(ref pendingSuspends);

        // ADR-022's second amendment (bs-ane): a record whose time was already up is dropped now, not
        // carried across the sleep; the rest have their deadline cleared, because the wait limit
        // counts from a resume (ResumeAsync sets it again) and nothing may expire while asleep.
        DropExpiredRemountRecords("suspend");
        foreach (var waiting in hosts.Values.Where(h => h.RemountAfterResume))
        {
            waiting.RemountDeadlineUtc = null;
        }

        suspended = true;

        // Taken BEFORE the sweep below: a host that this very call moves into Draining already
        // carries the suspend budget (DrainCause.Suspend) and must not be treated as "already
        // draining for an ordinary reason".
        var alreadyDraining = hosts.Values
            .Where(h => h.State == MountState.Draining && !h.DrainEscalated)
            .ToList();

        // bs-5o4 (decided 2026-11-02: "unmount all, then verify"): every host goes to Draining first,
        // with no rc call, and DrainAllForSuspendAsync then sends every mount/unmount before it
        // verifies any. Draining one host completely before starting the next let a stuck rcd hold
        // host N's request behind hosts 1..N-1 (about 80 s each), so later hosts were not unmounted
        // before the machine slept (I8).
        var newlyDraining = new List<HostRuntime>();
        foreach (var host in hosts.Values.Where(h => h.State is MountState.Mounting or MountState.Mounted).ToList())
        {
            // ADR-022 decision 1: remember that the user had this on-demand drive up, before the drain
            // takes it down. Set here, not in BeginDrainAsync, so a drain for any other cause (probe
            // failure, idle timeout, repair, config change) never sets it (decision 6). A host already
            // Draining or Disabled when suspend arrives is not in this list: it was not up. A second
            // suspend simply sets an already-set record again.
            if (host.Config.Mount.Mode == MountMode.OnDemand)
            {
                host.RemountAfterResume = true;
                host.RemountDeadlineUtc = null;
            }

            if (EnterDraining(host, "system suspend", DrainCause.Suspend, startedUtc: arrivalUtc))
            {
                newlyDraining.Add(host);
            }
        }

        await DrainAllForSuspendAsync(newlyDraining, ct).ConfigureAwait(false);

        // bs-u4a item 4: a host already Draining for an ordinary reason keeps its drain cause (so a
        // user unmount still parks it, a removed host is still evicted) but must not keep the 10 s
        // ordinary escalation deadline: that could fall after the machine has gone to sleep. It gets
        // the suspend budget instead -- shortened only, never lengthened.
        foreach (var host in alreadyDraining)
        {
            await ApplySuspendBudgetToDrainingHostAsync(host, arrivalUtc, ct).ConfigureAwait(false);
        }
    }

    private async Task ApplySuspendBudgetToDrainingHostAsync(HostRuntime host, DateTimeOffset arrivalUtc, CancellationToken ct)
    {
        var before = EffectiveDrainTimeout(host);

        // Counted from the suspend's ARRIVAL (rule 5, bs-7d7), not from now: the suspend may have
        // waited in the channel behind another action, and the machine does not wait for the channel.
        var suspendDeadline = arrivalUtc + TimeSpan.FromSeconds(global.SuspendUnmountTimeoutSeconds);
        if (host.SuspendEscalationDeadlineUtc is not { } existing || suspendDeadline < existing)
        {
            host.SuspendEscalationDeadlineUtc = suspendDeadline;
        }

        var after = EffectiveDrainTimeout(host);
        if (after >= before)
        {
            // The drain's own deadline is already at or before the suspend deadline: nothing to
            // shorten, and the retry timer already armed is aimed at it.
            return;
        }

        logger.LogInformation(
            "Host {HostKey} was already draining ({Reason}) when suspend arrived; its forced-unmount deadline is " +
            "shortened from {Before} to {After} after the drain began (the suspend budget)",
            host.Key, host.DrainReason, before, after);

        // The retry timer already armed was aimed at the old, later deadline. Supersede it with an
        // attempt now: it re-issues the unmount and re-verifies (exactly what a drain step does),
        // then re-arms the next check AT the new deadline.
        host.ProbeTimer?.Dispose();
        host.ProbeTimer = null;
        await AttemptDrainStepAsync(host, ct).ConfigureAwait(false);
    }

    public Task ResumeAsync(CancellationToken cancellationToken = default) => EnqueueAndWait(async ct =>
    {
        suspended = false;

        // ADR-022's second amendment (bs-ane): every record starts its wait from THIS resume, so a
        // second sleep restarts the clock for a record that survived the first. Set before the
        // re-derivation below so nothing on this resume's own path can see a record without one.
        var remountDeadline = timeProvider.GetUtcNow() + RemountRecordWaitLimit;
        foreach (var waiting in hosts.Values.Where(h => h.RemountAfterResume))
        {
            waiting.RemountDeadlineUtc = remountDeadline;
        }

        // ADR-017: resume does not trust its own Mounted state -- it is at least as strong as a
        // network change, never weaker (an earlier asymmetry here is exactly what let bs-brv look
        // correct in review). This reconciles against mount/listmounts and force-probes (shallow
        // *and* deep) every host still Mounted, BEFORE the per-state switch below runs -- so a host
        // found here is either no longer Mounted (handled, falls through the switch's default case)
        // or has already received its one forced shallow probe (must not be probed again below).
        await ReDeriveAfterTransitionAsync("resume", ct).ConfigureAwait(false);

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

                    // ADR-022 amendment: a remount record kept across an earlier gate (a second sleep
                    // while mounting was unavailable) is resolved by this fresh probe.
                    await ResolveRemountRecordAsync(host, "resume: kept remount record (ADR-022)", ct)
                        .ConfigureAwait(false);

                    // bs-bv0: a persistent host can be sitting in Ready un-mounted because the suspend
                    // gate in TryBeginMountAsync held its auto-mount back (it came up while suspended).
                    // A Ready host's probe success is an in-place probe -- it does not re-run
                    // OnEnteredReadyAsync -- so without this it would stay Ready forever. The forced
                    // probe above is the fresh evidence ADR-017 requires (a Ready that survived a
                    // transition is not trusted); only if it left the host Ready do we mount, and
                    // TryBeginMountAsync still deep-probes it first (I1). Parked and on-demand hosts
                    // are deliberately skipped, exactly as in OnEnteredReadyAsync.
                    if (host.State == MountState.Ready
                        && host.Config.Mount.Mode == MountMode.Persistent
                        && !host.UserParked)
                    {
                        await TryBeginMountAsync(host, "resume: persistent host held back while suspended", ct)
                            .ConfigureAwait(false);
                    }

                    break;
                case MountState.Unreachable:
                    // NO tier split here, deliberately. ADR-014 splits the RECURRING ladder only.
                    // Its stated cost -- "makes the UI slow and the auth logs noisy" -- is an
                    // argument about continuous traffic, every rung, forever; a resume is a rare,
                    // bounded event producing one probe per host. Suppressing it would leave an
                    // on-demand host displaying a stale Unreachable after the machine has woken
                    // into a changed network, which is exactly the sluggishness §4's Backoff
                    // section says the reset exists to eliminate; OPERATIONS.md T1 (overnight
                    // sleep) and T2 (network interruption) are the acceptance tests for it.

                    host.Backoff = BackoffState.Initial;
                    await ForceImmediateIdleProbeAsync(host, "resume: bypass backoff", ct).ConfigureAwait(false);
                    await ResolveRemountRecordAsync(host, "resume: kept remount record (ADR-022)", ct)
                        .ConfigureAwait(false);
                    break;
                default:
                    // Mounted was already handled above by ReDeriveAfterTransitionAsync (ADR-017),
                    // including its one forced shallow probe -- probing it again here would violate
                    // "one forced shallow probe, not two". A host that drained during that
                    // re-derivation is no longer Mounted, so it does not land here either; it will
                    // be picked up as Disabled the NEXT time resume or startup runs, same as any
                    // other drain. Mounting cannot practically be observed here: every transition out
                    // of Ready into Mounting runs to completion (Mounted or Draining) within the
                    // single channel continuation that started it (see the class remarks on global
                    // serialisation), so no other command -- including this one -- is ever dequeued
                    // mid-Mounting. Draining is left alone to keep retrying on its own schedule.
                    break;
            }
        }
    }, cancellationToken);

    public Task NetworkChangedAsync(CancellationToken cancellationToken = default) => EnqueueAndWait(async ct =>
    {
        // ADR-017: identical re-derivation to ResumeAsync -- reconcile against mount/listmounts,
        // then force-probe (shallow and deep) every host still Mounted, gated on rcd answering
        // core/version. This replaces the old shallow-only force-probe loop.
        await ReDeriveAfterTransitionAsync("network change", ct).ConfigureAwait(false);

        foreach (var host in hosts.Values.Where(h => h.State is MountState.Ready or MountState.Unreachable).ToList())
        {
            // No tier split -- same reasoning as ResumeAsync above. ADR-014 splits the recurring
            // ladder; a network change is one bounded probe per host, and it is the event T2 (network
            // interruption) turns on. An on-demand row left showing a stale Unreachable after the
            // link returns is worse than ADR-008's "unknown until acted on", because it is
            // confidently wrong rather than blank.
            host.Backoff = BackoffState.Initial;
            await ForceImmediateIdleProbeAsync(host, "network change: bypass backoff", ct).ConfigureAwait(false);
        }

        // ADR-022 amendment: the re-derivation above may have been the first time rcd answered since
        // it failed the gate at resume. A no-op unless a record is waiting and every gate is open.
        await SweepDeferredRemountsAsync("network change", ct).ConfigureAwait(false);
    }, cancellationToken);

    public Task RepairAllAsync(CancellationToken cancellationToken = default) => EnqueueAndWait(async ct =>
    {
        if (MountingSuspended)
        {
            logger.LogWarning("Repair (unmount all and re-probe) refused: the system is suspended");
            throw new RepairRefusedException("the system is suspended");
        }

        var mounted = hosts.Values.Where(h => h.State is MountState.Mounting or MountState.Mounted).ToList();
        logger.LogInformation(
            "Repair: unmount all and re-probe requested; draining {Count} mounted host(s), then re-probing every enabled host. " +
            "Hosts are not parked by this (a host the user parked earlier stays parked)",
            mounted.Count);

        // 1. Drain everything that is mounted. Each drain is the ordinary one: unmount, then verify
        //    against listmounts; a drain that cannot be confirmed stays Draining and retries on its
        //    own schedule, and the host only reaches Disabled once the unmount is confirmed.
        var drained = new HashSet<HostRuntime>();
        foreach (var host in mounted)
        {
            await BeginDrainAsync(host, "repair: unmount all and re-probe", DrainCause.Repair, ct).ConfigureAwait(false);
            drained.Add(host);
        }

        // 2. A host whose drain confirmed is re-enabled by CompleteDrainAsync (queued behind this
        //    command, so every drain above has finished first). Everything else that is enabled is
        //    re-probed now, with its backoff reset.
        foreach (var host in hosts.Values.Where(h => h.AdministrativelyEnabled && !drained.Contains(h)).ToList())
        {
            switch (host.State)
            {
                case MountState.Ready:
                case MountState.Unreachable:
                    // No tier split, as for ResumeAsync/NetworkChangedAsync: the user asked, and it is
                    // one bounded probe per host. A persistent host that was unreachable and now
                    // answers goes Ready and mounts (unless parked) -- after this probe, never before.
                    host.Backoff = BackoffState.Initial;
                    await ForceImmediateIdleProbeAsync(host, "repair: re-probe", ct).ConfigureAwait(false);
                    break;

                case MountState.Disabled:
                    // Waiting out a failed-mount pacing timer. A repair is a fresh start: try now,
                    // from the first rung. A failure still paces the next attempt.
                    host.ProbeTimer?.Dispose();
                    host.ProbeTimer = null;
                    host.MountRetryBackoff = BackoffState.Initial;
                    await EnableHostAsync(host, "repair: re-probe", ct).ConfigureAwait(false);
                    break;

                default:
                    // Draining (started before this command): owns its own re-enable. Probing and
                    // Mounting/Mounted cannot be observed between channel continuations.
                    break;
            }
        }
    }, cancellationToken);

    /// <summary>How long a <see cref="NoteUserRequestedRcloneRestart"/> mark stays valid if no
    /// <see cref="OnRcloneRestartedAsync"/> consumes it (bs-z9e). A restart is bounded by the stop,
    /// port-guard and 15 s health timeouts plus remote provisioning, so two minutes is generous; the
    /// cost of longer is muting a real crash, so it is not longer.</summary>
    internal static readonly TimeSpan UserRestartMarkLifetime = TimeSpan.FromMinutes(2);

    /// <summary>UTC ticks at which the user-restart mark expires, or 0 for none. Written from the
    /// caller's thread, read on the channel thread, hence Volatile.</summary>
    private long userRestartMarkExpiresTicks;

    public void NoteUserRequestedRcloneRestart()
    {
        Volatile.Write(ref userRestartMarkExpiresTicks, (timeProvider.GetUtcNow() + UserRestartMarkLifetime).UtcTicks);
        logger.LogInformation(
            "Rclone restart requested by the user; drives dropped by the reconciliation that follows are a repair, not an unexpected unmount");
    }

    private bool UserRestartMarkActive()
    {
        var expires = Volatile.Read(ref userRestartMarkExpiresTicks);
        return expires != 0 && timeProvider.GetUtcNow().UtcTicks < expires;
    }

    public Task OnRcloneRestartedAsync(CancellationToken cancellationToken = default) =>
        EnqueueAndWait(async ct =>
        {
            // The mark covers exactly this reconciliation: cleared once it has actually run (a
            // listmounts failure leaves it for the next tick, which is still that restart's), so a
            // later crash is Automatic again.
            if (await ReconcileAsync(ct).ConfigureAwait(false))
            {
                Volatile.Write(ref userRestartMarkExpiresTicks, 0);
            }

            // ADR-022 amendment: the caller reports rcd healthy again, which is the "rcd answers
            // again" signal for a remount record held back by a failed core/version gate. Confirm it
            // with the same call the gate uses rather than taking the report on trust.
            if (!rcdAnswering)
            {
                await TryVerifyRcdAsync(ct).ConfigureAwait(false);
            }

            await SweepDeferredRemountsAsync("rclone restarted", ct).ConfigureAwait(false);
        }, cancellationToken);

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

                // ADR-022 amendment: an on-demand host holding a remount record (the user's drive,
                // which a suspend took down while mounting was unavailable) is re-probed and mounted
                // here. On-demand hosts without a record still need no sweep (see above).
                await SweepDeferredRemountsAsync("mounting became available", ct).ConfigureAwait(false);
            }
            else if (!availability.IsAvailable && wasAvailable)
            {
                logger.LogWarning("Mounting became unavailable: {Reason}", availability.Reason);
            }
        }, cancellationToken);

    /// <inheritdoc/>
    public Task ConfigChangedAsync(BosunConfig newConfig, CancellationToken cancellationToken = default) =>
        EnqueueAndWait(ct => ApplyConfigChangeAsync(newConfig, ct), cancellationToken);

    public async ValueTask DisposeAsync()
    {
        reconciliationTimer?.Dispose();
        foreach (var host in hosts.Values)
        {
            host.ProbeTimer?.Dispose();
            host.DeepProbeTimer?.Dispose();
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
    /// <paramref name="cancellationToken"/> fires. Only <paramref name="cancellationToken"/>
    /// being cancelled ends it cleanly (by throwing <see cref="OperationCanceledException"/>, as
    /// before); nothing a single action does -- including an <see cref="OperationCanceledException"/>
    /// that is NOT shutdown, such as an HTTP timeout -- can end it (see
    /// <see cref="ProcessActionAsync"/>). If it ever exits without shutdown having been requested
    /// that is logged at Critical, because from that moment every timer enqueues into a channel
    /// nobody reads (bs-x57).
    /// </summary>
    /// <remarks>
    /// Not exercised through the production hosting path by any test (CLAUDE.md forbids running the
    /// app from a worktree), but driven directly, with fakes, by <c>SupervisorLoopSurvivalTests</c>.
    /// The state-machine LOGIC every continuation runs is otherwise covered via
    /// <see cref="DrainAsync"/>.
    /// </remarks>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        SetLoopRunning(true);
        try
        {
            while (await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out var action))
                {
                    await ProcessActionAsync(action, cancellationToken).ConfigureAwait(false);
                }
            }

            // WaitToReadAsync only returns false once the channel's writer has completed
            // (DisposeAsync). Reaching here with the token un-cancelled means supervision stopped
            // for a reason other than shutdown.
            if (!cancellationToken.IsCancellationRequested)
            {
                logger.LogCritical(
                    "The mount supervisor loop exited without shutdown being requested (its command " +
                    "channel was completed); mounts are no longer managed until Bosun restarts");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("The mount supervisor loop is stopping: shutdown requested");
            throw;
        }
        catch (Exception ex)
        {
            // ProcessActionAsync swallows per-action failures, so reaching here means the loop
            // machinery itself failed. Log here, at the source, rather than trusting whichever
            // caller observes the returned Task to do it (bs-x57: the caller's own handler
            // swallowed this silently for 2.5 days).
            logger.LogCritical(
                ex, "The mount supervisor loop faulted without shutdown being requested; mounts are no " +
                "longer managed until Bosun restarts");
            throw;
        }
        finally
        {
            SetLoopRunning(false);

            // bs-6to / ADR-020 Decision 7: whatever the reason the loop ended -- shutdown, a fault,
            // the channel being completed -- nothing will ever read the channel again, so nothing
            // may be left waiting on it. Every pending EnqueueAndWait caller is failed (they used to
            // wait forever, which is what hung the UI on 2026-10-01), and later callers fail fast.
            MarkLoopDeadAndFailPending();
        }
    }

    /// <summary>Test-only synchronous pump: processes whatever is currently queued (including any
    /// follow-up work a processed item itself enqueues) and returns once the channel is empty.
    /// Never used in production -- see <see cref="RunAsync"/>. Goes through the same
    /// <see cref="ProcessActionAsync"/> as the production loop so a test cannot pass on behaviour
    /// the real loop does not have.</summary>
    internal async Task DrainAsync(CancellationToken cancellationToken = default)
    {
        while (channel.Reader.TryRead(out var action))
        {
            await ProcessActionAsync(action, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs ONE channel continuation such that nothing but genuine shutdown can escape it
    /// (bs-x57). The loop is the channel's single consumer: if an action's exception propagated
    /// out, the loop would end, every later timer would enqueue into a channel nobody reads, and
    /// every <see cref="EnqueueAndWait"/> caller would wait forever -- with no log line, because
    /// nothing is left running to write one.
    /// </summary>
    /// <remarks>
    /// "Genuine shutdown" is decided by <paramref name="ct"/> -- the supervisor's OWN token --
    /// never by the exception's type. An <see cref="OperationCanceledException"/> raised while
    /// <paramref name="ct"/> is NOT cancelled (an HTTP timeout surfaces as
    /// <see cref="TaskCanceledException"/>) is an ordinary failure and is logged and survived like
    /// any other.
    /// </remarks>
    private async Task ProcessActionAsync(Func<CancellationToken, Task> action, CancellationToken ct)
    {
        TouchLoopActivity();
        try
        {
            await action(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception processing a supervisor action; the loop continues");
        }
        finally
        {
            TouchLoopActivity();
        }
    }

    // ------------------------------------------------------------------------------------------
    // Loop liveness (bs-x57): read-only signals for a future watchdog. Deliberately just data --
    // nothing in this class acts on them.
    // ------------------------------------------------------------------------------------------

    private long lastLoopActivityTicks;
    private int loopRunning;
    private int startedFlag;
    private int loopExited;

    /// <summary>
    /// True from the moment <see cref="StartAsync"/> has begun until <see cref="StopAsync"/> has run:
    /// the supervisor is supposed to be doing work, so a quiet loop means something is wrong (an
    /// idle, never-started supervisor legitimately has no activity). A thread-safe mirror of the
    /// private <c>started</c> field, which is only touched on the loop. (bs-6to)
    /// </summary>
    public bool IsStarted => Volatile.Read(ref startedFlag) == 1;

    /// <summary>
    /// Pending <see cref="EnqueueAndWait"/> callers, so the loop's death can fail them (bs-6to). An
    /// entry is added before the command is queued and removed when it completes for any reason.
    /// </summary>
    private readonly ConcurrentDictionary<long, TaskCompletionSource> pendingCommands = new();
    private long nextPendingCommandId;

    /// <summary>True from the moment <see cref="RunAsync"/> starts until it exits for any reason
    /// (shutdown, fault, or channel completion). False before it has started. Safe to read from
    /// any thread.</summary>
    public bool IsLoopRunning => Volatile.Read(ref loopRunning) == 1;

    /// <summary>
    /// The injected clock's time when the loop last started, began an action, or finished an
    /// action (or exited), or got a reply from an rc call or a probe (bs-6to: so a long composite
    /// action keeps stamping). <see cref="DateTimeOffset.MinValue"/> until <see cref="RunAsync"/>
    /// has started. Safe to read from any thread.
    /// </summary>
    /// <remarks>
    /// A stale value only means "wedged" when interpreted against the work the loop is expected to
    /// have: while the supervisor is started, the reconciliation timer enqueues an action every
    /// <see cref="ReconciliationInterval"/>, so a healthy started loop touches this at least that
    /// often, plus the duration of the slowest single rc call or probe (bounded by the per-call
    /// timeouts in <see cref="RcloneClient"/>). An idle, never-started supervisor legitimately goes stale.
    /// </remarks>
    public DateTimeOffset LastLoopActivityUtc =>
        new(Volatile.Read(ref lastLoopActivityTicks), TimeSpan.Zero);

    private void TouchLoopActivity() =>
        Volatile.Write(ref lastLoopActivityTicks, timeProvider.GetUtcNow().UtcTicks);

    private void SetLoopRunning(bool isRunning)
    {
        TouchLoopActivity();
        Volatile.Write(ref loopRunning, isRunning ? 1 : 0);
    }

    /// <summary>Posts a fire-and-forget continuation (timer callbacks). <c>internal</c> only so
    /// loop-survival tests can inject an action that throws; production code reaches it solely
    /// through this class's own timers.</summary>
    internal void Enqueue(Func<CancellationToken, Task> action) => channel.Writer.TryWrite(action);

    // ------------------------------------------------------------------------------------------
    // Recurring ticks and their preemption by a suspend (ADR-023, bs-4in)
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Posts one of the five recurring timer callbacks -- the idle probe, the mounted shallow probe
    /// (with its VFS sample), the mounted deep probe, the idle-unmount fire and the reconciliation
    /// tick -- so that it runs under <see cref="RunTickAsync"/>. Nothing else is a tick: commands,
    /// drains and the mount-retry re-enable go through <see cref="Enqueue"/> and are never preempted.
    /// </summary>
    /// <param name="tick">Receives the supervisor's own token (shutdown) and the PREEMPTION token.
    /// They mean different things and must never be conflated: the first ending means the process is
    /// stopping; the second ending means a suspend arrived and this tick's result no longer matters.</param>
    private void EnqueueTick(Func<CancellationToken, CancellationToken, Task> tick) =>
        Enqueue(ct => RunTickAsync(tick, ct));

    /// <summary>
    /// Runs one recurring tick with a cancellation source that <see cref="SuspendAsync"/> can reach
    /// (ADR-023). The source is published before the tick starts and withdrawn when it ends, and a
    /// tick that starts while a suspend is already pending cancels itself first: so a suspend that
    /// arrives at any moment finds the tick cancelled, or the tick finds the suspend, never neither.
    /// </summary>
    private async Task RunTickAsync(Func<CancellationToken, CancellationToken, Task> tick, CancellationToken ct)
    {
        var preemption = new CancellationTokenSource();

        // Exchange, not a plain write: the full barrier pairs with the Increment in SuspendAsync
        // (publish, then read the counter; there, bump the counter, then read the published source).
        Interlocked.Exchange(ref runningTick, preemption);
        try
        {
            if (SuspendPending)
            {
                await preemption.CancelAsync().ConfigureAwait(false);
            }

            await tick(ct, preemption.Token).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.CompareExchange(ref runningTick, null, preemption);
            preemption.Dispose();
        }
    }

    /// <summary>
    /// Whether a recurring tick has been preempted by a suspend -- decided by the PREEMPTION TOKEN
    /// and nothing else (ADR-023). Never by the probe's outcome and never by an exception's type:
    /// <c>HostProbe</c> reports a cancelled probe as an outcome (<c>Cancelled</c>), not as an
    /// exception, and the handlers count every outcome other than <c>Success</c> as a failure, so
    /// reading the outcome would turn every suspend into an <see cref="DrainCause.Automatic"/> drain
    /// (and a toast). When this returns true the caller discards the result of the I/O it just did
    /// (a Success as well), changes no counter, state, VFS bookkeeping or flag, and re-arms its own
    /// timer. One Information line is logged here.
    /// </summary>
    private bool TickPreempted(CancellationToken preemption, string tick, string scope)
    {
        if (!preemption.IsCancellationRequested)
        {
            return false;
        }

        logger.LogInformation(
            "The {Tick} tick for {Scope} was preempted by a pending suspend (Invariant I8, ADR-023): its result is " +
            "discarded, no counter or state changed, and its timer is re-armed",
            tick, scope);
        return true;
    }

    private Task EnqueueAndWait(Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = Interlocked.Increment(ref nextPendingCommandId);

        // Order matters (bs-6to). Register as pending FIRST, then check whether the loop is already
        // dead, then queue. MarkLoopDeadAndFailPending sets the dead flag BEFORE it sweeps the
        // pending set, so a caller is always either swept (registered before the sweep) or sees the
        // flag (registered after it). There is no window in which a caller can wait on a loop that
        // is gone.
        pendingCommands[id] = completion;
        if (Volatile.Read(ref loopExited) == 1)
        {
            pendingCommands.TryRemove(id, out _);
            return Task.FromException(new SupervisorStoppedException());
        }

        // A caller that gives up (the exit deadline, a UI cancellation) must not stay blocked until
        // the loop happens to reach its command -- if the loop is wedged, that is never. The queued
        // command stays in the channel; it is harmless once its completion is already settled.
        var registration = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(
                static state =>
                {
                    var (tcs, token) = ((TaskCompletionSource, CancellationToken))state!;
                    tcs.TrySetCanceled(token);
                },
                (completion, cancellationToken))
            : default;

        _ = completion.Task.ContinueWith(
            settled =>
            {
                registration.Dispose();
                pendingCommands.TryRemove(id, out _);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

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

    /// <summary>
    /// Runs once, when <see cref="RunAsync"/> exits for any reason (bs-6to): closes the command
    /// channel and fails every command still waiting on it with <see cref="SupervisorStoppedException"/>.
    /// </summary>
    private void MarkLoopDeadAndFailPending()
    {
        // The flag first, then the channel, then the sweep -- see EnqueueAndWait for why that order.
        Volatile.Write(ref loopExited, 1);
        channel.Writer.TryComplete();

        var failed = 0;
        foreach (var (id, completion) in pendingCommands)
        {
            if (completion.TrySetException(new SupervisorStoppedException()))
            {
                failed++;
            }

            pendingCommands.TryRemove(id, out _);
        }

        if (failed > 0)
        {
            logger.LogWarning(
                "The supervisor loop stopped with {Count} command(s) still waiting; each was failed rather than left to wait forever",
                failed);
        }
    }

    /// <summary>
    /// Logs an rc failure that is retried on a timer (bs-qcs): in full the first time (with a stack
    /// only if the exception type is unexpected), as a short reminder once per ten minutes, and not
    /// at all in between. A change in the kind of failure (HTTP 401 to connection refused) is logged
    /// at once.
    /// </summary>
    private void LogRepeatedRcFault(string source, Exception failure, string message, string context)
    {
        var kind = RcFaultKinds.Kind(failure);
        logger.LogRepeatingFault(
            repeatingFaults, source, kind, LogLevel.Warning, RcFaultKinds.ForLog(failure),
            $"{message} ({kind}: {failure.Message})", context, $"{kind}: {failure.Message}");
    }

    private HostRuntime RequireHost(string hostKey) =>
        hosts.TryGetValue(hostKey, out var host)
            ? host
            : throw new KeyNotFoundException($"No configured host '{hostKey}'.");

    // ------------------------------------------------------------------------------------------
    // Transition enforcement (see class remarks)
    // ------------------------------------------------------------------------------------------

    private void SetState(HostRuntime host, MountState to, string trigger, DrainCause? drainCause = null)
    {
        var from = host.State;
        if (!AllowedTransitions.TryGetValue(from, out var allowed) || Array.IndexOf(allowed, to) < 0)
        {
            throw new InvalidOperationException(
                $"Illegal mount-state transition for host '{host.Key}': {from} -> {to} (trigger: {trigger}). " +
                "This violates the transition table in docs/ARCHITECTURE.md §4 and indicates a bug in " +
                "MountSupervisor, not a recoverable runtime condition.");
        }

        var timestamp = timeProvider.GetUtcNow();

        host.State = to;
        host.LastTransitionUtc = timestamp;
        host.LastTransitionTrigger = trigger;

        RecordTransition(host.Key, from, to, trigger, timestamp, drainCause);

        logger.LogInformation(
            "Host {HostKey} transitioned {From} -> {To} (trigger: {Trigger})", host.Key, from, to, trigger);
    }

    /// <summary>
    /// Appends to the bounded in-memory transition history (bs-ww9.2) and trims from the front
    /// once <see cref="TransitionHistoryCapacity"/> is exceeded.
    /// </summary>
    /// <remarks>
    /// Called from exactly two places, deliberately -- <b>do not "simplify" this back to a single
    /// call from <see cref="SetState"/> alone.</b>
    /// <list type="number">
    /// <item><see cref="SetState"/> itself, for every ordinary transition.</item>
    /// <item>The one documented <c>SetState</c> bypass in
    /// <see cref="AdoptOrClearExistingMountsAsync"/> (crash-recovery adoption of an
    /// already-mounted host at startup). That bypass exists because there is no meaningful "from"
    /// state to validate against the transition table for a runtime object that was just
    /// constructed -- see its own remarks -- but it is still a REAL, logged transition
    /// (<c>Disabled -&gt; Mounted</c>, trigger "startup: adopted existing mount"), and it is
    /// specifically the one docs/OPERATIONS.md's T6 manual test (crash recovery) exercises. Someone
    /// opening the window after Bosun silently adopted a surviving mount on restart is exactly the
    /// moment this history exists to explain (ADR-018); if this call were removed, the history
    /// would have a blind spot at precisely that moment even though the log file would not.</item>
    /// </list>
    /// Both call sites already only ever run on the single channel-processing thread (see the class
    /// remarks), so this needs no lock -- see <see cref="transitionHistory"/>'s remarks for why the
    /// backing collection is a <see cref="ConcurrentQueue{T}"/> anyway (it is the read side, not the
    /// write side, that needs it).
    /// </remarks>
    private void RecordTransition(
        string hostKey, MountState from, MountState to, string trigger, DateTimeOffset timestampUtc,
        DrainCause? drainCause = null)
    {
        transitionHistory.Enqueue(new MountTransitionEntry
        {
            TimestampUtc = timestampUtc,
            HostKey = hostKey,
            From = from,
            To = to,
            Trigger = trigger,
            DrainCause = drainCause,
        });

        while (transitionHistory.Count > TransitionHistoryCapacity && transitionHistory.TryDequeue(out _))
        {
            // Trim from the front (FIFO) until back within capacity. The loop condition, not an
            // `if`, matters only in the pathological case of capacity dropping between calls,
            // which cannot happen since it is a compile-time constant -- kept as a loop anyway so
            // this reads correctly as "trim to capacity" rather than "trim by one".
        }
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

        if (SuspendPending)
        {
            // ADR-023 / Invariant I8: an enable is a probe towards a mount, and a suspend is already on
            // its way. Before SetState(Probing), so no transition is logged and no probe is spent; the
            // host stays Disabled and ResumeAsync enables every enabled Disabled host. Probing is not
            // preempted once it has started (no Probing -> Disabled edge exists, and none is added).
            logger.LogInformation(
                "Not enabling {HostKey} ({Trigger}): a suspend is pending (Invariant I8); staying Disabled -- resume " +
                "enables it",
                host.Key, trigger);
            return;
        }

        // ADR-022 decisions 2 and 3: the remount record belongs to THIS probe, the first one after the
        // suspend drain. A failed probe drops it (below). A successful one hands it to
        // OnEnteredReadyAsync -> ResolveRemountRecordAsync, which mounts, or, under the 2026-11-01
        // amendment, KEEPS it while something local is in the way (mounting unavailable, rcd not
        // answering core/version). Only an on-demand host honours it: a mode change clears it
        // already, so the mode test is belt and braces for a persistent host, which mounts itself.
        var hadRemountRecord = host.RemountAfterResume && host.Config.Mount.Mode == MountMode.OnDemand;

        SetState(host, MountState.Probing, trigger);

        var result = await ProbeShallowSafelyAsync(
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

            if (hadRemountRecord)
            {
                // A host-side reason (the host did not answer): the amendment does not touch this.
                host.RemountAfterResume = false;
                logger.LogInformation(
                    "Not remounting {HostKey} after resume: its first post-resume probe failed ({Outcome}); the " +
                    "record is dropped and the host stays unmounted until the user mounts it (ADR-022)",
                    host.Key, result.Outcome);
            }
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
        else if (host.RemountAfterResume)
        {
            // ADR-022 decision 2: an on-demand drive the user had up before the suspend. The probe that
            // just put the host in Ready is the fresh evidence I1 asks for, and TryBeginMountAsync
            // still deep-probes before mount/mount, exactly as for a click. Under the amendment, a
            // local gate that is closed (mounting unavailable, rcd not answering, suspended) keeps
            // the record and the host rests in Ready until SweepDeferredRemountsAsync re-probes it.
            await ResolveRemountRecordAsync(host, "remount after resume: user-mounted before suspend (ADR-022)", ct)
                .ConfigureAwait(false);
            if (host.State != MountState.Ready)
            {
                return;
            }
        }

        ArmIdleProbeTimer(host);
    }

    /// <summary>
    /// ADR-022 and its 2026-11-01 amendment (bs-hrg): decides what becomes of a remount record for a
    /// host that has JUST had a fresh shallow probe. The caller supplies the probe; this method never
    /// probes (so a Ready host is re-probed in place, rule 10, never through <c>Probing</c>).
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>A local gate is closed -- the system is suspended, mounting is unavailable
    /// (<see cref="mountingAvailability"/>), or <c>rclone rcd</c> did not answer <c>core/version</c>
    /// (<see cref="rcdAnswering"/>): the record is KEPT and nothing else happens. These are
    /// properties of this machine, not of the host, so they say nothing about whether the user still
    /// wants the drive. <see cref="SweepDeferredRemountsAsync"/> picks the record up when they clear.</item>
    /// <item>Gates open and the host is <see cref="MountState.Unreachable"/>: the record is dropped.
    /// The host is the problem, and ADR-022 decision 3 stands: nothing mounts it later, on whatever
    /// network the machine has moved to.</item>
    /// <item>Gates open and the host is <see cref="MountState.Ready"/>: the record is spent and the
    /// host mounts exactly as a click would, through <see cref="TryBeginMountAsync"/> (Ready only, deep
    /// probe first, I1). That attempt is not retried if it fails while mounting is available.</item>
    /// </list>
    /// </remarks>
    private async Task ResolveRemountRecordAsync(HostRuntime host, string trigger, CancellationToken ct)
    {
        if (!host.RemountAfterResume)
        {
            return;
        }

        if (host.Config.Mount.Mode != MountMode.OnDemand)
        {
            host.RemountAfterResume = false;
            return;
        }

        // Before the gate test, and ahead of every other outcome: an expired record never mounts,
        // whichever route brought it here (bs-ane).
        if (DropRemountRecordIfExpired(host, trigger))
        {
            return;
        }

        if (MountingSuspended || !mountingAvailability.IsAvailable || !rcdAnswering)
        {
            logger.LogInformation(
                "Keeping the remount record for {HostKey} ({Trigger}): {Gate}; the host will be re-probed once " +
                "that clears and mounted if it is still reachable (ADR-022 amendment)",
                host.Key,
                trigger,
                MountingSuspended ? "the system is suspended"
                    : !mountingAvailability.IsAvailable ? $"mounting is unavailable ({mountingAvailability.Reason})"
                    : "rclone rcd did not answer core/version");
            return;
        }

        if (host.State == MountState.Unreachable)
        {
            host.RemountAfterResume = false;
            logger.LogInformation(
                "Dropping the remount record for {HostKey} ({Trigger}): the host did not answer its probe, so it " +
                "stays unmounted until the user mounts it (ADR-022)",
                host.Key, trigger);
            return;
        }

        if (host.State == MountState.Ready)
        {
            await TryBeginMountAsync(host, trigger, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// ADR-022 amendment (bs-hrg): a gate that kept a remount record has cleared, so every on-demand
    /// host still holding one is probed once more, in place, and then resolved
    /// (<see cref="ResolveRemountRecordAsync"/>): <c>Ready</c> mounts, anything else drops the record.
    /// A no-op while any gate is still closed, so a host is never probed for a decision that cannot
    /// be taken. Called from every place a gate can open: <see cref="SetMountingAvailabilityAsync"/>,
    /// <see cref="OnRcloneRestartedAsync"/>, <see cref="ReconcileTickAsync"/>, and
    /// <see cref="NetworkChangedAsync"/>.
    /// </summary>
    private async Task SweepDeferredRemountsAsync(string trigger, CancellationToken ct)
    {
        // First, and before the gates: an expired record is dropped without spending a probe on it,
        // and even when a gate is still closed (bs-ane).
        DropExpiredRemountRecords(trigger);

        if (MountingSuspended || !mountingAvailability.IsAvailable || !rcdAnswering)
        {
            return;
        }

        foreach (var host in hosts.Values
            .Where(h => h.RemountAfterResume
                && h.AdministrativelyEnabled
                && h.Config.Mount.Mode == MountMode.OnDemand
                && h.State is MountState.Ready or MountState.Unreachable)
            .ToList())
        {
            host.Backoff = BackoffState.Initial;
            await ForceImmediateIdleProbeAsync(host, $"{trigger}: re-probe before the deferred remount (ADR-022)", ct)
                .ConfigureAwait(false);
            await ResolveRemountRecordAsync(host, $"deferred remount after {trigger} (ADR-022)", ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>ADR-022's second amendment (2026-11-01, bs-ane): whether <paramref name="host"/>'s
    /// remount record has waited out <see cref="RemountRecordWaitLimit"/> since the latest resume.
    /// The boundary is inclusive: at exactly the deadline the record is expired. A record with no
    /// deadline (the system is suspended) never is.</summary>
    private bool RemountRecordExpired(HostRuntime host) =>
        host.RemountAfterResume
        && host.RemountDeadlineUtc is { } deadline
        && timeProvider.GetUtcNow() >= deadline;

    /// <summary>Drops <paramref name="host"/>'s remount record if it has expired, with an Info log
    /// line. The host's state is left alone: it keeps resting as an on-demand host normally does, and
    /// a user click mounts it as usual. Returns whether it dropped one.</summary>
    private bool DropRemountRecordIfExpired(HostRuntime host, string trigger)
    {
        if (!RemountRecordExpired(host))
        {
            return false;
        }

        host.RemountAfterResume = false;
        host.RemountDeadlineUtc = null;
        logger.LogInformation(
            "Dropping the remount record for {HostKey} ({Trigger}): it waited {Limit} after the resume " +
            "without the obstacle clearing, so the host is not remounted automatically; a click still " +
            "mounts it (ADR-022 second amendment)",
            host.Key, trigger, RemountRecordWaitLimit);
        return true;
    }

    /// <summary>No I/O and no timers: a walk over the hosts, called from the places a record can be
    /// resolved or noticed (the sweep, the 30 s reconciliation tick, suspend) so an expired one does
    /// not linger in memory or cost a <c>core/version</c> call.</summary>
    private void DropExpiredRemountRecords(string trigger)
    {
        foreach (var host in hosts.Values.Where(RemountRecordExpired).ToList())
        {
            DropRemountRecordIfExpired(host, trigger);
        }
    }

    /// <summary>Asks <c>rclone rcd</c> for <c>core/version</c> and records the answer in
    /// <see cref="rcdAnswering"/>. Only for the paths that wait on a failed ADR-017 gate.</summary>
    private async Task<bool> TryVerifyRcdAsync(CancellationToken ct, CancellationToken preemption = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, preemption);
        try
        {
            await rcloneClient.GetVersionAsync(linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "rclone rcd still does not answer core/version");
            return false;
        }

        // A reply that a suspend has overtaken is discarded (ADR-023): rcdAnswering is not touched.
        if (TickPreempted(preemption, "reconciliation", "all hosts"))
        {
            return false;
        }

        rcdAnswering = true;
        return true;
    }

    /// <summary>The recurring shallow-probe check while idle (docs/ARCHITECTURE.md §3: "the
    /// recurring liveness check while a host is Ready/Unreachable/Mounted"). Deliberately does
    /// NOT revisit <see cref="MountState.Probing"/> -- that state is reserved for the one-shot
    /// initial check on "enable" (see <see cref="EnableHostAsync"/> and the delivery report's note
    /// on this reading of docs/ARCHITECTURE.md §4).</summary>
    /// <summary>The idle probe's TIMER callback (ADR-023): the tick wrapper around
    /// <see cref="HandleIdleProbeDueAsync"/>. Adds the one rule that is specific to a tick and not to
    /// the forced probes of resume, network change and "retry now", which share the handler: a
    /// tick that fires while the system is suspended does nothing (D5, matching
    /// <see cref="OnMountRetryElapsedAsync"/>). Its timer is still re-armed, so a host never ends
    /// up with nothing left to probe it (ADR-011, Invariant I2); resume probes every such host at
    /// once anyway.</summary>
    private async Task HandleIdleProbeTickAsync(HostRuntime host, CancellationToken ct, CancellationToken preemption)
    {
        if (host.State is not (MountState.Ready or MountState.Unreachable))
        {
            return;
        }

        if (suspended)
        {
            logger.LogInformation(
                "Idle probe tick for {HostKey} skipped: the system is suspended; the host is probed again on resume " +
                "(ADR-023, Invariant I8)", host.Key);
            ArmIdleProbeTimer(host);
            return;
        }

        await HandleIdleProbeDueAsync(host, "periodic idle probe", ct, preemption).ConfigureAwait(false);
    }

    private async Task HandleIdleProbeDueAsync(
        HostRuntime host, string trigger, CancellationToken ct, CancellationToken preemption = default)
    {
        if (host.State is not (MountState.Ready or MountState.Unreachable))
        {
            // Stale timer: state moved on (e.g. into Mounting) before this fired.
            return;
        }

        if (TickPreempted(preemption, "idle probe", host.Key))
        {
            ArmIdleProbeTimer(host);
            return;
        }

        var result = await ProbeShallowSafelyAsync(
            host.Config.Hostname, host.Config.Port, ProbeTimeout(), ct, preemption).ConfigureAwait(false);

        if (TickPreempted(preemption, "idle probe", host.Key))
        {
            // Whatever the probe said, Success included, is discarded: no Backoff change, no Ready or
            // Unreachable transition, no auto-mount. The suspend is about to drain everything anyway.
            ArmIdleProbeTimer(host);
            return;
        }

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
            () => EnqueueTick((ct, preemption) => HandleIdleProbeTickAsync(host, ct, preemption)));
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

        if (MountingSuspended)
        {
            // bs-bv0 / Invariant I8: the suspend gate. This is the ONLY method that moves a host into
            // Mounting and the only caller of mount/mount, so refusing here covers every route in --
            // the persistent auto-mount in OnEnteredReadyAsync (whether the host arrived at Ready by a
            // periodic probe, a retry-now, a network change or a config change), a user mount request
            // against a Ready host, the mounting-became-available sweep, and a tier change to
            // persistent -- including any path added later. A mount created after the suspend handler
            // has run survives the sleep (or appears before ResumeAsync has re-derived anything),
            // which is the exact thing I8 exists to prevent.
            //
            // "Suspended" here is MountingSuspended: the flag the suspend ACTION sets, OR a suspend
            // that has been called and not yet begun (ADR-023). Without the second half, anything
            // queued ahead of the suspend -- a click, a tick that auto-mounts, a resume's sweep --
            // would still mount in the time the suspend spends waiting its turn.
            //
            // The host stays Ready and nothing is lost: ResumeAsync clears the flag, force-probes
            // every Ready host afresh (ADR-017), and mounts the persistent, non-parked ones that come
            // back still Ready. No new transition; no deep probe, no rc call, no SetState.
            logger.LogInformation(
                "Not mounting {HostKey} ({Trigger}): the system is suspended or a suspend is pending (Invariant I8); " +
                "staying {State} -- a persistent host is mounted by resume once a fresh probe passes",
                host.Key, trigger, host.State);
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

        // ADR-022: the remount record is spent by the mount attempt itself, however it ends. A
        // remount whose own deep probe or mount/mount fails while mounting IS available is not
        // retried, and a click that mounts the host while a record waits supersedes the record.
        host.RemountAfterResume = false;

        SetState(host, MountState.Mounting, trigger);
        host.ProbeTimer?.Dispose();
        host.ProbeTimer = null;

        var deep = await ProbeDeepSafelyAsync(host.Key, ProbeTimeout(), ct).ConfigureAwait(false);
        if (deep.Outcome != DeepProbeOutcome.Success)
        {
            logger.LogWarning(
                "Deep probe failed for {HostKey} while entering Mounting ({Outcome}: {Detail}); draining rather " +
                "than mounting (Invariant I1 -- deep probe failure never reaches Mounted)",
                host.Key, deep.Outcome, deep.Detail);
            host.MountRetryBackoff = host.MountRetryBackoff.RecordFailure();
            // Detail, not just Outcome. Outcome alone is the enum -- "Failed" -- which reaches the
            // window as "last error: deep probe failed entering Mounting: Failed" and tells the
            // user nothing they can act on. Detail carries the actual cause from rclone, e.g.
            // "failed to parse private key file: ssh: no key found", which names the fix. That
            // difference is the whole of ADR-012 Decision 3: a degraded state must explain itself,
            // or the user reads a log to find out what a dialog could have told them.
            var deepProbeFailureReason = string.IsNullOrWhiteSpace(deep.Detail)
                ? $"deep probe failed entering Mounting: {deep.Outcome}"
                : $"deep probe failed entering Mounting: {deep.Detail}";
            host.ConsecutiveMountFailures++;
            host.LastMountFailureReason = deepProbeFailureReason;
            await BeginDrainAsync(host, deepProbeFailureReason, DrainCause.MountFailure, ct)
                .ConfigureAwait(false);
            return;
        }

        if (MountingSuspended)
        {
            // ADR-023 decision D1 / Invariant I8: the deep probe is not preempted, so a suspend can
            // arrive while it runs. The mount is the one thing that must not follow it -- a drive
            // created now would be torn down at once by the suspend, or would survive the sleep if
            // that teardown runs out of budget. Checked AFTER the deep probe's failure branch, so a
            // genuine deep-probe failure still wins and is still a mount failure; checked BEFORE
            // mount/mount, which once sent is never cancelled.
            //
            // The mount is abandoned on the existing Mounting -> Draining edge with the suspend's own
            // cause. It is not a mount failure: nothing is counted, MountRetryBackoff and
            // LastMountFailureReason are untouched, and the window does not report a failed mount.
            // An on-demand host is a drive the user asked for (a click, or an ADR-022 remount that
            // this attempt just spent), so it keeps or gets the ADR-022 record; the suspend action
            // that follows cannot set it, because by then this host is no longer Mounting/Mounted.
            // A persistent host needs no record: resume enables it and its tier mounts it.
            if (host.Config.Mount.Mode == MountMode.OnDemand)
            {
                host.RemountAfterResume = true;
                host.RemountDeadlineUtc = null;
            }

            logger.LogInformation(
                "Abandoning the mount of {HostKey} ({Trigger}): its deep probe passed but a suspend is pending " +
                "(Invariant I8); mount/mount is not sent. Draining as a suspend, not a mount failure (ADR-023)",
                host.Key, trigger);
            await BeginDrainAsync(
                host, "system suspend pending: mount abandoned before mount/mount", DrainCause.Suspend, ct)
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
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // bs-x57: every rc catch in this class filters on the supervisor's OWN token, never on
            // the exception type. `ex is not OperationCanceledException` read an rc/HTTP timeout
            // (a TaskCanceledException) as shutdown, let it escape, and ended the channel loop.
            // Only cancellation of `ct` is shutdown; anything else is an ordinary rc failure.
            logger.LogError(ex, "mount/mount failed for {HostKey} at {Drive}", host.Key, drive);
            host.MountRetryBackoff = host.MountRetryBackoff.RecordFailure();
            var mountFailureReason = $"mount/mount failed: {ex.Message}";
            host.ConsecutiveMountFailures++;
            host.LastMountFailureReason = mountFailureReason;
            await BeginDrainAsync(host, mountFailureReason, DrainCause.MountFailure, ct).ConfigureAwait(false);
            return;
        }

        // bs-dtk: Invariant I7 runtime guard. mount/mount succeeding is not proof the drive is
        // presented as a network drive -- rclone could keep accepting network_mode and stop acting
        // on it. A fixed-disk presentation of an SFTP mount produces pathological Explorer
        // behaviour, so a mount that is not verifiably DriveType.Network is a failed mount: it
        // takes the existing mount-failure route (Mounting -> Draining, no new transition), so the
        // reason reaches the window and tray through the same LastMountFailureReason /
        // MountFailing path as every other failed mount (ADR-012 Decision 3), and the retry is
        // paced on the mount-retry ladder rather than hammering.
        var driveTypeProblem = DescribeDriveTypeProblem(host, drive);
        if (driveTypeProblem is not null)
        {
            host.MountRetryBackoff = host.MountRetryBackoff.RecordFailure();
            host.ConsecutiveMountFailures++;
            host.LastMountFailureReason = driveTypeProblem;
            await BeginDrainAsync(host, driveTypeProblem, DrainCause.MountFailure, ct).ConfigureAwait(false);
            return;
        }

        host.ConsecutiveMountedFailures = 0;
        host.ConsecutiveDeepProbeFailures = 0;
        host.MountRetryBackoff = BackoffState.Initial;
        host.ConsecutiveMountFailures = 0;
        host.LastMountFailureReason = null;
        SetState(host, MountState.Mounted, "mount/mount succeeded");
        ResetVfsActivityTracking(host);
        ArmMountedProbeTimer(host);
        ArmMountedDeepProbeTimer(host);
        ArmIdleUnmountTimer(host);

        // bs-obf: the baseline for activity detection is the VFS as it stands at mount, so even an
        // idle period shorter than the probe interval compares against something real.
        //
        // Skipped while a suspend is pending (ADR-023 D1): this mount raced it and is about to be
        // drained, so the baseline would be thrown away, and a hung rcd would hold the channel for a
        // probe timeout that the suspend budget cannot afford. The poll is not preemptible here (this
        // is the tail of a mount command, not a tick), so not starting it is the only protection.
        if (!SuspendPending)
        {
            await SampleVfsActivityAsync(host, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// bs-dtk: null when <paramref name="drive"/> reads back as <see cref="DriveType.Network"/>
    /// (the verified baseline for a correct <c>--network-mode</c> rclone mount is Network /
    /// <c>FUSE-rclone</c>); otherwise the user-facing reason the mount is rejected. Fails closed:
    /// a drive type that cannot be read, or is anything but Network (including
    /// <see cref="DriveType.NoRootDirectory"/> -- <c>mount/mount</c> said yes but no drive exists --
    /// and <see cref="DriveType.Unknown"/>), is a mount that cannot be shown to satisfy I7.
    /// Reads only the type, never the volume (see <see cref="IDriveTypeReader"/>).
    /// </summary>
    private string? DescribeDriveTypeProblem(HostRuntime host, string drive)
    {
        DriveType driveType;
        try
        {
            driveType = driveTypeReader.GetDriveType(drive);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex, "Could not read the drive type of {Drive} for {HostKey} after mounting; unmounting rather " +
                "than keeping a mount that cannot be verified as a network drive (Invariant I7)",
                drive, host.Key);
            return $"could not verify that {drive} is a network drive (reading its drive type failed: {ex.Message}); unmounted";
        }

        if (driveType == DriveType.Network)
        {
            return null;
        }

        logger.LogError(
            "Mounted drive {Drive} for {HostKey} reports DriveType {DriveType}, expected Network; unmounting " +
            "(Invariant I7 -- a non-network presentation of an SFTP mount hangs Explorer)",
            drive, host.Key, driveType);

        return driveType == DriveType.NoRootDirectory
            ? $"mount/mount reported success but {drive} does not exist (drive type NoRootDirectory); unmounted"
            : $"{drive} is presented as a {driveType} drive instead of a network drive (--network-mode was not applied); unmounted";
    }

    // ------------------------------------------------------------------------------------------
    // Mounted: always probed (ADR-011)
    // ------------------------------------------------------------------------------------------

    /// <param name="preemption">The recurring tick's preemption token (ADR-023); <c>default</c> for the
    /// forced probe of <see cref="ReDeriveAfterTransitionAsync"/>, which is a command and is never
    /// preempted.</param>
    private async Task HandleMountedProbeDueAsync(
        HostRuntime host, string trigger, CancellationToken ct, CancellationToken preemption = default)
    {
        if (host.State != MountState.Mounted)
        {
            return;
        }

        if (TickPreempted(preemption, "mounted shallow probe", host.Key))
        {
            ArmMountedProbeTimer(host);
            return;
        }

        var result = await ProbeShallowSafelyAsync(
            host.Config.Hostname, host.Config.Port, ProbeTimeout(), ct, preemption).ConfigureAwait(false);

        if (TickPreempted(preemption, "mounted shallow probe", host.Key))
        {
            // The probe came back Cancelled -- or Success, or Timeout, or anything: it does not matter
            // and is not looked at. A preempted probe is not a failed probe (ADR-023): counting it would
            // drain a host one failure from its threshold as Automatic, with a toast, on every suspend.
            ArmMountedProbeTimer(host);
            return;
        }

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

        if (result.Outcome == ShallowProbeOutcome.Success)
        {
            // bs-obf: the activity sample rides the mounted probe's own cadence and timer, so no
            // extra timer exists per host. Only after a PASSING shallow probe: a host that is
            // failing probes is heading for a drain and its idle timer no longer matters.
            // The sample is part of this tick: a suspend that lands during it preempts it too.
            await SampleVfsActivityAsync(host, ct, preemption).ConfigureAwait(false);
        }

        ArmMountedProbeTimer(host);
    }

    private void ArmMountedProbeTimer(HostRuntime host)
    {
        host.ProbeTimer?.Dispose();
        var seconds = EffectiveMountedProbeIntervalSeconds(host.Config, global);
        host.ProbeTimer = CreateOneShotTimer(
            TimeSpan.FromSeconds(seconds),
            () => EnqueueTick((ct, preemption) =>
                HandleMountedProbeDueAsync(host, "periodic mounted probe", ct, preemption)));
    }

    /// <summary>Hardcoded, not config-driven (docs/CONFIG-SCHEMA.md's validation-audit principle
    /// applies only to VALUES fed a safety decision from config -- this is an internal tuning
    /// constant, same category as <see cref="DrainRetryInterval"/>/<see cref="ReconciliationInterval"/>
    /// above). See ADR-016's "Reasoning for a fixed threshold of 2, not 1 and not
    /// <c>failures_before_unmount</c>" for why this is neither.</summary>
    private const int MountedDeepProbeFailureThreshold = 2;

    /// <summary>
    /// bs-2eg / ADR-016: the recurring DEEP probe for a host in <c>Mounted</c>, on its own
    /// slower cadence (<c>global.mounted_deep_probe_interval_seconds</c>), alongside -- never
    /// instead of -- <see cref="HandleMountedProbeDueAsync"/>'s shallow probe. Exists because a
    /// shallow (TCP) probe continuing to succeed is not evidence the mount is alive: the SSH
    /// channel underneath a mount can die while the TCP-level host stays reachable (sleep/resume,
    /// a network blip too brief to trip the shallow-failure threshold but long enough to kill the
    /// channel) -- see docs/DECISIONS.md ADR-016 for the two scenarios this closes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately uses its OWN counter (<see cref="HostRuntime.ConsecutiveDeepProbeFailures"/>),
    /// never <see cref="HostRuntime.ConsecutiveMountedFailures"/> (the shallow probe's counter).
    /// An earlier version of this method shared the counter -- a deep-probe success reset it, a
    /// deep-probe failure jumped it straight to <see cref="EffectiveFailuresBeforeUnmount"/> -- and
    /// that version broke <c>Independent/FailureAccountingTests</c>: with the shipped defaults
    /// (60s shallow / 300s deep, an exact 5:1 multiple), the deep timer and the 5th consecutive
    /// shallow-failure tick land on the SAME instant on every cycle, and depending on which of the
    /// two same-instant channel continuations happened to run first, a coincidentally-successful
    /// deep probe could silently erase a shallow-failure streak one tick before it reached
    /// threshold -- an order-dependent race, not a one-off test artefact, since both timers are
    /// armed from the same instant every time a host enters <c>Mounted</c> and the default interval
    /// ratio guarantees recurring exact coincidence. A fully separate counter makes the two probes'
    /// bookkeeping structurally independent, so no interleaving of the two timers' channel
    /// continuations can affect the other probe's count -- "exactly N consecutive SHALLOW failures
    /// unmounts" (the invariant <c>Independent/FailureAccountingTests</c> pins) now holds
    /// unconditionally, regardless of what the deep probe is doing at the same instant.
    /// </para>
    /// <para>
    /// A failed deep probe is deliberately NOT weighted as one shallow-probe-equivalent failure: it
    /// needs only <see cref="MountedDeepProbeFailureThreshold"/> (2) consecutive failures, a fixed
    /// constant independent of the configurable <c>failures_before_unmount</c> ladder, not
    /// <see cref="EffectiveFailuresBeforeUnmount"/>'s (often larger, e.g. the default 3) value --
    /// see ADR-016. A successful deep probe resets ITS OWN counter to zero, exactly as a shallow
    /// success resets the shallow counter, so a single transient loopback-HTTP hiccup against
    /// <c>rclone rcd</c> does not, by itself, drain a genuinely healthy mount.
    /// </para>
    /// </remarks>
    private async Task HandleMountedDeepProbeDueAsync(
        HostRuntime host, string trigger, CancellationToken ct, CancellationToken preemption)
    {
        if (host.State != MountState.Mounted)
        {
            // Stale timer: state moved on (drained, or the host was suspended) before this fired.
            return;
        }

        if (TickPreempted(preemption, "mounted deep probe", host.Key))
        {
            ArmMountedDeepProbeTimer(host);
            return;
        }

        var result = await ProbeDeepSafelyAsync(host.Key, ProbeTimeout(), ct, preemption).ConfigureAwait(false);

        if (TickPreempted(preemption, "mounted deep probe", host.Key))
        {
            // Not a failed deep probe (ADR-023): ConsecutiveDeepProbeFailures is left exactly as it
            // was, so a host one failure from ADR-016's threshold is not drained as Automatic.
            ArmMountedDeepProbeTimer(host);
            return;
        }

        if (result.Outcome == DeepProbeOutcome.Success)
        {
            host.ConsecutiveDeepProbeFailures = 0;
        }
        else
        {
            host.ConsecutiveDeepProbeFailures++;
            logger.LogWarning(
                "Deep probe failed for mounted host {HostKey} ({Outcome}: {Detail}): {Failures}/{Threshold} " +
                "consecutive deep-probe failures -- shallow TCP probes may still be succeeding, but the SSH " +
                "channel underneath the mount is what the deep probe actually verifies (ADR-016)",
                host.Key, result.Outcome, result.Detail, host.ConsecutiveDeepProbeFailures, MountedDeepProbeFailureThreshold);

            if (host.ConsecutiveDeepProbeFailures >= MountedDeepProbeFailureThreshold)
            {
                await BeginDrainAsync(
                    host,
                    $"{host.ConsecutiveDeepProbeFailures} consecutive deep-probe failures while mounted " +
                    $"(threshold {MountedDeepProbeFailureThreshold}): SSH channel confirmed dead (ADR-016)",
                    DrainCause.Automatic,
                    ct).ConfigureAwait(false);
                return;
            }
        }

        ArmMountedDeepProbeTimer(host);
    }

    private void ArmMountedDeepProbeTimer(HostRuntime host)
    {
        host.DeepProbeTimer?.Dispose();
        host.DeepProbeTimer = CreateOneShotTimer(
            TimeSpan.FromSeconds(global.MountedDeepProbeIntervalSeconds),
            () => EnqueueTick((ct, preemption) =>
                HandleMountedDeepProbeDueAsync(host, "periodic mounted deep probe", ct, preemption)));
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

        // Retires any fire already queued from the timer just disposed (see IdleTimerGeneration).
        var generation = ++host.IdleTimerGeneration;

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
            () => EnqueueTick((ct, preemption) => HandleIdleUnmountDueAsync(host, generation, ct, preemption)));
    }

    // ------------------------------------------------------------------------------------------
    // Idle unmount: activity comes from rclone's VFS counters (bs-obf, docs/ARCHITECTURE.md §4 rule 7)
    // ------------------------------------------------------------------------------------------

    /// <summary>The six <c>vfs/stats</c> counters whose change counts as activity (bs-obf). Value
    /// equality is the whole comparison. <c>inUse</c> is deliberately not here: it is a refcount on
    /// the VFS object, not a count of open files.</summary>
    private readonly record struct VfsCounters(
        long BytesUsed, long CacheFiles, long UploadsInProgress, long UploadsQueued, long MetaDirs, long MetaFiles);

    /// <summary>One successful poll of a host's VFS. <see cref="QueueLength"/> is null when the poll
    /// did not ask for the upload queue (the periodic sample), non-null at fire time.</summary>
    private sealed record VfsReading(VfsCounters Counters, long ErroredFiles, int? QueueLength);

    /// <summary>A poll either yields a reading or says why it could not. There is no third outcome,
    /// and no default reading: every caller treats a failure as "do not unmount".</summary>
    private sealed record VfsPoll(VfsReading? Reading, string? Failure);

    /// <summary>Only on-demand hosts with <c>idle_unmount_seconds &gt; 0</c> are ever polled.</summary>
    private static bool UsesIdleUnmount(HostRuntime host) =>
        host.Config.Mount.Mode == MountMode.OnDemand && (host.Config.Mount.IdleUnmountSeconds ?? 0) > 0;

    private static void ResetVfsActivityTracking(HostRuntime host)
    {
        host.VfsBaseline = null;
        host.UploadFailureCount = 0;
        host.LastIdleBlockReason = null;
        host.VfsPollFaultLogged = false;
    }

    /// <summary>
    /// One look at <paramref name="host"/>'s VFS: <c>vfs/list</c> to find it, <c>vfs/stats</c> for the
    /// counters and, when <paramref name="includeQueue"/>, <c>vfs/queue</c>. Loopback HTTP to rcd
    /// only; the drive letter is never touched (rule 3a). All the calls share ONE budget of the
    /// probe timeout, so a hung rcd holds the serialised channel no longer than a single probe can.
    /// </summary>
    /// <remarks>
    /// The VFS is identified by the exact <c>fs</c> string <see cref="TryBeginMountAsync"/> passes to
    /// <c>mount/mount</c> (<see cref="RcloneRemoteNaming.RemoteFsPath"/>), compared ordinally with
    /// the names <c>vfs/list</c> returns. No match is a failure, never a guess: another VFS's
    /// counters would say nothing about this drive, and "no activity seen" is the answer that
    /// unmounts it. A missing <c>diskCache</c> (cache mode off) or <c>metadataCache</c> is a failure
    /// for the same reason.
    /// </remarks>
    private async Task<VfsPoll> PollVfsAsync(
        HostRuntime host, bool includeQueue, CancellationToken ct, CancellationToken preemption = default)
    {
        var fs = RcloneRemoteNaming.RemoteFsPath(host.Key, host.Config.Mount.RemotePath!);

        // The preemption token is linked in so a suspend releases a hung rcd call at once (ADR-023),
        // but it is deliberately NOT part of the catch filter below: that filter asks "is this
        // shutdown?" of the supervisor's own token alone. A preempted poll comes back as an ordinary
        // failed poll and the CALLER decides, by the preemption token, to discard it.
        using var budget = new CancellationTokenSource(ProbeTimeout(), timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token, preemption);

        try
        {
            var names = await rcloneClient.ListVfsAsync(linked.Token).ConfigureAwait(false);
            if (!names.Contains(fs, StringComparer.Ordinal))
            {
                return new VfsPoll(null, $"no VFS named '{fs}' (rcd lists: {(names.Count == 0 ? "none" : string.Join(", ", names.Select(n => $"'{n}'")))})");
            }

            var stats = await rcloneClient.GetVfsStatsAsync(fs, linked.Token).ConfigureAwait(false);
            if (stats.DiskCache is not { } disk)
            {
                return new VfsPoll(null, "vfs/stats has no diskCache section");
            }

            if (stats.MetadataCache is not { } meta)
            {
                return new VfsPoll(null, "vfs/stats has no metadataCache section");
            }

            int? queueLength = null;
            if (includeQueue)
            {
                queueLength = (await rcloneClient.GetVfsQueueAsync(fs, linked.Token).ConfigureAwait(false)).Count;
            }

            return new VfsPoll(
                new VfsReading(
                    new VfsCounters(disk.BytesUsed, disk.Files, disk.UploadsInProgress, disk.UploadsQueued, meta.Dirs, meta.Files),
                    disk.ErroredFiles,
                    queueLength),
                null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Same rule as every rc catch here (bs-x57): filter on the supervisor's own token, never
            // the exception type, so a timeout (the probe-timeout budget, or the client's own) is an
            // ordinary failed poll and not shutdown.
            return new VfsPoll(null, $"{RcFaultKinds.Kind(ex)}: {ex.Message}");
        }
    }

    /// <summary>
    /// The periodic activity sample (bs-obf). The first sample after a mount only sets the baseline;
    /// after that, any change in <see cref="VfsCounters"/> is activity and does what
    /// <see cref="RecordActivityAsync"/> does: restarts the idle clock. A failed sample records
    /// nothing, so it can only ever let the idle timer run on -- and the fire-time check
    /// (<see cref="HandleIdleUnmountDueAsync"/>) refuses to unmount on a failed poll.
    /// </summary>
    private async Task SampleVfsActivityAsync(HostRuntime host, CancellationToken ct, CancellationToken preemption = default)
    {
        if (host.State != MountState.Mounted || !UsesIdleUnmount(host))
        {
            return;
        }

        if (TickPreempted(preemption, "mounted VFS sample", host.Key))
        {
            return;
        }

        var poll = await PollVfsAsync(host, includeQueue: false, ct, preemption).ConfigureAwait(false);
        if (TickPreempted(preemption, "mounted VFS sample", host.Key))
        {
            // Before any bookkeeping: no baseline, no UploadFailureCount, no fault-logged flag, no
            // idle-clock restart. A hung sample that a suspend released is not a failed sample.
            return;
        }

        if (poll.Reading is not { } reading)
        {
            if (!host.VfsPollFaultLogged)
            {
                host.VfsPollFaultLogged = true;
                logger.LogWarning(
                    "Could not sample VFS activity for {HostKey}: {Failure}. No activity is recorded; the idle " +
                    "unmount will not fire while the VFS cannot be read (bs-obf, fail closed)",
                    host.Key, poll.Failure);
            }

            return;
        }

        host.VfsPollFaultLogged = false;
        host.UploadFailureCount = reading.ErroredFiles;

        var previous = host.VfsBaseline;
        host.VfsBaseline = reading.Counters;
        if (previous is { } before && before != reading.Counters)
        {
            logger.LogDebug(
                "VFS activity for {HostKey}: {Before} -> {After}; restarting the idle clock", host.Key, before, reading.Counters);
            ArmIdleUnmountTimer(host);
        }
    }

    /// <summary>
    /// The idle clock ran out (bs-obf). Looks again before unmounting: the drive is taken down only
    /// if the VFS can be read, has nothing uploading, queued or failed, and shows no change since the
    /// last sample. Anything else skips the unmount and re-arms the clock for a full period.
    /// </summary>
    private async Task HandleIdleUnmountDueAsync(
        HostRuntime host, int generation, CancellationToken ct, CancellationToken preemption)
    {
        if (host.State != MountState.Mounted || generation != host.IdleTimerGeneration)
        {
            return;
        }

        if (TickPreempted(preemption, "idle-unmount", host.Key))
        {
            ArmIdleUnmountTimer(host);
            return;
        }

        var poll = await PollVfsAsync(host, includeQueue: true, ct, preemption).ConfigureAwait(false);

        if (TickPreempted(preemption, "idle-unmount", host.Key))
        {
            // Not an idle verdict either way: no VFS bookkeeping, no skip reason, and above all no
            // unmount for IdleTimeout. The suspend that preempted this drains the host as Suspend.
            ArmIdleUnmountTimer(host);
            return;
        }

        string? skipReason = null;
        var worthAWarning = false;
        if (poll.Reading is not { } reading)
        {
            skipReason = $"the VFS could not be read ({poll.Failure})";
            worthAWarning = true;
        }
        else
        {
            host.UploadFailureCount = reading.ErroredFiles;
            var previous = host.VfsBaseline;
            host.VfsBaseline = reading.Counters;

            if (reading.Counters.UploadsInProgress > 0 || reading.Counters.UploadsQueued > 0 || reading.QueueLength > 0)
            {
                skipReason = $"uploads are pending ({reading.Counters.UploadsInProgress} in progress, " +
                             $"{reading.Counters.UploadsQueued} queued, {reading.QueueLength} in the upload queue)";
            }
            else if (reading.ErroredFiles > 0)
            {
                skipReason = $"{reading.ErroredFiles} file(s) failed to upload";
                worthAWarning = true;
            }
            else if (previous is not { } before)
            {
                skipReason = "no earlier activity sample to compare with; recorded one";
            }
            else if (before != reading.Counters)
            {
                skipReason = "the VFS changed since the last sample (activity)";
            }
        }

        if (skipReason is null)
        {
            host.LastIdleBlockReason = null;
            await BeginDrainAsync(
                host, $"idle timeout ({host.Config.Mount.IdleUnmountSeconds}s without activity)", DrainCause.IdleTimeout, ct)
                .ConfigureAwait(false);
            return;
        }

        if (!string.Equals(host.LastIdleBlockReason, skipReason, StringComparison.Ordinal))
        {
            host.LastIdleBlockReason = skipReason;
            logger.Log(
                worthAWarning ? LogLevel.Warning : LogLevel.Information,
                "Not idle-unmounting {HostKey}: {Reason}; restarting the idle clock (bs-obf)", host.Key, skipReason);
        }
        else
        {
            logger.LogDebug("Still not idle-unmounting {HostKey}: {Reason}", host.Key, skipReason);
        }

        ArmIdleUnmountTimer(host);
    }

    // ------------------------------------------------------------------------------------------
    // Draining, with forced-unmount escalation verified against listmounts
    // ------------------------------------------------------------------------------------------

    private async Task BeginDrainAsync(
        HostRuntime host, string reason, DrainCause cause, CancellationToken ct, DateTimeOffset? startedUtc = null)
    {
        if (!EnterDraining(host, reason, cause, startedUtc))
        {
            return;
        }

        await AttemptDrainStepAsync(host, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The synchronous half of beginning a drain: the <c>Mounting|Mounted -&gt; Draining</c> transition
    /// and the drain's bookkeeping, with no rc call. <see cref="BeginDrainAsync"/> follows it with one
    /// drain step; the suspend sweep (<see cref="DrainAllForSuspendAsync"/>, bs-5o4) enters every host
    /// first and drains them together. Returns <see langword="false"/>, changing nothing, when the host
    /// is not <c>Mounting</c> or <c>Mounted</c>.
    /// </summary>
    private bool EnterDraining(HostRuntime host, string reason, DrainCause cause, DateTimeOffset? startedUtc)
    {
        if (host.State is not (MountState.Mounting or MountState.Mounted))
        {
            logger.LogDebug(
                "Ignoring drain request for {HostKey}: not Mounting/Mounted (currently {State}); reason was: {Reason}",
                host.Key, host.State, reason);
            return false;
        }

        SetState(host, MountState.Draining, reason, cause);
        host.DrainReason = reason;
        // A suspend passes its ARRIVAL time (bs-7d7): the budget counts from when suspend arrived, not
        // from when the channel got round to it.
        host.DrainStartedUtc = startedUtc ?? timeProvider.GetUtcNow();
        host.DrainEscalated = false;
        host.SuspendEscalationDeadlineUtc = null;
        host.DrainCause = cause;

        host.ProbeTimer?.Dispose();
        host.ProbeTimer = null;
        host.DeepProbeTimer?.Dispose();
        host.DeepProbeTimer = null;
        host.IdleUnmountTimer?.Dispose();
        host.IdleUnmountTimer = null;
        ResetVfsActivityTracking(host);

        return true;
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

        LogDrainUnmountOutcome(host, mountPoint, await TryUnmountAsync(mountPoint, ct).ConfigureAwait(false));

        if (await IsStillMountedAsync(mountPoint, ct).ConfigureAwait(false) is false)
        {
            logger.LogInformation(
                "Unmount confirmed for {HostKey} at {MountPoint} (reason: {Reason})", host.Key, mountPoint, host.DrainReason);
            await CompleteDrainAsync(host, ct).ConfigureAwait(false);
            return;
        }

        var elapsed = timeProvider.GetUtcNow() - host.DrainStartedUtc!.Value;
        var timeout = EffectiveDrainTimeout(host);

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
            catch (Exception ex) when (!ct.IsCancellationRequested)
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

        ArmDrainRetryTimer(host, elapsed, timeout);
    }

    /// <summary>
    /// Arms the next drain attempt for a host that is still <c>Draining</c> after a step. Shared by
    /// <see cref="AttemptDrainStepAsync"/> and the suspend sweep (<see cref="DrainAllForSuspendAsync"/>),
    /// so a host left unconfirmed by the sweep retries exactly as one left unconfirmed by a single step.
    /// </summary>
    private void ArmDrainRetryTimer(HostRuntime host, TimeSpan elapsed, TimeSpan timeout)
    {
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

    /// <summary>How long after <see cref="HostRuntime.DrainStartedUtc"/> this drain escalates to a
    /// forced unmount: the suspend budget for a <see cref="DrainCause.Suspend"/> drain, the ordinary
    /// <see cref="DrainConfirmTimeout"/> otherwise -- and, for a drain that was already running when
    /// suspend arrived, never later than <see cref="HostRuntime.SuspendEscalationDeadlineUtc"/>.</summary>
    private TimeSpan EffectiveDrainTimeout(HostRuntime host)
    {
        var timeout = host.DrainCause == DrainCause.Suspend
            ? TimeSpan.FromSeconds(global.SuspendUnmountTimeoutSeconds)
            : DrainConfirmTimeout;

        if (host.SuspendEscalationDeadlineUtc is { } suspendDeadline)
        {
            var viaSuspend = suspendDeadline - host.DrainStartedUtc!.Value;
            if (viaSuspend < timeout)
            {
                timeout = viaSuspend;
            }
        }

        return timeout;
    }

    private async Task<bool> IsStillMountedAsync(string mountPoint, CancellationToken ct) =>
        IsListed(await TryListMountsAsync([mountPoint], ct).ConfigureAwait(false), mountPoint);

    /// <summary>One <c>mount/listmounts</c> call verifying <paramref name="mountPoints"/>. Returns
    /// <see langword="null"/> when the call failed, which <see cref="IsListed"/> reads as "still
    /// mounted" for every host: per docs/ARCHITECTURE.md §4 rule 4, never assume a drive is gone. If we
    /// cannot even ask, assume the worst so the caller keeps retrying rather than declaring victory on
    /// missing information. Fault logging stays keyed per mount point, as for a single check.</summary>
    private async Task<IReadOnlyList<RcloneMountInfo>?> TryListMountsAsync(
        IReadOnlyCollection<string> mountPoints, CancellationToken ct)
    {
        IReadOnlyList<RcloneMountInfo> mounts;
        try
        {
            mounts = await rcloneClient.ListMountsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            foreach (var mountPoint in mountPoints)
            {
                LogRepeatedRcFault(
                    $"verify-listmounts:{mountPoint}", ex,
                    $"mount/listmounts failed while verifying unmount of {mountPoint}; assuming still mounted",
                    $"mount/listmounts verifying {mountPoint}");
            }

            return null;
        }

        foreach (var mountPoint in mountPoints)
        {
            logger.LogFaultRecovered(repeatingFaults, $"verify-listmounts:{mountPoint}", $"mount/listmounts verifying {mountPoint}");
        }

        return mounts;
    }

    private static bool IsListed(IReadOnlyList<RcloneMountInfo>? snapshot, string mountPoint) =>
        snapshot is null ||
        snapshot.Any(m => string.Equals(m.MountPoint, mountPoint, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Sends <c>mount/unmount</c> for <paramref name="mountPoint"/> and returns the failure instead of
    /// throwing it (<see langword="null"/> on success), so a caller can start several of these together
    /// and log their outcomes afterwards, on the supervisor's channel. It deliberately logs nothing and
    /// touches no supervisor state: the continuation after the rc call runs on a thread-pool thread,
    /// and with several in flight two of them could otherwise race on the fault-logger's state. A
    /// cancellation of <paramref name="ct"/> (shutdown) still propagates.
    /// </summary>
    private async Task<Exception?> TryUnmountAsync(string mountPoint, CancellationToken ct)
    {
        try
        {
            await rcloneClient.UnmountAsync(mountPoint, ct).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return ex;
        }
    }

    private void LogDrainUnmountOutcome(HostRuntime host, string mountPoint, Exception? failure)
    {
        if (failure is null)
        {
            logger.LogFaultRecovered(repeatingFaults, $"drain-unmount:{host.Key}", $"mount/unmount for {host.Key} at {mountPoint}");
            return;
        }

        LogRepeatedRcFault(
            $"drain-unmount:{host.Key}", failure,
            $"mount/unmount call failed for {host.Key} at {mountPoint}; will re-verify against listmounts",
            $"mount/unmount for {host.Key} at {mountPoint}");
    }

    /// <summary>
    /// The suspend drain (bs-5o4, the maintainer's decision of 2026-11-02: "unmount all, then verify").
    /// Drains every host in <paramref name="batch"/>, all already <c>Draining</c> with the suspend
    /// budget, as ONE step in three phases instead of one full drain step per host in turn:
    /// <list type="number">
    /// <item><b>Request.</b> Every host's <c>mount/unmount</c> is started before any is awaited, so each
    /// request is on the wire at once and the last host's does not wait for the first host's. Each call
    /// keeps its own timeout (<c>RcloneClient.UnmountTimeout</c>, 30 s), so the whole phase takes as long
    /// as its slowest call, not the sum. Before this, one stuck rcd made host N's request wait for hosts
    /// 1..N-1 each running a whole step (about 80 s) and the later hosts were not unmounted before sleep.</item>
    /// <item><b>Verify.</b> One <c>mount/listmounts</c> covers all hosts. A host the table no longer lists
    /// completes its drain. Nothing is taken from an unmount call's own outcome (rule 4).</item>
    /// <item><b>Escalate.</b> A host still listed whose budget (counted from arrival, bs-7d7) has run out
    /// gets its forced unmount, again all together, then one more <c>mount/listmounts</c>. A host not yet
    /// due, or still unconfirmed, keeps retrying on the per-host timer exactly as after a single drain
    /// step (<see cref="ArmDrainRetryTimer"/>).</item>
    /// </list>
    /// Each host keeps its own <c>Draining</c> state, escalation flag and deadline; no transition is
    /// added. A failure on one host's unmount is that host's alone: the others are already in flight and
    /// are verified regardless. The concurrent part is only the rc calls (<see cref="TryUnmountAsync"/>):
    /// every state change and every log line happens on the channel, between them.
    /// </summary>
    private async Task DrainAllForSuspendAsync(IReadOnlyList<HostRuntime> batch, CancellationToken ct)
    {
        var draining = batch.Where(h => h.State == MountState.Draining).ToList();
        if (draining.Count == 0)
        {
            return;
        }

        // Request. ToList() starts every call; Task.WhenAll then waits for all of them together.
        var requests = draining
            .Select(host =>
            {
                var mountPoint = host.Config.Mount.Drive!;
                return (Host: host, MountPoint: mountPoint, Outcome: TryUnmountAsync(mountPoint, ct));
            })
            .ToList();
        await Task.WhenAll(requests.Select(r => r.Outcome)).ConfigureAwait(false);
        foreach (var request in requests)
        {
            LogDrainUnmountOutcome(request.Host, request.MountPoint, request.Outcome.Result);
        }

        // Verify.
        var snapshot = await TryListMountsAsync(requests.Select(r => r.MountPoint).ToList(), ct).ConfigureAwait(false);
        var unconfirmed = new List<(HostRuntime Host, string MountPoint)>();
        foreach (var (host, mountPoint, _) in requests)
        {
            if (IsListed(snapshot, mountPoint))
            {
                unconfirmed.Add((host, mountPoint));
                continue;
            }

            logger.LogInformation(
                "Unmount confirmed for {HostKey} at {MountPoint} (reason: {Reason})", host.Key, mountPoint, host.DrainReason);
            await CompleteDrainAsync(host, ct).ConfigureAwait(false);
        }

        // Escalate. The same test as AttemptDrainStepAsync's, taken once, after the verification, so a
        // request phase that ate the budget (a stuck rcd) escalates every host that is still listed.
        var elapsedNow = timeProvider.GetUtcNow();
        var escalating = new List<(HostRuntime Host, string MountPoint)>();
        foreach (var entry in unconfirmed)
        {
            var timeout = EffectiveDrainTimeout(entry.Host);
            if (!entry.Host.DrainEscalated && elapsedNow - entry.Host.DrainStartedUtc!.Value >= timeout)
            {
                entry.Host.DrainEscalated = true;
                logger.LogWarning(
                    "Unmount for {HostKey} at {MountPoint} did not confirm within {Timeout}; escalating to forced unmount",
                    entry.Host.Key, entry.MountPoint, timeout);
                escalating.Add(entry);
            }
        }

        if (escalating.Count > 0)
        {
            var forced = escalating.Select(e => (e.Host, e.MountPoint, Outcome: TryUnmountAsync(e.MountPoint, ct))).ToList();
            await Task.WhenAll(forced.Select(f => f.Outcome)).ConfigureAwait(false);
            foreach (var (host, mountPoint, outcome) in forced)
            {
                if (outcome.Result is { } failure)
                {
                    logger.LogWarning(failure, "Forced unmount re-attempt failed for {HostKey} at {MountPoint}", host.Key, mountPoint);
                }
            }

            var forcedSnapshot = await TryListMountsAsync(forced.Select(f => f.MountPoint).ToList(), ct).ConfigureAwait(false);
            foreach (var (host, mountPoint, _) in forced)
            {
                if (!IsListed(forcedSnapshot, mountPoint))
                {
                    logger.LogInformation("Forced unmount confirmed for {HostKey} at {MountPoint}", host.Key, mountPoint);
                    await CompleteDrainAsync(host, ct).ConfigureAwait(false);
                    unconfirmed.RemoveAll(u => u.Host == host);
                    continue;
                }

                logger.LogError(
                    "Forced unmount for {HostKey} at {MountPoint} STILL not confirmed by mount/listmounts; the drive " +
                    "letter may still be present. Retrying rather than declaring the drain complete.",
                    host.Key, mountPoint);
            }
        }

        // Still Draining: retry on the per-host timer, as after any single drain step.
        // Elapsed is read again here: the forced phase above may have taken a whole unmount timeout.
        foreach (var (host, _) in unconfirmed)
        {
            ArmDrainRetryTimer(
                host, timeProvider.GetUtcNow() - host.DrainStartedUtc!.Value, EffectiveDrainTimeout(host));
        }
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
        host.ConsecutiveDeepProbeFailures = 0;

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

        // bs-7ck: a mount-affecting config change queued a newer definition while this host was
        // still Mounting/Mounted/Draining -- applied now, the moment the drain is confirmed, so
        // everything downstream (a paced remount, an immediate re-enable, or eviction below) already
        // sees it. See PendingConfig's remarks for why this could not simply be applied eagerly.
        if (host.PendingConfig is not null)
        {
            host.Config = host.PendingConfig;
            host.PendingConfig = null;
        }

        if (host.DrainCause == DrainCause.ConfigRemoved)
        {
            // bs-7ck / Invariant I2: the host's key no longer exists in config at all -- unlike
            // every other drain cause, there is no "re-enable" to pace or skip here, only eviction.
            RemoveFromSupervision(host);
            return Task.CompletedTask;
        }

        // MountingSuspended (ADR-023): a drain that completes while a suspend is merely PENDING is not
        // re-enabled either. The re-enable would probe the host and, for a persistent one, mount it,
        // in the seconds before the machine sleeps; the host stays Disabled and resume enables it.
        if (host.AdministrativelyEnabled && !MountingSuspended)
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
                // Repair is not a parking cause (the UserUnmount block above is the only one): the
                // host is re-enabled exactly as after any automatic drain, and a persistent host
                // remounts only after the probe that re-enable runs (Invariant I1).
                var trigger = host.DrainCause == DrainCause.Repair
                    ? "repair: re-probe after drain"
                    : "auto re-enable after drain";
                Enqueue(ct2 => EnableHostAsync(host, trigger, ct2));
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
            () => Enqueue(ct => OnMountRetryElapsedAsync(host, ct)));
    }

    /// <summary>The paced re-enable after a failed mount attempt (<see cref="ArmMountRetryTimer"/>).
    /// bs-bv0 / Invariant I8: a timer armed just before suspend was processed fires during the
    /// suspend; it must not probe and re-enter the mount path then. The host is left
    /// <see cref="MountState.Disabled"/> and <see cref="ResumeAsync"/> enables every enabled
    /// <c>Disabled</c> host. (Even if this did enable the host, <see cref="TryBeginMountAsync"/> would
    /// still refuse to mount; skipping here just avoids probe traffic in the seconds before sleep.)</summary>
    private Task OnMountRetryElapsedAsync(HostRuntime host, CancellationToken ct)
    {
        if (MountingSuspended)
        {
            logger.LogInformation(
                "Mount-retry backoff elapsed for {HostKey} while the system is suspended; leaving it {State} for " +
                "resume to re-enable (Invariant I8)", host.Key, host.State);
            return Task.CompletedTask;
        }

        return EnableHostAsync(host, "mount-retry backoff elapsed", ct);
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
        catch (Exception ex) when (!ct.IsCancellationRequested)
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

                var adoptTimestamp = timeProvider.GetUtcNow();
                host.State = MountState.Mounted; // see remarks: the one documented SetState bypass
                host.LastTransitionUtc = adoptTimestamp;
                host.LastTransitionTrigger = "startup: adopted existing mount";
                // Bypasses SetState (see remarks above) but is a real, logged transition -- T6's
                // crash-recovery scenario (docs/OPERATIONS.md) is exactly what the transition
                // history needs to show, so record it here the same way SetState would. Pinned by
                // TransitionHistoryTests.Startup_adoption_of_an_existing_mount_is_recorded_in_the_transition_history
                // -- deleting this call fails that test, not just a comment's say-so.
                RecordTransition(host.Key, MountState.Disabled, MountState.Mounted, "startup: adopted existing mount", adoptTimestamp);
                host.ConsecutiveMountedFailures = 0;
                host.ConsecutiveDeepProbeFailures = 0;
                ResetVfsActivityTracking(host);
                ArmMountedProbeTimer(host);
                ArmMountedDeepProbeTimer(host);
                ArmIdleUnmountTimer(host);
                await SampleVfsActivityAsync(host, ct).ConfigureAwait(false);
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
                catch (Exception ex) when (!ct.IsCancellationRequested)
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
            _ => EnqueueTick(ReconcileTickAsync), null, ReconciliationInterval, ReconciliationInterval);
    }

    /// <summary>The periodic reconciliation, plus (ADR-022 amendment) the one place the supervisor
    /// notices on its own that <c>rclone rcd</c> is answering again after failing ADR-017's gate:
    /// only while a remount record is waiting and <see cref="rcdAnswering"/> is false does it spend
    /// a <c>core/version</c> call per tick, and a successful one sweeps the waiting records.
    /// Needed because nothing tells the supervisor an rcd that merely stalled across a resume has
    /// recovered: neither <see cref="OnRcloneRestartedAsync"/> nor an availability change fires
    /// for a process that never exited.</summary>
    private async Task ReconcileTickAsync(CancellationToken ct, CancellationToken preemption)
    {
        // Before the core/version check below: an expired record must not cost a call, nor mount.
        DropExpiredRemountRecords("reconciliation tick");

        // ADR-023: a tick that starts after a suspend arrived does no I/O at all, and one a suspend
        // interrupts discards what it was told. The timer is periodic, so there is nothing to re-arm.
        if (TickPreempted(preemption, "reconciliation", "all hosts"))
        {
            return;
        }

        var reconciled = await ReconcileAsync(ct, preemption).ConfigureAwait(false);

        // The core/version call below must not start once a suspend has been called. When the
        // reconciliation itself was cut short it has already logged that; when it finished just
        // before the suspend, this is the first the tick hears of it.
        if (reconciled ? TickPreempted(preemption, "reconciliation", "all hosts") : preemption.IsCancellationRequested)
        {
            return;
        }

        if (!rcdAnswering
            && hosts.Values.Any(h => h.RemountAfterResume)
            && await TryVerifyRcdAsync(ct, preemption).ConfigureAwait(false))
        {
            await SweepDeferredRemountsAsync("reconciliation tick: rclone rcd answers again", ct).ConfigureAwait(false);
        }
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
    private const string ReconcileFaultSource = "reconcile-listmounts";

    private async Task<bool> ReconcileAsync(CancellationToken ct, CancellationToken preemption = default)
    {
        IReadOnlyList<RcloneMountInfo> mounts;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, preemption);
        try
        {
            mounts = await rcloneClient.ListMountsAsync(linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // A listmounts that a suspend cut short is not an rc fault: nothing to log as one, and
            // nothing to count towards the repeating-fault reminder.
            if (!TickPreempted(preemption, "reconciliation", "all hosts"))
            {
                LogRepeatedRcFault(
                    ReconcileFaultSource, ex, "Reconciliation: mount/listmounts failed; skipping this tick",
                    "Reconciliation: mount/listmounts");
            }

            return false;
        }

        if (TickPreempted(preemption, "reconciliation", "all hosts"))
        {
            // The answer arrived but a suspend has since been called: it is not acted on. No drain for
            // a mount "missing" from a list that is already stale, no orphan unmount.
            return false;
        }

        logger.LogFaultRecovered(repeatingFaults, ReconcileFaultSource, "Reconciliation: mount/listmounts", "ticks");

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
                // bs-z9e: a mount lost to a restart the user asked for is a repair, not a loss.
                var restartedByUser = UserRestartMarkActive();
                await BeginDrainAsync(
                    host,
                    restartedByUser
                        ? "reconciliation: mount missing from listmounts after the user restarted rclone"
                        : "reconciliation: mount missing from listmounts",
                    restartedByUser ? DrainCause.Repair : DrainCause.Automatic,
                    ct).ConfigureAwait(false);
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
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    logger.LogError(ex, "Reconciliation: failed to clear orphaned mount for {HostKey}", host.Key);
                }
            }
        }

        return true;
    }

    // ------------------------------------------------------------------------------------------
    // ADR-017: re-derivation after a power or network transition
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// bs-mk4 / bs-brv / ADR-017: after any power or network transition, the supervisor does not
    /// trust its own <c>Mounted</c> state. Shared by <see cref="ResumeAsync"/> and
    /// <see cref="NetworkChangedAsync"/> so both call the exact same sequence instead of each
    /// hand-rolling a subset of it -- that gap (resume skipping <c>Mounted</c> hosts entirely, a
    /// network change force-probing shallow only) is what let the three defects this ADR closes
    /// look locally reasonable in review. See docs/DECISIONS.md ADR-017 and
    /// docs/ARCHITECTURE.md §3/§4 rule 5.
    /// </summary>
    /// <remarks>
    /// Order is fixed by the ADR and matters:
    /// <list type="number">
    /// <item>Gate on <c>rclone rcd</c> answering <c>core/version</c>. Without it, a failed deep probe
    /// cannot be told apart from "rcd has not finished coming back yet" -- acting on the second
    /// unmounts a healthy drive on every resume. Same posture as Invariant I1: no action on
    /// unverified state. If the gate fails, nothing below runs; the ordinary cadence retries later.</item>
    /// <item>Reconcile against <c>mount/listmounts</c> (reusing <see cref="ReconcileAsync"/> --
    /// no second copy of that logic) -- ground truth before any probe result is interpreted.</item>
    /// <item>Force both the shallow and the deep probe on every host still <c>Mounted</c> after
    /// reconciliation. The shallow probe uses its ordinary accumulating threshold
    /// (<see cref="HandleMountedProbeDueAsync"/>, unchanged); a forced deep-probe failure here is
    /// conclusive at a single failure, not ADR-016's ordinary threshold of 2 -- the rcd gate above is
    /// what makes that safe, and a transition is corroborating evidence (see
    /// <see cref="ForceDeepProbeAfterTransitionAsync"/>).</item>
    /// </list>
    /// Each host's armed timers are disposed before its forced probe runs, so the forced probe and
    /// the host's own periodic timer can never both be in flight for the same probe kind.
    /// </remarks>
    private async Task ReDeriveAfterTransitionAsync(string trigger, CancellationToken ct)
    {
        try
        {
            await rcloneClient.GetVersionAsync(ct).ConfigureAwait(false);
            rcdAnswering = true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // ADR-022 amendment: remembered, so a remount record is not spent against a dead rcd.
            rcdAnswering = false;
            logger.LogWarning(
                ex,
                "Re-derivation after {Trigger} skipped: rclone rcd did not answer core/version. Cannot " +
                "distinguish a dead SSH channel from rcd still coming back, so nothing is reconciled or " +
                "drained this time -- the ordinary probe/reconciliation cadence will retry (ADR-017)",
                trigger);
            return;
        }

        await ReconcileAsync(ct).ConfigureAwait(false);

        foreach (var host in hosts.Values.Where(h => h.State == MountState.Mounted).ToList())
        {
            host.ProbeTimer?.Dispose();
            host.ProbeTimer = null;
            await HandleMountedProbeDueAsync(host, $"{trigger}: force-probe mounted host (shallow, ADR-017)", ct)
                .ConfigureAwait(false);

            if (host.State != MountState.Mounted)
            {
                // The forced shallow probe just drained it via the ordinary accumulating threshold --
                // nothing left in Mounted to deep-probe.
                continue;
            }

            host.DeepProbeTimer?.Dispose();
            host.DeepProbeTimer = null;
            await ForceDeepProbeAfterTransitionAsync(host, trigger, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// ADR-017's settled sub-decision: a deep-probe failure forced immediately after a power/network
    /// transition is conclusive on its own -- it drains on a SINGLE failure, unlike the recurring
    /// periodic deep probe (<see cref="HandleMountedDeepProbeDueAsync"/>), which still needs
    /// <see cref="MountedDeepProbeFailureThreshold"/> (2) consecutive failures. Only reachable from
    /// <see cref="ReDeriveAfterTransitionAsync"/>, which has already gated on <c>rclone rcd</c>
    /// answering <c>core/version</c> -- that gate is what makes a single failure here safe to act on.
    /// </summary>
    private async Task ForceDeepProbeAfterTransitionAsync(HostRuntime host, string trigger, CancellationToken ct)
    {
        var result = await ProbeDeepSafelyAsync(host.Key, ProbeTimeout(), ct).ConfigureAwait(false);

        if (result.Outcome == DeepProbeOutcome.Success)
        {
            host.ConsecutiveDeepProbeFailures = 0;
            ArmMountedDeepProbeTimer(host);
            return;
        }

        host.ConsecutiveDeepProbeFailures++;
        logger.LogWarning(
            "Forced deep probe failed for mounted host {HostKey} immediately after {Trigger} ({Outcome}: " +
            "{Detail}): conclusive at a single failure with rclone rcd confirmed live -- draining " +
            "(ADR-017; the periodic deep probe would ordinarily need {Threshold} consecutive failures, " +
            "see ADR-016)",
            host.Key, trigger, result.Outcome, result.Detail, MountedDeepProbeFailureThreshold);

        await BeginDrainAsync(
            host,
            $"deep probe failed immediately after {trigger}: SSH channel confirmed dead post-transition " +
            "(ADR-017 -- conclusive at 1 failure with rclone rcd confirmed live)",
            DrainCause.Automatic,
            ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------------------------------
    // Config reload (bs-7ck): IMountSupervisor.ConfigChangedAsync's implementation. See that
    // interface method's remarks for the classification this whole section implements.
    // ------------------------------------------------------------------------------------------

    private async Task ApplyConfigChangeAsync(BosunConfig newConfig, CancellationToken ct)
    {
        if (!started)
        {
            // Nothing is running yet -- StartAsync will build the host set straight from
            // configStore.Current whenever it eventually runs. Diffing against an empty host set
            // here would make every host look "added", for no benefit.
            logger.LogDebug(
                "Config changed before the supervisor started; ignoring (StartAsync will read the " +
                "current config directly when it runs)");
            return;
        }

        // GLOBAL CHANGES: adopted for FUTURE scheduling only. Every consumer (ProbeTimeout,
        // EffectiveFailuresBeforeUnmount, ArmIdleProbeTimer's backoff ladder,
        // EffectiveMountedProbeIntervalSeconds, ...) reads this field live at the moment it acts, so
        // simply swapping it is enough -- nothing here re-arms a timer or resets a counter for any
        // host, which is exactly the "a config save must never look like a successful probe"
        // requirement for a host whose own definition did not change.
        global = newConfig.Global;

        // HOST REMOVED: drain first if live, then stop being supervised entirely (Invariant I2).
        // Done before the per-host diff below so a key present in both old and new config is never
        // confused with one that just disappeared.
        foreach (var host in hosts.Values.Where(h => !newConfig.Hosts.ContainsKey(h.Key)).ToList())
        {
            await RemoveHostAsync(host, ct).ConfigureAwait(false);
        }

        var addedHosts = new List<HostRuntime>();
        foreach (var (key, newHostConfig) in newConfig.Hosts)
        {
            if (hosts.TryGetValue(key, out var host))
            {
                await ApplyHostConfigChangeAsync(host, newHostConfig, ct).ConfigureAwait(false);
            }
            else
            {
                // HOST ADDED: recorded now, begins supervision below -- exactly the same
                // Disabled-with-AdministrativelyEnabled construction StartAsync uses.
                var added = new HostRuntime
                {
                    Key = key,
                    Config = newHostConfig,
                    State = MountState.Disabled,
                    AdministrativelyEnabled = newHostConfig.Mount.Mode != MountMode.None,
                };
                hosts[key] = added;
                addedHosts.Add(added);
            }
        }

        foreach (var host in addedHosts)
        {
            if (host.AdministrativelyEnabled)
            {
                await EnableHostAsync(host, "config change: host added", ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The per-host diff for a key present in both the old and new config -- the actual
    /// classification work <see cref="ConfigChangedAsync"/>'s DESIGN field specifies. See that
    /// interface method's remarks for the four categories this implements.
    /// </summary>
    private async Task ApplyHostConfigChangeAsync(HostRuntime host, HostConfig newHostConfig, CancellationToken ct)
    {
        var oldHostConfig = host.Config;
        var oldMode = oldHostConfig.Mount.Mode;
        var newMode = newHostConfig.Mount.Mode;

        if (newMode != oldMode)
        {
            // ADR-022 decision 5: a mode change clears the remount record. The user's mount click was
            // about an on-demand drive; whatever tier the host has now is a different arrangement.
            host.RemountAfterResume = false;
        }

        if (newMode == MountMode.None && oldMode != MountMode.None)
        {
            // TIER CHANGE, anything -> none: drain (if live) and stop supervising -- but the host's
            // KEY still exists in config, so unlike a genuinely removed host it stays in `hosts`,
            // parked Disabled/administratively-disabled, exactly like a mode = "none" host at
            // startup (StartAsync's own `!host.AdministrativelyEnabled` skip).
            await DisableForModeNoneAsync(host, newHostConfig, ct).ConfigureAwait(false);
            return;
        }

        if (oldMode == MountMode.None)
        {
            // Covers both none -> none (nothing to do besides the Config swap -- an unreachable
            // combination of "mount fields changed" anyway, since ConfigValidator requires them
            // absent when mode = none) and none -> anything (begin supervising from Disabled,
            // exactly as at startup).
            host.Config = newHostConfig;
            if (newMode != MountMode.None)
            {
                host.AdministrativelyEnabled = true;
                if (host.State == MountState.Disabled)
                {
                    await EnableHostAsync(host, "config change: mount.mode changed from none", ct).ConfigureAwait(false);
                }
            }

            return;
        }

        // From here on, neither the old nor the new mode is "none".
        var mountAffecting = IsMountAffectingChange(oldHostConfig, newHostConfig);

        if (mountAffecting)
        {
            // MOUNT-AFFECTING: the live mount (if any) no longer matches intent -- drain and let
            // the normal re-enable/auto-mount path remount it, never directly (Invariant I1: it is
            // re-probed from Ready, same as any other arrival there).
            switch (host.State)
            {
                case MountState.Mounting:
                case MountState.Mounted:
                    // PendingConfig, not an immediate Config swap -- see its remarks: the drain
                    // (and any retry) must keep targeting the mount point that is ACTUALLY live.
                    host.PendingConfig = newHostConfig;
                    await BeginDrainAsync(host, "config change: mount-affecting field changed", DrainCause.ConfigChanged, ct)
                        .ConfigureAwait(false);
                    return;
                case MountState.Draining:
                    // Already draining for some other reason (probe failures, idle timeout, ...) --
                    // let that drain run its course; apply the new definition once it confirms.
                    host.PendingConfig = newHostConfig;
                    return;
                default:
                    // Ready / Unreachable / Disabled: no live mount to disturb. Nothing was
                    // draining, so it is safe to apply the new definition immediately. Force a
                    // fresh probe against the NEW identity now, rather than silently continuing to
                    // probe (or sitting on a stale Unreachable against) the OLD one until the next
                    // scheduled tick -- this is a genuine new probe, not a fabricated success, so
                    // resetting Backoff here does not violate "a config save must never look like a
                    // successful probe" (that protects hosts whose OWN definition did not change).
                    host.Config = newHostConfig;
                    if (host.State is MountState.Ready or MountState.Unreachable)
                    {
                        host.Backoff = BackoffState.Initial;
                        await ForceImmediateIdleProbeAsync(host, "config change: mount-affecting field changed", ct)
                            .ConfigureAwait(false);
                    }

                    return;
            }
        }

        // Not mount-affecting: no live mount depends on whatever changed, so it is always safe to
        // apply the new definition immediately.
        host.Config = newHostConfig;

        if (newMode != oldMode)
        {
            // TIER CHANGE, persistent <-> on-demand.
            if (newMode == MountMode.Persistent && host.State == MountState.Ready)
            {
                // on-demand -> persistent, not currently mounted, currently reachable: mount now.
                // TryBeginMountAsync's own success path already arms (or leaves unarmed, correctly
                // for the new persistent tier) the idle-unmount timer -- see the branch below.
                await TryBeginMountAsync(host, "config change: tier changed to persistent", ct).ConfigureAwait(false);
            }
            else if (newMode == MountMode.Persistent && host.State == MountState.Unreachable)
            {
                // on-demand -> persistent while the host is DARK. Found by independent testing of
                // bs-7ck; this branch did not exist and the promotion silently bought nothing.
                //
                // ADR-014 splits the recurring ladder by tier: an on-demand host in Unreachable is
                // not polled AT ALL, deliberately, so it sits dark until the user acts. Promoting
                // it to persistent is precisely the user saying "bring this up on your own" -- but
                // there is no timer running to ever notice the host came back, and nothing else
                // will ever start one, because starting one is what entering Unreachable does and
                // it is already there. The host stays Unreachable forever.
                //
                // Arming the ladder here is what the promotion actually buys. Backoff is left where
                // it stands rather than reset: the host's own reachability has not changed, and a
                // config save must never look like a successful probe.
                ArmIdleProbeTimer(host);
            }
            else if (host.State == MountState.Mounted)
            {
                // Already mounted through the flip, in EITHER direction -- no drain either way
                // (DESIGN: "keep any live mount"), but the idle-unmount timer is an on-demand-only
                // mechanic (rule 7) and must be re-evaluated for the NEW tier: persistent ->
                // on-demand needs one armed now (it never had one while persistent, so idle-unmount
                // could otherwise never fire until some later full mount cycle); on-demand ->
                // persistent needs any existing one disarmed (a persistent host must never
                // auto-unmount on an idle timeout). ArmIdleUnmountTimer disposes whatever is
                // already there before deciding, so one call is correct for both directions.
                ArmIdleUnmountTimer(host);
            }

            // persistent -> on-demand while NOT mounted, or any other combination: nothing further.
            // The Config swap above already means OnEnteredReadyAsync's tier check will not
            // auto-mount a persistent-turned-on-demand host on the NEXT arrival at Ready.
            return;
        }

        if (IsSchedulingAffectingChange(oldHostConfig, newHostConfig))
        {
            // SCHEDULING-AFFECTING: re-arm timers for the new cadence. Never drain, never touch a
            // failure counter or Backoff.
            switch (host.State)
            {
                case MountState.Ready:
                case MountState.Unreachable:
                    ArmIdleProbeTimer(host);
                    break;
                case MountState.Mounted:
                    ArmMountedProbeTimer(host);
                    ArmIdleUnmountTimer(host);
                    break;
            }

            return;
        }

        // COSMETIC / SESSION-ONLY: host.Config is already updated above; nothing else to do. These
        // fields already flow through their own Terminal-fragment rewrite path
        // (FragmentRewriteCoordinator), independently of the mount supervisor.
    }

    /// <summary>TIER CHANGE, anything -&gt; none, for a host whose key still exists in config
    /// (see <see cref="ApplyHostConfigChangeAsync"/>). Mirrors <see cref="RemoveHostAsync"/>'s
    /// Mounting/Mounted/Draining handling exactly, except the host stays in <see cref="hosts"/>
    /// afterward -- parked Disabled/administratively-disabled -- rather than being evicted.</summary>
    private async Task DisableForModeNoneAsync(HostRuntime host, HostConfig newHostConfig, CancellationToken ct)
    {
        host.AdministrativelyEnabled = false;

        switch (host.State)
        {
            case MountState.Mounting:
            case MountState.Mounted:
                host.PendingConfig = newHostConfig;
                await BeginDrainAsync(host, "config change: mount.mode set to none", DrainCause.ConfigChanged, ct)
                    .ConfigureAwait(false);
                return;
            case MountState.Draining:
                host.PendingConfig = newHostConfig;
                return;
            case MountState.Ready:
            case MountState.Unreachable:
                host.Config = newHostConfig;
                host.ProbeTimer?.Dispose();
                host.ProbeTimer = null;
                SetState(host, MountState.Disabled, "config change: mount.mode set to none");
                return;
            default:
                // Disabled already (and, defensively, Probing -- never actually observable here:
                // every arrival into Probing runs to completion, Ready or Unreachable, within the
                // single channel continuation that started it, and ConfigChangedAsync's own
                // continuation cannot interleave with that one -- see the class remarks on global
                // channel serialisation).
                host.Config = newHostConfig;
                return;
        }
    }

    /// <summary>HOST REMOVED (bs-7ck, Invariant I2): drains first if the host is currently
    /// Mounting/Mounted, then evicts it from <see cref="hosts"/> -- either immediately (nothing was
    /// live) or once the drain confirms (via <see cref="DrainCause.ConfigRemoved"/>,
    /// <see cref="CompleteDrainAsync"/>).</summary>
    private async Task RemoveHostAsync(HostRuntime host, CancellationToken ct)
    {
        host.AdministrativelyEnabled = false;

        switch (host.State)
        {
            case MountState.Mounting:
            case MountState.Mounted:
                await BeginDrainAsync(host, "config change: host removed", DrainCause.ConfigRemoved, ct).ConfigureAwait(false);
                return;
            case MountState.Draining:
                // Already draining for some other reason (a user unmount, probe failures, ...) --
                // upgrade the cause so CompleteDrainAsync evicts the host once it confirms, instead
                // of parking or re-enabling a host nothing in config describes any more.
                host.DrainCause = DrainCause.ConfigRemoved;
                return;
            case MountState.Ready:
            case MountState.Unreachable:
                host.ProbeTimer?.Dispose();
                host.ProbeTimer = null;
                SetState(host, MountState.Disabled, "config change: host removed");
                RemoveFromSupervision(host);
                return;
            default:
                // Disabled already (and, defensively, Probing -- see DisableForModeNoneAsync's
                // identical remark for why that is not actually reachable here).
                RemoveFromSupervision(host);
                return;
        }
    }

    /// <summary>Disposes every timer a <see cref="HostRuntime"/> can be holding and evicts it from
    /// <see cref="hosts"/>. The only place a host ever leaves <see cref="hosts"/> once added --
    /// called from <see cref="RemoveHostAsync"/> directly (nothing was live) and from
    /// <see cref="CompleteDrainAsync"/> (a live mount just finished draining because the host was
    /// removed from config).</summary>
    private void RemoveFromSupervision(HostRuntime host)
    {
        host.ProbeTimer?.Dispose();
        host.ProbeTimer = null;
        host.DeepProbeTimer?.Dispose();
        host.DeepProbeTimer = null;
        host.IdleUnmountTimer?.Dispose();
        host.IdleUnmountTimer = null;
        hosts.Remove(host.Key);
    }

    /// <summary>MOUNT-AFFECTING, per <see cref="ConfigChangedAsync"/>'s DESIGN classification: the
    /// live mount (if any) no longer matches intent, so it must drain and be re-probed before it can
    /// remount. Deliberately excludes <see cref="MountConfig.Mode"/> and
    /// <see cref="MountConfig.IdleUnmountSeconds"/> -- those are their own categories (tier change,
    /// scheduling), checked separately.</summary>
    private static bool IsMountAffectingChange(HostConfig oldConfig, HostConfig newConfig) =>
        oldConfig.Mount.Drive != newConfig.Mount.Drive ||
        oldConfig.Mount.RemotePath != newConfig.Mount.RemotePath ||
        oldConfig.Mount.VfsCacheMode != newConfig.Mount.VfsCacheMode ||
        oldConfig.Mount.NetworkMode != newConfig.Mount.NetworkMode ||
        oldConfig.Hostname != newConfig.Hostname ||
        oldConfig.Port != newConfig.Port ||
        oldConfig.User != newConfig.User ||
        oldConfig.IdentityFile != newConfig.IdentityFile;

    /// <summary>SCHEDULING-AFFECTING, per <see cref="ConfigChangedAsync"/>'s DESIGN classification:
    /// re-arms timers for the new cadence, never drains, never touches a failure counter.</summary>
    private static bool IsSchedulingAffectingChange(HostConfig oldConfig, HostConfig newConfig) =>
        oldConfig.Probe.IntervalSeconds != newConfig.Probe.IntervalSeconds ||
        oldConfig.Probe.DeepProbe != newConfig.Probe.DeepProbe ||
        oldConfig.Mount.IdleUnmountSeconds != newConfig.Mount.IdleUnmountSeconds;

    // ------------------------------------------------------------------------------------------
    // Shared helpers
    // ------------------------------------------------------------------------------------------

    private TimeSpan ProbeTimeout() => TimeSpan.FromSeconds(global.ProbeTimeoutSeconds);

    /// <summary>
    /// <see cref="IProbe.ProbeShallowAsync"/> promises never to throw (<c>HostProbe</c> maps every
    /// failure to an outcome), but the supervisor must not depend on that: an exception here would
    /// skip the code after it that either re-arms the host's probe timer or moves it out of a
    /// transitional state, stranding the host with nothing left to probe it (bs-x57). A throw that
    /// is not shutdown is therefore folded into a failed probe result, which every caller already
    /// handles. A throw while <paramref name="ct"/> is cancelled is shutdown and propagates.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two tokens, never one</b> (ADR-023). <paramref name="ct"/> is the supervisor's own token:
    /// its cancellation is SHUTDOWN, and it alone decides the catch filter and the check after the
    /// call. <paramref name="preemption"/> is a recurring tick's preemption token: it is linked into
    /// the token handed to the probe, so a suspend releases a hung probe at once, and it is NOT
    /// consulted here. The caller reads it to decide whether to discard the result. Handing the probe
    /// the linked token and filtering on the same token would make every suspend look like shutdown
    /// (or, with <c>HostProbe</c>, which returns <c>Cancelled</c> instead of throwing, like a failed probe).
    /// </para>
    /// <para>
    /// A probe that comes back while <paramref name="ct"/> is cancelled is shutdown whatever it
    /// returned (bs-4ca): <c>HostProbe</c> reports a cancelled probe as an outcome, and the handlers
    /// count every outcome but Success as a failure, so without this check a host one failure short of
    /// its threshold is drained as <see cref="DrainCause.Automatic"/> (with a toast) during shutdown.
    /// </para>
    /// </remarks>
    private async Task<ShallowProbeResult> ProbeShallowSafelyAsync(
        string hostname, int port, TimeSpan timeout, CancellationToken ct, CancellationToken preemption = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, preemption);
        try
        {
            var result = await probe.ProbeShallowAsync(hostname, port, timeout, linked.Token).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return result;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            if (preemption.IsCancellationRequested)
            {
                logger.LogDebug(ex, "Shallow probe of {Hostname}:{Port} threw after being preempted by a suspend", hostname, port);
            }
            else
            {
                logger.LogWarning(ex, "Shallow probe of {Hostname}:{Port} threw instead of returning a result; treating as failed", hostname, port);
            }

            return new ShallowProbeResult
            {
                Outcome = ShallowProbeOutcome.OtherFailure,
                Elapsed = TimeSpan.Zero,
                Detail = ex.Message,
            };
        }
        finally
        {
            // bs-6to: a probe that returned is progress, same as an rc reply.
            TouchLoopActivity();
        }
    }

    /// <summary>Deep-probe counterpart of <see cref="ProbeShallowSafelyAsync"/>. Matters most in
    /// <see cref="TryBeginMountAsync"/>, where the host is already in <c>Mounting</c> when the
    /// probe runs: a throw there would leave it in <c>Mounting</c> forever instead of draining.</summary>
    private async Task<DeepProbeResult> ProbeDeepSafelyAsync(
        string hostKey, TimeSpan timeout, CancellationToken ct, CancellationToken preemption = default)
    {
        // Two tokens, never one: see ProbeShallowSafelyAsync's remarks (ADR-023, bs-4ca).
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, preemption);
        try
        {
            var result = await probe.ProbeDeepAsync(hostKey, timeout, linked.Token).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return result;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            if (preemption.IsCancellationRequested)
            {
                logger.LogDebug(ex, "Deep probe of {HostKey} threw after being preempted by a suspend", hostKey);
            }
            else
            {
                logger.LogWarning(ex, "Deep probe of {HostKey} threw instead of returning a result; treating as failed", hostKey);
            }

            return new DeepProbeResult
            {
                Outcome = DeepProbeOutcome.Failed,
                Elapsed = TimeSpan.Zero,
                Detail = ex.Message,
            };
        }
        finally
        {
            TouchLoopActivity();
        }
    }

    private ITimer CreateOneShotTimer(TimeSpan due, Action callback) =>
        timeProvider.CreateTimer(_ => callback(), null, due, Timeout.InfiniteTimeSpan);
}
