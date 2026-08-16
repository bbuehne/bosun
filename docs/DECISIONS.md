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

## ADR-012 — The startup contract: ordered start, and a channel per failure

**Status:** Accepted (2026-08-16). Resolves `bs-bw4`, `bs-127`, `bs-o09`. Makes
`bs-16b` a blocker rather than a nice-to-have.

**Context.** Five epics landed with no agreed answer to a question none of them
owned: *what happens, in what order, when Bosun starts?* Each deferred it
correctly rather than guessing, and the deferrals have accumulated into a gap
that currently stops the tool working at all.

- `IRcloneRemoteProvisioner` exists, is tested, and **is called by nothing**. No
  `bosun-<hostKey>` remote is ever written to `rclone.conf`, so every deep probe
  and every mount fails on a real machine.
- `RcloneProcessService` is deliberately **not** an `IHostedService`, because
  registering it would force `IHostConfigStore` to resolve during
  `host.StartAsync()` and break a test that builds a host whose `ConfigPath`
  points nowhere.
- `BosunHostOptions.ConfigPath` defaults to `<app dir>/config/hosts.toml`, which
  was an assumption. `docs/OPERATIONS.md` documents where logs go and is silent
  on configuration.
- `HostConfigStore.Load` throws on an invalid initial config, by design. Nobody
  decided what the application does with that exception.
- `FileSystemConfigWatcher`'s constructor throws if its directory does not exist,
  so a genuine first run dies before any friendlier message is possible.

The through-line: **five components each degrade sensibly on their own, and
nothing composes them.**

### Decision 1 — one owner, one ordered sequence

A single hosted service owns startup and runs an explicit sequence:

```
load config -> detect WinFsp -> start rclone rcd + health check
  -> provision remotes -> adopt-or-clear existing mounts -> run the supervisor
```

The order is not arbitrary. Remotes must exist before any deep probe can pass, and
I1 forbids mounting without one. Adopt-or-clear must precede the supervisor loop:
`docs/ARCHITECTURE.md` §6 requires surviving mounts to be reconciled "before doing
anything else", because a mount left over from a crashed run is the drive letter
most likely to be pointing somewhere wrong.

One owner rather than several independent `IHostedService` registrations, because
ordering *is* the problem — registration order encodes the sequence somewhere
invisible and fragile.

### Decision 2 — the mechanism matches the failure

An earlier draft of this ADR said "degrade rather than fail fast" and treated
readiness as a value the tray exposes. That was wrong in a way worth recording,
because the reasoning that produced it was wrong:

> **Fail-fast is not the same as fail-silent.** A process can show a dialog and
> *then* exit. The invisibility this ADR set out to avoid is a property of exiting
> quietly, not of failing fast.

And "expose readiness in the tray" is *discoverable*, not *communicated*. It fails
the standard ADR-005 already sets for a less serious condition — drives
disappearing "must be communicated in the UI, not hidden" — and E9 already owes a
balloon on unexpected unmount for exactly that reason.

So there is no single policy. Four conditions, four channels:

| Condition | Presentation |
|---|---|
| **Cannot construct the host at all** — no log directory, container fails | `MessageBox` naming the reason, then exit |
| **Missing dependency** — no WinFsp, no rclone | Persistent error-state tray icon, plus one toast; terminal features keep working and the status window says so |
| **Config invalid** | Toast immediately, error-state icon, line and column in the status window; the last valid config stays in service |
| **No config at all** | First-run **window**, shown actively. Not an error |

**The catastrophic case is the one place to fail fast, and it must be loud.**
There is no tray icon yet to hang state on, and per `bs-16b` nothing reaches a log
either, so a blocking dialog is the only surface that exists. Blocking is correct
precisely because there is nothing else.

**A blocking dialog is the wrong hammer everywhere else.** Bosun launches from
`shell:startup`. At login the user is walking away, windows are fighting for
focus, and a modal that appears *every* login because WinFsp is not installed
trains people to dismiss it reflexively — which destroys the signal for the case
that actually matters.

**No config at all is not a failure.** It is first run, the expected path for a
fresh install, and it deserves a setup window rather than a warning. Conflating it
with degradation was an error in the earlier draft.

### Decision 3 — the tray icon encodes aggregate health

A degraded Bosun must not look identical to a healthy one **at a glance**. Not a
tooltip; nobody hovers. This is a hard requirement on E9, not a footnote — every
other channel is a one-shot the user can miss, so the icon is the only always-on
surface.

Notifications must also be **causal**: "P: is not mounted — WinFsp is not
installed" rather than "some features unavailable". A user who configured a
persistent mount and finds no drive letter will otherwise blame Windows, the VPN,
or the network, and burn twenty minutes before suspecting the tray app sitting
there looking healthy. Degradation that does not explain itself does not merely
fail to inform — it actively misdirects.

