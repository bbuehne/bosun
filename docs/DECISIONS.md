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
   ctypes — fragile exactly where fragility is least affordable. The events a
   mount must survive are power transitions and network changes, and a mount that
   is up when one arrives is the case that wedges Explorer.
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

**Amendment (`bs-7mb`): the hardware this actually runs on.** Reason 1 above
originally read *"For a laptop tool used many hours a day, closing the lid at one
location and opening it at another is the central use case, not an edge case."*
That premise is false for the only deployment. Bosun runs on a Windows **desktop**;
the maintainer's laptop is a Mac and cannot run it at all. The dock/undock scenario
named as central does not occur.

The decision stands. Reasons 2–4 are untouched, and reason 1 survives on its own
terms — the events still matter, they are simply *different* events. A desktop
sleeps overnight, and a network still drops. Those two are the real failure modes,
and both are harder than docking, because in both the host is typically still
reachable afterwards: the shallow TCP probe passes while the SSH channel under the
mount is dead. That is `bs-2eg` / ADR-016, and ADR-017 extends it to the transition
itself.

What the false premise actually cost was emphasis, not the choice: it is why
`docs/OPERATIONS.md` T2 was written as an unrunnable "move to a different network"
test (`bs-aya` rewrites T1–T3), and why the README described someone else's machine.
Recorded because it was repeated across four documents and restated many times
before anyone checked it against the machine the code was being built on — the
class of error worth a note is an unverified premise that propagates, not the
premise itself.

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
`%LOCALAPPDATA%\Microsoft\Windows Terminal\Fragments\Bosun\bosun.json`. Never
read or write the user's `settings.json`.

**Reasoning.** Terminal rewrites `settings.json` whenever the user changes
anything in its settings UI. Two writers means clobbering. Fragments are merged
at load time, are owned solely by us, and yield stable profile GUIDs derived from
source name plus profile name. Clean ownership split: Bosun owns host profiles,
Terminal owns themes and keybindings.

