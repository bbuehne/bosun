# Architecture Decision Records

These record decisions already made, with the reasoning that produced them. The
reasoning matters more than the conclusion — it is what lets you tell whether a
new situation is covered.

**Do not silently deviate.** If a decision looks wrong, say so, explain what
changed, and propose an amendment.

---

## ADR-001 — .NET 10 + WPF rather than Python

**Status:** Accepted

**Context.** The maintainer's default language is Python and his C# is rusty.
The initial framing of the tool — read config, shell out, draw a table — favoured
Python. That framing changed once the persistent-mount supervisor entered scope.

**Decision.** .NET 10 (LTS), C# 14, WPF.

**Reasoning.**

1. *System event integration is decisive.* The tool must react to
   `PowerModeChanged`, `NetworkAddressChanged`, and `SessionSwitch`. In .NET
   these are in-box, a handful of lines each. In Python, receiving
   `WM_POWERBROADCAST` requires a hidden Win32 window and a message pump via
   ctypes — fragile exactly where fragility is least affordable. For a laptop
   tool used many hours a day, closing the lid at one location and opening it at
   another is the central use case, not an edge case.
2. *Hosting model.* The supervisor is a long-running service with cancellation,
   structured logging, config binding, and DI. `BackgroundService` provides this
   as a base class; in Python it is hand-rolled.
3. *Packaging.* Single-file self-contained publish, no PyInstaller temp-unpack on
   every launch, no Defender heuristic friction on a binary launched at login.
4. *Optionality.* If an Explorer shell namespace extension is ever wanted, C#
   keeps it reachable. From Python it is effectively out of reach.

**Costs accepted.** No `psutil`. Enumerating `ssh.exe` and its socket state
requires CIM plus a `GetExtendedTcpTable` P/Invoke — three lines of Python
becomes one contained class in C#.

**Rejected alternatives.** Python (above). Rust — `windows-rs` is excellent but
there is no payoff: not compute-bound, weaker native UI story, and the maintainer
loses the ability to read the code, which is a stated requirement.

---

## ADR-002 — rclone + WinFsp rather than sshfs-win

**Status:** Accepted

**Decision.** Mount SFTP via `rclone mount` over WinFsp.

**Reasoning.** sshfs-win is the obvious-looking choice and is the wrong one: no
releases since 2021, and its bundled SSH client is Cygwin-based. Cygwin's
OpenSSH is the same component that makes SSH multiplexing unreliable on Windows
(see ADR-007); the same fragility, in a different corner. rclone is actively
maintained, has a native Go SFTP client, and has real VFS caching.

**Consequences.** WinFsp is a hard prerequisite and must be detected at startup
with an actionable message. `--vfs-cache-mode writes` and `--network-mode` are
mandatory on every mount (Invariants I6, I7).

---

## ADR-003 — Mount via the rclone rc HTTP API, not spawned processes

**Status:** Accepted

**Decision.** Run one long-lived `rclone rcd` child process; perform all mount
operations via `mount/mount`, `mount/unmount`, `mount/listmounts`.

**Reasoning.** Spawning one `rclone mount` process per host means tracking N
child processes, parsing their stderr for failure, and handling orphans after a
crash. The rc API replaces all of that with HTTP calls and gives
`mount/listmounts` as a reconciliation primitive — the ability to ask what is
*actually* mounted rather than inferring it from process state. Persistent and
on-demand tiers then differ only in who triggers the call, sharing one code path.

---

## ADR-004 — Supervisor in-process, not a Windows Service

**Status:** Accepted

**Decision.** The supervisor runs as a `BackgroundService` inside the tray
application.

**Reasoning.** Drive letters are scoped to a logon session. A service running as
`SYSTEM` mounts successfully and the user's Explorer cannot see the result. This
is a hard Windows constraint, not a preference. It also simplifies the design —
one process, one lifetime, no IPC.

**Consequence.** Mounts exist only while the user is logged in and Bosun is
running. That is correct for this tool.

---

## ADR-005 — Unmount on failure, never retry through failure

**Status:** Accepted

**Decision.** After N consecutive probe failures, unmount immediately. Remount
only after a fresh successful probe.

**Reasoning.** This is the single most important behavioural decision in the
project. A mounted drive letter pointing at an unreachable host does not fail
locally — it blocks Explorer, `dir`, and file-open dialogs in unrelated
applications, producing unexplained multi-second freezes across the OS. rclone
will happily retry underneath and keep the mount nominally "alive" while every
filesystem call hangs. Proactively removing the drive is strictly better than
preserving it.

