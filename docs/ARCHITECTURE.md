# Bosun — Architecture

## 1. System context

Bosun does not implement SSH, SFTP, filesystem drivers, or terminal emulation.
It orchestrates four things that already exist:

```
        ┌──────────────────────────────────────────────┐
        │                   Bosun                      │
        │  (WPF tray app, user's interactive session)  │
        └───┬──────────┬─────────────┬─────────────┬───┘
            │          │             │             │
     writes │   HTTP   │      spawns │       reads │
   fragment │  rc API  │             │             │
            ▼          ▼             ▼             ▼
   ┌────────────┐ ┌─────────┐ ┌────────────┐ ┌──────────┐
   │  Windows   │ │ rclone  │ │  wt.exe /  │ │  Win32   │
   │  Terminal  │ │  rcd    │ │  ssh.exe   │ │ process  │
   │            │ │    ↓    │ │            │ │ + TCP    │
   │            │ │  WinFsp │ │            │ │  tables  │
   └────────────┘ └─────────┘ └────────────┘ └──────────┘
```

The reliability of Bosun is bounded by WinFsp and rclone. Bosun's job is to
never put those components in a state that harms the user — principally, to
never leave a mounted drive letter pointing at an unreachable host.

Since ADR-021 there is a fifth, independent concern: Claude Code sessions running
in Windows Terminal tabs. Bosun learns about them from a hook helper
(`bosun-claude-hook.exe`) that it ships inside a Claude Code plugin, and it
reopens them through `wt.exe` after a reboot or a Terminal exit. Nothing in that
path touches a mount, `rclone rcd`, or the supervisor.

```
   Claude Code process ──hooks──► bosun-claude-hook.exe ──writes──► %LOCALAPPDATA%\Bosun\claude-sessions\*.json
                                                                                  │
   wt.exe new-tab ... claude --resume <id>  ◄──── Bosun (ClaudeSessionService) ◄──┘ reads, classifies, deletes
```

## 2. Process model

**One process.** A WPF application with a main window, a tray icon, and a
`BackgroundService` host running the supervisor loop.

The window is optional, not incidental (**ADR-018**): Bosun starts to tray with
no window when launched at login, shows the window when launched by hand, and
hides rather than exits when the window is closed. The tray icon is the
always-on surface and keeps every obligation ADR-012 gave it, because the window
is closed most of the time. Exactly one instance may run — a second launch
focuses the first and exits (`bs-2wa`), which is what keeps two supervisors from
racing for the same drive letters.

This is forced by a Windows constraint: **drive letters are scoped to a logon
session**. A Windows Service running as `SYSTEM` can mount `P:` successfully and
the user's Explorer will not see it. The supervisor therefore has to run as the
user, in the interactive session.

`rclone rcd` runs as a child process, started by Bosun at launch and stopped at
exit. Bosun talks to it over `http://127.0.0.1:<port>` and never spawns
`rclone mount` directly.

The child is assigned to a Win32 Job Object with `KILL_ON_JOB_CLOSE`, so the OS ends it when Bosun
dies for any reason (ADR-020 §1). Before each launch Bosun checks the rc port: a stale `rcd` of its
own is killed; any other holder is reported by PID and image path and never killed (ADR-020 §2).

```
Bosun.exe                      (single instance, named mutex — bs-2wa)
├── App (WPF, ShutdownMode=OnExplicitShutdown)
├── MainWindow  ── shown on manual launch, hidden at login; close hides
├── TrayIcon (H.NotifyIcon) ──► show/hide MainWindow, per-host actions, exit
└── IHost
    ├── RcloneProcessService   : IHostedService    (owns rclone rcd lifetime)
    ├── SupervisorService      : BackgroundService (the loop, §4)
    ├── SessionMonitorService  : BackgroundService (ssh.exe enumeration)
    ├── SystemEventService     : IHostedService    (power/network/session hooks)
    ├── FragmentWriterService  : IHostedService    (writes WT fragment on change)
    └── ClaudeSessionService   (Claude Code session records, ADR-021; started and
                                stopped by StartupOrchestrator, not AddHostedService)
```

`bosun-claude-hook.exe` is a separate, short-lived process started by Claude
Code, never by Bosun. It does not need Bosun to be running: records written while
Bosun is down are evaluated at its next start.

## 3. Component responsibilities

### `IHostConfigStore`
Loads and watches `config/hosts.toml`. Emits a change event on write. Validates
drive-letter collisions and duplicate host names at load time; refuses to apply
an invalid config rather than partially applying it.

### `IRcloneClient`
Thin typed wrapper over the rclone rc HTTP API. The endpoints in use:

| Endpoint | Purpose |
|---|---|
| `core/version` | Startup health check |
| `config/create`, `config/get` | Ensure an sftp remote exists per host |
| `mount/mount` | Mount a remote at a drive letter |
| `mount/unmount` | Unmount |
| `mount/listmounts` | Reconcile actual state against intended state |
| `operations/list` | Deep probe — verifies SFTP works, not just TCP |
| `vfs/list`, `vfs/stats`, `vfs/queue` | Idle-unmount activity and upload safety for on-demand hosts (§4 rule 7). Read-only; never `inUse` |

### `IProbe`
Two levels:

- **Shallow** — TCP connect to `host:port`, 5s timeout. Cheap. This is the
  recurring liveness check.
- **Deep** — `operations/list` on the remote root, depth 1. Proves
  authentication and the SFTP subsystem work. Run once before each transition
  into `Mounting`, **and** on its own recurring (slower) cadence for every host
  already in `Mounted` — see §4 rule 3a and ADR-016.

