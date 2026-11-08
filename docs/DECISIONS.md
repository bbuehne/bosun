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

**Amendment 1 (2026-08-17, `bs-dkm`) — Decision 1 is reversed. The commandline
uses the host's own connection details, not its config key.**

Decision 1 was wrong, and the way it failed is the tell.

A host was added through the GUI with hostname, port, user and key all filled in.
Its drive mounted. Its terminal then failed with
`ssh: Could not resolve hostname aws-lucid-forge-tm` — ssh had been handed the
config key, found no matching `Host` block, fell back to treating the alias as a
literal hostname, and asked DNS to resolve it. The only reason the *first*
configured host worked is that a matching `Host` block happened to already exist
for it.

Both mitigations this ADR specified were in place and neither helped: README's
Requirements paragraph said it, and `docs/OPERATIONS.md` had the triage row. The
prerequisite was documented *and* still discovered at the point of failure, which
is exactly what the Consequences section said must not happen. A trap that is
documented is still a trap.

**The revised decision.** The emitted commandline is:

```
ssh -i "<identity_file>" -p <port> <user>@<hostname>
ssh -t -i "<identity_file>" -p <port> <user>@<hostname> tmux new -A -s <session>
```

*Why the original reasoning does not survive.* It rested on a false dichotomy —
that a fully-specified invocation means "`ssh_config` is bypassed". It does not.
ssh matches `Host` patterns against **the target named on the command line**, so a
`Host <hostname>` block still applies and `ProxyJump` keyed on the real hostname
still works. What changes is only that reaching a bastion no longer *requires*
inventing a per-host alias first. And where a command-line option does override
the config file, that precedence is the one a user filling in a dialog expects:
the port they typed wins.

The original ADR also argued that `hostname`/`port`/`user`/`identity_file` "earn
their place serving rclone whatever the profile emits". True, but it proves less
than it appears to: it explains why those fields exist, not why the terminal path
should decline to use them. ADR-019 later put those four fields in a GUI form.
Collecting them in a dialog and then not connecting with them is indefensible.

*What is unaffected.* Decision 2 stands unchanged — the profile `guid` is still
derived from the config key, and the key remains Bosun's stable identity for
supervisor state, session correlation, and fragment identity. The key stopped
being a *network name*; it did not stop being an *identity*.

Decision 3 is withdrawn. There is no longer a prerequisite for README to state.

*Cost, paid.* E8's `SshCommandLineParser` was **not** already correct under this
change — the claim above that it needed none was true only while E7 emitted a bare
target. It now walks the argument list getopt-style rather than matching two rigid
shapes, so a flag's value is never mistaken for the target, and correlating a live
`ssh.exe` back to a host key moved from a dictionary lookup to a hostname match in
`SshSessionMonitor`, narrowed by user and port. An ambiguous match resolves to
**no** host rather than a guess: reporting a session under the wrong host would
show activity on a host that has none.

The E7/E8 contract test the Consequences called for now runs the invocation
through a real `SshSessionMonitor` rather than the parser alone, because with the
key no longer appearing in the command line, a parser-only assertion would no
longer test the property that has to hold — that a terminal opened from a Bosun
profile shows up in the tray under the right host.

*Verified, not assumed.* Against a real `cmd.exe` and the real Windows OpenSSH
binary: a quoted key path containing spaces survives the reconnect wrapper as a
single argv entry (no `/s` needed), and Windows OpenSSH expands a leading `~` in
`-i` itself — so the generator passes the path through unexpanded and stays pure.

**Amendment 2 (2026-11-02, `bs-4ic`, `bs-2s1`) — launch by GUID, not by name.**
Decision 2 makes the GUID the stable identity, but launch sites and the docs still
said `wt -p "<host>"`. That selects by profile **name**, and the name is
`display_name`, not the config key, so it matched only when the two happened to be
equal. Every Bosun launch of a profile (tray "Open terminal", autostart tabs) now
passes the braced GUID — `wt -w 0 new-tab -p "{guid}"` — built in one place,
`TerminalProfileArgument`. Windows Terminal documents `--profile` as accepting a
name or a GUID; the maintainer confirmed on 2026-11-02 that the braced GUID form
opens the right profile on their machine. CLAUDE.md §7 item 1 and
`docs/WORK-BREAKDOWN.md` (E7) state the GUID form.

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

**Note (2026-11-02, `bs-brb`): the test this ADR cites was rewritten.** Decision 1 above
says persistent polling is "`docs/OPERATIONS.md` T2". When this ADR was accepted, T2 was the
laptop test "close the lid on one network, open it on another". `bs-aya` replaced it (see
ADR-001's amendment): T1 is now an overnight sleep and T2 a network interruption. The decision
is unchanged. The behaviour it names, a persistent host returning on its own once its server is
reachable again, is now exercised by T1 (a wake before the network is up) and by T2 (the link
coming back).


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
until one of: an explicit user remount, or an application restart. The host
continues to probe normally while parked — it simply does not auto-mount. The
parked state is exposed in the supervisor snapshot.

**Amendment (`bs-7ck`): a config reload no longer un-parks.** This ADR originally
listed "a configuration reload" alongside an explicit remount and a restart, and
`docs/ARCHITECTURE.md` §4 rule 9 said the same. That was written when a reload
meant the user had deliberately opened `hosts.toml` in an editor and saved it —
itself an act of intent, and a rare one.

ADR-019 makes that false. The window now writes `hosts.toml` on **every** host
edit, so under the original wording a drive the user had explicitly unmounted
would come back the next time they changed *any* host's tab colour. The click that
un-parks would be one the user never connected to that host at all.

Parking is deliberately in-memory and is not persisted to config, so a restart
still clears it — that is unchanged and correct: a fresh process has no memory of
a session-scoped decision. What is removed is the middle term. The rule is now
symmetric with how parking is *set*: an explicit user action sets it, and only an
explicit user action clears it.

Found by independent test authorship of `bs-7ck`, which spotted that its spec and
this one disagreed rather than implementing to one and leaving the other stale.

**Amendment (2026-11-07, `bs-uzy`, maintainer decision B): changing a host's mode
from on-demand to persistent un-parks it.** The previous amendment says only an
explicit user action clears what an explicit user action set. Editing a host's
`mount.mode` to `persistent` in the host editor is such an action: it means "use
this drive again". So that one edit now clears the park, and the host then behaves
as an unparked persistent host: if `Ready` it mounts now, if `Unreachable` it
mounts when it next reaches `Ready`, and in any other state the ordinary persistent
paths mount it later. Every one of those mounts still goes through
`TryBeginMountAsync` and its probe (Invariant I1). One `Information` line is logged
when this clears a park.

What does **not** clear a park: any other edit (hostname, idle seconds, tab colour
and so on); a change to `on-demand` (an on-demand host never auto-mounts, so there
is nothing to un-park and the park stays); and a change from `none`, which was not
in the decision (**corrected 2026-11-07, `bs-uzy.1`**: a change *out of* `none` does
clear it, see below).

**Correction (2026-11-07, `bs-uzy.1`, maintainer decision A): two edge cases clear
the park too.** The `bs-uzy` amendment left two cases open; the maintainer decided
both clear the park.

1. *An edit to `persistent` while the user's unmount is still draining.* A park is
   set only when a user-unmount drain completes. Clearing it at the edit therefore
   did nothing: the drain finished afterwards and parked the host as `persistent`,
   so the user's later act was ignored. The edit now marks the in-flight drain so
   its completion does not park; the host goes through the ordinary re-enable
   (probe, then the persistent paths mount it from `Ready`). Nothing is mounted
   from the drain completion, so Invariant I1 holds. A drain that completes with
   the host still `on-demand`, and a `persistent` to `on-demand` edit during a user
   unmount, still park as before.
2. *Any change out of `none`.* `IMountSupervisor.ConfigChangedAsync` already says a
   change from `none` "begins supervising from `Disabled`, exactly as at startup",
   and nothing is parked at startup. So a parked host edited to `none` and later to
   `persistent` (or `on-demand`) is no longer parked. This replaces the sentence
   above that a change from `none` does not clear a park. A change **to** `none`
   still does not clear it.

The same `Information` line is logged when either case clears a park that was
already set.

What this fixes: before, the `Ready` branch of the on-demand-to-persistent change
mounted the host without looking at the park, while the other three persistent
auto-mount sites respected it. A host the user had unmounted, switched to
persistent and back to on-demand, then put through a suspend, was therefore
remounted at wake by the ADR-022 record. That left a host `Mounted` while parked,
which undid the user's unmount. With this amendment `Mounted` and parked no longer
coexist on any path found.

*Rejected: option A, "respect the park"* (add `&& !host.UserParked` at the mount
site, so the switch to persistent leaves the host parked). It is the smaller
change and keeps the amendment above literally true, but a user who deliberately
switches a drive to persistent would then find it still absent, with the tray
showing it parked and no hint that the edit they just made was ignored.

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

### Amendment (2026-11-02, `bs-gbf`): the detection bound includes the probe time

**Context.** The Consequence paragraph bounds detection at
`2 × mounted_deep_probe_interval_seconds`. Writing the manual test T7 showed the bound is
short. It counts the two waits but not the two probes. Reading the code confirms why:
`HandleMountedDeepProbeDueAsync` re-arms the deep-probe timer only after the probe returns, so
consecutive probes start `D + (time the previous probe took)` apart, and a probe that fails by
timing out takes the whole of `global.probe_timeout_seconds` (`P`), the budget
`HostProbe.ProbeDeepAsync` enforces. A probe that fails fast takes less, so `P` is the ceiling.

**Amendment.** The worst case from a mount's last good deep probe to the start of its drain is
`2D + 2P`, where `D` is `mounted_deep_probe_interval_seconds` and `P` is
`probe_timeout_seconds`: wait `D`, first probe fails (up to `P`), wait `D`, second probe fails
(up to `P`), drain begins. With the defaults (`D` = 300, `P` = 5) that is 610 s, not 600 s.
The unmount that follows the drain's start is not counted in either figure. The decision
(two consecutive failures, own counter, own cadence) and the trade-off argued above are
unchanged; only the arithmetic is corrected. `docs/ARCHITECTURE.md` §6 and
`docs/CONFIG-SCHEMA.md` carry the corrected figure; `docs/OPERATIONS.md` T7 already allows
`2D + 2P`.


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

**Amendment (`bs-ww9.3`): the launch-context flag.** The Consequences section above
says E10's autostart registration "must pass whatever flag rule 2 keys off" without
naming it — this records the answer, verified rather than left to be reinvented.

The flag is a command-line switch, `--autostart`, matched case-insensitively and
independent of position (`Bosun.UI.LaunchContextDetector.Detect`, tested in
`LaunchContextDetectorTests`). E10's `shell:startup` shortcut must point at
`Bosun.exe --autostart`; anything launched without that exact flag — a desktop
shortcut, `Bosun.exe` typed at a prompt, a debugger launch — is `LaunchContext.Manual`
and shows the window. A command-line switch was chosen over an environment variable
or a marker file because it is visible in the shortcut's own Target field (so the
autostart registration is self-documenting to anyone who inspects it) and needs no
process-wide state that could leak into a manual launch started from the same shell.

**Amendment (`bs-e28`): the launch context also gates `session.autostart`.**
`hosts.<key>.session.autostart = true` opens a Windows Terminal tab for that host only
when Bosun starts with `--autostart` at Windows login (`LaunchContext.Autostart`). A
manual start, a watchdog restart and a user restart open nothing: a restart happens
mid-session, and a second set of tabs is a duplicate the user closes by hand. All
autostart hosts open as tabs of one `wt -w 0` call, once per process, after the Terminal
fragment write succeeds (skipped and logged if it failed). It does not wait for rclone or
a mount and does not react to config reloads. A `mode = "none"` host gets its tab:
autostart is a terminal-session axis, independent of mounting (ADR-008).


---

## ADR-019 — The window edits hosts; `hosts.toml` gets a single writer

**Status:** Accepted. Supersedes ADR-018's "not a management UI" clause and the
"no settings UI" sentence of `CLAUDE.md` §5 **for host definitions specifically**.

**Context.** ADR-018 drew the window's scope deliberately narrow: *"The window is
for seeing, not configuring. Host tiers, drives, colours and probe intervals stay
in the config file, which this tool's one user is comfortable editing."* That
rested on a premise about the user, and the user has said it is wrong: he wants to
add and edit hosts in the GUI, with sane defaults, and to import much of a host's
detail from an existing Bitvise/Tunnelier profile.

That is the maintainer overruling a scope decision about his own tool, which is
decisive. What follows is not whether, but the two things that are genuinely
hard about it.

