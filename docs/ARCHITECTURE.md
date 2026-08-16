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

## 2. Process model

**One process.** A WPF application with no main window, a tray icon, and a
`BackgroundService` host running the supervisor loop.

This is forced by a Windows constraint: **drive letters are scoped to a logon
session**. A Windows Service running as `SYSTEM` can mount `P:` successfully and
the user's Explorer will not see it. The supervisor therefore has to run as the
user, in the interactive session.

`rclone rcd` runs as a child process, started by Bosun at launch and stopped at
exit. Bosun talks to it over `http://127.0.0.1:<port>` and never spawns
`rclone mount` directly.

```
Bosun.exe
├── App (WPF, no MainWindow, ShutdownMode=OnExplicitShutdown)
├── TrayIcon (H.NotifyIcon) ──► StatusWindow (on demand)
└── IHost
    ├── RcloneProcessService   : IHostedService    (owns rclone rcd lifetime)
    ├── SupervisorService      : BackgroundService (the loop, §4)
    ├── SessionMonitorService  : BackgroundService (ssh.exe enumeration)
    ├── SystemEventService     : IHostedService    (power/network/session hooks)
    └── FragmentWriterService  : IHostedService    (writes WT fragment on change)
```

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

### `IProbe`
Two levels:

- **Shallow** — TCP connect to `host:port`, 5s timeout. Cheap. This is the
  recurring liveness check.
- **Deep** — `operations/list` on the remote root, depth 1. Proves
  authentication and the SFTP subsystem work. Run once before each transition
  into `Mounting`, not on the recurring timer.

TCP success does not imply SFTP success. Never mount on a shallow probe alone.

Probe scheduling depends on state, not only on configuration: an idle host may
be left unpolled, a **mounted** host never is. See §4 rule 3 and ADR-011.

### `IMountSupervisor`
Owns the state machine in §4. The only component permitted to call
`mount/mount` and `mount/unmount`.

### `IFragmentWriter`
Serialises the host list to Windows Terminal's fragment schema under
`...\Windows Terminal\Fragments\Bosun\bosun.json`. **Verify the exact path
against current Terminal documentation before implementing** — it differs
between Store and unpackaged installs.

Each host becomes a profile whose `commandline` is either:

```
ssh <config-key>                                  # tmux = false
ssh -t <config-key> tmux new -A -s <session>      # tmux = true
```

`<config-key>` is the host's **TOML key** — `example-nas`, not `display_name` and
not `user@hostname`. Bosun therefore expects a matching `Host <config-key>` block
in the user's `~/.ssh/config`, which is a documented prerequisite (README) and a
triage row (`docs/OPERATIONS.md`). The fully-specified alternative would bypass
`ProxyJump` and everything else the user configured there. See **ADR-013**.

Bosun emits an explicit profile `guid`, derived from the config key rather than
letting Terminal derive one from `display_name` — otherwise renaming a host
silently orphans the user's per-profile customisation. Also ADR-013.

wrapped in the reconnect loop script if `session.reconnect = true`.

### `ISessionMonitor`
Enumerates `ssh.exe` processes, resolves their command lines via CIM, and
correlates them to configured hosts. Cross-references `GetExtendedTcpTable` for
socket state. This is the one component with meaningful P/Invoke; keep it behind
the interface and fake it in tests.

### `ISystemEventSource`
Wraps three .NET event sources:

| Event | Reaction |
|---|---|
| `PowerModeChanged` → `Suspend` | Unmount all, enter `Suspended` |
| `PowerModeChanged` → `Resume` | Re-probe all immediately, bypassing backoff |
| `NetworkAddressChanged` | Re-probe all immediately; force-probe mounted hosts |
| `SessionSwitch` → `SessionLock` | No action (v1); reserved |

The suspend handler must complete quickly. Windows does not wait indefinitely.
Issue unmounts with a short timeout and accept that a forced unmount may be
needed on resume.

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
4. `Draining` calls `mount/unmount`. If unmount does not confirm within the
   timeout, escalate to a forced unmount, then verify against `mount/listmounts`.
   Never leave the state machine believing a drive is gone when it is not.