### Decision 4 — configuration lives at `%LOCALAPPDATA%\Bosun\hosts.toml`

First run creates the directory and writes a template derived from
`hosts.example.toml`. The repo-relative path survives only as a development
override through the already-injected `BosunHostOptions`.

Symmetry with the log path is worth real money in triage: `docs/OPERATIONS.md`
gets to say "your configuration and your logs are both under
`%LOCALAPPDATA%\Bosun`", which is one sentence a stranger can follow. The
`<app dir>` default is additionally wrong under E10's single-file publish, where
`<app dir>` can resolve to an extraction directory rather than where the
executable lives, and hostile to an install under `Program Files`.

### On the lazy-config objection

`bs-127` recorded a real reason not to register `RcloneProcessService` as a hosted
service: it would force config resolution during `StartAsync` and break a passing
test. That is a **test concern that leaked into production design**. The real
application should load its configuration at startup. Tests should build a host
*without* the startup orchestrator, which `BosunHostFactory` is already composable
enough to allow. Fix the test, not the architecture.

This is the weakest part of the proposal — it trades a currently-green test for an
argument about intent, and is the most likely thing here to be worth arguing with.

**Consequences.**

- `bs-16b` is promoted to a blocker. The catastrophic path depends on a dialog
  appearing when the logger does not yet exist, and every fail-soft path depends
  on *something* reaching the user.
- E9 inherits two obligations that must be in its brief before it is written: the
  icon encodes aggregate health, and the status window explains a degraded Bosun
  causally.
- A first-run experience becomes real work — create the directory, write the
  template, and let `FileSystemConfigWatcher` tolerate a directory that did not
  exist a moment ago.
- Startup needs a test per failure mode: no config, invalid config, no WinFsp, no
  rclone binary, rcd fails to start, provisioning fails for one host but not
  others.

**Rejected alternatives.**

*Uniform fail-fast.* Correct for the catastrophic case, wrong at login for
recurring conditions, where a modal every session is hostile and self-defeating.

*Uniform fail-soft with passive readiness.* The earlier draft of this ADR.
Rejected on the maintainer's objection: it leaves the user to notice that nothing
is working, which recreates the silent failure in a new costume — an application
that looks healthy and does nothing is worse than one that visibly did not start.

*Provision remotes lazily on first mount.* Needs no startup ordering at all, which
is genuinely tempting. Rejected because it puts a config-file write on the mount
path — latency-sensitive, and already the most dangerous code in the project — and
because a provisioning failure would then surface as a mysterious mount failure
rather than a startup condition the user can see and fix.


---

## ADR-013 — The Terminal profile contract: the config key is the identity

**Status:** Accepted (2026-08-16). Resolves `bs-z0q` and `bs-08g`. Amends ADR-006's
implementation note.

**Context.** `docs/ARCHITECTURE.md` §3 said a host's profile commandline is
`ssh <host>` without ever saying what `<host>` expands to, and ADR-006 described
profile GUIDs loosely. E7 and E8 were built independently against that same
sentence, which is not a safe way to build a contract between two components.

Verified research into Terminal's fragment mechanism (recorded on `bs-3ir`)
established that Terminal derives a fragment profile's GUID as
`UUIDv5(UUIDv5(TERMINAL_FRAGMENT_NS, app-name), profile-name)` — the GUID is a
hash of the **name**.

**Decision.**

1. The emitted commandline uses the host's **config key**:
   `ssh example-nas`, or `ssh -t example-nas tmux new -A -s <session>`.
2. Bosun emits an **explicit `guid`**, derived by the documented UUIDv5 algorithm
   from the host's **config key** rather than letting Terminal derive one from
   `display_name`.
3. Using key-based profiles means Bosun's Terminal profiles depend on the user's
   own `~/.ssh/config`. That is a **documented prerequisite**, stated in README's
   Requirements and given a triage row in `docs/OPERATIONS.md`.

**Reasoning.**

*Why the config key and not a fully-specified `ssh -i <key> -p <port> <user>@<host>`.*
The fully-specified form looks more self-contained and is worse. It targets
`user@hostname` directly, so it does **not** match a `Host <alias>` block in the
user's `ssh_config` — which silently discards `ProxyJump`, custom ciphers, agent
forwarding, and everything else configured there. ADR-010 explicitly courts users
with bastion hosts, and `ProxyJump` is exactly how those are reached. `hosts.toml`
does not model any of it and should not; `ssh_config` already does that job well.