TCP success does not imply SFTP success. Never mount on a shallow probe alone —
and, symmetrically, a shallow probe continuing to succeed on a `Mounted` host
does not imply the mount is still alive: the TCP-level host can stay reachable
after the SSH channel underneath the mount has died (a sleep/resume cycle, a
network blip too short to trip the shallow-failure threshold but long enough to
kill the channel). That is what the deep probe on `Mounted` hosts exists to
catch, and it is why it is a *second*, independent probe rather than a
replacement for the shallow one.

Probe scheduling depends on state, not only on configuration: an idle host may
be left unpolled, a **mounted** host never is — for either probe. See §4 rule 3
/ rule 3a and ADR-011 / ADR-016.

### `IMountSupervisor`
Owns the state machine in §4. The only component permitted to call
`mount/mount` and `mount/unmount`.

### `IFragmentWriter`
Serialises the host list to Windows Terminal's fragment schema at
`%LOCALAPPDATA%\Microsoft\Windows Terminal\Fragments\Bosun\bosun.json`.

The path was verified against Microsoft Learn and is recorded in full — with the
all-users alternative, the UUIDv5 profile-GUID derivation, and Terminal's silent
fragment-failure behaviour — in ADR-006's amendment. Note the `Microsoft\`
segment. The Store-vs-unpackaged distinction an earlier version of this section
warned about does not apply to Bosun; ADR-006 explains why.

Each host becomes a profile whose `commandline` is either:

```
ssh -i "<identity_file>" -p <port> <user>@<hostname>                                 # tmux = false
ssh -t -i "<identity_file>" -p <port> <user>@<hostname> tmux new -A -s <session>      # tmux = true
```

The four connection fields come straight from the host's `hosts.toml` entry — the
same four the Add Host dialog collects. No `~/.ssh/config` alias is required.

This reverses ADR-013's original Decision 1, which emitted the bare config key as
an `ssh_config` alias; see **ADR-013 Amendment 1** for why that failed in practice
and why `ProxyJump` still works without it. The config key remains Bosun's
identity — it is still the profile GUID's hash input — but it is no longer a
network name.

Bosun emits an explicit profile `guid`, derived from the config key rather than
letting Terminal derive one from `display_name` — otherwise renaming a host
silently orphans the user's per-profile customisation. Also ADR-013.

wrapped in the reconnect loop script if `session.reconnect = true`.

### `ISessionMonitor`
Enumerates `ssh.exe` processes, resolves their command lines via CIM, and
correlates them to configured hosts. Cross-references `GetExtendedTcpTable` for
socket state. This is the one component with meaningful P/Invoke; keep it behind
the interface and fake it in tests.

### Claude Code session restore (ADR-021)

Five parts, each behind its own seam:

