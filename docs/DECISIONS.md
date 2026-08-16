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

**Status:** Accepted

**Decision.** Mount lifecycle, terminal session autostart, and probe cadence are
configured separately per host, not collapsed into a single "persistent /
on-demand" switch.

**Reasoning.** The combinations are real: a jump host wants a login session and
no mount; an archive server wants a mount and no shell; a production box wants a
mount, a red-tinted manual session, and aggressive probing. Collapsing these
forces bad defaults.

**Consequence.** On-demand hosts are **not polled**. Probing hosts the user is
not using costs little bandwidth but makes the UI slow and the auth logs noisy.
On-demand rows render as unknown until acted on.

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