The tempting counter-argument — that `hostname`, `port`, `user` and
`identity_file` become dead weight if the terminal profile ignores them — is
wrong. Those four fields are what `config/create` writes into the rclone sftp
remote. They earn their place serving rclone whatever the profile emits.

*Why an explicit GUID from the config key.* Because the GUID is a hash of the
profile name, renaming a host's `display_name` makes Terminal see an entirely new
profile, silently orphaning whatever per-profile customisation the user had
layered onto the old one. The config key is already the stable identity
everywhere else in the system: E2 deliberately separated `Key` from
`DisplayName`, E5 keys supervisor state on it, E8 correlates live sessions to it.
Letting Terminal identity follow `display_name` would make this the single place
where identity means something different.

Owning the derivation costs about twenty lines, and Microsoft publishes a worked
test vector (app `"Git"`, profile `"Git Bash"` →
`{2ece5bfe-50ed-5f3a-ab87-5cd4baafed2b}`) so the implementation is verifiable
rather than hopeful. Use it as a unit test.

**Consequences.**

- README's Requirements section must state that Bosun's Terminal profiles expect a
  matching `Host <config-key>` block in `~/.ssh/config`. This is the one genuine
  cost of the decision and it must not be discovered by a user at the point of
  failure.
- `docs/OPERATIONS.md` gains a triage row: profile opens but the connection fails
  → check for a matching `Host` block.
- E8's `SshCommandLineParser` is already correct under this decision and needs no
  change. The E7/E8 contract should still be pinned by a test that feeds E7's
  actual emitted commandline through E8's parser and asserts it correlates —
  cheap, and it is the contract.
- Bosun may *read* `~/.ssh/config` if validating the prerequisite ever becomes
  worthwhile. Invariant I5 forbids touching Terminal's `settings.json`; it says
  nothing about ssh's config. Not worth building until wanted.

---

## ADR-014 — Unreachable hosts: poll by tier, and make the Mount click mean something

**Status:** Accepted (2026-08-16). Resolves `bs-gaw`. Amends ADR-008 and ADR-011.

**Context.** Independent adversarial testing of E5 surfaced a contradiction
between two accepted decisions.

ADR-011 rule 1 says `probe.interval_seconds` governs polling while a host is
**idle**, naming `Ready` *and* `Unreachable`, and that `0` means do not poll while
idle. Its Consequence paragraph is blunter: "on-demand hosts generate no traffic
until the user mounts one."

`docs/ARCHITECTURE.md` §4 Backoff says `Unreachable → Probing` uses the backoff
ladder, unconditionally, with no exemption for `interval_seconds = 0`. The
implementation follows §4 and polls an `interval_seconds = 0` on-demand host every
300s forever while its server is off.

Taking ADR-011 literally is worse, and this is the part that makes the decision
non-obvious: an `Unreachable` host that is never re-polled can never reach
`Ready`, and transition rule 1 forbids `Mounting` from anywhere else. So the
tray's Mount click on that host becomes a **permanent silent no-op** — the user
clicks, nothing happens, forever, with no error. Today `RequestMountAsync` in
that state logs at Debug and returns.

**Decision.**

1. A host in `Unreachable` is polled on the backoff ladder **if its mount mode is
   `persistent`**. Persistent hosts must keep polling: that is the mechanism by
   which a drive returns on its own, which is `docs/OPERATIONS.md` T2.
2. An **on-demand** host in `Unreachable` is **not** polled. It stays dark until
   the user acts.
3. `RequestMountAsync` on an `Unreachable` host **triggers an immediate probe**,
   and proceeds to mount if it passes. It is never a silent no-op.

**Reasoning.**

The split by tier is what makes ADR-008 and ADR-011 true rather than
aspirational. "On-demand hosts generate no traffic until the user mounts one"
holds literally, because under decision 3 the user's click is precisely what
generates the traffic.

Decision 3 also honours Invariant I1 without weakening it. The click does not
mount; it *probes*, and mounts only if the probe passes. Nothing reaches
`Mounting` except from `Ready` after a passing deep probe, exactly as before — the
user's action simply supplies the trigger that a suppressed timer would not.

The alternative, polling every `Unreachable` host regardless of tier, costs one
probe per down host per 300s. Small, but it is exactly the noise ADR-008 set out
to avoid, and on-demand hosts are frequently the ones that are off.

**Consequences.**

- `docs/ARCHITECTURE.md` §4 gains an explicit rule for `RequestMountAsync` from
  `Unreachable`. "Silently does nothing" is not a defensible behaviour for a tray
  menu item and should never have been reachable.
- ADR-011's Consequence paragraph becomes literally true rather than approximately
  true.
- A persistent host that is unreachable still generates ladder traffic while its
  server is off. That is intended: it is what makes the drive come back without
  the user doing anything.