**Decision.**

1. **The window can create, edit and delete hosts.** Fields it edits are the ones
   a host actually needs — key, display name, hostname, port, user, identity file,
   mount tier/drive/remote path, session options, probe intervals — with defaults
   pre-filled so a new host is a few fields, not a blank form.
2. **Bosun becomes the sole writer of `hosts.toml`.** Edits are written by Bosun,
   atomically (temp file + replace), and the file remains fully hand-editable when
   Bosun is not running.
3. **Validation runs before a write, not after.** The existing `ConfigValidator`
   is the same gate the file path already uses, so the window cannot produce a
   config the loader would reject.
4. **Import from a Bitvise/Tunnelier profile pre-fills a form; it never writes a
   host directly.** The user confirms before anything is saved.
5. **Import never supplies key material.** See below.

**Reasoning.**

*Why a single writer, and why this is the real risk.* Two writers to one file is
exactly the failure ADR-006 refused to accept for Windows Terminal's
`settings.json` — *"Terminal rewrites it whenever the user changes anything in its
settings UI. Two writers means clobbering."* Bosun already **watches**
`hosts.toml` and reloads on change (E2). Adding a GUI writer creates the same
hazard against ourselves: the user edits the file in an editor while the window
holds older values, then saves from the window and silently reverts them. Making
Bosun the writer, writing atomically, and reloading from what actually landed
keeps one authority at a time. Hand-editing stays supported because it is how the
file is documented and how a broken install gets repaired.

*Why validation moves in front of the write.* A GUI that can save an invalid
config turns a startup-time error into an error the user caused and cannot see.
`ConfigValidator` already enforces the invariants that matter — drive letters in
`[D-Z]`, `vfs_cache_mode` no weaker than `writes` (I6), `network_mode` true (I7),
identity file exists, no duplicate drives or display names. Running it before the
write means the window refuses to save rather than saving something the loader
will reject on next start.

*Why import cannot supply the key, which is the one thing users will expect it
to.* Bitvise keeps keypairs in its own store (`HKCU\Software\Bitvise\Keypairs`),
not as files on disk, and a profile's key material is inside its proprietary
blob. Bosun requires `identity_file` to be a path that **exists** (Invariant I10,
key-based auth only), because that is what `ssh` itself will read. So an imported
host arrives with everything except a key, and the form must say so plainly rather
than leaving a field mysteriously empty. Exporting a key from Bitvise is a
deliberate act in Bitvise's own key manager and is not Bosun's job to automate.

*What the profile format actually is.* Length-prefixed big-endian records, version
tagged in the header (`Tunnelier 9.51`), undocumented. It is not parsed
completely and should not be: the import extracts the fields it can identify
confidently — hostname, username, port — and leaves the rest. Verified across
profiles written by three different versions (7.16, 9.17, 9.51). A partial,
honest extraction that pre-fills three fields is worth far more than a brittle
full parse that breaks on the next Bitvise release.

**Consequences.**

- `IHostConfigStore` grows a write path. It currently only loads and watches.
- The window's own reload must not fight the file watcher: a write Bosun made
  should not be re-processed as an external change.
- `CLAUDE.md` §5's "no settings UI for options the configuration file already
  covers" now has a stated exception for host definitions. It still holds for
  *global* tuning — probe timeouts, backoff ladders, the rc port — which stay
  file-only, because those are set once and never touched.
- Deleting a host that is currently `Mounted` has to drain it first; it cannot
  simply vanish from the config while a drive letter is live.

**Rejected alternatives.**

*Keep the window read-only.* What ADR-018 said. Rejected because the premise it
rested on — that the user prefers editing TOML — turned out to be false, and a
premise about the user is the user's to correct.

*Let the window edit a copy and ask the user to merge.* Avoids the two-writer
problem by refusing to solve it. Rejected as busywork.

*Parse the Bitvise profile format completely.* Attractive, and it would recover
port forwarding and SFTP settings too. Rejected for now: undocumented, version
tagged, and the marginal fields are ones Bosun has no model for anyway.


---

## ADR-020 — Bosun detects and repairs its own failures

**Status:** Accepted (2026-10-01).

**Context.** On 2026-09-28 a drain's `mount/unmount` call timed out. `HttpClient`
reports a timeout as `TaskCanceledException`, which every rc catch site treated as
shutdown. The exception killed the supervisor's single channel loop, and the
orchestrator swallowed the loop's death as "normal shutdown". For 2.5 days no host
was probed, drained, or remounted, and nothing said so. On 2026-10-01 the frozen
instance was killed by Windows (AppHang). Its `rclone rcd` child survived, still
holding the rc port with the dead instance's credential. Every later launch got
HTTP 401 on `core/version` and showed the misleading "did not respond within 15s"
on every host (bs-x57, bs-772, bs-o5x).

The individual bugs are fixed in their own issues. This ADR covers the
maintainer's requirement that comes out of them: **failures of this class must be
caught and repaired by Bosun itself, or be fixable by the user in one click with
an accurate explanation. Fixing them must not need a coding agent.**

**Decision.**