**Amendment (`bs-4jn`): the verified path and GUID algorithm.** This ADR
originally abbreviated the path as `...\Windows Terminal\Fragments\Bosun\` and
told the implementer to verify it rather than hardcode from memory. That
verification happened (against Microsoft Learn, `ms.date` 2025-11-10, fetched
2026-08-16); this records the result, because the abbreviation **omits a path
segment** and is wrong as a literal.

*Path.* Two locations are read, regardless of whether Terminal itself is a Store
or unpackaged install:

| Scope | Path |
|---|---|
| All users | `%ProgramData%\Microsoft\Windows Terminal\Fragments\<app>\<file>.json` |
| Per user | `%LOCALAPPDATA%\Microsoft\Windows Terminal\Fragments\<app>\<file>.json` |

Bosun is an unpackaged per-user win-x64 executable, so it writes the **per-user**
path. Note the `Microsoft\` segment. The Store-vs-unpackaged distinction this
ADR's original note warned about is a red herring: it applies only to
Store-*packaged* apps registering via an app-extension manifest
(`PublicFolder\Fragments`), a different mechanism Bosun does not use.

*Profile GUID.* UUIDv5, derived in two levels, with name strings encoded
**UTF-16LE** before hashing:

```
appNamespace = UUIDv5(TERMINAL_FRAGMENT_NS, <app-name>)
profileGuid  = UUIDv5(appNamespace, <profile-name>)
TERMINAL_FRAGMENT_NS = {f65ddb7e-706b-4499-8a50-40313caf510a}
```

Microsoft documents a test vector: app `Git`, profile `Git Bash` →
`{2ece5bfe-50ed-5f3a-ab87-5cd4baafed2b}`. Reproducing it is the unit test that
proves the derivation, and it is pinned as one.

*Failure surface.* Since microsoft/terminal#10601 (shipped 1.10.1933.0) Terminal
wraps per-fragment parsing in try/catch, so a malformed fragment is skipped and
others still load — but there is **no UI surface, toast, or default log entry**
indicating that a fragment failed. Profiles appearing in Terminal's dropdown is
the only available confirmation. Hence: validate our own JSON before writing it,
and `docs/OPERATIONS.md`'s "Terminal profiles missing" triage row says so.

*Related.* Invariant I5 in `CLAUDE.md` carried the same abbreviated path and is
corrected alongside this. The shipped code was already right
(`FragmentWriterOptions`), so this amendment closes a doc/code divergence rather
than a defect.

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


---

## ADR-015 — A user-requested unmount parks the host

**Status:** Accepted (2026-08-16). Resolves `bs-cql`. Clarifies ADR-005 and
`docs/ARCHITECTURE.md` §4 rule 6.

**Context.** Independent adversarial testing of E5 found that a **persistent**
host the user unmounted from the tray drained and then **remounted itself within
the same drain**. `CompleteDrainAsync` auto-re-enabled every host, and
`OnEnteredReadyAsync` auto-mounts persistent hosts on every arrival at `Ready` —
including the arrival immediately following the user's own unmount.

The behaviour was implemented as "stays parked" ahead of this decision, and the
implementation shipped on `main`. This ADR records the reasoning and ratifies it,
rather than leaving live behaviour resting on an unrecorded interpretation.

**Decision.** A user-requested unmount **parks** the host. It stays unmounted
until one of: an explicit user remount, a configuration reload, or an application
restart. The host continues to probe normally while parked — it simply does not
auto-mount. The parked state is exposed in the supervisor snapshot.

**Reasoning.**

*The spec already says this, read as a whole.* §4 lists "user unmount" as a
`Mounted → Draining` trigger. `Draining`'s only outgoing edge is `→ Disabled`.
`Disabled`'s only outgoing edge is "enable", and its only annotation on the
diagram is "user disables". Followed end to end, a user unmount ends **parked**.
Rule 6 governs a *tier's resting state* — where a host sits when nothing has
happened — not the override of an explicit command. ADR-005 concerns unmounting
on *probe failure* and says nothing about undoing a deliberate instruction.

*And it is what the click means.* Clicking "unmount" and watching the drive
reappear a minute later reads as the tool ignoring you. There are ordinary
reasons to want a letter released: copying a large file locally, a flaky link,
maintenance on the far end, or running a backup that should not traverse a network
mount. Without this there is no in-app way to achieve any of them short of
editing `hosts.toml`.

*Why it still probes while parked.* So the tray can show live reachability. A
parked host that also stopped probing would render as unknown, which looks like a
fault; the whole point is that a drive deliberately absent must not look like a
drive that broke.

**Consequences.**

- `docs/ARCHITECTURE.md` §4 gains rule 9 stating this explicitly. The ambiguity
  existed because the diagram implied it without any rule saying it.
- The tray (E9) must distinguish **parked** from **unreachable** from **mounted**.
  A user who parked a drive should see that they parked it. This joins ADR-012's
  requirement that the icon and status window explain state causally.
- `Disabled` currently conflates three things — never-enabled (`mode = "none"`),
  parked-by-suspend, and drained-and-about-to-re-enable. That conflation is *why*
  this bug existed. A distinct parked state, or an `AdministrativelyEnabled` flag
  the user unmount clears, would make rule 6 decidable rather than inferable.
  Worth revisiting if the supervisor's state handling is touched again.

**Rejected alternatives.**

*Keep auto-remounting and make the UI honest* ("unmount (will remount when
reachable)"). Cheap, and it removes the surprise without removing the problem —
the user still cannot release a drive letter.

*Treat a user unmount as administratively disabling the host until re-enabled.*
Strongest reading, but it makes the persistent tier stop meaning anything for that
host until the user remembers to re-enable it, and a forgotten disable is a worse
failure than a forgotten park — the drive silently never returns, even after a
restart.

---

## ADR-016 — A mounted host is also deep-probed, on its own cadence

**Status:** Accepted (2026-08-16). Resolves `bs-2eg`. Amends ADR-011.

**Context.** ADR-011 settled *that* a `Mounted` host is always probed; it did
not revisit *what the probe checks*. `HandleMountedProbeDueAsync` calls only
`ProbeShallowAsync` — a TCP connect to `host:port`. `docs/ARCHITECTURE.md` §3
already states the principle this violates: "TCP success does not imply SFTP
success. Never mount on a shallow probe alone" — but the principle was applied
only to the transition *into* `Mounting`, never to the recurring check on a host
already `Mounted`.

That gap is not an edge case for this project's actual deployment (desktop +
LAN NAS, not the laptop dock/undock scenario the spec was originally framed
around). It is the maintainer's two most likely failure modes:

1. **Overnight sleep.** The desktop sleeps with a mount up. The NAS stays on the
   same LAN and keeps answering port 22, but the SSH channel underneath the
   mount dies — timed out, `sshd` restarted, session reaped. On wake, the
   shallow probe **succeeds**, `ConsecutiveMountedFailures` resets to zero, and
   every filesystem call into that drive hangs. Explorer freezes.
2. **A network interruption** shorter than `failures_before_unmount ×
   mounted_probe_interval_seconds` never trips the shallow-failure threshold,
   but is easily long enough to kill the SSH channel underneath the mount.
   Identical signature once the host returns: probe passes, mount is dead.

Invariant I8 (unmount on suspend) defends case 1 only if the drain completes
before the machine actually sleeps — §3 already documents it as best-effort,
and it does not defend case 2 at all. Nothing else in the state machine watches
for this, because by the only measure ADR-011 established, the host is healthy.
This is an Invariant I2 hole ADR-005's design did not anticipate: ADR-005
assumes it is the host becoming *unreachable* that signals trouble, and in both
scenarios above it never does.

**Decision.**

1. A host in `Mounted` is deep-probed (`operations/list`, via
   `IProbe.ProbeDeepAsync`) on its own recurring cadence,
   `global.mounted_deep_probe_interval_seconds` (default 300s), **in addition
   to**, never instead of, the shallow cadence ADR-011 established. The two
   probes run independently — a change to one's cadence or outcome must not
   affect the other's schedule.
2. The deep probe goes to `rclone rcd` over loopback HTTP, exactly as the
   pre-mount deep probe does, and is bounded by `probe_timeout_seconds`. It
   never touches the mounted drive letter directly (`DriveInfo`,
   `Directory.GetFiles`, a shell enumeration, etc.) — that is precisely the
   call that can hang, and `MountSupervisor`'s channel is *globally*
   serialised (see its class remarks), so one hung enumeration would stall
   every other host's probing and mounting, turning a single dead mount into a
   total supervisor stall. If drive-letter-level verification is ever wanted,
   it must be strictly out-of-band with a hard, enforced timeout — not this
   mechanism.
3. A failed deep probe accumulates on its **own counter**,
   `ConsecutiveDeepProbeFailures`, structurally separate from the shallow
   probe's `ConsecutiveMountedFailures` — never shared, never merged. It drains
   after a **fixed 2** consecutive deep-probe failures — a hardcoded internal
   constant, not a new `[global]` field (see Rejected alternatives for why
   neither "share the shallow counter" nor "make the threshold configurable"
   survived contact with the actual implementation). A successful deep probe
   resets its own counter to zero.
4. `global.mounted_deep_probe_interval_seconds` is a new `[global]` setting,
   default `300`. Absent means `300`, not a validation error — the same
   "absent means default" treatment ADR-011 established for
   `mounted_probe_interval_seconds`, for the same reason (rejecting a whole
   config for a newly-introduced key would break every existing `hosts.toml`
   on upgrade). Zero or negative *is* a validation error, because it would
   disable the cadence this ADR exists to guarantee.

**Reasoning for a fixed threshold of 2, not 1 and not `failures_before_unmount`.**
A deep probe failure is qualitatively stronger evidence than a shallow one: a
shallow (TCP) failure could mean the host rebooted, a firewall blipped, or the
network hiccuped for a moment that says nothing about the mount itself, while a
deep probe failure means the thing that actually serves the drive letter's I/O
just failed to list its own root directory over the very channel the mount
depends on. Treating it as one shallow-probe-equivalent failure against the
*shallow* threshold (default 3, `+1` per deep failure) would mean the tool has
strong evidence of a dead mount and chooses to wait for further ticks anyway —
the "may be too lenient" reading this ADR is scoped to reject — and, worse, the
implementation this ADR originally specified (`+1` against a *shared* counter,
jumping straight to `failures_before_unmount` on a single failure) turned out to
be actively unsafe: see the "Rejected: share the counter" entry below for the
concrete bug it caused. `2` is not `1` because a single loopback-HTTP hiccup
against `rclone rcd` — itself possibly momentarily busy, e.g. mid VFS-cache
flush — should not be instantly fatal to an otherwise healthy mount; the deep
probe is strong evidence, not infallible. `2` is well short of the shallow
default of `3` specifically because the underlying signal is stronger, and it
is a fixed constant rather than a fraction of `failures_before_unmount` so that
tuning the shallow ladder for an unrelated reason (a flakier network, a stricter
SLA) cannot silently make the deep-probe path more lenient than intended.

**Consequence.** Worst-case detection latency for a mount whose SSH channel has
died while the TCP-level host stays reachable is bounded by
`2 × mounted_deep_probe_interval_seconds` (default 600s / 10 minutes) — not by
`mounted_probe_interval_seconds × failures_before_unmount`, which never fires in
this failure mode because the shallow probe keeps succeeding. Ten minutes is
slower, in raw wall-clock terms, than the shallow ladder's own worst case (three
minutes with the defaults) — a comparison worth naming explicitly rather than
glossing over: it is not an apples-to-apples comparison, because two independent
deep-probe failures are qualitatively stronger evidence of a dead mount than
three TCP timeouts, and because the prior state of the world was "never
detected at all." Ten minutes to detect and clear a wedge the tool previously
could not detect *ever* is the actual trade-off this ADR makes, not "ten minutes
instead of three." `docs/OPERATIONS.md` should gain a T-series row for this
scenario once the manual test protocol is next revisited (tracked separately;
not part of this delivery — see the implementer's report).

**Rejected alternatives.**

*Share `ConsecutiveMountedFailures` with the shallow probe, weighting a deep
failure heavily (jump straight to `failures_before_unmount`) rather than `+1`.*
This was the ORIGINAL design for this ADR, and it shipped, briefly, inside this
delivery before being caught: with the shipped defaults (60s shallow / 300s
deep — an exact 5:1 ratio), both timers are armed from the same instant every
time a host enters `Mounted`, so the deep-probe tick and the 5th consecutive
shallow-probe tick land on the *exact same instant*, every cycle, for the life
of every mount. `MountSupervisor`'s channel processes same-instant timer
continuations one at a time, in whichever order they happened to be enqueued —
and with a shared counter, a coincidentally-successful deep probe processed
first could silently reset a shallow-failure streak one tick before it reached
threshold, defeating `docs/ARCHITECTURE.md` §4 rule 3's "exactly N consecutive
failures unmounts" guarantee. This was caught by
`Independent/FailureAccountingTests` (`Exactly_N_consecutive_failures_unmount…`
and `Interleaved_successes_reset_the_count…`) going red against a default
configuration that is a completely ordinary, recommended one — not a contrived
adversarial config. It is an order-dependent race baked into the shipped
defaults, not a one-off test artefact, and no adjustment to the default interval
values would have been a real fix (a user is free to choose any two intervals
where one divides the other — 60/120, 30/300, etc. — and hit the identical
coincidence). Structurally separating the counters removes the race entirely:
neither probe's bookkeeping can be affected by the other's outcome, regardless
of instant-level coincidence or channel-processing order.

*Make the deep-probe failure threshold a new configurable `[global]* field.*
Rejected under this project's "do not add a setting for every knob" style
(`CLAUDE.md` §5) and the fact that no defensible value range presented itself —
unlike `mounted_deep_probe_interval_seconds` (a cadence, naturally expressed in
seconds with an obvious sensible default), a *failure count* threshold's only
principled values are small integers close to `1`, and the reasoning above
already picks `2` without needing a knob. If real-world use ever demonstrates
`2` is wrong, it is a one-line constant to change, not a schema migration.