| Part | Responsibility | Tested with |
|---|---|---|
| `bosun-claude-hook.exe` (`src/Bosun.ClaudeHook/`) | Hook payload on stdin, plus its own parent chain, environment and token, becomes one record file (ADR-021 Decision 4). Never writes stdout; exits 0 on every path within an internal deadline. Its P/Invoke lives in one file of its own. | A pure mapping function from (payload, environment, process facts) to record; no real files |
| `IClaudePluginWriter` | Generates the `bosun` directory marketplace and the `bosun-claude-sessions` plugin (`plugin.json`, `hooks/hooks.json` in exec form with `${CLAUDE_PLUGIN_ROOT}` and a `timeout` per hook, the helper under a versioned file name) in `%LOCALAPPDATA%\Bosun\claude-plugin\`. Rewrites only when the content changed, atomically, like `IFragmentWriter`. | A temp directory |
| `ClaudeSessionClassifier` | Pure function: records, a live-process snapshot (PID, creation time, image), the current logon session, whether the session is ending, and the clock in; one outcome per record out, per ADR-021 Decision 5. | Exhaustive table tests, written independently by `test-author` |
| `wt.exe` argument builder | Pure function from dispatched records to one `wt.exe` argument list (ADR-021 Decision 6). | Quoting tests: paths with spaces, a `;` in a path (Terminal treats a bare `;` as a subcommand separator), each tab root's keep-open form, a tab root that is Claude Code itself, a missing profile ID |
| `ClaudeSessionService` | Started and stopped by `StartupOrchestrator`. Timer; process snapshot through `ISessionMonitor`'s interop layer; the startup restore step; detection of a newly launched `WindowsTerminal.exe` for pending restores; dispatch, confirmation and timeout; record deletion; the restore toast; the window's session list and "not reporting" state (Decision 9). Every tick is wrapped so a fault reports into the health model (ADR-020 §4) instead of escaping. Restore goes through `IExternalLauncher`. | Fakes for the snapshot, the clock, the records store and the launcher |

### `ISystemEventSource`
Wraps three .NET event sources:

| Event | Reaction |
|---|---|
| `PowerModeChanged` → `Suspend` | Unmount all, enter `Suspended`; **no mount happens while suspended** (§4 rule 5) |
| `PowerModeChanged` → `Resume` | **Re-derive** (below) |
| `NetworkAddressChanged` | **Re-derive** (below) |
| `SessionSwitch` → `SessionLock` | No action (v1); reserved |

**Re-derive**, per ADR-017: after any power or network transition the supervisor
does not trust its own mounted state. It reconciles against `mount/listmounts`
first, then force-probes every host still `Mounted` with **both** the shallow and
the deep probe, then re-probes everything else immediately, bypassing backoff.

Resume and network change take the *same* path. An earlier version of this table
gave resume the weaker treatment; that asymmetry was backwards — a network change
means the path to the host may have altered, a resume means everything may have,
including `rclone rcd` and WinFsp themselves — and it is what let `bs-brv` look
correct in review.

**The network-change event is debounced, and the debounce is bounded**
(`Win32SystemEventSource`; `bs-u4a` item 2). Windows raises
`NetworkAddressChanged` several times for one physical transition (adapter down,
DHCP, adapter up), so the source coalesces a burst into one event. Two fixed
numbers govern it; neither is configurable:

| Number | Value | Meaning |
|---|---|---|
| Quiet period | 750 ms | The event fires once no further raw event has arrived for this long (trailing edge: each raw event restarts the window). |
| Maximum deferral | 5 s | The event fires **at the latest** 5 s after the *first* raw event of a burst, however many more arrive. |

So "re-probe all immediately" in the table above means 750 ms after the network
settles, or 5 s after it started changing if it never settles. Without the cap a
link flapping faster than the quiet period would defer the event indefinitely —
and a network interruption is one of the two failure modes the project exists
for. A burst begins with the first raw event after the previous public event;
firing ends it, so the next raw event starts a new burst with a fresh 5 s.

Re-derivation is gated on `rclone rcd` answering `core/version`. If it does not,
nothing is reconciled or drained; the supervisor waits for the ordinary cadence
rather than acting on state it cannot verify.

The suspend handler must complete quickly. Windows does not wait indefinitely:
Microsoft's `PBT_APMSUSPEND` reference says "The system allows approximately two
seconds for an application to handle this notification. If an application is
still performing operations after its time allotment has expired, the system may
interrupt the application"
(<https://learn.microsoft.com/en-us/windows/win32/power/pbt-apmsuspend>).
`global.suspend_unmount_timeout_seconds` therefore defaults to **3** (`bs-ajh`):
the documented two seconds plus one second of headroom. A longer default would
only hold `SystemEvents`' single notification thread past the point Windows
stops waiting. Issue unmounts with that short timeout and accept that a forced
unmount may be needed on resume — that is what re-derivation is for, and it is
now implemented rather than aspirational.

**When the supervisor faults during suspend** (`bs-3al`; `bs-u4a` item 5). The
suspend handler runs on `Microsoft.Win32.SystemEvents`' single notification
thread, which has no `catch` above it: an exception that escaped the handler
would terminate the process, and a dead process never runs resume-side
re-derivation. So the handler never throws. `SuspendAsync` can fault — the
supervisor's channel re-surfaces any exception raised while it drains as a
faulted task — and the adapter handles that without rethrowing:

- Fault *inside* the budget: logged at error level with the exception, and the
  handler returns to Windows at once. It does not retry and does not compensate.
- Budget elapsed first: logged as a warning, and the handler returns. The
  in-flight call is left running; if it faults later, that is logged at error
  level too rather than left as an unobserved task exception.
- The same holds for resume and network change, which are fire-and-forget: a
  faulted call is logged and nothing more.

Whatever the faulted suspend did not unmount is recovered on resume by
re-derivation (ADR-017), which is why that path is not optional. A fault is
visible in the log only; the suspend handler does not report it into the health
model. If the container cannot construct the supervisor at all (`bs-4js`), no
system-event subscription is made: `StartupOrchestrator` creates the adapter only
once the supervisor object exists, and the inert stand-in used otherwise mounts
nothing, so there is nothing to unmount. (A supervisor that was constructed but
whose initial `StartAsync` failed *is* subscribed, and a suspend then sweeps whatever
that supervisor holds.)

### Tray notifications

The tray raises a balloon (through `TrayIconController.ShowBalloon`, the one place a balloon
is shown) for two things:

- **A mounted drive lost without anyone asking for it** (`UnexpectedUnmountDetector`).
  Decided by the typed `DrainCause` on the `Mounted -> Draining` transition, never by the
  trigger text. Only `DrainCause.Automatic` toasts: a shallow-probe or deep-probe failure
  threshold, a forced deep-probe failure after a power or network transition, or
  reconciliation drift. Silent, but still logged and shown in the window: `UserUnmount`,
  `Suspend`, `IdleTimeout` (the on-demand idle timer), `Repair` (the user's "Unmount all and
  re-probe"), `ConfigChanged` and `ConfigRemoved` (a host edit or removal), and
  `MountFailure` (an attempt that never reached `Mounted`). Decided 2026-11-01 (bs-p97).
  Not yet silent: reconciliation after a user "Restart rclone" (bs-z9e). Restarting rcd does
  not go through the supervisor, which learns of it only as `OnRcloneRestartedAsync`
  (the same call a crash recovery makes), so that drain is still `Automatic`.
- **Bosun entering a Faulted state** (`DegradedStateNotifier`, bs-ww9.1.1; ADR-012's channel
  table). One balloon, titled "Bosun" with the issue's title as text, when a Faulted issue
  code newly appears on `IAppHealth`. Degraded issues never toast. A code that stays Faulted
  does not toast again; it can only after it has cleared (or dropped to Degraded) and come
  back. While `AppHealth.IsStarting` is true nothing is raised, and a fault seen then is
  toasted once when that ends. The always-on surface remains the tray icon (ADR-012
  Decision 3); the toast is the one-shot.

## 4. The mount state machine

One instance per configured host. This is the core of the application; get it
right before building any UI.

```
                    ┌──────────┐
         ┌─────────►│ Disabled │◄──────── user disables / mode = "none"
         │          └────┬─────┘
         │               │ enable
         │               ▼
         │          ┌──────────┐  probe fail   ┌─────────────┐
         │     ┌───►│ Probing  ├──────────────►│ Unreachable │
         │     │    └────┬─────┘               └──────┬──────┘
         │     │         │ probe ok                   │ backoff elapsed
         │     │         ▼                            │ / network change
         │     │    ┌──────────┐                      │ / resume
         │     │    │  Ready   │◄─────────────────────┘
         │     │    └────┬─────┘
         │     │         │ mount requested
         │     │         │ (persistent: automatic
         │     │         │  on-demand: user action)
         │     │         ▼
         │     │    ┌──────────┐  deep probe fail
         │     │    │ Mounting ├───────────────┐
         │     │    └────┬─────┘               │
         │     │         │ mount ok            │
         │     │         ▼                     │
         │     │    ┌──────────┐               │
         │     │    │ Mounted  │               │
         │     │    └────┬─────┘               │
         │     │         │ N probe failures    │
         │     │         │ / idle timeout      │
         │     │         │ / suspend           │
         │     │         │ / user unmount      │
         │     │         ▼                     │
         │     │    ┌──────────┐               │
         │     └────┤ Draining │◄──────────────┘
         │          └────┬─────┘
         │               │ unmount confirmed
         └───────────────┘