1. **The rcd child is bound to Bosun's lifetime by the OS.** It runs inside a
   Win32 Job Object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`. However Bosun dies
   (crash, AppHang kill, Task Manager), rclone dies with it.
2. **A stale rcd is killed automatically.** At startup, if the rc port is held by
   an `rclone.exe` whose command line is exactly Bosun's own
   (`rcd --rc-addr 127.0.0.1:<rclone_rc_port>` and Bosun's `--config` path) and
   which runs as the current user, Bosun kills it, logs that it did, and
   continues. Any drives it held go away with it. That is consistent with I2,
   and startup crash recovery plus a fresh probe (I1) remount them. Bosun never
   kills a port holder that does not match exactly. It reports that holder by
   PID and image path instead.
3. **A watchdog checks the supervisor loop.** It runs independently of the loop it
   watches. It compares the loop's liveness signal against a threshold that is
   comfortably above the longest single bounded rc action (the 60 s
   `mount/mount` timeout) and above the reconciliation cadence. When the loop has
   died or stalled, the watchdog logs at Error and recovers by **restarting the
   Bosun process**. The restart is rate-limited: at most 3 automatic restarts in
   any 1 hour. Past that limit it stops trying and leaves the health banner up,
   so a persistent fault cannot become a restart loop.
4. **Health is a first-class, visible state.** Bosun keeps one application-health
   model: rclone process status with its *real* cause, supervisor loop liveness,
   and the watchdog's last action. When health is not OK, the window shows a
   banner naming the specific cause, and the tray icon or tooltip reflects it.
   Per-host rows are not used to carry an application-wide fault.
5. **Repair actions, one click each:** *Restart rclone* (restart rcd, then
   reconcile every host from scratch, per the E3 brief), *Unmount all &
   re-probe*, and *Restart Bosun*.
6. **Diagnostics bundle.** *Copy diagnostics* writes one zip, under
   `%LOCALAPPDATA%\Bosun\diagnostics\`, and reveals it in Explorer. The zip
   contains recent logs, the health model, every host's state and last
   transition reason, the rclone processes and the rc port's owner, mapped drive
   letters, and the version and build. Secrets are excluded: no rc credential,
   and no key contents. *Amended (bs-1kt, bs-6bc, bs-egu):* the bundle does not need the
   mount supervisor or the rc client. They are read inside the sections that use them, so when
   either cannot be built (the case bs-4js surfaces) those sections read `unavailable: <reason>`
   and the log tail, config, health issues and a new `startup.json` (which holds
   `SupervisorFaultMessage`) are still written. It also holds `rclone-mounts.json` (`mount/listmounts`,
   asked only after `core/version` answers) and `%TEMP%\Bosun\bootstrap-failures.log` if present.
   A failure to write the bundle, and a bundle written with collection problems, are shown through
   the dispatched, window-owned prompt (the bs-3hx pattern), never an inline `MessageBox`.
7. **Application shutdown is bounded.** Exit completes within a fixed bound even
   when the supervisor is wedged. If the loop has died, pending
   `EnqueueAndWait` callers are failed rather than left waiting forever.

**Reasoning.**

*Why the process restart is the recovery and an in-process loop restart is not.*
A stalled loop may still be awaiting inside an action that holds per-host state.
Starting a second loop over the same state would break the one-consumer
guarantee the state machine relies on. A process restart is the "reconcile from
scratch" the E3 brief already requires after any rcd restart: the Job Object
ends the old rcd, the startup crash-recovery path adopts or clears leftover
mounts, and every host is probed before it is mounted (I1). It is the one
recovery whose correctness the existing design already proves.

*Why auto-kill a stale rcd and not ask.* The 2026-10-01 failure left every host
broken until a human found a PID. The exact-command-line, same-user match
identifies a process only Bosun would have launched. Leaving it alive helps no
one, because Bosun cannot authenticate to it.

**Consequences.**

- `RcloneProcessService` gains a Job Object and a port-holder check. The
  health-check fault message names the real cause (401 from another process,
  port held, child exited with code N) (bs-o5x).
- `MountSupervisor` exposes `IsLoopRunning` / `LastLoopActivityUtc` (bs-x57). The
  watchdog depends on these, not on log scraping.
- The single-instance mechanism must allow a self-restart handoff: the new
  instance waits for the old one to exit.

**Rejected alternatives.**

*Restart only the loop in-process.* Rejected for the reason above.

*Notify only, never auto-recover.* Rejected. The incident ran 2.5 days because
nothing acted, and the maintainer asked for the opposite.

*Unlimited automatic restarts.* Rejected. A deterministic startup fault would
turn into a restart storm that hides the banner.

**Amendment (`bs-6to`): how Decisions 3 and 7 were built.** Choices the decision text
left open, recorded so they are not relitigated:

- *Threshold: 3 minutes.* The loop stamps its liveness at every action boundary and when
  every rc call or probe returns, so the longest honest silence is one call: 60 s for
  `mount/mount`. 3 minutes is 3x that and 6x the 30 s reconciliation tick. Dead and
  stalled loops are judged the same way, from the last stamp, so a dead loop is acted on
  after the same threshold, not sooner. Checked every 15 s.
- *Waking from sleep is not a stall.* A gap between two watchdog checks of more than
  4 intervals means the machine slept; the watchdog then measures silence from the wake,
  not from the loop's stamp from before the sleep.
- *The restart limit survives the restart.* History is a small JSON file
  (`%LOCALAPPDATA%\Bosun\watchdog-restarts.json`, beside `hosts.toml`) written before the
  restart is requested. If it cannot be written, the watchdog does not restart and says
  so. A launch that fails still counts against the limit.
- *Handoff.* The old instance launches the new one with `--restarted-by-watchdog <pid>`
  first, then exits. The new instance waits up to 30 s for that pid to exit before it
  tries the single-instance mutex. If the old process outlives the wait nothing is killed:
  the mutex decides, and the overrun is recorded.
- *Bounded exit: 15 s.* A deadline timer is armed when exit begins (a watchdog restart
  arms it too). If shutdown has not finished, it logs at Error and calls
  `Environment.Exit`, with a 5 s backstop thread that terminates the process outright if
  `Exit` itself blocks. 30 s for the handoff wait is twice that.
- *Pending supervisor commands fail, they do not wait.* When the loop exits for any
  reason, every command waiting on it fails with `SupervisorStoppedException`, and later
  commands fail at once. A caller's cancellation token now releases its wait.

**Amendment (`bs-aoz`): how Decision 5 (the repair actions) was built, and two follow-ups from
`bs-6to`.** Choices the decision text left open:

- *"Unmount all & re-probe" is a repair, so it does not park (ADR-015).* A user unmount parks a
  host because it says "keep this down". A repair says "drop everything and start clean". The
  supervisor therefore has a separate command, `IMountSupervisor.RepairAllAsync`, whose drain
  cause (`DrainCause.Repair`) re-enables the host exactly as an automatic drain does and never
  sets `UserParked`. A persistent host remounts only through `Disabled -> Probing -> Ready ->
  Mounting`, so it is probed (shallow, then deep) after the unmount and before the remount
  (Invariant I1). An on-demand host rests in `Ready` and stays unmounted. The repair issues its
  first drain attempt for every mounted host before it re-enables anything. After that, each host
  comes back on its own as soon as rclone confirms *its* unmount. A host whose unmount rclone does
  not confirm stays `Draining` (retrying) rather than reaching `Disabled`, and it does **not** hold
  back the others. (An earlier wording of this paragraph said all drains complete before any host
  is re-enabled. Read literally, one wedged drive would then keep every host down for good; the
  independent bs-aoz tests caught the contradiction, and this is the intended behaviour.) Every
  other enabled host is re-probed at once with its backoff
  reset; one waiting out a failed-mount pacing timer gets a fresh attempt from the first rung.
  The command is refused while suspended (Invariant I8).
- *A repair does not un-park either.* A host the user unmounted earlier is not mounted, so there
  is nothing to drain, and it stays parked (still probed). ADR-015's amendment says only an
  explicit action on that host clears a park; a repair click for another problem is not one.
  The alternative, remounting everything the repair touches, would bring back a drive the user
  took down on purpose to run a backup, with nothing on screen to say why.
- *"Restart rclone" adds one public method, `RcloneProcessService.RestartAsync`, and no second
  launch path.* It stops the supervise loop (so the loop does not read the kill as a crash), kills
  the old rcd and waits for it to exit, then calls the same `AttemptStartAsync` the first start
  and every crash restart use. The port guard, Job Object and fault messages all apply. The
  status goes to `Starting` before the old process is touched, which closes the mounting gate.
  The supervisor learns that rcd's mounts are gone from the `Healthy` transition that
  `StartupOrchestrator` already turns into `OnRcloneRestartedAsync` (reconcile against
  `mount/listmounts`). Nothing assumes it.
- *Restart Bosun reuses `IAppRestarter` with a `RestartKind`.* A manual restart launches the
  replacement with `--restarted-by-user <pid>` (the watchdog's flag is `--restarted-by-watchdog`),
  and drops `--autostart` so the window shows. It is never recorded in the watchdog's restart
  history: the repair command has no reference to that store, so it cannot be. A manual restart
  neither counts against the 3-per-hour limit nor is blocked by it.
- *A watchdog restart starts hidden.* `--restarted-by-watchdog` is a `LaunchContext` of its own
  that does not show the window, whether or not `--autostart` was present. The user's restart
  shows it.
- *The "Bosun restarted itself" notice (`watchdog.restarted`, Degraded).* The old instance writes
  the reason into the restart-history file in the same write that counts the restart
  (`Last: { At, Reason }`); the new instance reads it. The restarts list is unchanged, so an old
  file loads (no reason) and an old build reading a new file still enforces the limit. A record
  older than 10 minutes belongs to an earlier restart and is not used. The notice then says the
  reason was not recorded. It clears on Dismiss or 24 hours after it appears.
- *Where the buttons are and in what order.* The tray has a "Repair" submenu and the banner has
  the same actions, both through `HostActionDispatcher.Repair`. All three repairs are always
  offered while the banner is up. `rclone.*` issues lead with Restart rclone; every other issue
  leads with Restart Bosun; the restart notice leads with Dismiss.
- *Confirmation.* A modal dialog behind `IRepairPrompt`. Unmount all and Restart rclone ask only
  when a drive is mounted. Restart Bosun always asks. *Amended (bs-3hx):* it was first a plain
  `MessageBox` shown inline from the tray menu's `Click`, with no owner. On the real machine it
  flashed and closed without a click and the repair was skipped: `Click` is raised after the menu
  has started to close, an owner-less `MessageBox` takes the thread's active window (the menu's own
  popup), and WPF reports a message box destroyed without an answer as "No". It is now queued on
  the dispatcher at Background priority, owned by the main window (shown and activated first), and
  is a small code-built window so that No and "closed with no answer" are different results. The
  log says "declined" only for No, and "not confirmed" otherwise.
- *A stuck Unmount all does not lock out Restart Bosun.* Each repair has its own in-flight guard,
  and Unmount all gives up after 90 s and says so.

---

## ADR-021 · Restore Claude Code sessions that Windows Terminal lost

**Status:** Accepted (2026-11-01; proposed the same day). Extends Bosun's scope (`CLAUDE.md` §1),
narrows `CLAUDE.md` §4's trimming rule and the interop rule for one new, non-WPF project,
and amends ADR-020 §6 (what the diagnostics bundle may contain).

**Context.** The maintainer runs several Claude Code sessions at once, each in its own
Windows Terminal tab, and drives most of them through Remote Control from the Desktop app,
a phone, and tablets. A reboot, a logoff, a shutdown, or Terminal exiting for any reason
kills every one of those processes.

Terminal's own "Open windows from a previous session" (`firstWindowPreference:
persistedWindowLayout`) does not cover this. It brings back tabs and titles, the working
directory when the shell reports it, and in recent versions the buffer text, but not
processes: each tab comes back as a fresh shell, the conversation is not resumed, and Remote
Control is off. Getting back to work means finding each session in `claude --resume` by
hand, while the phone lists sessions that will never answer.

Claude Code already has the resume half. `claude --resume <session-id>` reloads a
conversation from its transcript, from any directory. What is missing is a record, kept
outside the processes that die, of which sessions were open, where, and whether each one
ended on purpose.

Bosun already runs all day in the user's interactive session, already enumerates processes
(E8), and already launches `wt.exe` (`IExternalLauncher`). That makes it the natural owner.

**Decision.**

1. **Scope.** Bosun restores interactive Claude Code sessions that were running in Windows
   Terminal tabs on this machine and were killed by a reboot, a shutdown, a logoff, or
   Terminal exiting. Out of scope: sessions hosted elsewhere (Desktop app, VS Code), sessions
   running on a remote machine inside an SSH tab (those want tmux on the remote end),
   non-interactive `claude -p` runs, and sessions in an elevated Terminal window (restoring
   them would silently drop the elevation).

2. **Tracking is done by Claude Code hooks, shipped as a Claude Code plugin.** The plugin
   (`bosun-claude-sessions`, in a directory marketplace named `bosun`) registers
   `SessionStart`, `UserPromptSubmit` and `SessionEnd` in its `hooks/hooks.json`, each with an
   explicit `timeout`. Bosun generates it under `%LOCALAPPDATA%\Bosun\claude-plugin\` and owns
   it the way it owns the Terminal fragment. The user installs it once, with
   `claude plugin marketplace add` and `claude plugin install`. Bosun never edits
   `~/.claude/settings.json`, for ADR-006's reason: Claude Code rewrites that file from
   `/config`, and two writers clobber each other.

3. **The hooks run a small helper executable, not `Bosun.exe`.** `bosun-claude-hook.exe`
   (new project `src/Bosun.ClaudeHook/`) reads the hook payload from stdin, writes or updates
   one record, and exits 0 on every path, including its own failures, within an internal
   deadline well under the hook `timeout`. It never writes to stdout: Claude Code adds a
   `SessionStart` or `UserPromptSubmit` hook's stdout to Claude's context. A tracking fault
   must never disturb a Claude Code session. Hooks are registered in exec form (`command` plus
   `args`, with `${CLAUDE_PLUGIN_ROOT}`), so no shell should sit between Claude Code and the
   helper (Assumption 4 below). The helper reads process facts through its own P/Invoke file,
   not `System.Management`, which does not suit NativeAOT. It is not `Bosun.exe` because the
   single-instance guard (`bs-2wa`) would hand the call to the running instance and exit, and
   because `Bosun.exe` is a compressed single-file bundle that decompresses on launch, a poor
   fit for `SessionEnd`'s shared 1.5 second default budget.

4. **One record per Claude Code process, not per session ID.** `/clear` and `/resume` replace
   the session inside a running process, and `SessionStart` fires again with `source` set to
   `clear` or `resume`; the helper overwrites that process's record. A record holds:

   | Field | Source |
   |---|---|
   | Session ID, `cwd`, transcript path, session title (when set) | Hook payload (`session_id`, `cwd`, `transcript_path`, `session_title`) |
   | Claude Code process: image path, PID, creation time | The helper's parent process |
   | Tab root process: image path, PID, creation time | The ancestor whose parent is the `WindowsTerminal.exe` ancestor |
   | Terminal process: PID, creation time | Nearest `WindowsTerminal.exe` ancestor |
   | Tab identity and profile | `WT_SESSION`, `WT_PROFILE_ID` from the environment |
   | Logon session: ID and logon time | The helper's own token |
   | `prompted` | See below |
   | Last `SessionEnd` reason, and whether its Terminal process was alive at that moment | `SessionEnd` payload (`reason`) and a liveness check |
   | Helper version | Build constant |

   `prompted` is set by the first `UserPromptSubmit`. On `SessionStart` it is `true` when
   `source` is `resume` or `fork` (the transcript already has a conversation), `false` for
   `startup` and `clear`, and unchanged for `compact`. `UserPromptSubmit` fires on every
   prompt, so the helper returns at once when `prompted` is already set.

   The helper writes no record, and removes any it wrote, when: the environment has no
   `WT_SESSION` or there is no `WindowsTerminal.exe` ancestor (a process launched from a tab,
   such as VS Code, inherits `WT_SESSION`); the Claude Code process's command line carries
   `-p` or `--print`; or its token is elevated. Checking these at `SessionStart` rather than
   in Bosun means a `-p` run killed by a reboot was never tracked, and an elevated process's
   command line, which a medium-integrity Bosun cannot read, never needs to be read.

   Records live in `%LOCALAPPDATA%\Bosun\claude-sessions\`, one JSON file per process,
   written atomically (temp file, then rename). Bosun moves a record into `dispatched\` when it
   restores it and deletes records; the helper never touches `dispatched\`.

5. **Classification.** Bosun evaluates records on a short timer while running, and once at
   startup. Process identity is PID plus creation time, never PID alone, with creation times
   compared at 1 ms resolution: the helper and Bosun may read them through different APIs
   with different precision, and an exact comparison would read a live session as dead and
   open a duplicate.

   | # | Situation | How Bosun knows | Outcome |
   |---|---|---|---|
   | 1 | The session is still running | PID and creation time match a live process | Keep watching |
   | 2 | The user ended it: `/exit`, Ctrl+D, `/logout` | `SessionEnd` reason `prompt_input_exit` or `logout` | Delete the record |
   | 3 | It was opened and never used | `prompted` is false | Delete the record |
   | 4 | Its transcript is gone (Claude Code's `cleanupPeriodDays` sweep, or deleted by hand) | The transcript path no longer exists | Delete the record |
   | 5 | Reboot, shutdown, or logoff | The record's logon session (ID and logon time) is not the current one | Restore at startup |
   | 6 | The user closed that tab or window | Claude Code process gone, and its `WindowsTerminal.exe` outlived it by at least a grace period; or `SessionEnd` recorded its Terminal alive | Delete the record |
   | 7 | Terminal exited: a crash, an update, or closing the whole app | Claude Code process gone and its `WindowsTerminal.exe` gone too, within the grace period | Pending: restore when Terminal is next launched |

   Rows are evaluated in order and the first match wins. Row 5 must precede rows 6 and 7,
   because after a logoff or reboot the Terminal process is gone too. The logon session, not
   the boot time, decides row 5: a logoff does not change the boot time, and neither does
   Windows' default "Shut down" with Fast Startup on. `SessionEnd` reason `other` is ambiguous
   and never counts as deliberate on its own. `clear` and `resume` are not ends; the
   `SessionStart` that follows updates the record. While Windows is ending the session
   (`SystemEvents.SessionEnding`), Bosun stops classifying altogether, so a tab that dies
   moments before its Terminal during logoff is not mistaken for row 6.

   Two accepted limitations. Closing the last tab, or the only window, also ends Terminal, so
   it matches row 7 and is restored at the next Terminal launch; Terminal's own session restore
   behaves the same way when the last window is closed. And a tab closed while Bosun is not
   running, followed by a logoff or reboot before Bosun runs again, is restored unless its
   `SessionEnd` recorded the Terminal alive (Assumption 1 says whether it can).

6. **After a reboot, shutdown, or logoff, restore at startup without asking.** The maintainer
   chose automatic reopening over a tray prompt. Restore is a `StartupOrchestrator` step right
   after the Terminal fragment write, ungated in the same way: it never waits for or depends on
   the rclone chain, catches every exception, and reports failure into the health model
   (ADR-012 Decision 1, ADR-020 §4). A record whose session ID is already held by a live
   record (for example, the user resumed it by hand) is deleted, not restored. The rest are
   moved to `dispatched\`, and one `wt.exe -w 0` invocation opens a tab per session, chained
   with `;`:

   ```
   new-tab -p <WT_PROFILE_ID> -d <cwd> --title <title> <tab root> <keep-open> <claude> --resume <session-id>
   ```

   Claude Code runs inside the tab root it was started from, so `/exit` leaves a prompt in that
   folder as before: `pwsh.exe` and `powershell.exe` get `-NoExit -Command`, `cmd.exe` gets
   `/k`. When the tab root is the Claude Code process itself (a profile that runs `claude`
   directly), there is no wrapper. Any other tab root gets no wrapper either, and the tab
   closes when Claude Code exits. `<claude>` is `claude` resolved by the shell; with no
   wrapper, it is the recorded Claude Code image path, because `wt.exe` cannot launch the npm
   `claude.cmd` shim by name. The title is the session title when set, else the folder name,
   and is only the tab's first label: Claude Code may set its own.

   A launch is confirmed, not assumed: `wt.exe` is an app-execution alias, so a started process
   proves nothing about the tab. A dispatched record is confirmed when a new record with the
   same session ID appears, and is then deleted. One not confirmed within a timeout is reported
   by title in the window and in the toast ("Could not resume 'auth refactor'"), and stays in
   `dispatched\` to be retried at the next trigger (the next Bosun start or Terminal launch). After
   three unconfirmed attempts Bosun stops retrying it: the record stays listed in the window as
   "Could not resume", with a Dismiss action that deletes it, and no further toast is raised for
   it. Without the cap, a session whose folder was deleted or moved would raise the same failure
   at every login and Terminal launch indefinitely. The toast reports confirmed restores
   ("Restored 4 Claude Code sessions after restart"), per ADR-012's rule that state changes are
   communicated causally.

7. **After Terminal exits, restore when Terminal is next launched.** Bosun watches for a new
   `WindowsTerminal.exe` and dispatches pending records with `-w 0`, which targets the most
   recently used window; that should be the one just opened (Assumption 7). At the process
   level, closing the whole Terminal app on purpose and Terminal crashing look the same.
   Reopening into the next launch is right for both; reopening immediately would fight a
   deliberate close. Pending records are files, so they survive a Bosun restart.

8. **Remote Control comes back through Claude Code, not Bosun.** With `remoteControlAtStartup`
   set to `true` (in `/config`: "Enable Remote Control for all sessions"), every interactive
   start connects, a resume included (Assumption 5). Bosun passes no Remote Control flags.

9. **The window shows what Bosun is tracking.** The main window lists tracked, pending and
   unconfirmed sessions. Tracking that reports nothing is a visible state, not silence: a live,
   Terminal-hosted Claude Code process in Bosun's snapshot with no record for more than a
   minute raises "Claude session tracking is not reporting. Is the Bosun plugin installed in
   Claude Code? Sessions started before it was installed are not tracked." A record whose
   helper version differs from Bosun's raises "The Bosun plugin in Claude Code is out of
   date" with the update commands.

10. **The helper ships inside `Bosun.exe`.** It is embedded as a resource, and
    `IClaudePluginWriter` writes it into the plugin directory under a versioned file name, so a
    helper that a hook is running at that moment is never overwritten in place. The release
    stays one artifact; `release.yml` attaches nothing new.

11. **One configuration switch.** `[claude_sessions] enabled`, `false` when absent. Turning it
    on leads to a plugin the user installs into their own Claude Code configuration, which is
    not something to do unasked. With it `false`, Bosun neither classifies nor restores, and
    leaves existing records alone.

**Reasoning.**

*Why hooks and not process inspection alone.* Bosun can enumerate Claude Code processes and
read their command lines, but a command line names the session only when the session was
started with `--resume <id>`. Mapping a fresh `claude` process to its transcript means
guessing from `cwd` and file times, and `/clear` changes the right answer mid-process. The
hook payload carries `session_id` and `cwd` directly, at the moment they change.

*Why per process.* What a reboot destroys is a tab running a process. The session ID inside
it is an attribute that changes.

*Why the logon session decides row 5.* It is the boundary that actually kills the processes.
Boot time misses logoff and Fast Startup shutdowns, which are the commonest ways this user's
sessions die besides Restart.

*Why "is its Terminal still alive" decides deliberate closes, with a grace period.* What
`SessionEnd` reports when a tab is closed with its X is undocumented, and may be nothing. The
process-level test does not depend on it: a tab that went away while Terminal kept running was
closed by choice. The grace period exists because Terminal ending is not instantaneous: its
tabs' processes can die a moment before it does, and a single timer tick in that gap would
otherwise delete records that row 7 should keep. If testing shows a reliable `SessionEnd`
signal for a tab close, it strengthens row 6; it does not replace the process-level test.

*Why `prompted`, and why a resume counts as prompted.* The real cost of automatic reopening is
bringing back sessions nobody wanted, and a session opened and never used is the commonest of
those. A resumed session already holds a conversation; treating it as unused would drop every
restored tab at the next reboot unless the user had typed into it in between.

*Why confirmation by record.* The only reliable evidence that a tab resumed is the resumed
process announcing itself through the same hook. Anything earlier, such as `wt.exe` starting,
can succeed while the tab fails (the folder is gone, `claude` is not on the profile's PATH).

*Why the helper filters `-p` and elevation, not Bosun.* At `SessionStart` the helper's parent
is alive and readable, and the helper's own token answers the elevation question directly.
Bosun sees a process only when its timer happens to run, and cannot read an elevated
process's command line at all.

*Why one window.* A record knows its tab (`WT_SESSION`) but not which Terminal window the tab
was in. Restoring into one window is predictable; reconstructing window layout is Terminal's
own job, and the user turns that off (Consequences).

*Why automatic restore does not contradict ADR-012 and ADR-018 on login-time focus.* Those
rules keep Bosun's own windows and dialogs from appearing at login, because a notification
that appears every login trains the user to dismiss it. For this user a restore will follow
most reboots. It is still not a notification: it opens the Terminal tabs the user would
otherwise reopen by hand, and only when there are sessions to reopen.

**Consequences.**

- `CLAUDE.md` §1 gains a fifth responsibility, and §4 gains the helper. §4's "never set
  `PublishTrimmed`" rule exists because trimming breaks WPF. The helper is a console app,
  published with `PublishAot` (which trims) so it starts in milliseconds and stays small. The
  rule now covers every WPF project, `Bosun.ClaudeHook` is the one exception, and it must never
  reference WPF or the `Bosun` project. The same wording goes into `Directory.Build.props`,
  `release.yml`, and the `.claude/agents/` definitions that repeat the rule, because subagents
  do not inherit `CLAUDE.md`.
- NativeAOT is a new build dependency: it needs the MSVC build tools ("Desktop development with
  C++") on every machine that builds the helper. Because Decision 10 embeds the helper in
  `Bosun.exe`, that is every machine that builds `Bosun` at all, including every `dotnet build`
  and `dotnet test` (agent worktrees and CI's test job), not only release publishing. The helper
  build must be incremental (declared inputs and outputs) so an unchanged helper adds no time to
  a build. The maintainer accepted this requirement (2026-11-01) over embedding the helper only
  at publish, which would leave development builds unable to run the feature. `scripts/dev-setup.ps1` installs or checks for
  them, and CI's Windows runner already has them. If they prove a problem, a self-contained,
  untrimmed console helper is the fallback, provided its measured cold start is well under the
  `SessionEnd` budget; record the switch as an amendment.
- The interop rule ("Win32 interop stays behind `ISessionMonitor`") gains one exception, the
  helper's own interop file. On Bosun's side, the Claude Code process snapshot (PID, creation
  time, image, logon session) sits behind `ISessionMonitor`'s existing interop layer. The rule
  already has one unrecorded exception inside `Bosun`: ADR-020's Job Object
  (`Rclone/Process/Interop/Win32ProcessJob.cs`) is P/Invoke outside `ISessionMonitor`. This ADR
  records that exception rather than adding to it: nothing else in `Bosun` gains P/Invoke.
  Reading another process's logon session needs new token calls (`OpenProcessToken`,
  `GetTokenInformation`), and they go in `SessionMonitor/Interop/`.
- Nothing subscribes to `SessionEnding` today: `ISystemEventRegistrar` exposes only
  `PowerModeChanged` and `SessionSwitch`. Decision 5's freeze needs a new registrar method.
- `CLAUDE.md` §6's worktree-safety rules extend to this feature: no default-suite test may
  launch `wt.exe`, read or write `%LOCALAPPDATA%\Bosun\claude-sessions\`, or write
  `%LOCALAPPDATA%\Bosun\claude-plugin\`. The classifier is sent to `test-author`, like E5.
- `HostConfigTomlWriter` must round-trip `[claude_sessions]`. Under ADR-019 Bosun is the sole
  writer of `hosts.toml`, so a host edit in the window that dropped the section would silently
  turn the feature off.
- The user turns off Terminal's "Open windows from a previous session". Otherwise Terminal
  reopens the same tabs as plain shells beside Bosun's restored ones. `docs/OPERATIONS.md`
  says so.
- A watchdog restart (ADR-020 §3) cannot duplicate tabs: a live process's record is never
  restored, a session ID already live is never restored, and a dispatched record is not
  dispatched again until its confirmation times out.
- **ADR-020 §6 amendment.** Diagnostics bundles may be attached to public issues, and records
  hold folder paths, transcript paths and session titles. The bundle includes each record's
  classification state, timestamps, end reason, process facts and helper version, and omits
  `cwd`, the transcript path and the title.
- The default test suite never touches real records, the real plugin directory, or `wt.exe`.
  Classification is a pure function over records, a process snapshot, the current logon
  session, and a clock, and the `wt.exe` argument list is built by a pure function too.

**Assumptions the E13 spike must confirm.** These are the parts of the design that rest on
undocumented behaviour or on inference. The spike records what it finds as an amendment here,
and any assumption that fails reopens the decision that rests on it.

1. What `SessionEnd` reports, if anything, for each of: closing a tab with its X, closing a
   window with its X, killing `WindowsTerminal.exe`, logoff, Restart, and Shut down with Fast
   Startup on, and whether the helper can still check its Terminal's liveness at that moment.
2. `WT_SESSION` and `WT_PROFILE_ID` reach the hook process's environment.
3. The parent chain from the Claude Code process up to `WindowsTerminal.exe`: that a tab's root
   process is a direct child of `WindowsTerminal.exe`, including in an elevated window and with
   the npm `claude.cmd` shim in between. Windows parent PIDs can be stale once a parent exits,
   so an ancestor counts only if it was created before its child.
4. Under exec form, the helper's parent is the Claude Code process itself, for both the native
   install (`claude.exe`) and an npm install (`node.exe`).
5. `remoteControlAtStartup: true` reconnects a session started with `claude --resume <id>`.
6. Whether a directory-marketplace plugin loads in place or from a copy in Claude Code's plugin
   cache, and exactly what the user runs after Bosun writes a new helper version.
7. `wt.exe -w 0 new-tab ...`, issued just after a new `WindowsTerminal.exe` appears, lands in
   that new window rather than racing its startup.
8. At logoff and shutdown, Bosun sees `SessionEnding` before Terminal starts tearing tabs down.
9. The helper's measured cold start, which must sit well inside the `SessionEnd` budget even at
   logoff, when the machine is busy.
10. `claude --resume <id>` keeps the same `session_id` in its `SessionStart` payload. Decision 6
    confirms a restore by matching that ID; if a resume gets a new ID, confirmation never
    succeeds and every restored session is reported as failed.
11. `SessionEnd` hooks share a 1.5 second default budget. Decision 3's case for a separate
    NativeAOT helper rests on this figure.

Not every item is settled by the same instrument. The hook-logging spike settles 1 to 6, 10
and 11; item 6 needs a real `claude plugin marketplace add` and `install`, uninstalled
afterwards. Items 7 and 8 are about Terminal and Bosun, not hooks, and need their own small
probes: a script that runs `wt.exe` just after Terminal starts, and a test program that logs
`SessionEnding` against tab teardown. Item 9 can only be measured once the helper exists, so it
belongs to the helper's own issue.

**Open decisions (filed as `needs-decision`; not part of this ADR):**

- **Pinning.** Sessions reopened every time Bosun starts, even after a deliberate close,
  keyed on session name rather than ID: a name set with `--name` or `/rename` survives
  `/clear`, and `claude --resume <name>` resumes an exact match directly (Claude Code's
  sessions documentation). Same shape as the persistent mount tier. Whether the maintainer
  wants it is undecided.
- **Recently closed.** Whether deliberately closed sessions stay reachable from the tray or
  window for a while, rather than being forgotten at once.

**Rejected alternatives.**

*Rely on Terminal's own session restore.* It restores layout, not processes.

*Write hooks into `~/.claude/settings.json`.* ADR-006's two-writers problem, in a file Claude
Code rewrites whenever the user changes a setting.

*Ask before restoring.* Simpler to get right, and rejected by the maintainer in favour of
automatic reopening.

*Restore only sessions the user registered.* Fails in exactly the case that motivated this
ADR: the session nobody thought to register before the reboot. Registration survives as the
pinning question above, which is a different job ("always open"), not crash recovery.

*Detect reboots by boot time.* Misses logoff, and misses Windows' default shutdown with Fast
Startup on, both of which kill every session.

*Run `Bosun.exe` as the hook command.* The single-instance guard and the startup cost, as in
Decision 3.

*Reopen immediately when Terminal exits.* Fights a deliberate close of the whole app.

*Delete a record once `wt.exe` starts.* `wt.exe` starting does not mean the tab resumed; a
failed resume would lose the session without a word.

---

## ADR-022 — An on-demand drive the user mounted comes back after a sleep

**Status:** Accepted (2026-11-01). The maintainer made each choice below in conversation on
2026-11-01 (`bs-u4a` item 6, `bs-hrg`). Mirrors ADR-015.

**Context.** Invariant I8 drains every mounted host before the machine sleeps. On resume a
persistent host comes back on its own (§4 rule 6). An on-demand host does not: it rests in
`Ready`, so a drive the user mounted from the tray twenty minutes before closing the lid is
gone after wake, and the user has to click Mount again. ADR-015 settled the reverse case, a
user's unmount that Bosun must not undo. Nothing recorded whether a suspend may undo a
user's mount. Independent testing of E6 found the gap (`bs-u4a` item 6).

**Decision.**

1. When suspend arrives, the supervisor records, in memory, each on-demand host that is
   `Mounted` or `Mounting`. It then drains them under I8 as before.
2. After resume, such a host takes the ordinary path back (`Disabled → Probing`). If that
   **first** post-resume probe reaches `Ready`, the supervisor mounts it exactly as a user
   click would, through `Ready → Mounting` (Invariant I1 is unchanged).
3. If that first probe does not reach `Ready`, the record is dropped. The host shows
   `Unreachable` and stays unmounted; a later click mounts it as usual. Bosun never mounts it
   later on its own, on whatever network the machine has moved to by then.
4. The remount is a new mount: the idle-unmount timer starts fresh.
5. The record is cleared by: the remount itself, a user unmount, the host being removed from
   the config or its mode changed, or a restart of Bosun. It is never persisted.
6. Scope is suspend only. An on-demand drive drained because its host failed (probe failure,
   deep probe after a network change) does not come back on its own; rule 6 still governs it.

**Reasoning.** The user's click expressed intent that the suspend did not revoke; the suspend
drain exists for safety (I8), not because the user changed their mind. Requiring the first
post-resume probe to pass keeps I1 intact, and dropping the intent when it fails keeps the
remount tied to the moment of wake, which is when the user expects it. A mount that appears
hours later on another network would be a surprise of the kind ADR-015 exists to prevent.

**Consequences.**

- `docs/ARCHITECTURE.md` §4 gains a rule stating this, next to rule 9.
- One more piece of per-host, in-memory state in the supervisor, set only by suspend.
- ADR-015's note that `Disabled` conflates several meanings applies here too: a host
  suspend-drained with this record is a fourth reading of `Disabled`.

**Rejected alternatives.**

*Keep the intent until the host is next reachable.* On-demand hosts that are `Unreachable`
are not polled (ADR-014), so this would need new polling after wake, and it produces the
late surprise mount described above.

*Carry the idle time used before the sleep.* More state for little gain; a drive that was
nearly idle-expired would unmount soon after wake, which reads as a fault.

*Leave it as it is.* Simple and safe, and declined by the maintainer: remounting by hand after
every wake is the friction the on-demand tier was not meant to add.

### Amendment (2026-11-01, bs-hrg): a local obstacle keeps the record

**Context.** Decision 3 said the record is dropped "if that first probe does not reach `Ready`",
and the implementation went further: it spent the record at the first probe whatever happened
next. Independent testing found two cases where that loses the user's drive for a reason that
has nothing to do with the host. If `rclone rcd` is restarting across the sleep, mounting is
unavailable (`MountingAvailability`) when the probe reaches `Ready`, the remount is refused, and
nothing ever retries it. If rcd has not answered `core/version` at the resume re-derivation
(ADR-017's gate), the remount went ahead anyway, failed against the dead rcd, and was never
retried. The maintainer decided on 2026-11-01 that both keep the record.

**Amendment.**

1. If the first post-resume probe reaches `Ready` but the remount cannot go ahead because
   mounting is unavailable, the record is **kept** and the host rests in `Ready`.
2. The same applies if `rclone rcd` failed ADR-017's `core/version` gate at resume. The remount
   is not attempted against an rcd that has just failed to answer.
3. When mounting becomes available again, or rcd answers again, the host is probed once more.
   If that probe leaves it `Ready`, it is mounted as a click would be (deep probe first, I1).
   If not, the record is dropped.
4. Unchanged: a failed first probe for a host-side reason drops the record (decision 3). The
   clear conditions of decision 5 still apply, and a user unmount or a mode change clears a
   waiting record exactly as it clears one that is not yet probed. Nothing mounts while the
   system is suspended (rule 5, `bs-bv0`); a suspension with a record waiting keeps the record
   and resume resolves it.

**How it is detected.** The supervisor keeps no new state machine edge. The
record stays on the host's runtime; one flag, `rcdAnswering`, remembers whether the last
`core/version` call succeeded. The gate "has cleared" when any of these happens:

- `SetMountingAvailabilityAsync` goes from unavailable to available. This is the existing
  "mounting became available" hook, which until now swept persistent hosts only. The startup
  orchestrator calls it when WinFsp or rcd health changes, so it covers an rcd that restarted.
- `OnRcloneRestartedAsync` runs. The supervisor does not take the report on trust: if the flag
  says rcd failed the gate, it asks `core/version` again first.
- The 30-second reconciliation tick finds `core/version` answering, but only while a record is
  waiting and the flag says rcd failed the gate. This third route exists because rcd can stall
  across a resume without exiting. Nothing then calls `OnRcloneRestartedAsync` or changes the
  availability, and without the tick the record would wait indefinitely.
- A network change or a resume re-derivation that finds `core/version` answering.

In each case the sweep does nothing while any gate (suspended, unavailable, `rcdAnswering`) is
still closed, so a host is never probed for a decision that cannot be taken. The re-probe is the
ordinary in-place probe of rule 10; a `Ready` or `Unreachable` host is not sent through `Probing`.
The record is cleared as the host enters `Mounting`. A remount whose own deep probe or
`mount/mount` fails while mounting is available is therefore not retried, as before, and so is
a deferred one.

**Reading I chose, for the maintainer to confirm.** The amendment says that when mounting
becomes available "the host is probed once more; if Ready, mount; otherwise drop the record".
I read "a remount that fails while mounting is available" in the brief as outside it, because
the amendment concerns obstacles that stop the attempt from being made. If the maintainer wants
a failed deferred attempt retried, that is a further amendment.

**Consequences.**

- A record can now outlive the resume. It is held in memory only, and a Bosun restart still
  drops it. It has no timeout: if mounting never becomes available, the record waits until the
  user unmounts or remounts the host, changes its mode, or restarts Bosun. When it finally
  resolves it is guarded by a fresh probe, so it cannot mount a host that is not reachable
  now, but it can mount on a network the user has since moved to. That follows from the rule as decided; an expiry would be a further amendment.
- `ARCHITECTURE.md` §4 rule 9a is updated.
- No new state, transition or rc call. Only `IMountSupervisor` still calls `mount/mount`.

### Second amendment (2026-11-01, bs-ane): a waiting record expires ten minutes after the resume

**Context.** The first amendment above lets a record outlive the resume, and its Consequences
say it has no timeout and that "an expiry would be a further amendment". This is that
amendment. Without it a record kept because mounting stayed unavailable could mount a drive
hours later, on a network the user had since moved to, which is the late surprise mount that
decision 3 exists to prevent.

**Amendment.** The maintainer decided on 2026-11-01 that a record which is **waiting** (kept
because mounting was unavailable, or because `rclone rcd` failed ADR-017's `core/version`
gate) expires **10 minutes after the resume**. When it expires:

- the record is dropped, with an Info log line;
- the host is not remounted automatically, and rests in whatever state it was in (normally
  `Ready`, as an on-demand host does);
- a user click mounts it exactly as before.

Decision text, the first amendment's rules and the other clear conditions are unchanged. A
record that is not waiting (the first probe resolved it) never meets the limit.

**Details I chose, for the maintainer to confirm.**

- **A second suspend and resume restarts the ten minutes.** The record survives the sleep under
  the first amendment, and each wake is a new "waking up", so the limit counts from the latest
  resume. Time spent suspended does not count: the deadline is cleared at suspend and set again
  at resume, and nothing expires while the system is asleep. A record that was already past its
  limit when the next suspend arrived is dropped there, not revived by the wake.
- **The limit is a named constant** (`MountSupervisor.RemountRecordWaitLimit`), not a config
  option. The boundary is inclusive: at exactly ten minutes the record is expired.
- **No timer.** The deadline is stored with the record and checked where a waiting record would
  otherwise be resolved: the deferred sweep (before its gates are tested, so an expired record is
  never probed for), the resume and first-probe resolution, suspend, and the 30-second
  reconciliation tick, which drops it so it does not linger in memory and stops the tick's
  `core/version` polling on its behalf. Dropping can therefore be up to one tick late with no
  event to prompt it, but it can never mount in that gap: every route that mounts checks first.

**Consequences.**

- The first amendment's "it has no timeout" no longer holds; the wait ends at the limit, or
  sooner by any clear condition.
- One more in-memory field per host (the deadline). Not persisted, not in `HostMountSnapshot`.
- `ARCHITECTURE.md` §4 rule 9a is updated. No new state, transition or rc call.

**Note (2026-11-02, `bs-brb`):** the Context's "closing the lid" is the laptop-era phrasing
ADR-001's amendment retracted. On the desktop this runs on, the event is the machine going to
sleep; the Decision is unchanged.

---

## ADR-023 — A suspend preempts routine checks

**Status:** Accepted (2026-11-02). The maintainer decided it in conversation on 2026-11-02
(`bs-4in`; `bs-7d7` and `bs-4ca` share its mechanism and are decided with it). Extends Invariant
I8 and ADR-017. It adds no state, no transition and no rc call.

**Context.** Windows gives the suspend handler about two seconds; `global.suspend_unmount_timeout_seconds`
defaults to 3 (`bs-ajh`). The supervisor's channel runs one action at a time, and `SuspendAsync` was an
ordinary command on it, so it waited for whatever was running. Independent review of `bs-obf` found the
worst case, and the troubleshooter's analysis of 2026-11-02 (at `9a93b77`) measured it:

- One hung recurring check is enough. A mounted on-demand host's shallow tick is a probe plus a
  `vfs/stats` sample, up to 5 s plus 5 s. With the host's other ticks queued behind it, about 30 s;
  each further on-demand host with an idle clock adds 20 s.
- The I8 gate was the `suspended` flag, which the suspend action sets only when it is dequeued. Work
  queued **ahead** of the suspend could still send `mount/mount` in the meantime: a user's click, the
  auto-mount of a persistent host whose idle probe was queued, a resume's sweep. A drive created in the
  seconds before sleep survives the sleep, which is what I8 exists to prevent.
- Rule 5 says the budget counts "from the moment suspend arrives". The code counted from when the suspend
  was dequeued (`bs-7d7`), so a suspend that had waited its whole budget in the channel still allowed a
  further whole budget.
- `HostProbe` reports a cancelled probe as the outcome `Cancelled`, not as an exception, and the mounted
  handlers count every outcome other than `Success` as a failure. Any fix that cancels a probe by linking
  a token therefore turns every suspend into an `Automatic` drain (with a toast) for a host one failure
  short of its threshold. The same flaw already existed at shutdown (`bs-4ca`).

**Decision.**

1. **The gate closes on the caller's thread.** `SuspendAsync` increments a `pendingSuspends` counter
   before it queues anything; the suspend action decrements it as its first statement. Every place that
   used `suspended` as an I8 gate reads `suspended || SuspendPending` (`MountingSuspended`) instead:
   `TryBeginMountAsync`, `RequestMountAsync`, `RepairAllAsync`, the mount-retry callback, the re-enable
   after a drain, and the remount-record resolution and sweep. `EnableHostAsync` returns before
   `SetState(Probing)` while a suspend is pending; the host stays `Disabled` and resume enables it. It is
   a counter, not a flag, because suspend, resume, suspend can be pending together.
2. **A suspend preempts the recurring tick that is running.** Five timer callbacks run under
   `RunTickAsync`, which publishes a per-tick `CancellationTokenSource`: the idle probe, the mounted
   shallow probe (including its VFS sample), the mounted deep probe, the idle-unmount fire and the
   reconciliation tick. The prologue of `SuspendAsync` calls `CancelAsync()` on the running one. It is
   `CancelAsync`, never `Cancel`: `Cancel` runs the registered callbacks (the linked sources that release
   a hung probe or rc call) inline, on the calling thread, which in production is `SystemEvents`' single
   notification thread. The prologue never throws and never blocks; a fault in it is logged. The order is
   fixed: increment, then cancel, then queue. A tick that starts while a suspend is pending sees the
   counter, cancels itself and skips its I/O, so ticks queued ahead of the suspend do no work either.
3. **"Preempted" is decided by the token, never by the outcome or the exception type.** When a tick's
   preemption token is cancelled, whatever its I/O returned is discarded, `Success` included. No counter
   (`ConsecutiveMountedFailures`, `ConsecutiveDeepProbeFailures`, `Backoff`), state, VFS bookkeeping,
   `rcdAnswering`, warning or repeating-fault entry changes. The tick re-arms its own timer and logs one
   Information line. The probe wrappers keep two tokens apart: the supervisor's own token (shutdown), which
   alone decides their catch filter, and the preemption token, which is only linked into the token the probe
   is handed. Using one token for both would let a cancellation escape and strand a `Mounted` host with no
   probe timer (ADR-011, I2).
4. **Not preempted:** drains, `mount/mount` once sent, commands (resume, network change, user actions,
   repair, config change, `OnRcloneRestartedAsync`), and the probe that `EnableHostAsync` is already running.
   There is no `Probing -> Disabled` edge and none is added. *(Amended 2026-11-06, `bs-5ga.1`: the call a
   command has in flight is still never cancelled, but a command that works through several hosts now
   declines to start the next one once a suspend is pending. See the amendment below.)*
5. **D1: a mount that has passed its deep probe is abandoned if a suspend is pending.** After the deep probe
   passes and before `mount/mount` is sent, a pending suspend abandons the mount on the existing
   `Mounting -> Draining` edge with `DrainCause.Suspend`. It is not a mount failure: no counter, no retry
   backoff, no failure row. An on-demand host keeps or gets the ADR-022 remount record, because by the time
   the suspend action runs this host is no longer `Mounting` and the action cannot set it. A deep-probe
   *failure* still wins and is still a mount failure. The VFS baseline sample that follows a mount is
   skipped while a suspend is pending.
6. **D5: idle-probe ticks skip while `suspended`**, as the mount-retry callback does. The timer is still
   re-armed. The forced probes of resume, network change and "retry now" are commands and still probe.
7. **`bs-7d7`: the budget counts from arrival.** The arrival time is read from the injected `TimeProvider` on
   the caller's thread and passed into the suspend action; suspend drains start their clock from it, and a
   host already draining gets the deadline `arrival + budget`.
8. **`bs-4ca`: a probe that returns while the supervisor's own token is cancelled is shutdown**, whatever it
   returned. The probe wrappers throw `OperationCanceledException` in that case, so a cancelled probe is not
   counted as a failure and a host one failure short of its threshold is not drained as `Automatic` while
   Bosun exits. This is the same check as decision 3, applied to the other token.

**What is not preempted, and why.** A drain is what a suspend wants; cancelling its `mount/unmount` would
abandon the one thing the budget is for. `mount/mount` once sent has an unknown outcome if cancelled, and a
half-made mount is the worse state to hand to a suspend. A command changes state the suspend is about to
reason about; cancelling it leaves the change half-applied, and which commands are safe to cancel is a
separate question (D3, below). The enable probe runs in `Probing`, which has no edge to `Disabled`.
What remains is bounded by those actions and not by the number of hosts' routine checks. The adapter's race
against the budget (`WaitForSuspendDrainAsync`) stays as the backstop for it.

**Rejected alternatives.**

*A priority lane for suspend.* Let the suspend jump the channel. It breaks suspend, resume, suspend: the
second suspend would run before the resume it follows, and the resume would then run last, leaving the
machine "resumed" as it goes to sleep. It also reorders every command queued ahead of the suspend (a
user's unmount, for one), so each would run against state the suspend had already changed. The
troubleshooter ruled it out on those two grounds (`bs-4in` notes). The counter gives the same early gate
without reordering anything.

*Make the suspend's drain not wait behind the channel.* The drain is channel work; running it beside the
channel gives up the single-writer discipline that §5 depends on.

**Not taken, and deferred.**

*D2: a `Probing -> Disabled` edge, so an enable probe in flight could be abandoned.* Not taken. It would add a
transition and change rule 10 (`Probing` is one-shot) for a probe that is bounded by the probe timeout. The
early return in `EnableHostAsync` closes the part of the hole that matters, which is a probe *starting*.

*D3: preempting commands.* Deferred to its own issue. It needs a decision per command. *(2026-11-06,
`bs-5ga.1`: still deferred, and now rejected for the multi-host case in favour of the between-hosts check in
the amendment below. Cancelling a command in flight remains not done.)*

**Consequences.**

- `docs/ARCHITECTURE.md` §3, §4 rules 5 and 10 and §5 are updated. The `SuspendAsync` prologue is the one
  piece of supervisor code that runs off the channel; it increments a counter and cancels a token and
  does nothing else.
- Existing tests whose meaning changed: the ones that reached `Ready` through an idle tick *while suspended*
  now use "retry now" (D5), and the ones that suspend during a held deep probe now see an abandoned mount
  (D1). Each was reworded against its stated intent, not edited to pass; the mount-in-flight interleaving
  they used to cover is kept as its own test with `mount/mount` held open.
- A `mount/mount` request queued ahead of a suspend is now refused with "the system is suspended", the same
  refusal as during the suspend (`bs-oly`). It used to mount and be drained a moment later.
- *(Was a residual; closed 2026-11-07, `bs-5ga.3` -- see the amendment at the end of this ADR.)* A suspend
  that arrives during the VFS baseline sample that follows a mount used to wait for it (at most one probe
  timeout), because the sample is the tail of a command and not a tick. It now cancels the sample.
- Residual (`bs-5ga` item 1, accepted 2026-11-07 by the maintainer): a host that reaches `Ready` inside an
  idle-probe tick starts its mount in that tick, and the mount's pre-mount deep probe is not handed the
  tick's preemption token (pinned by
  `The_deep_probe_of_a_mount_started_inside_a_tick_is_not_cancelled_and_the_mount_is_abandoned_cleanly`).
  A suspend that arrives during it waits for it, at most one probe timeout (5 s by default, past the
  roughly 2 s Windows allows), so other hosts' unmounts can land after the machine has slept; resume
  reconciles (ADR-017). The host being probed never mounts: D1 abandons the mount once the probe returns.
  This is the same wait the decision already accepts for a user's "retry now" and an enable probe. Making
  that probe preemptible was considered and not taken: it changes the I1 mount-entry path every mount
  goes through, and a mistake there books a suspend as a mount failure, for a race that needs a host
  coming back, its probe hanging and the lid closing during that probe.
- The counter is not decremented if the supervisor loop is already dead. Nothing can mount without the loop,
  so the closed gate costs nothing.

### Amendment (2026-11-06, bs-5ga.1): multi-host commands stop between hosts while a suspend is pending

**Amendment.** Decision 4 leaves commands unpreempted, and the commands that reach every host were the
largest piece of work a suspend could still wait behind. Resume and a network change run the ADR-017
re-derivation (`core/version`, `mount/listmounts`, then for each `Mounted` host a forced shallow probe and a
forced deep probe) and then probe each `Ready` or `Unreachable` host; "Unmount all & re-probe" and the
deferred-remount sweep walk the hosts the same way. With the machine going to sleep, a suspend that arrived
during any of these waited for the rest of the walk: up to about 5 s (a probe at the default
`probe_timeout_seconds`) per remaining probe, against the roughly 2 s Windows allows.

The maintainer decided on 2026-11-06 (option B of `bs-5ga` item 3): **commands are still not preempted, but a
command that works through several hosts checks for a pending suspend before it starts the next unit of work
and stops if one is pending.** Option C, full D3 (cancel the command in flight and treat a cancelled forced
probe as "preempted, not failed" in ADR-017's gate), was rejected: nothing in flight is cancelled here, so that
handling is not needed, and decision 3's rule is not extended to commands.

*What it does.* The check is `MountSupervisor.StopForPendingSuspend`, which reads the same `pendingSuspends`
counter as the decision-1 gate. It reads `SuspendPending` and not `MountingSuspended`: commands that
legitimately run while the system is suspended (a network change) keep probing. It runs before:

- `core/version`, `mount/listmounts` and each `Mounted` host's forced shallow probe, and between that host's
  shallow and deep probes (`ReDeriveAfterTransitionAsync`, shared by resume and network change);
- each host of resume's loop, of network change's loop, and of the deferred-remount sweep
  (`SweepDeferredRemountsAsync`, which `NetworkChangedAsync`, `SetMountingAvailabilityAsync`,
  `OnRcloneRestartedAsync` and the reconciliation tick reach);
- each drain of "Unmount all & re-probe" and each host of its re-probe step.

A single-host command (`RequestRetryNowAsync`, a mount or unmount click) has no next host and is unchanged.

*Note (2026-11-07, `bs-5ga.2`, maintainer decision A): a repair that stops reports it.* "Unmount all &
re-probe" used to stop between hosts and then return normally, so the click reported success for a repair that
only partly ran. It now throws `RepairStoppedException` (reason "the computer is going to sleep") from either
loop, the drain step or the re-probe step, and `RepairCommands` shows that in the same dialog, with the same
fail-soft path, as a refused repair (`RepairRefusedException`, `bs-mhs`), worded so it does not say "nothing was
unmounted" (some of it ran). The two stay distinct: refused means nothing started (the system was suspended or
a suspend was already pending when the command was dequeued), stopped means partway. The outcome is otherwise
unchanged: work already done stays done, hosts the repair did not reach are left for the suspend (drained as
`DrainCause.Suspend`, so an on-demand one gets its ADR-022 record and returns at wake, rule 9a; a completed
repair would have left it `Ready`), and no counter or backoff is touched. A repair that completes before any
suspend is pending still returns normally. The other stopped commands (resume, network change, the sweep) are
background work with no caller to tell and are unchanged.

*Note (2026-11-07, `bs-5ga.5`, maintainer decision A): a repair whose suspend arrives in its last step reports
it too.* `bs-5ga.2` covered a suspend that stops the repair before the next host. A suspend that becomes pending
during the last drain, or during the last re-probe, leaves nothing to skip, so the repair returned normally,
yet a host it drained ended `Disabled`: decision 1 does not re-enable a drain that completes while a suspend is
pending (the drain's completion and the queued re-enable both refuse), so the "then re-probe" half did not
happen for it. The repair now throws `RepairStoppedException`, same reason and same log path, when it reaches
its end with a suspend pending **and** a host it drained still waiting for that re-enable (`Disabled`, or
`Draining` whose drain will complete under the suspend). It is not a bare "a suspend is pending at the end",
because the two differ in one case: a repair that drained nothing and had already re-probed every host when
the suspend arrived did all of its work, and still returns normally. When something was drained the two agree,
since the suspend stays pending until after every re-enable this command queued has been refused. Only what the
caller is told changes: no host state, counter, timer or the suspend's handling. A repair that completes with
no suspend pending returns normally, and one refused before it starts is unchanged (`RepairRefusedException`).
One window remains by construction: the re-enables run in continuations queued behind the command, so a
suspend that arrives after the command has returned and before one of them runs leaves that host `Disabled`
with the repair already reported complete. Resume re-enables it, as for any `Disabled` host.

*The bound.* A suspend now waits behind **one call in flight**, not one per remaining host: `core/version`
(client timeout 5 s), a forced probe (`probe_timeout_seconds`, default 5 s), or `mount/listmounts` (client
timeout 10 s). Two caveats, stated so the bound is not read as tighter than it is:

- A forced probe whose failure drains the host is a longer unit: the drain's `mount/unmount` (30 s client
  timeout) and its `mount/listmounts` verification are part of the host's step and are not preempted
  (decision 4). "Unmount all & re-probe" starts with drains, so its in-flight unit is one drain.
- `rclone rcd` takes one lock around `mount/mount`, `mount/unmount` and `mount/listmounts` (`bs-6lu`), so a
  `listmounts` that arrives while an unmount or mount holds the lock waits for it, up to its 10 s client
  timeout. `core/version` and the probes do not take it.

*What a skipped host is left as.* Exactly as it was: no state change, no failure counter touched (so nothing
counts toward `failures_before_unmount`, and nothing is booked `Automatic`), no backoff reset, no remount record
dropped, and **no timer disposed**. Every call site checks before it resets a backoff or disposes a timer; the
forced-probe helper disposes the host's timer first, so a check placed after it would strand the host.
Nothing is left unprobed afterwards, by state:

- `Mounted`: its shallow and deep timers are still armed, and the suspend that is pending drains it
  (`DrainCause.Suspend`; an on-demand host gets its ADR-022 record).
- `Ready` and `Unreachable`: its idle-probe timer is still armed (it skips and re-arms while suspended, D5), and
  the resume that follows the suspend force-probes it (rule 5; no tier split).
- `Disabled` and administratively enabled: re-enabled by that resume. (`EnableHostAsync` already returned before
  `Probing` while a suspend is pending, so resume's loop was safe for these hosts before this change.)
- A host holding an ADR-022 remount record keeps it: the suspend leaves records in place and clears only their
  deadline, and resume sets a new deadline and resolves them.

A pending suspend cannot be cleared without running: `SuspendAsync` queues its action before it returns, the
action decrements the counter as its first statement, and a caller that gives up still leaves the queued action to
run. So the case "pending, then no suspend" does not arise; the case "suspend ran, no resume followed" is the
ordinary one in which every drained host waits for resume, and skipped hosts are no worse off than the rest.

*Not changed, and why.* These are loops over hosts that this amendment deliberately leaves alone:

- **Config reload** (`ApplyConfigChangeAsync`). Stopping would drop part of the new definition; a changed host's
  forced probe cannot be skipped either, because an `Unreachable` on-demand host has no timer to probe it later.
- **`ReconcileAsync`'s drain loop**, the `mount/unmount` it issues for each mount rclone no longer lists.
  The re-derivation does stop before it starts `ReconcileAsync`. (Settled 2026-11-07, `bs-5ga.3`: these
  are drains and orphan unmounts, so decision 4 already covers them -- see the amendment at the end of
  this ADR.)
- **A drain in flight**, and the drain a forced probe's failure starts (decision 4).
- **Startup.** `EnableHostAsync` already declines to start a probe while a suspend is pending.

*Consequences.* `docs/ARCHITECTURE.md` §3 and §4 rule 5 now say that a multi-host command stops between hosts.
The test that pinned "commands are not preempted" (`SuspendPreemptionTests`, "retry now") is still true for the
call in flight and for a single-host command; its comment now says so. `SuspendStopsMultiHostCommandsTests` pins
the new behaviour.

### Amendment (2026-11-02, bs-5o4): the suspend sweep unmounts all hosts, then verifies

**Amendment.** ADR-023 makes sure the suspend is not kept waiting behind routine checks. It left the
suspend's own work serial: `RunSuspendAsync` drained each `Mounted` or `Mounting` host in turn, and one
drain step is `mount/unmount`, `mount/listmounts`, and (past the budget) a forced `mount/unmount` and
another `mount/listmounts`. Against a stuck `rclone rcd` a step takes up to about 80 s (30 + 10 + 30 + 10),
so host N's unmount was not even requested until hosts 1 to N-1 had each finished one, and the later
hosts were not unmounted before the machine slept (Invariant I8).

The maintainer decided on 2026-11-02: **on suspend, send every `mount/unmount` first, then verify them;
every drive gets its unmount request inside the budget.** Built as follows (`docs/ARCHITECTURE.md` §4
rule 5 has the behaviour):

- The suspend moves every host to `Draining` with no rc call, then sends all the `mount/unmount` requests
  together (every call is started before any is awaited; each keeps its own 30 s `RcloneClient` timeout),
  verifies with one `mount/listmounts`, and escalates what is still listed and past the budget the same
  way (all forced requests together, one more `mount/listmounts`). Concurrent requests were chosen over
  sequential-without-verification because a stuck call would still hold the next host's request for its
  whole 30 s timeout; concurrent puts the last host's request on the wire at the instant of the first's.
- The per-host `Draining` state, the escalation flag, the arrival-counted budget (`bs-7d7`) and the
  per-host retry timer are unchanged; no state or transition was added. Only the rc calls run
  concurrently; every state change and log line stays on the channel. Only `MountSupervisor` makes the
  calls.
- Scope: the suspend sweep only. Hosts that were already `Draining` when the suspend arrived keep the
  behaviour of `bs-u4a` item 4 (each is re-driven one at a time after the sweep). The general drain path
  is not changed for any other drain cause. *(The parenthesis is superseded 2026-11-07, `bs-mua.1`: the
  already-`Draining` hosts' re-issue now goes in the sweep's batch. See the note at the end of this
  amendment.)*
- Not changed, and left open: a host that is still unconfirmed after the sweep retries on its own timer,
  so when several reach the same deadline their re-issued requests run one host's step at a time. Each
  of them has already had its first request issued.

*Note (2026-11-02, independent review of `bs-5o4`).* The sweep awaits every request before
verifying, on the channel, so one hung `mount/unmount` (up to its 30 s timeout) delays the other hosts'
verification and forced unmount past arrival plus the budget. Accepted rather than adding per-host
continuations: every host's first request still leaves at arrival, which is what I8 needs; a second
identical request at 3 s or 30 s both land after Windows has slept (~2 s); resume reconciles
(ADR-017). `docs/ARCHITECTURE.md` §4 rule 5 states this.

*Correction (2026-11-06, bs-6lu; the maintainer's decision, option A).* The amendment above and its first
note say the request phase "takes as long as its slowest call, not the sum". That is wrong against the real
`rclone rcd`, and is corrected here rather than rewritten out of the record. rcd holds one package-level mutex
(`mountMu`, `cmd/mountlib/rc.go`, checked at rclone v1.75.0) for the whole of `mount/mount` (including the SFTP
connect), `mount/unmount` (including the WinFsp unmount and an up-to-10 s poll for the drive letter to go),
`mount/listmounts`, `mount/types` and `mount/unmountall`; `core/version`, `operations/list` and `vfs/*` do not
take it. The `mount/unmount` and `mount/listmounts` handlers ignore their context, so a call Bosun times out on
keeps running in rcd and keeps the lock. What is true: Bosun sends every host's unmount at arrival, and the
amendment removed Bosun's own per-host round trips (the serial drain step of up to about 80 s per host). What
is not: rcd executes those requests one at a time, so the sweep takes the **sum** of the unmounts, and one
hung unmount holds the rest inside rcd (their drives stay mounted) until it ends, however many requests are
already on the wire. The verification `mount/listmounts` and any forced unmount queue behind it too.
**Accepted as a residual**: Bosun cannot parallelise around rcd's lock. A separate `rclone rcd` per host
would, and was considered and rejected for now. The per-unmount latency on the maintainer's machine is unmeasured;
manual test T1 will show it. The test fake (`HoldableRclone`) now models the lock, and the tests that assumed
parallel unmounts were rewritten to the real behaviour. `docs/ARCHITECTURE.md` §4 rule 5 states this.

*Amendment (2026-11-07, bs-mua.1; the maintainer's decision, option B of `bs-mua` item 1; option A,
"document as won't-fix", was recommended and declined).* This replaces the statement above that hosts
already `Draining` when the suspend arrives are "re-driven one at a time after the sweep". They are not any
more. Such a host, when the suspend budget shortens its forced-unmount deadline (`bs-u4a` item 4), now has its
unmount re-issued in the sweep's arrival batch: `RunSuspendAsync` moves the `Mounted`/`Mounting` hosts to
`Draining`, counts the budget for the already-`Draining` hosts, and passes both groups to
`DrainAllForSuspendAsync` (swept hosts first, then the re-driven ones: a swept host has never had an unmount
sent, an already-draining one has). All requests are started before any is awaited, so with a stuck rcd a
later already-`Draining` host's request no longer waits behind an earlier host's whole step (up to about 80 s
each) on Bosun's side. The re-driven hosts then share the sweep's one `mount/listmounts` and its forced-unmount
phase. A host whose deadline the budget does not shorten is still not re-issued.

*What is unchanged:* each host's drain cause (a user unmount still parks it, a removed host is still
evicted, an idle timeout stays an idle timeout, a probe-failure drain stays `Automatic`), its single
`Draining` transition-log entry, its once-only escalation flag, and the arrival-counted budget (`bs-7d7`).

*What it does not do:* make rcd faster. `rclone rcd` runs `mount/*` calls one at a time under one lock
(`bs-6lu`, above), so this removes Bosun-side serialisation only; the sweep still takes the sum of the unmounts
inside rcd, and a hung one still holds the rest. The sweep's own accepted residuals (verification and forced
unmount wait for every request to return) apply to the re-driven hosts too.

*A request already in flight (decided here, not before).* Every rc call a drain makes is awaited on the
supervisor's channel, which runs one action at a time. So a host's own unmount (a retry step stuck on a slow
rcd) has returned, or timed out, before the suspend action starts; the suspend waits behind it, as ADR-023
decision 4 already says (drains are not preempted). The batch then sends that host exactly one re-issue. No
host is in the batch twice (a host is either `Draining` or `Mounting`/`Mounted` when the two lists are
taken), so two unmounts for one drive are never in flight from Bosun together, and nothing new is cancelled.
`docs/ARCHITECTURE.md` §4 rule 5 states this.

*Accepted residual (2026-11-07, `bs-mua` item 2; the maintainer's decision, option A):* a host still
unconfirmed after the sweep retries on its own drain-retry timer, so when several are due together their
re-issues run one host's step at a time. Not batched. Those retries fire at or after the suspend budget, so
after the machine has slept, and every host's first request already left at arrival; the cost is a slower
recovery at wake against a stuck rcd, where resume queues behind those steps. rcd runs `mount/*` calls one at
a time anyway (`bs-6lu`), so combining due retries into one step (about 40-60 lines, moderate risk) would
remove only Bosun's own round trips. A retry step already queued on the channel before a suspend can still
send one extra unmount for a host the batch left unconfirmed; that is part of this residual.

### Amendment (2026-11-07, bs-5ga.3): a suspend preempts the post-mount VFS baseline sample

*Maintainer decision B; option A (close as covered by the skip-when-pending check) was recommended and declined.*
This replaces the residual bullet in Consequences ("a suspend that arrives during the VFS baseline sample that
follows a mount waits for it"). The sample is the tail of a mount command (or of the idle tick that began the
mount), so it has no tick source of its own for the `SuspendAsync` prologue to cancel. It now runs under a source
of its own, published in one field (`runningBaselineSample`) that `PreemptRunningTick` cancels beside the running
tick's source: the same `CancelAsync` on the caller's thread, and the same preemption-token check
(`TickPreempted` semantics) afterwards, so nothing about it is a second mechanism. The source is unpublished
before it is disposed, and a cancel that loses the race to the disposal is swallowed.

*A preempted sample is "no baseline taken", not a failed poll.* It is discarded before any bookkeeping: no
baseline, no `UploadFailureCount`, no `VfsPollFaultLogged`, no idle-clock restart, no counter, no backoff, no
warning (one Information line). It cannot reach the fail-closed "Which VFS" path of rule 7, so idle-unmount
eligibility is unchanged: the idle timer, armed at the mount, runs on exactly as it does after a baseline poll
that failed. The mount itself had already succeeded, so the host stays `Mounted` and the suspend drains it with
`DrainCause.Suspend`. Only a suspend preempts the sample; a sample that fails any other way is the failed poll it
always was. If the suspend does not arrive, nothing changes. A suspend already pending still skips the sample
(decision 5).

*Decision 4, spelled out for `ReconcileAsync`.* Its drains, and the `mount/unmount` it issues for each mount that
rclone no longer lists (orphans), are drains: decision 4 already says they are not preempted, and a pending
suspend does not cancel them. (The reconciliation tick's own `mount/listmounts` call stays preemptible, decision
2, and a preempted one acts on nothing; it is the unmounts after it that finish.) This closes `bs-5ga` item 4.

---

## ADR-024 · Start and resume Claude Code sessions remotely, from a Claude conversation

**Status:** Proposed (2026-11-07). The maintainer asked for the capability and settled its shape in
conversation on 2026-11-07: the tool set, the one-time handoff, and resume-by-default. The
architecture in Decision 2 is partly Claude's proposal and is not yet confirmed; the Open questions
are undecided. Nothing here is built. Would extend Bosun's scope (`CLAUDE.md` §1), add the first
network-reachable surface Bosun has had, and share machinery with ADR-021.

**Where this stands (2026-11-07).** The text below is kept as first written; dated addenda at the end
record what has been decided since, and they win where the two differ.

- Decided: an existing remote desktop as the safety net, then specific tools that Bosun runs Claude
  Code for in a pseudo console it owns, not a Windows Terminal tab (supersedes Decision 1's "open a
  Windows Terminal tab"). Tools are a fixed, modular list inside the development area, sessions use
  the normal permission settings, Bosun returns each session's name and its link when printed,
  repositories are always private, Peek is deferred, configuration is a `[remote_sessions]` table.
- Open question "how Remote Control is turned on unattended": answered. `claude --remote-control`
  at launch; the one-time "Enable Remote Control?" prompt is visible to Bosun in the pseudo console.
  Decision 4's "first spike" is now `bs-4xw.3`.
- Open questions "what an authenticated caller may do", "where the development area is configured"
  and "what the way to reach the session is": answered in the addenda.
- Still open: how the request gets from the phone to Bosun (shape A's Remote Control server session
  or shape B's connector through a tunnel, which carries the Cloudflare Access sign-in question);
  how this shares code with ADR-021.

**Context.** The maintainer spends much of his day driving Claude Code sessions remotely, from a
phone, while away from the workstation. Often something comes up that needs a new Claude Code
session and none is running on the workstation. Today that means waiting until he is at the desk.
The same gap exists for an existing project that has no live session: nothing is open in it, so
there is nothing to connect to.

He wants to close the gap from inside a Claude conversation. For a new project, he and Claude
settle the concept in chat; when he gives the go-ahead, Claude does the setup on the workstation
without him. For an existing project, Claude lists what is there and reopens it.

This is a handoff, not remote control by Claude. Once a session is running on the workstation with
Remote Control enabled, he switches to Claude Code Remote Control and works there. Sessions tend to
stay up once started, so the hard part is only getting one started and connectable.

Bosun already runs all day in the user's interactive session (I3), already launches `wt.exe`
through `IExternalLauncher`, and under ADR-021 already knows which Claude Code sessions exist and
how to resume them with `claude --resume <id>`. That makes it the natural owner.

**Decision (proposed).**

1. **What it does.** Given a project, open a Windows Terminal tab in that project's directory, start
   Claude Code in it, and turn Remote Control on, then return the way to reach the session. A
   conversation in the Claude app triggers it. It is a one-time handoff per session; it does not
   keep driving the session afterwards.

2. **Shape.** A local service owned by Bosun on the workstation.
   - The core is a plain local API, so it is useful on its own and could be described by a UTCP
     manual later. A thin MCP layer sits in front of it so the Claude app can use it as a custom
     connector. As far as known, the Claude app's connectors speak MCP only; this is unverified.
   - It is exposed through a Cloudflare Tunnel, with Cloudflare Access so only the maintainer can
     reach it.
   - The Claude connector expects OAuth, so sign-in has to come from Access acting as the identity
     layer rather than from a static service token. Whether that works today is unverified.
   - Tunnel credentials and tokens are secrets and stay out of the repository (I9).

3. **Tools.** Ten, all accepted by the maintainer:

   | Tool | Purpose |
   |---|---|
   | Workstation check | Machine awake, service healthy, Claude Code logged in. A clear answer instead of a silent failure. |
   | List projects | Directories in the development area. |
   | Create project | New folder, a brief written from the concept agreed in chat, a terminal there, Claude Code started with the brief, Remote Control on. |
   | Open or resume project | Terminal in an existing project, Claude Code started, Remote Control on. Resumes the prior conversation by default. |
   | List resumable sessions | A project's saved conversations with last-active time and a one-line summary, so one can be picked in chat. |
   | List running sessions | Live sessions Bosun launched, with how to reach each, to avoid duplicates. |
   | Close session | Shut down a finished session. |
   | Clone and open | Clone a GitHub repository into the development area and start a session in it. |
   | New-project setup | `git init`, create the GitHub repository, set up Beads and Graphify, so new projects start the same way. |
   | Peek at session | Recent output of a running session, for a status check. |

4. **Order and effort.**
   1. Groundwork, the largest piece: the local service, the tunnel, OAuth through Access, the MCP
      layer, and starting a terminal plus Claude Code plus Remote Control reliably with nobody at
      the keyboard. The first spike is proving Remote Control can be turned on unattended.
   2. Low effort once the groundwork exists: workstation check, list projects, create project,
      open project.
   3. Moderate, because Bosun must track the sessions it launches: list resumable sessions and
      resume, list running sessions, close session.
   4. Moderate, mostly scripting: clone and open, new-project setup.
   5. Hardest after the groundwork: peek at session. Windows Terminal does not expose a tab's
      contents to other processes, so this may need sessions launched in a way that captures
      output.

**Open questions.** To be filed as `needs-decision`; not part of the decision above.

- How Remote Control is turned on unattended, and whether it can be at launch rather than by
  typing into the session afterwards.
- Whether Cloudflare Access can act as the OAuth provider the Claude custom connector requires,
  and what the maintainer's current Access setup already allows.
- What an authenticated caller may do. The service creates directories and starts processes, so
  authentication is the whole security boundary. Whether it may only act inside the development
  area, and never run an arbitrary command, is to be confirmed.
- Where the development area is configured. Bosun's configuration has no such setting today
  (`docs/CONFIG-SCHEMA.md`), so a key has to be added or an existing location reused.
- How this shares code with ADR-021: the session records, the process snapshot, and the
  `claude --resume` launch through `IExternalLauncher`. It may also need its own opt-in table, as
  `[claude_sessions]` has, since it opens a path into the machine.
- What "the way to reach the session" is: a Remote Control link, a session name, or something
  else, and whether the tool can return it in the same call.

**Consequences, if accepted.** `CLAUDE.md` §1 gains a fifth responsibility. A new project holds
the service and the MCP layer, and it must not reference WPF (the same rule as `Bosun.ClaudeHook`).
Default-suite tests must not launch `wt.exe`, start Claude Code, or touch the tunnel. The work is
filed as Beads issues from this ADR; no `TODO.md` or plan file is added.

### Addendum (2026-11-07): two candidate shapes, A under investigation, B the fallback

The maintainer asked for the simpler shape below to be investigated, and for the shape in Decision 2
to be kept as the fallback. The maintainer is sceptical that A will give the functionality wanted. Neither is
chosen; the status stays Proposed.

**Shape B, the fallback (Decision 2 as written above).** A local service owned by Bosun, a thin MCP
layer, a Cloudflare Tunnel with Access. It stays the plan of record if A falls short, and is not to be
dropped because A is being looked at. Two findings from the 2026-11-07 research apply to it whichever
way A goes: a claude.ai custom connector on a Pro/Max account authenticates by OAuth (dynamic client
registration or a client ID metadata document) or not at all, so Cloudflare Access service tokens are
not an option; and the authorisation server's metadata must be reachable from Anthropic's egress range,
so an Access policy has to let those requests through.

**Shape A, under investigation: Claude Code's own Remote Control server mode.** `claude
remote-control` (present in the installed Claude Code 2.1.319) is a long-running process that keeps a
directory reachable from the Claude app; the app can start new sessions on it (`--spawn
same-dir|worktree|session`, `--capacity`). The connection is outbound only, so nothing is exposed to the
internet and there is no tunnel, OAuth or MCP layer to build. Bosun's part would be to keep such a server
running in the development area (it already runs all day, I3) and to restart it when it dies. A session
started from the phone on that server would then do the work of the ten tools itself: create a project,
or open a Windows Terminal tab in another project with `claude --remote-control` (or `claude --resume
<id> --remote-control`) and report how to reach it.

Known gaps in A, to be settled by the investigation (`bs-` issue filed 2026-11-07):

- The first run of `claude remote-control` asks "Enable Remote Control? (y/n)" once, and a directory
  not yet trusted asks "Trust <directory>?"; without a terminal the trust prompt fails. A brand-new
  project folder would meet it with nobody at the keyboard.
- Choosing the folder from the phone is not documented: a server serves the directory it was started
  in.
- `--continue` and `--session-id` reattach only for about four hours after the server stops; older
  conversations need `claude --resume` in a new terminal.
- No documented service mode; the process must stay running. Windows is not named explicitly in the
  Remote Control documentation.
- Whether a session can find out the URL or name of a session it launched, so it can hand it back.
- Peek at a running session has the same Windows Terminal limit as in B.

Sources: code.claude.com/docs/en/remote-control.md, settings-reference.md (`remoteControlAtStartup`),
cli-reference.md; claude.com/docs/connectors/building/authentication; support.claude.com article
11175166.

### Addendum (2026-11-07): reaching the workstation remotely, maintainer decision A

The maintainer wants a working way to do these things remotely, whether by remote desktop or by
specific tasks Bosun automates. Decided, in this order:

1. **An existing remote desktop tool as the safety net, set up first.** Chrome Remote Desktop
   (shares the signed-in console session, outbound connection, phone client) or Windows RDP over a
   private network path. Nothing is built in Bosun for this. It covers the "Trust this folder?"
   prompt and anything the tools below do not. A remote-control server of Bosun's own (VNC-style)
   is rejected: existing tools are safer than anything Bosun would expose.
2. **Specific tasks Bosun automates, built afterwards** (the tools in Decision 3). Finding behind
   the design: Bosun cannot send keystrokes to a locked workstation, and cannot read a Windows
   Terminal tab, so it cannot answer a prompt in a Terminal tab while the maintainer is away. Instead,
   Bosun would start Claude Code in a pseudo console (ConPTY) it owns, which works while the
   workstation is locked. Bosun can then see a trust prompt, relay it, and answer it through Claude
   Code's own prompt when the maintainer approves. That is not an edit of `~/.claude.json`, so
   ADR-021 Decision 2 holds. The same mechanism gives Peek at session its output. The cost: such
   sessions have no visible Terminal tab (they are reached through Remote Control) and end if Bosun
   exits. Unverified: that Claude Code's interactive UI, including Remote Control, runs normally
   under a ConPTY host with no window. To be proved by a spike.

The status stays Proposed.

### Addendum (2026-11-07): what the remote tools may do, maintainer decision A

- Bosun's tools are a fixed list. They act only inside the development area, and none of them runs a
  command passed in by the caller. Anything outside the list goes through the remote desktop.
- Sessions they start use the maintainer's normal Claude Code permission settings, exactly as a
  session started at the desk. Bosun passes no permission-mode flag. Approval prompts reach the
  maintainer through Remote Control.
- **Modular, so tools can be added.** Each tool is one class behind one small interface (its name,
  description, parameters, and handler), registered in one list. Adding a tool is a new class, one
  registration line, and its tests; nothing else changes. This is compile-time modularity, not a
  plugin system (`CLAUDE.md` §5): no tools are loaded at runtime from outside Bosun.

### Addendum (2026-11-07): handing back the session, maintainer decision A

Bosun names every session it starts (for example `bosun · <project>`) and always returns that name,
which the maintainer can find in the Claude app's session list. If the session prints its Remote
Control link, which Bosun can read from the pseudo console, the link is returned too. A missing link
is not a failure. Whether the link is printed is checked by spike `bs-4xw.3`.

Correction to the issue list given in conversation: ADR-021 Decision 8 does not forbid reading a
Remote Control session id. And restored sessions reconnecting is already ADR-021 Assumption 5
(`remoteControlAtStartup`), so it is not an ADR-024 issue.

**Sleep (2026-11-07).** Already covered: the maintainer never lets the workstation sleep. Nothing is
built for it. The workstation check cannot report "asleep"; if the machine ever is, the remote
desktop app showing it offline is the signal.

**Peek at session (2026-11-07).** Left out of the first build. The maintainer may add it back if a
gap shows up in use; as a modular tool reading the pseudo console's recent output, it is one more
class. Tracked as a deferred `bs-` issue so it is not forgotten.

**GitHub repository visibility (2026-11-07, maintainer decision A).** New-project setup always
creates a private repository under the account `gh` is signed in as. The tool has no option to make
one public; the maintainer does that on GitHub by hand. The workstation check confirms `gh` is signed
in.

**Configuration (2026-11-07, maintainer decision A).** A table of its own, off unless set:

```toml
[remote_sessions]
enabled          = false                        # ADR-024
development_area = "%USERPROFILE%\Development"  # required when enabled; must exist
```

Bosun refuses to start the tools when `enabled = true` and `development_area` is missing or does not
exist. `%VAR%` tokens are kept literal and expanded at use, as `rclone_config_path` is. Separate
from `[claude_sessions]`: restore acts only on the machine, these tools open a way in from outside.
`docs/CONFIG-SCHEMA.md` changes only when the ADR is accepted.