*Weight a deep failure at the full shallow threshold via a shared counter but
fix the race some other way (e.g. process same-instant timers in a fixed
order).* Considered, and rejected: making correctness depend on a specific,
undocumented processing order for same-instant channel continuations is fragile
in exactly the way the shared-counter design already proved itself to be —
it trades one order-dependent behaviour for a different, only slightly less
fragile one, rather than removing the dependency. Structurally independent
counters are the fix that makes the *order* of same-instant processing
irrelevant, which is the actual property wanted.

*Force a deep probe immediately on every shallow-probe cycle instead of a
separate slower cadence.* Rejected: it would make the deep probe as frequent as
the shallow one, generating continuous SFTP-level traffic against a host the
user is not actively touching in most minutes of the day, for a payoff (faster
detection) that is not actually needed — a 300s cadence with a 2-failure
threshold is already fast enough that the worst case is "wake the desktop, wait
up to ten minutes," not the alternative of a wedge that never resolves at all.


---

## ADR-017 — State that survived a transition is not trusted

**Status:** Accepted. Resolves `bs-mk4`, `bs-brv`, and item 1 of `bs-u4a`.
Extends ADR-016.

**Context.** Three defects were filed independently, by three different agents,
against three different components:

- `bs-mk4` — a network change force-probes mounted hosts with the **shallow**
  probe only. The deep probe added by ADR-016 keeps its own timer and is not
  forced.