5. On suspend, every host in `Mounted` or `Mounting` goes to `Draining`, then
   `Disabled`. On resume, everything previously enabled goes to `Probing`.
6. On-demand hosts rest in `Ready`, not `Mounted`. They do not auto-mount.
7. On-demand hosts with `idle_unmount_seconds > 0` transition
   `Mounted → Draining` after that period without filesystem activity.

8. **`RequestMountAsync` on an `Unreachable` host triggers an immediate probe**
   and proceeds to mount if it passes. It is never a silent no-op — a tray menu
   item that does nothing, forever, with no error is not a defensible behaviour.
   This does not weaken rule 1: the click causes a *probe*, and `Mounting` is
   still reached only from `Ready` after a passing deep probe. See ADR-014.
9. **A user-requested unmount parks the host.** It stays unmounted until an
   explicit user remount, a config reload, or an application restart — including
   for `persistent` hosts, which would otherwise auto-mount again on their very
   next arrival at `Ready` and make the tray's Unmount item look like it did
   nothing. A parked host **keeps probing**, so the tray can show live
   reachability; it simply does not auto-mount. Rule 6 governs a tier's *resting*
   state, not the override of an explicit command. See ADR-015.

### Backoff

`Unreachable → Probing` uses the configured backoff ladder, default
`[5, 15, 30, 60, 300]` seconds, holding at the last value. Backoff is **reset to
zero** by a network address change, a power resume, or an explicit user "retry
now". This is what makes the dock/undock experience feel immediate rather than
sluggish.

**The ladder runs for `persistent` hosts only.** An **on-demand** host in
`Unreachable` is not polled at all; it stays dark until the user acts, and rule 8
is what makes that safe. This is what makes ADR-008's and ADR-011's promise —
"on-demand hosts generate no traffic until the user mounts one" — literally true
rather than approximately true. Persistent hosts must keep polling: that is the
mechanism by which a drive returns on its own, which is `docs/OPERATIONS.md` T2.
See ADR-014.

**Resume and network change do NOT inherit the tier split.** They reset backoff and
force an immediate probe for **every enabled host**, on-demand included. Only the
*recurring* ladder is tier-split.

The distinction is scale, not principle. ADR-014's stated cost is that polling
hosts the user is not using "makes the UI slow and the auth logs noisy" — an
argument about *continuous* traffic, every rung, forever. A resume or a dock is a
rare, bounded event producing exactly one probe per host. That cost is not the
cost ADR-014 was avoiding.

And suppressing it would defeat the thing the reset exists for. `docs/OPERATIONS.md`
T2 — close the lid on one network, open it on another — is the acceptance test that
matters, and an on-demand host still displaying a stale `Unreachable` after the
machine has moved networks is precisely the sluggishness this section says the
reset eliminates. ADR-008 says on-demand rows "render as unknown until acted on";
a host that is *known-stale* is a worse answer than one that is unknown.

`mode = "none"` hosts remain untouched: they are not enabled, and are never probed.

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
| `rclone rcd` dies | Detect via health check, restart it, reconcile mounts from scratch. |
| WinFsp not installed | Detect at startup, show an actionable message, disable all mount features, leave terminal features working. |
| Drive letter already in use | Refuse the mount, surface the conflict in the tray, do not silently pick another letter. |
| Machine sleeps with mounts up | Unmount on suspend. If we missed it, force-unmount and reconcile on resume. |
| Config file invalid | Keep running on the last valid config; surface the parse error. Never apply a partial config. |
| Bosun crashes with mounts up | On next start, enumerate existing mounts via `mount/listmounts` and adopt or clear them before doing anything else. |

## 7. Explicitly out of scope for v1

- Password and MFA authentication (see ADR-007)
- SSH connection multiplexing / ControlMaster
- Explorer shell namespace extension (drive letters only)
- Non-SFTP rclone backends
- Any plugin or extension mechanism
- Cross-machine config sync