```

### Transition rules

1. `Mounting` is reachable **only** from `Ready`. There is no path from
   `Unreachable` or `Probing` directly to `Mounting`. (Invariant I1.)
2. Entering `Mounting` triggers a deep probe. Deep probe failure routes to
   `Draining`, not to `Mounted`.

2a. **A confirmed `mount/mount` is not yet `Mounted`: the drive must read back as
    a network drive (Invariant I7, `bs-dtk`).** Once `mount/mount` returns, the
    supervisor reads the drive's type through `IDriveTypeReader` (production:
    `new DriveInfo(letter).DriveType`; pure BCL, no P/Invoke). Only
    `DriveType.Network` proceeds to `Mounted` (the verified baseline for a
    correct `--network-mode` mount is `Network` with `DriveFormat` `FUSE-rclone`).
    Anything else (`Fixed`, `Removable`, `NoRootDirectory`, `Unknown`, ...) or a
    read that throws is treated as a **failed mount**: `Mounting -> Draining`,
    the existing mount-failure edge (no new transition), then `mount/unmount`
    through the supervisor. A drive presented as a fixed disk makes Explorer
    behave pathologically, so the supervisor does not keep it. The reason is
    recorded in `LastMountFailureReason` and counted in `ConsecutiveMountFailures`,
    which is the route ADR-012 Decision 3 already uses for a host that answers TCP
    but cannot be mounted: the window row reads "mount failed N times; last error:
    ...", and a persistent host puts the tray icon in the error state. The retry
    is paced on the mount-retry ladder (see Backoff), not immediate.

    The check **fails closed**: a read that throws, or `NoRootDirectory` straight
    after a successful `mount/mount`, cannot be shown to satisfy I7, so it drains
    like any other mismatch, with a distinct reason ("could not verify ...",
    "does not exist"). Only the type is read, never `DriveFormat`, `IsReady` or
    the size, because those touch the volume (see rule 3a). It applies to the
    mount this supervisor just made; a mount adopted at startup
    (Reconciliation) is not re-checked.
3. While `Mounted`, a host is **always** shallow-probed, whatever
   `probe.interval_seconds` says. The effective mounted interval is:

   ```
   interval_seconds > 0  →  min(interval_seconds, global.mounted_probe_interval_seconds)
   interval_seconds == 0 →  global.mounted_probe_interval_seconds
   ```

   `failures_before_unmount` consecutive failures (default 3) → `Draining`.

   `probe.interval_seconds = 0` means "do not poll this host while it is idle"
   (ADR-008). It must never mean "do not poll it while it is mounted": a mounted
   host that is not probed can never accumulate failures, never reaches
   `Draining`, and leaves a drive letter pointing at a dead host — the exact
   failure this project exists to prevent (Invariant I2). The upper clamp exists
   for the same reason: a host configured with a long idle interval must not
   inherit an unbounded unmount latency once mounted. See ADR-011.

3a. While `Mounted`, a host is **also** deep-probed (`operations/list`), on its
    own cadence: `global.mounted_deep_probe_interval_seconds` (default 300),
    independent of and slower than the shallow cadence in rule 3. Shallow
    success alone is not evidence the mount is alive (§3) — only the deep probe
    proves the SSH channel underneath the mount still works. Deep-probe
    failures accumulate on their **own counter**, `ConsecutiveDeepProbeFailures`
    — never the shallow probe's `ConsecutiveMountedFailures` — and drain after
    a fixed **2** consecutive deep-probe failures, a threshold deliberately
    smaller than the configurable `failures_before_unmount` (default 3): a
    dead-mount signal from `operations/list` is stronger evidence than a TCP
    timeout, so it should not need as many consecutive confirmations. A
    deep-probe success resets `ConsecutiveDeepProbeFailures` to zero,
    independently of whatever the shallow counter reads. The two counters are
    kept structurally separate — not merely started at zero independently —
    because the two probes fire on independent timers that can land on the
    same tick (the shipped defaults, 60s shallow / 300s deep, coincide exactly
    every 5th shallow tick for the life of every mount): sharing one counter
    between them makes the outcome of that coincidence depend on which of the
    two timer continuations happens to be processed first, an order-dependent
    race rather than a deterministic policy. Never implemented by enumerating
    the drive letter (`DriveInfo`, `Directory.GetFiles`, etc.) — that is
    precisely the call that can hang (the one exception is rule 2a's single
    `DriveInfo.DriveType` read, taken once right after `mount/mount` succeeded
    against a host that has just passed its deep probe, never on the recurring
    probe path), and the supervisor's channel is globally
    serialised (see `MountSupervisor`'s class remarks), so one hung enumeration
    would stall every host, not just this one. See ADR-016.
4. `Draining` calls `mount/unmount`. If unmount does not confirm within the
   timeout, escalate to a forced unmount, then verify against `mount/listmounts`.
   Never leave the state machine believing a drive is gone when it is not.
5. On suspend, every host in `Mounted` or `Mounting` goes to `Draining`, then
   `Disabled`.

   A host that is **already `Draining`** for an ordinary reason when suspend
   arrives (a probe-failure threshold, an idle timeout, a user unmount, a repair)
   keeps its drain cause — a user unmount still parks it — but its forced-unmount
   escalation deadline becomes the **suspend budget**
   (`global.suspend_unmount_timeout_seconds`, counted from the moment suspend
   arrives) instead of the ordinary 10 s `DrainConfirmTimeout`, which could fall
   after the machine has gone to sleep. The supervisor re-issues the unmount at
   once and aims the next check at the new deadline. The change only ever
   **shortens** a deadline: a drain whose ordinary deadline is already sooner than
   suspend arrival plus the budget keeps it, and a second suspend never moves a
   deadline later. A host already escalated is left alone (it is already on the
   forced path). `bs-u4a` item 4.

   On resume, the supervisor **does not assume suspend succeeded** — it is
   best-effort by design, bounded by a budget that can elapse. It reconciles
   against `mount/listmounts` first; any host still `Mounted` is force-probed
   shallow *and* deep, and drains on a single deep failure once `rclone rcd` has
   answered `core/version`. Everything else previously enabled is re-probed
   immediately: a `Disabled` host through `Probing`, a `Ready` or `Unreachable`
   host in place (rule 10).
   A network address change takes the identical path. See ADR-017.

   **No mount happens while the supervisor is suspended** (Invariant I8,
   `bs-bv0`). The suspend handler runs once; a mount created after it, in the
   seconds before the machine sleeps, survives the sleep. So the suspended flag is
   checked at the one place a host enters `Mounting` — the entry guard of
   `TryBeginMountAsync`, the only caller of `mount/mount` — which covers every
   route in at once: the persistent auto-mount on arriving at `Ready` (by the idle
   probe, a network change, "retry now" or a config change), an explicit mount
   request for a `Ready` host, the mounting-became-available sweep, and any route
   added later. The mount-retry timer (a failed mount's paced re-enable) is also
   skipped while suspended, so it does not probe in those seconds either: the host
   stays `Disabled`, and resume re-enables it. A host held back this way simply
   stays `Ready` (no new transition, no `Mounting`, no rc call).

   At resume the flag is cleared and the held-back hosts are picked up through the
   re-probe above, not around it. A persistent, non-parked host found `Ready` is
   force-probed again like any other `Ready` host (ADR-017: a state that survived
   a transition is not trusted), and only if that probe leaves it `Ready` does it
   enter `Mounting`, with the deep probe of rule 2 first (rule 1 and Invariant I1
   hold). A host whose probe fails goes `Unreachable` instead and mounts when it
   next becomes `Ready`. A parked host (rule 9) and an on-demand host (rule 6) are
   not mounted by resume; a `Disabled` or `Unreachable` host reaches `Ready`
   through `Probing` or the backoff ladder as before and the ordinary persistent
   auto-mount applies. (Re-mounting an on-demand host that suspend dropped is rule 9a.)
6. On-demand hosts rest in `Ready`, not `Mounted`. They do not auto-mount. The one
   exception is ADR-022: an on-demand host the user mounted before a suspend is
   mounted again after resume if its first post-resume probe reaches `Ready` (rule 9a
   covers what happens when mounting is unavailable at that moment).
7. On-demand hosts with `idle_unmount_seconds > 0` transition
   `Mounted → Draining` after that period without filesystem activity (bs-obf).

   **What counts as activity.** Only what rclone itself reports, through its rc API
   (`vfs/list`, `vfs/stats`, `vfs/queue`, over loopback HTTP to `rclone rcd` — never by
   touching the drive letter, rule 3a). While a host is `Mounted`, the supervisor samples
   `vfs/stats` on the mounted shallow-probe cadence (rule 3; no extra timer per host) and
   restarts the idle clock whenever any of these changed since the previous sample:
   `diskCache.bytesUsed`, `diskCache.files`, `diskCache.uploadsInProgress`,
   `diskCache.uploadsQueued`, `metadataCache.dirs`, `metadataCache.files`. The first
   sample after a mount (taken at the mount itself) is the baseline; it is thrown away
   on every unmount, so a remount never compares against the previous mount. `vfs/stats`'s
   `inUse` is never read: it is a reference count on the VFS object, not a count of open
   files. Only on-demand hosts with `idle_unmount_seconds > 0` are polled at all.

   **Which VFS.** rclone keys a VFS by the `fs` string of the mount. The supervisor
   looks for the exact `fs` it passed to `mount/mount` (`bosun-<key>:<remote_path>`) among the
   names `vfs/list` returns. No exact match is not guessed at: no activity is recorded and
   the idle unmount does not happen, with a log line saying why.

   **Safety when the clock fires.** The supervisor reads `vfs/stats` and `vfs/queue` once more
   and unmounts only if the VFS can be read, `uploadsInProgress` and `uploadsQueued` are
   zero, the upload queue is empty, `erroredFiles` is zero, and nothing changed since the last
   sample. Otherwise it skips the unmount, logs the reason and restarts the clock for a full
   period. Failing to read the VFS (rcd not answering, no matching VFS name, `diskCache`
   absent because the cache mode is off) counts as a reason: the rule fails closed, because
   unmounting a drive that has pending writes loses them. A clock that fires with no earlier
   sample to compare against (an idle period shorter than the probe interval, and a baseline
   that could not be taken) takes one and waits one more period. Every such poll runs in the
   supervisor's serialised channel under one probe timeout (`global.probe_timeout_seconds`), so
   a hung rcd cannot stall it longer than a hung probe can.

   **Failed uploads.** `diskCache.erroredFiles > 0` keeps the drive up (above) and is shown on
   that host's status row as "N files failed to upload". It is host-specific, so it is on
   the row, not in the app-wide banner, and it does not change the tray icon.

   **Known limit.** Re-browsing content rclone has cached, and re-reading files it has
   cached, do not touch any counter above and are invisible to rclone rc. They do **not**
   keep the drive up: a user who only re-reads cached files is idle by this definition.
   Cache expiry (`--dir-cache-time`, `--vfs-cache-max-age`) also moves these counters, so it
   can restart the clock — in the safe direction, keeping the drive up a little longer.
   The activity is seen at the granularity of the probe interval: the clock restarts up to one
   mounted-probe interval after the activity itself.

8. **`RequestMountAsync` on an `Unreachable` host triggers an immediate probe**
   and proceeds to mount if it passes. It is never a silent no-op — a tray menu
   item that does nothing, forever, with no error is not a defensible behaviour.
   This does not weaken rule 1: the click causes a *probe*, and `Mounting` is
   still reached only from `Ready` after a passing deep probe. See ADR-014.
9. **A user-requested unmount parks the host.** It stays unmounted until an
   explicit user remount or an application restart — including for `persistent`
   hosts, which would otherwise auto-mount again on their very next arrival at
   `Ready` and make the tray's Unmount item look like it did nothing. A parked
   host **keeps probing**, so the tray can show live reachability; it simply does
   not auto-mount. Rule 6 governs a tier's *resting* state, not the override of an
   explicit command. See ADR-015.

   A **config reload does not un-park** (ADR-015's amendment). It used to, back
   when a reload meant deliberately editing `hosts.toml` by hand. Since ADR-019
   the window saves that file on every host edit, so un-parking on reload would
   mean a drive the user explicitly unmounted returns when they change some other
   host's tab colour. Only an explicit action clears what an explicit action set.

   The **repair** "Unmount all & re-probe" (`RepairAllAsync`, ADR-020's `bs-aoz`
   amendment) neither parks nor un-parks. It drains every mounted host with its own
   drain cause; a persistent host then re-enters through `Probing -> Ready` and
   remounts after a fresh probe, an on-demand host rests in `Ready`, and a host the
   user parked earlier stays parked.

9a. **An on-demand drive the user had mounted comes back after a sleep**
    (ADR-022, `bs-hrg`). The reverse of rule 9: a suspend must not undo a user's
    mount any more than a drain may undo a user's unmount.

    - **Recorded at suspend.** The suspend handler notes, in memory, each
      `on-demand` host that is `Mounted` or `Mounting`, then drains it under rule 5
      as before. A host already `Draining` or `Disabled` when suspend arrives is not
      noted (it was not up), and a drain for any other cause (probe failures, the
      idle timeout, a repair, a config change, a failed mount, a deep probe after a
      network change) never sets the record. A `persistent` host is not noted; it
      comes back by its own tier rule (rule 6).
    - **Resolved by the first post-resume probe.** The record belongs to the host's
      first probe after the suspend drain: the `Disabled → Probing` that resume
      runs, or, if the unmount had not yet confirmed at resume, the ordinary
      re-enable that follows the drain. If that probe reaches `Ready` and nothing
      local is in the way, the host enters `Mounting` exactly as a user click would
      (rule 1; the deep probe of rule 2 comes first). If it does not reach `Ready`,
      the host is `Unreachable`, stays unmounted and is not polled (rule 8's click
      still works), and the record is dropped; nothing mounts it later, on whatever
      network the machine has moved to.
    - **Kept while something local is in the way** (ADR-022's amendment of
      2026-11-01). If the probe reaches `Ready` but mounting is unavailable
      (`MountingAvailability`: `rclone rcd` restarting, WinFsp missing), or `rclone
      rcd` failed ADR-017's `core/version` gate at the resume re-derivation, or the
      system is suspended again (rule 5), the remount is not attempted and the record
      is **kept**. These are facts about this machine, not about the host, and say
      nothing about whether the user still wants the drive. The host rests in
      `Ready`. When the gate clears, the supervisor probes the host **once more**,
      in place (rule 10: a `Ready` or `Unreachable` host does not go through
      `Probing`): if that probe leaves it `Ready`, it enters `Mounting` as above, and
      if not the record is dropped. The gate clears when `SetMountingAvailabilityAsync`
      reports available, when `OnRcloneRestartedAsync` finds `core/version` answering,
      or when a reconciliation tick finds it answering; and a resume after a second
      sleep resolves it with its own re-probe. A closed gate never causes a probe, and
      nothing mounts while suspended (rule 5).
    - **A waiting record expires ten minutes after the resume** (ADR-022's second
      amendment of 2026-11-01, `bs-ane`). If the record is still waiting when
      `RemountRecordWaitLimit` (10 minutes, a constant, not config) has passed since the
      resume, it is dropped with an Info log line and the host is not remounted
      automatically; it rests as an on-demand host normally does, and a click mounts it
      as usual. A second suspend and resume restarts the ten minutes from the latest
      resume (time spent asleep does not count). The deadline is stored with the record
      and checked where the record would be resolved (the sweep, before its gates; the
      resume and first-probe resolution; suspend) and on the 30-second reconciliation
      tick, which drops an expired record so it does not linger or keep polling
      `core/version`. There is no timer.
    - **A remount attempt spends the record.** It is cleared as the host enters
      `Mounting`, so a remount whose own deep probe or `mount/mount` fails while
      mounting is available is not retried, and a user click that mounts the host
      first supersedes the record.
    - **The idle timer starts fresh.** The remount is a new mount (rule 7), so
      `idle_unmount_seconds` counts from it, not from the time already idle before
      the sleep.
    - **Cleared by** the remount attempt, a failed first probe, a user unmount
      (including a click while the suspend drain is still running, and a click while
      the record waits), the host's mode changing, the host being removed from the
      config, its wait limit passing, or Bosun restarting. It is never persisted, and a network change
      neither spends nor sets it.

    No new transition: the path is `Disabled → Probing → Ready → Mounting`, which
    rules 1, 2 and 10 already describe.

10. **`Probing` is one-shot** (`bs-but`). A host enters `Probing` only from
    `Disabled`, for the initial check that follows an "enable": at startup, on
    resume for a host the suspend left `Disabled`, after a drain's automatic or
    paced re-enable (including the repair and mount-retry paths above), and when a
    host is added to the config or changed from `mode = "none"`. The probe's
    outcome leaves `Probing` for `Ready` or `Unreachable`, and it is not entered
    again from either.

    **Recurring probes run in place and log no transition.** The idle probe on a
    `Ready` or `Unreachable` host (the backoff ladder, an immediate re-probe on
    resume or network change, "retry now") and the shallow and deep probes on a
    `Mounted` host all happen without leaving the state they run in. Only a probe
    whose *result* changes the state produces a transition — `Ready → Unreachable`
    and `Unreachable → Ready` on the idle probe, `Mounted → Draining` on the
    failure threshold — so a state change in the transition log means something
    happened. If every recurring probe re-entered `Probing`, the log (the primary
    triage artifact in `docs/OPERATIONS.md`) would record a transition per probe
    per host and bury the ones that matter. Anywhere else in this document that
    says a host is "probed" without naming a state change means this in-place probe.

### Backoff

An `Unreachable` host's next probe, which runs in place (rule 10), is paced by the
configured backoff ladder, default
`[5, 15, 30, 60, 300]` seconds, holding at the last value. Backoff is **reset to
zero** by a network address change, a power resume, or an explicit user "retry
now". This is what makes recovery after a resume or a network interruption feel
immediate rather than sluggish.

**The ladder runs for `persistent` hosts only.** An **on-demand** host in
`Unreachable` is not polled at all; it stays dark until the user acts, and rule 8
is what makes that safe. This is what makes ADR-008's and ADR-011's promise —
"on-demand hosts generate no traffic until the user mounts one" — literally true
rather than approximately true. Persistent hosts must keep polling: that is the
mechanism by which a drive returns on its own, which `docs/OPERATIONS.md` T1 (the
wake where the network is not up yet) and T2 (the link coming back) both exercise.
See ADR-014.

**Resume and network change do NOT inherit the tier split.** They reset backoff and
force an immediate probe for **every enabled host**, on-demand included. Only the
*recurring* ladder is tier-split.

The distinction is scale, not principle. ADR-014's stated cost is that polling
hosts the user is not using "makes the UI slow and the auth logs noisy" — an
argument about *continuous* traffic, every rung, forever. A resume or a network
change is a rare, bounded event producing exactly one probe per host. That cost is not the
cost ADR-014 was avoiding.

And suppressing it would defeat the thing the reset exists for. `docs/OPERATIONS.md`
T1 (overnight sleep) and T2 (network interruption) are the acceptance tests that
matter, and an on-demand host still displaying a stale `Unreachable` after the
machine has woken, or its network has come back, is precisely the sluggishness this
section says the reset eliminates. ADR-008 says on-demand rows "render as unknown until acted on";
a host that is *known-stale* is a worse answer than one that is unknown.

`mode = "none"` hosts remain untouched: they are not enabled, and are never probed.

#### A failed mount attempt is paced too, on its own ladder

Everything above paces the re-probe of an `Unreachable` host — one that cannot be *reached*.
A host can be perfectly reachable and still be impossible to **mount**: an expired
key, a disabled SFTP subsystem, a drive letter already claimed by something else.
That is a different failure and it needs its own rule (`bs-k4k`).

**Every failed mount attempt paces the re-enable that follows it**, through a
backoff ladder kept separate from the probe ladder (`MountRetryBackoff`). The
failing attempt increments it before the drain begins, so the first retry already
sits on rung one — **a failed mount is never retried immediately**. It uses the
same configured `backoff_seconds` values.

*Why this is not optional.* Without it the cycle
`Ready → Mounting → Draining → Disabled → Probing → Ready` closes on a persistent
host **with no clock advance anywhere in it**, and it is stack-recursive rather
than channel-driven. In production that pegs a core, floods the target's auth log
and `rclone` as fast as the network allows, and terminates in a
`StackOverflowException` — which .NET permits no `catch` to intercept, so the
process dies. Bosun dying is itself an Invariant I2 hazard: §6 handles
crash-with-mounts-up via adopt-or-clear, but only if the user restarts it.

*Why pace from the first failure rather than allowing N free retries.* The
conditions that cause it do not clear in seconds. An expired key stays expired; a
drive letter in use stays in use — and §6 already requires that case to "refuse the
mount, surface the conflict in the tray, do not silently pick another letter."
Free retries are nearer to *retry forever* than to *refuse*. The first rung is 5s
by default, quick enough that a genuinely transient failure recovers unnoticed.

*A successful mount resets the ladder*, exactly as a successful probe resets the
probe ladder.

**Pacing is only half the requirement.** A reachable host that cannot be mounted is
a condition the user must be able to *see*; retrying it quietly forever is the
failure ADR-012 Decision 3 exists to prevent, and it is worse here than the
missing-WinFsp case, because the host looks healthy and only one drive is affected.

`HostMountSnapshot` therefore carries `ConsecutiveMountFailures` and
`LastMountFailureReason` (`bs-ww9.4`), captured at the failure sites and reset on a
successful mount, and `Bosun.Status` derives the user-facing sentence from them —
*"`P:` is not mounted — mount failed 4 times; last error: …"*. Rendering it is E9's
job.

### Reconciliation

Every supervisor tick, compare intended state against `mount/listmounts`. Drift
is expected — rclone can lose a mount, or a mount can survive a Bosun crash.
Reconcile toward intended state; log every correction.

## 5. Threading

The supervisor loop is single-threaded and `async`. Per-host state transitions
are serialised through one channel so that a slow unmount cannot interleave with
a probe result for the same host. UI reads a snapshot; the UI never mutates
state directly, it posts commands to the same channel.

## 6. Failure modes to design against

| Failure | Required behaviour |
|---|---|
| Host unreachable while mounted | Unmount within `interval × failures_before_unmount`. Explorer must not hang. |
| Host stays TCP-reachable but the mount's SSH channel dies (sleep/resume, a network blip too brief to trip the shallow threshold) | Detected by the recurring deep probe, not the shallow one — the drain begins within `2 × mounted_deep_probe_interval_seconds + 2 × probe_timeout_seconds` (two consecutive failures, the fixed deep-probe failure threshold, each failing probe taking up to the timeout); 610 s with the defaults 300 and 5. See ADR-016 and its 2026-11-02 amendment. |
| `rclone rcd` dies | Detect via health check, restart it, reconcile mounts from scratch. |
| `rclone rcd` orphaned by a dead Bosun, still holding the rc port | Prevented by the Job Object; if one survives anyway (the few ms before assignment), the next launch kills it when its command line is exactly Bosun's own and it runs as the current user. Otherwise the port holder is reported by PID and image path. The fault text names the real cause (HTTP 401, exit code, port holder), never a generic "did not respond". See ADR-020 §1-2. |
| WinFsp not installed | Detect at startup, show an actionable message, disable all mount features, leave terminal features working. |
| Drive letter already in use | Refuse the mount, surface the conflict in the tray, do not silently pick another letter. |
| `mount/mount` succeeds but the drive is not `DriveType.Network` (rclone accepted `network_mode` and stopped honouring it; the drive is presented as a fixed disk) | Treat as a failed mount: drain and unmount, record the reason on the host, show it in the window and tray as a mount failure. Never keep a fixed-disk presentation of an SFTP mount. See §4 rule 2a (`bs-dtk`). |
| Machine sleeps with mounts up | Unmount on suspend. If we missed it, force-unmount and reconcile on resume. |
| Config file invalid | Keep running on the last valid config; surface the parse error. Never apply a partial config. On the *first* load there is no last valid config: Bosun still builds its tray icon and window (on an empty stand-in config), shows `hosts.toml is invalid` with the errors, and mounts nothing. |
| The mount supervisor cannot be built or started (e.g. the container cannot construct a dependency) | Fail soft, visibly (bs-4js). Bosun keeps running. The failure is published as state (`StartupReadiness.SupervisorFaultMessage`), which the health model turns into a Faulted `startup.supervisor-failed` issue titled `The mount supervisor failed to start: <the exception's message>`; the tray icon goes red and the window banner and header show it. "Not started yet" is not a failure: only an attempted-and-failed start sets the message. No host is probed or mounted, and nothing retries — restart Bosun after fixing the cause. |
| A service the UI is built from cannot be resolved (the supervisor, the config store, the session monitor) | The UI is still built (bs-4js). The tray icon and window exist, using inert stand-ins (`UnavailableMountSupervisor`, `EmptyHostConfigStore`, `NoSessionMonitor`) that show no host rows and make every command a no-op, so nothing mounts or throws. The cause is reported by the health model, not by the stand-in. The host editor is left out when the config it needs is missing. *Copy diagnostics* is not (bs-1kt): it reads the supervisor and the rc client only inside the sections that need them, so without them the bundle's hosts and rclone sections say `unavailable: <reason>` and everything else is still collected. The faults that most need reporting are the ones that make the container throw, so the channel that reports them must not depend on the container. |
| An unhandled exception reaches the WPF dispatcher | Before the logger exists, it is terminal and is reported through the catastrophic channel (ADR-012). After the logger exists, Bosun survives it (bs-twz): it is logged as Critical, `e.Handled` is set, and a Faulted `ui.unhandled-exception` issue titled `A UI error occurred; see the log` is reported with the exception's type and message, so the tray icon goes red and the banner shows it. Supervision keeps running. The issue stays until Bosun is restarted — a later recovery of rclone or the supervisor says nothing about whether the window or tray is sound, and the banner offers *Restart Bosun* first. Runaway guard: more than 5 such exceptions within 60 s (a timer throwing on every tick) are no longer handled and the process exits, with the reason logged. `DispatcherExceptionPolicy` holds this logic; `App` only calls it, and an exception inside the handler itself ends the process rather than recursing. |
| Bosun crashes with mounts up | On next start, enumerate existing mounts via `mount/listmounts` and adopt or clear them before doing anything else. |
| Claude session tracking is on but a live, Terminal-hosted Claude Code process has no record (plugin not installed or removed, hooks not firing) | Say so causally in the window (ADR-021 Decision 9). Never restore anything on a guess. |
| The hook helper fails (bad payload, records directory unwritable) or hangs | Exit 0 anyway, inside its own deadline and the hook `timeout`; Claude Code is never disturbed. The session is simply not tracked, and the "not reporting" state covers it. |
| A restore does not resume (`wt.exe` fails, the folder is gone, `claude` not found) | The dispatched record is never confirmed. Report it by title in the window and the toast, keep it in `dispatched\`, retry at the next trigger. After three unconfirmed attempts, stop retrying and list it as "Could not resume" with a Dismiss action. |
| A record's transcript is gone | Delete the record; do not open a tab that can only print "No conversation found". |
| Windows is logging off or shutting down | Stop classifying at `SessionEnding`, so records survive to be restored at the next logon. |
| Bosun restarts (watchdog, crash, manual) while sessions are running | No duplicates: a live process's record and a live session ID are never restored, and a dispatched record waits out its confirmation timeout. |

## 7. Explicitly out of scope for v1

- Password and MFA authentication (see ADR-007)
- SSH connection multiplexing / ControlMaster
- Explorer shell namespace extension (drive letters only)
- Non-SFTP rclone backends
- Any plugin or extension mechanism (ADR-021's Claude Code plugin is something
  Bosun generates for Claude Code, not an extension point in Bosun)
- Cross-machine config sync
- Restoring Claude Code sessions hosted outside Windows Terminal, sessions running
  on a remote machine inside an SSH tab, non-interactive `claude -p` runs, or
  sessions in an elevated Terminal window (ADR-021 Decision 1)
- Reconstructing which Terminal window each restored tab was in (ADR-021)
