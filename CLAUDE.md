# Bosun — Agent Operating Instructions

You are building **Bosun**, a Windows tray application that manages SSH sessions,
Windows Terminal profiles, and supervised SFTP drive mounts.

Read this file first, every session. Then read `docs/ARCHITECTURE.md` and
`docs/DECISIONS.md` before writing any code.

---

## 0. Session start checklist

Run these in order at the beginning of every session:

1. `bd ready` — see what work is unblocked. This is the authoritative work queue.
2. Read `graphify-out/GRAPH_REPORT.md` if it exists. Do not grep the codebase
   before consulting the graph.
3. Skim `docs/DECISIONS.md`. Do not relitigate a decision recorded there.

If `bd` is not initialised yet, that is task one — see §3.

---

## 1. What this project is

A single-process WPF tray application (.NET 10, C# 14) that:

- **Generates Windows Terminal profiles** for each configured SSH host, via
  Terminal's *fragment extension* mechanism (never by editing `settings.json`).
- **Supervises SFTP drive mounts** through `rclone`'s remote-control HTTP API,
  with per-host lifecycle tiers (persistent / on-demand / none).
- **Monitors live SSH sessions** by enumerating `ssh.exe` processes and their
  TCP state.
- **Reacts to system events** — sleep, resume, network change, session lock — by
  unmounting proactively rather than letting mounts wedge.

It is a personal tool, shared publicly. It is **not** a product.

---

## 2. Non-negotiable invariants

These are correctness requirements, not preferences. Violating any of them
produces a tool that hangs the user's File Explorer.

| # | Invariant |
|---|-----------|
| I1 | **Never mount a host that has not passed a probe.** A drive letter pointing at an unreachable host blocks Explorer, `dir`, and file dialogs process-wide across the OS. |
| I2 | **Unmount on failure; do not retry through failure.** After N consecutive probe failures (default 3), unmount immediately and mark the host unreachable. Remount only after a fresh successful probe. |
| I3 | **The supervisor runs in the user's interactive session.** Never a Windows Service running as `SYSTEM` — drive letters are per-logon-session and a `SYSTEM` mount is invisible to the user's Explorer. |
| I4 | **All mount operations go through `rclone rc` HTTP calls**, never by spawning and tracking `rclone mount` processes. One code path serves both persistent and on-demand tiers. |
| I5 | **Windows Terminal profiles are written as a fragment**, at `%LOCALAPPDATA%\Microsoft\Windows Terminal\Fragments\Bosun\bosun.json` (note the `Microsoft\` segment — see ADR-006's amendment). Never read, write, or merge the user's `settings.json` — Terminal rewrites it from its own UI and you will clobber each other. |
| I6 | **`--vfs-cache-mode writes` is the minimum** for every mount. Without it, applications that open files for random read-write (editors, Office) fail outright. |
| I7 | **`--network-mode` on every mount.** Windows optimises fixed and network drives differently; presenting an SFTP mount as a fixed disk produces pathological Explorer behaviour. |
| I8 | **On power suspend, unmount everything** before the machine sleeps. On resume, re-probe immediately rather than waiting out the backoff timer. |
| I9 | **No secrets in the repository.** `config/hosts.toml` is gitignored. Only `config/hosts.example.toml` is committed, and it contains no real hostnames, usernames, or key paths. |
| I10 | **Key-based SSH authentication only** in v1. No password prompts, no MFA handling, no connection multiplexing. See ADR-007. |

---

## 3. Tooling: Beads and Graphify

### Beads (`bd`) — the work queue

Beads is the **single source of truth for what work exists and what is next**.
Markdown task lists are forbidden; do not create a `TODO.md`, a `PLAN.md`, or a
`plans/` directory.

- Issue prefix for this project: **`bs-`**
- File an issue for any work that will take longer than roughly two minutes.
- File issues for work you *discover* mid-task rather than silently expanding
  scope — that is what Beads is for.
- When you finish a unit of work, close its issue and commit
  `.beads/issues.jsonl` so the issue graph lands in git alongside the code.
  **If it conflicts on a merge, do not hand-merge it.** It is a generated export
  of the Dolt database, which is the source of truth — take either side and run
  `bd export`, which rewrites it authoritatively. `.beads/interactions.jsonl` is
  an audit trail and is gitignored for the same reason.
  `export.auto` is enabled, so bd refreshes that file after write commands
  (throttled); `bd export` forces it immediately. There is **no `bd sync`
  command** — it shipped only in the withdrawn v1.2.0/v1.2.1 releases, and
  v1.2.2 is a re-release of the 1.1 line that deliberately excludes it. The
  Dolt database in `.beads/embeddeddolt/` is gitignored, so without that commit
  the issue graph does not survive a clone.

**Do not assume `bd` flag syntax from this document.** Beads is under active
development. Run `bd quickstart` and `bd --help` and use the syntax the installed
version actually accepts.

If `.beads/` does not exist: initialise it, set the prefix to `bs-`, then seed the
graph from `docs/WORK-BREAKDOWN.md`, preserving the epic structure and the
dependency edges as specified there.

### Graphify — codebase navigation

```
uv tool install graphifyy      # note: package is graphifyy, command is graphify
graphify install
graphify claude install        # from inside the repo
```

Then `/graphify .` in-session to build the graph.

- Consult `graphify-out/GRAPH_REPORT.md` **before** running Glob or Grep.
- `graphify-out/` is gitignored — it is a local build artifact.
- Rebuild via git hooks (`graphify hook install`), not on every Claude Code
  session start; rebuilds are CPU-intensive and will pile up.

---

## 4. Stack constraints

| Concern | Choice | Notes |
|---|---|---|
| Runtime | .NET 10 (LTS), C# 14 | TFM `net10.0-windows` |
| UI | WPF | Not WinUI 3 — tray support and unpackaged deploy are rougher there |
| Tray icon | `H.NotifyIcon` | |
| Hosting | `Microsoft.Extensions.Hosting` `BackgroundService` | In-process, not a service |
| HTTP | `HttpClient` + `System.Text.Json` | For `rclone rc` |
| Retry/backoff | `Polly` | |
| Config | TOML — `Tomlyn` | |
| Logging | Serilog, rolling file | |
| Tests | xUnit | |
| Publish | single-file, self-contained, win-x64 | |

**Do not set `PublishTrimmed`.** Trimming is not supported for WPF and will
produce an application that fails at runtime in non-obvious ways.

The one genuinely awkward area is enumerating live `ssh.exe` sessions and their
socket state. `Process.GetProcessesByName` gets you the processes; command lines
need CIM/WMI; TCP state needs a `GetExtendedTcpTable` P/Invoke. Isolate all of
this behind one interface (`ISessionMonitor`) so the ugliness is contained and
testable.

---

## 5. Working style

- **Small commits, conventional messages.** Reference the Beads issue:
  `feat(mount): add probe backoff (bs-0042)`.
- **Build must pass before you commit.** `dotnet build` and `dotnet test`.
- **Do not generalise prematurely.** No plugin architecture. No provider
  abstraction for "other mount backends". No settings UI for options the
  configuration file already covers. This tool has one user; features that serve
  a hypothetical second user make it worse for the actual one.
- **Do not add dependencies** without recording why in `docs/DECISIONS.md`.
- **When a decision in `docs/DECISIONS.md` appears wrong**, say so explicitly and
  propose an amendment — do not silently implement something different.
- **Ask before inventing behaviour.** If the spec is ambiguous, file a `bs-` issue
  tagged `needs-decision` and move to other unblocked work rather than guessing.

---

## 6. Agent topology

The **interfacing agent** (the one the human talks to) is the **orchestrator and
validator**, not primarily the implementer. Project default effort is **high**
(`.claude/settings.json`); bump to **xhigh** with `/effort` for
architecture-heavy sessions — the state machine qualifies.

Subagent definitions live in **`.claude/agents/`** with model and effort in
frontmatter; a Task invocation may override either per-call:

| Agent | Model | Effort | Use for |
|-------|-------|--------|---------|
| `researcher` | sonnet | low | Lookups — .NET/WPF APIs, rclone rc endpoints, WinFsp, Terminal fragment schema |
| `implementer` | sonnet | high (→medium for small mechanical tasks) | Most code tasks, with their unit tests |
| `test-author` | opus | high | Independent tests for the state machine and invariants |
| `validator` | sonnet | high | Verifying deliverables before they land |
| `troubleshooter` | opus | xhigh | Wedged mounts, Explorer hangs, cross-layer failures |

**Gotcha — subagents do not inherit this file.** The agent definitions embed the
load-bearing rules for exactly that reason. Keep the definitions and §2 in sync
when invariants change; both are reviewed artifacts.

### Delegation rules

- Decompose epics into bd issues, then spawn subagents per issue or small
  issue-cluster. Parallelise independent work; the Beads dependency DAG tells you
  what is ready.
- **Effort selection is part of every spawn decision:** medium for mechanical,
  low-ambiguity changes; high for anything touching the supervisor, the probe
  engine, config validation, or interop; xhigh means spawn `troubleshooter`
  instead. When unsure, err upward.
- Give subagents **complete briefs**: the bd issue, the relevant spec sections
  (`docs/ARCHITECTURE.md` §4 for anything supervisor-adjacent), the Graphify
  context, and the acceptance criteria.
- Subagents write code **and its tests together**. For E5 (the supervisor), E6
  (system events), and E2 (config validation), also send `test-author` —
  independent test authorship catches spec misreadings that self-testing cannot.
  Those three are where a bug becomes a hung Explorer rather than a red test.

### Worktree safety

Subagents run in `.claude/worktrees/<agent>/`. Bosun mounts real drive letters,
writes a real Terminal fragment, and drives a real `rclone rcd` — so an agent that
runs the app, or a test that reaches a real `IRcloneClient`, can mount drives on
the maintainer's machine and wedge his Explorer while he is working.

`dotnet build` and `dotnet test` are safe from a worktree. `dotnet run` is not.
Every test in the default suite uses a fake `IRcloneClient`, a fake `IProbe`,
injected time, and a temp fragment path. Anything needing real rclone or WinFsp is
a marked integration test the human runs deliberately.

### Validation is the interfacing agent's responsibility

Every subagent deliverable gets validated before it lands — by you directly, or by
the `validator` subagent for large or critical changes:

1. Read the diff — actually read it, against the bd issue's acceptance criteria.
2. Run the full test suite, not just the new tests. Build stays warning-clean.
3. Spot-check that new tests fail when the code under test is broken.
4. Confirm nothing new reaches `Mounting` except from `Ready`, and that only
   `IMountSupervisor` calls `mount/mount` / `mount/unmount`.
5. Confirm no default-suite test can touch a real mount, drive letter, or the real
   fragment path.

Never mark a bd issue done on a subagent's claim alone.

---

## 7. Definition of done for the v1 milestone

Bosun is v1 when, on the maintainer's machine:

1. Configured hosts appear as Windows Terminal profiles with correct colours,
   and `wt -w 0 new-tab -p "<host>"` opens a working session.
2. Persistent-tier hosts mount at login, appear as drive letters, and support
   drag-and-drop with Explorer in both directions.
3. On-demand hosts mount and unmount from the tray menu, and auto-unmount after
   their configured idle timeout.
4. Sleeping the machine overnight with mounts up, and waking it the next
   morning, leaves **no wedged drive letters** and results in all reachable hosts
   remounted without user intervention.
5. A network interruption shorter than `failures_before_unmount × interval` —
   long enough to kill the SSH channel, too short to trip the unmount threshold —
   leaves no wedged drive letter either.
6. Killing a host mid-mount results in the drive disappearing within the probe
   window rather than Explorer hanging.

Items 4 and 5 are the acceptance tests that matter. Everything else is table
stakes.

Both are harder than they look, and harder than the dock/undock scenario an
earlier version of this list named (see ADR-001's amendment — that scenario cannot
occur on this hardware). In a dock/undock the host goes away, which is the case
Bosun was originally built to notice. In these two the host is typically *still
reachable* afterwards: the shallow TCP probe keeps succeeding while the SSH channel
underneath the mount is already dead. Detecting that needs the deep probe
(ADR-016), and detecting it *at the transition* rather than up to ten minutes later
needs re-derivation (ADR-017).