**Consequence.** Drives disappear and reappear. This is intended and must be
communicated in the UI, not hidden.

---

## ADR-006 — Windows Terminal fragment extensions, not settings.json

**Status:** Accepted

**Decision.** Write profiles to a fragment file under
`...\Windows Terminal\Fragments\Bosun\`. Never read or write the user's
`settings.json`.

**Reasoning.** Terminal rewrites `settings.json` whenever the user changes
anything in its settings UI. Two writers means clobbering. Fragments are merged
at load time, are owned solely by us, and yield stable profile GUIDs derived from
source name plus profile name. Clean ownership split: Bosun owns host profiles,
Terminal owns themes and keybindings.

**Note for implementation.** The fragment directory differs between Store and
unpackaged Terminal installs. Verify the current path against Microsoft's
documentation rather than hardcoding from memory.

---

## ADR-007 — Key-based authentication only; no connection multiplexing

**Status:** Accepted

**Context.** The original goal was to replicate Bitvise: a persistent connection,
unlimited terminals without reauthentication, and SFTP over the same connection.
The OpenSSH equivalent is `ControlMaster` + `ControlPersist`.

**Decision.** v1 assumes key-based auth with `ssh-agent`, and does not implement
multiplexing.

**Reasoning.** Microsoft's Win32-OpenSSH explicitly scopes out client-side
`ControlMaster`; it is listed among features that will not work on Windows, and
the tracking issue has been open since 2019. Cygwin/MSYS2 clients nominally
support it but fail in practice with `mm_send_fd` /
`mux_client_request_session` errors — projects have removed the options from
Windows configurations for this reason. The only reliable path would be running
the SSH client inside WSL.

With key auth, that whole apparatus buys almost nothing: `ssh-agent` signs
silently, so the user never sees a prompt. The cost of not multiplexing is a few
hundred milliseconds of handshake per new tab and N entries in the server auth
log instead of one. Not worth a WSL dependency.

**Revisit if.** A host requires password or MFA authentication. Then multiplexing
becomes genuinely valuable and the WSL-hosted-client design should be
reconsidered.

---

## ADR-008 — Three independent per-host axes

**Status:** Accepted — amended by ADR-011 (the "not polled" consequence applies
only while a host is idle, never while it is mounted)

**Decision.** Mount lifecycle, terminal session autostart, and probe cadence are
configured separately per host, not collapsed into a single "persistent /
on-demand" switch.

**Reasoning.** The combinations are real: a jump host wants a login session and
no mount; an archive server wants a mount and no shell; a production box wants a
mount, a red-tinted manual session, and aggressive probing. Collapsing these
forces bad defaults.

**Consequence.** On-demand hosts are **not polled while idle**. Probing hosts the
user is not using costs little bandwidth but makes the UI slow and the auth logs
noisy. On-demand rows render as unknown until acted on. Once such a host is
mounted it *is* polled — see ADR-011.

---

## ADR-009 — Beads is the work queue; docs are the spec; GitHub Issues is an inbox

**Status:** Accepted

**Decision.**

- **Specification** lives in versioned files (`CLAUDE.md`, `docs/`). Not in
  issues.
- **Work queue** lives in Beads (`bs-` prefix), including work the agent
  discovers mid-build.
- **GitHub Issues** is a thin intake channel — file from a phone when something
  breaks in daily use, triage into Beads at session start. It is not a tracker.

**Reasoning.** Beads and GitHub Issues compete for the same job; running both as
trackers guarantees drift. Meanwhile a spec filed as issues is the worst of all
options: not greppable, not diffable, not reviewable, and it rots silently while
the code moves. Files for the durable spec, Beads for the live queue, Issues for
intake from outside the dev machine.

---

## ADR-010 — Public repository, personal tool

**Status:** Accepted

**Decision.** MIT, public from the first commit, no roadmap and no support
commitment.

**Reasoning.** The mount-manager space is crowded (Mountain Duck, ExpanDrive,
SSHFS-Win Manager). The unoccupied ground is the *combination* of Terminal
profile generation, session monitoring, and supervised mount lifecycle under one
supervisor that reacts to sleep and network changes. That audience is thousands,
not hundreds of thousands — but those users are the argument: they run corporate
VPNs, domain-joined machines, docking stations, and bastion hosts, and they will
find the failure modes that a single maintainer cannot generate alone. Public
from day one also forces clean config/secret separation as a habit rather than a
cleanup task.

**The trap this must avoid.** Premature generalisation. No plugin architecture,
no settings UI for unused options, no support for key formats nobody here has.
Every such addition makes the tool worse for its actual user. Bug reports
welcome; feature requests that do not serve the maintainer's workflow are
declined without apology.

**Required disclosure.** The README must state near the top that Bosun manages
filesystem mounts and that a failure can hang File Explorer. A stranger's bad
day is louder than a stranger's good one; setting this expectation makes the
occasional angry issue fair rather than surprising.

---

## ADR-011 — A mounted host is always probed

**Status:** Accepted. Amends ADR-008.

**Context.** ADR-008 established that on-demand hosts are not polled, and
`docs/CONFIG-SCHEMA.md` gives `probe.interval_seconds = 0` as their sensible
default — `config/hosts.example.toml`'s `example-remote` is configured exactly
that way. Separately, `docs/ARCHITECTURE.md` §4 rule 3 made unmount-on-failure
depend entirely on that same recurring probe: `failures_before_unmount`
consecutive failures move a host from `Mounted` to `Draining`.

Composed, those two rules produced a hole. An on-demand host with
`interval_seconds = 0` that the user mounts from the tray is never probed while
mounted. It therefore never accumulates a failure, never reaches `Draining`, and
if the server dies the drive letter stays up pointing at nothing — the wedged
Explorer that Invariant I2 and ADR-005 exist to prevent. The configuration the
example file ships as a *recommended* default was the one that disabled the
project's central safety property.

**Decision.**

1. `probe.interval_seconds` governs polling **while a host is idle** (`Ready`,
   `Unreachable`) only. `0` means "do not poll while idle".
2. A host in `Mounted` is **always** probed. The effective interval is
   `min(interval_seconds, mounted_probe_interval_seconds)` when
   `interval_seconds > 0`, and `mounted_probe_interval_seconds` when it is `0`.
3. `global.mounted_probe_interval_seconds` is a new setting, default `60`. Absent
   means `60`, not a validation error — no other `[global]` field is mandatory,
   and rejecting a whole config for a newly-introduced key would break every
   existing `hosts.toml` on upgrade. Zero or negative *is* a validation error,
   because it would disable the cadence this ADR exists to guarantee.

**Reasoning.** ADR-008's justification for not polling is that probing hosts *the
user is not using* makes the UI slow and the auth logs noisy. A mounted host is
by definition one the user is using, so the justification does not reach it —
this is a clarification of ADR-008's scope rather than a reversal of it. The
upper clamp in rule 2 exists for the same reason as the rule itself: a host
configured with a long idle cadence must not inherit an unbounded unmount
latency the moment it is mounted. With the defaults, worst-case time from host
death to drive removal is `60 × 3 = 3 minutes`, which is what
`docs/OPERATIONS.md` T3 already asserts.

**Consequence.** On-demand hosts generate no traffic until the user mounts one,
then probe at the mounted cadence until it is unmounted. The idle/mounted split
must be visible in the supervisor's probe scheduling, not buried in a config
default, and it is a named test case: a regression that stops probing mounted
hosts is an I2 violation, not a performance change.

---

## ADR-012 — The startup contract: ordered, fail-soft, one owner

**Status:** **PROPOSED** — awaiting the maintainer's decision. Nothing has been
implemented against it. Resolves `bs-bw4`, `bs-127`, `bs-o09`, and informs
`bs-16b`.

**Context.** Five epics landed with no agreed answer to a question none of them
owned: *what happens, in what order, when Bosun starts?* Each deferred it
correctly rather than guessing, and the deferrals have now accumulated into a
gap.

- `IRcloneRemoteProvisioner` exists, is tested, and **is called by nothing**. No
  `bosun-<hostKey>` remote is ever written to `rclone.conf`, so on a real machine
  every deep probe and every mount fails. E3 documented the wiring as belonging
  to orchestration; E5 declined to invent it. The tool cannot currently work.
- `RcloneProcessService` is deliberately **not** registered as an `IHostedService`,
  because doing so would force `IHostConfigStore` to resolve during
  `host.StartAsync()`, breaking the lazy-config pattern E1 established and a test
  that depends on it.
- `BosunHostOptions.ConfigPath` defaults to `<app dir>/config/hosts.toml`, which
  was an assumption. `docs/OPERATIONS.md` documents where logs go and is silent
  on configuration.
- `HostConfigStore.Load` throws on an invalid initial config, by design — there is
  no previous config to fall back to. Nobody has decided what the application does
  with that exception.
- `FileSystemConfigWatcher`'s constructor throws if its directory does not exist,
  so a genuine first run currently dies before any friendlier message is possible.

The through-line: **five components each degrade sensibly on their own, and
nothing composes them.**

**Decision.**

1. **One owner.** A single hosted service owns startup and runs an explicit,
   ordered sequence:

   ```
   load config → detect WinFsp → start rclone rcd + health check
     → provision remotes → adopt-or-clear existing mounts → run the supervisor
   ```

   The order is not arbitrary. Remotes must exist before any deep probe can pass
   (I1 forbids mounting without one). Adopt-or-clear must precede the supervisor
   loop — `docs/ARCHITECTURE.md` §6 says existing mounts are reconciled "before
   doing anything else", because a surviving mount from a crashed run is exactly
   the drive letter most likely to be pointing somewhere wrong.

2. **Every step degrades; no step aborts startup.** Each failure disables the
   capabilities that depend on it, records a reason, and leaves everything else
   working. This is not a new principle — it is the WinFsp rule from
   `docs/ARCHITECTURE.md` §6 ("detect at startup, show an actionable message,
   disable all mount features, leave terminal features working") generalised to
   every startup dependency.

3. **An invalid config degrades too.** Bosun starts, mounts and probes are
   disabled, the parse error is surfaced, and the config watcher keeps running so
   that fixing the file recovers without a restart. Terminal profiles continue to
   be served from the last valid config if one exists.

4. **Readiness is a typed, observable value**, not a log line — a record the tray
   renders and the status window explains. "Why is nothing mounted" must be
   answerable from the UI.

5. **Configuration lives at `%LOCALAPPDATA%\Bosun\hosts.toml`.** First run creates
   the directory and writes a template derived from `hosts.example.toml`. The
   repo-relative path survives only as a development override through the
   already-injected `BosunHostOptions`.

**Reasoning.**

*Why fail-soft rather than fail-fast.* Ordinarily a tool that cannot do its job
should say so and stop. Bosun cannot: it has **no main window**. A hard startup
failure produces a tray application that simply never appears, and `bs-16b`
records that a failure before the logger is constructed is currently written
nowhere at all — no file, no debug sink, no dialog. The honest-looking choice is
in practice the silent one. Degrading with a visible reason is both kinder and
more informative.

It also matches what the tool already promises. A user with no WinFsp still gets
their Terminal profiles. A user with a typo in `hosts.toml` should still get the
profiles from the last config that parsed, and a tray icon telling them which
line is wrong.

*Why one owner rather than several hosted services.* Ordering is the whole
problem. Independent `IHostedService` registrations start in registration order,
which encodes the sequence somewhere invisible and fragile. An explicit sequence
in one place can be read, tested, and logged as a unit.

*Why the lazy-config objection does not survive.* `RcloneProcessService` was kept
out of the hosted-service pipeline to preserve laziness that exists for a *test*:
one that builds a host whose `ConfigPath` points nowhere. That is a test concern
that leaked into production design. The real application should load its config at
startup. Tests should build a host **without** the startup orchestrator, which
`BosunHostFactory` is already composable enough to allow. Fix the test, not the
architecture.

*Why `%LOCALAPPDATA%`.* Symmetry with the log path is worth real money during
triage — `docs/OPERATIONS.md` gets to say "your configuration and your logs are
both under `%LOCALAPPDATA%\Bosun`", which is one sentence a stranger can follow.
The `<app dir>` default is additionally wrong under E10's single-file publish,
where `<app dir>` can resolve to an extraction directory rather than where the
executable lives, and hostile to an install under `Program Files`.

**Consequences.**

- A new startup component, its ordered sequence, and its degraded modes all need
  tests. Each step's failure is a case: no config, invalid config, no WinFsp, no
  rclone binary, rcd fails to start, provisioning fails for one host but not
  others.
- The tray (E9) gains a real job beyond per-host status: explaining a degraded
  Bosun. That should be in E9's brief before it is written.
- First-run bootstrap becomes real work — create the directory, write the
  template, and make `FileSystemConfigWatcher` tolerate a directory that did not
  exist a moment ago.
- `bs-16b` becomes more urgent, not less. Fail-soft means more paths that must
  report *something*, and a pre-logger failure that vanishes silently undermines
  the whole model.

**Rejected alternatives.**

*Fail fast on invalid config.* Defensible for a CLI, wrong for a windowless tray
app whose failure would be invisible. Revisit only if `bs-16b` is fixed such that
a startup failure is guaranteed to reach the user.

*Provision remotes lazily, on first mount attempt.* Tempting — it needs no
startup ordering at all. Rejected because it puts a config-file write on the
mount path, which is latency-sensitive and already the most dangerous code in the
project, and because a provisioning failure would then surface as a mysterious
mount failure rather than a startup condition the user can see and fix.