- `bs-brv` — `ResumeAsync` drops `Mounted`/`Mounting` into a `default:` case
  commented *"suspend already drained them"*, and never calls `mount/listmounts`
  at all.
- `bs-u4a` item 1 — `docs/ARCHITECTURE.md` §3's event table gives **network
  change** the stronger treatment ("re-probe all immediately; force-probe mounted
  hosts") and **resume** the weaker one ("re-probe all immediately").

Each was locally reasonable. `bs-brv`'s comment is a correct reading of rule 5 in
isolation. `bs-mk4` is a defensible scope boundary for the epic that introduced
the deep probe. §3's asymmetry was written before either existed.

Together they are one defect wearing three costumes: **the supervisor trusts
mounted state that has just survived a power or network transition.**

That state is the least trustworthy state the application ever holds. Across a
suspend, `rclone rcd`'s child process, the WinFsp driver, the TCP stack and the
SSH channel underneath every mount have all been through a power transition. The
supervisor's belief about what is mounted was formed before all of that and has
been verified against nothing since.

The specification already says so, in the row that matters most —
`docs/ARCHITECTURE.md` §6: *"Machine sleeps with mounts up | Unmount on suspend.
**If we missed it, force-unmount and reconcile on resume.**"* — and §3 agrees from
the other side: *"accept that a forced unmount may be needed on resume."* No
component implements it.

**Decision.**

After **any** power or network transition — resume, or a network address change —
the supervisor does not trust its own mounted state. It re-derives it:

1. **Reconcile against `mount/listmounts` first.** Ground truth before anything
   else. A mount the supervisor believes in that is absent, or present and
   unknown, is corrected before any probe result is interpreted.
2. **Force both probes for hosts that are still `Mounted`** — shallow *and* deep.
   ADR-016's deep probe is the only check that can see a dead channel under a live
   host, and a transition is precisely when that becomes likely.
3. **Resume is at least as strong as a network change**, never weaker. §3's event
   table is corrected accordingly.

**Reasoning.**

*Why the asymmetry was backwards.* A network change means the path to the host may
have altered. A resume means **everything** may have altered — including the local
components doing the mounting. Giving resume the weaker treatment inverts the risk
ordering, and it is what let `bs-brv` look correct in review.

*Why reconciliation comes before probing.* A probe answers "is the host
reachable"; `listmounts` answers "what is actually mounted". After a transition the
second question is the one whose answer we have no basis for, and interpreting
probe results against a stale mount table is how a host gets marked healthy while
its drive letter points at nothing. Since `bs-ka9`, adoption is `Fs`-aware, so
reconciliation is also where a mount pointing at the *wrong host* gets caught.

*Why this costs almost nothing.* Transitions are rare — a sleep/wake cycle a day,
network changes occasionally, and those are now debounced. The cost is one
`listmounts` plus one deep probe per mounted host per transition. Against that: the
current periodic cadence means a dead mount can persist roughly ten minutes after
an event that explicitly told us to look.

*Why a principle rather than three patches.* Three agents independently produced
three locally-sound decisions that combined into a hole. A rule stated once —
*after a transition, re-derive; do not trust* — is checkable in review, and the
next transition-like event added to the system inherits it instead of re-deriving
it badly.

**Consequences.**

- `ResumeAsync` and `NetworkChangedAsync` converge on a shared "re-derive" path
  rather than each hand-rolling a subset.
- `docs/ARCHITECTURE.md` §3's event table and §4 rule 5 both need amending; §6's
  failure row finally becomes implemented rather than aspirational.
- ADR-016's deep probe becomes reachable by force, not only by its timer.
- More probe traffic immediately after a transition. That is the intended trade
  and is bounded by the number of mounted hosts.

**The sub-decision this left open, settled at implementation.**

The question was whether a forced deep-probe failure *immediately after a
transition* is conclusive enough to drain, or must still meet ADR-016's threshold
of two.

**Settled: one post-transition deep failure is conclusive, but only once `rclone
rcd` has answered `core/version`.** The gate is what makes the single failure
safe. Without it the two readings of a failed `operations/list` — "the SSH channel
under this mount is dead" and "rcd has not finished coming back yet" — are
indistinguishable, and acting on the second unmounts a healthy drive on every
resume. With rcd confirmed live, only the first reading remains, and a transition
is corroborating evidence that makes waiting for a second failure a cost paid in
exactly the window where a wedged drive is most likely.

If `core/version` does not answer, the reconciliation does not run at all: the
supervisor cannot distinguish anything without rcd, so it retries on the ordinary
cadence rather than guessing. That is the same posture as I1 — no action on
unverified state — applied to the supervisor's own dependency.

**Rejected alternatives.**

*Fix the three issues separately.* Cheaper now, and it leaves the underlying rule
unstated — so the fourth instance arrives with the next transition-like event.

*Rely on the 30-second periodic reconciliation.* It already exists and did not
prevent any of this. A cadence is not a guarantee tied to an event, and the whole
point of a transition is that it tells you exactly when to look.

*Unmount everything on resume, unconditionally.* Simple, and defensible given the
drives-disappear-and-reappear posture of ADR-005. Rejected because it discards
working mounts that survived a sleep perfectly well, and the daily cost of that
lands on the one user this tool exists for.

---

## ADR-018 — A windowed application that lives in the tray, not a tray applet

**Status:** Accepted. Supersedes the "no main window" clause of
`docs/ARCHITECTURE.md` §2. Extends ADR-012 rather than replacing it.

**Context.** The original design was a tray applet: no main window, a context
menu, and a status window popped on demand. That was a reasonable default for a
daemon — Bosun's real output is drive letters and Terminal profiles, and you
interact with it through Explorer and Windows Terminal, not through Bosun.

Two things pushed against it. ADR-012 loaded real weight onto the visible
surface — the tray icon must encode aggregate health at a glance, and every
degraded state must explain itself *causally* ("`P:` is not mounted — WinFsp is
not installed"), because a user who finds no drive letter will otherwise blame
Windows, the VPN, or the network. And `docs/OPERATIONS.md`'s entire triage story
is "read the transition log", which today means opening a file by hand. A context
menu is a poor host for a diagnostic table, and a popup window that only exists
while you are looking at it is a poor host for watching a resume happen.

**Decision.** Bosun is a normal windowed WPF application that happens to live in
the tray. Concretely:

1. **A real main window.** Taskbar button, Alt+Tab, resizable, remembers its size
   and position. Not a borderless popup anchored to the tray.
2. **Launch context decides visibility.** Started from `shell:startup` at login,
   Bosun starts to tray with **no window**. Started manually, it shows the window.
3. **Closing the window does not exit.** It hides to tray.
   `ShutdownMode=OnExplicitShutdown` stays. Exit is deliberate — tray menu, or
   the window's own explicit Exit.
4. **Taskbar presence only while the window is shown.** A hidden window is not an
   Alt+Tab entry.
5. **The tray icon keeps every obligation ADR-012 gave it.** It is still the
   only always-on surface, because the window is closed most of the time. Nothing
   in Decision 3 of ADR-012 is relaxed.
6. **One instance, enforced** (`bs-2wa`). A second launch shows the running
   instance's window and exits.

**Reasoning.**

*Why rule 2 is not negotiable.* ADR-012 already made this argument in the
opposite direction and it applies unchanged: *"Bosun launches from
`shell:startup`. At login the user is walking away, windows are fighting for
focus."* A windowed app that presents its window every login is precisely the
irritant that trains people to dismiss Bosun reflexively — which would destroy
the signal ADR-012's whole degradation contract depends on. The window is for
when you *asked* for it.

*Why a window at all, given the tool is invisible by design.* Because there are
exactly two moments when you need to look at Bosun, and both were underserved.
When something breaks, you want a table you can read and a transition log you can
scroll — not a balloon that has already gone. And on first run, ADR-012 *already*
specified a window shown actively; that was a special case bolted onto an app
that otherwise had no windows. Making the window first-class removes the special
case rather than adding one.

*Why one instance becomes load-bearing now.* Under the tray-only design the only
launch was autostart, once per login. Once there is a window, "is it running? let
me double-click it" is the obvious user action, and it is the action that starts a
second supervisor racing the first for the same drive letters. The guard is not
polish; it is what makes rule 1 safe. See `bs-2wa`.

*Why not a full management UI.* `CLAUDE.md` §5 stands: no settings UI for what
`hosts.toml` already covers. The window is for *seeing*, not configuring. Host
tiers, drives, colours and probe intervals stay in the config file, which this
tool's one user is comfortable editing. The window's content is state and history
— what is mounted, why it is not, and what happened.

**Consequences.**

- `ARCHITECTURE.md` §2's "A WPF application with no main window" is wrong and is
  amended. The process model is otherwise unchanged: one process, in the user's
  interactive session, `rclone rcd` as a child (ADR-004 untouched).
- E9 grows: window chrome, show/hide/restore lifecycle, launch-context detection,
  and window-state persistence are now in scope alongside the tray icon and
  context menu.
- `bs-2wa` (single instance) becomes a prerequisite for shipping the window, not
  an independent nicety.
- The autostart registration in E10 must pass whatever flag rule 2 keys off, so
  "started by Windows" is distinguishable from "started by the user".
- Two surfaces can now trigger the same action (tray menu and window). Both post
  commands to the supervisor channel; neither mutates state directly. The
  existing E9 rule — *"actions post commands to the supervisor channel; UI never
  mutates state"* — is what keeps that from being a problem.

**Rejected alternatives.**

*Keep the tray applet.* Cheaper, and it is what the spec said. Rejected because
the diagnostic content ADR-012 requires does not fit a context menu, and because
the popup-only status window is unusable for the one thing it is most needed for
— watching state change during a resume.

*Show the window at every launch, including login.* Simpler rule, no launch-context
detection. Rejected on ADR-012's own reasoning about login-time focus stealing.

*A dashboard meant to be left open.* Considered and not chosen as a *goal* — the
window is built to be closed. Nothing here prevents leaving it open, and if that
turns out to be how it gets used, the content is the same either way.

