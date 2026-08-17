# Work Breakdown — seed for Beads

Seed the Beads graph from this document on first run, using prefix `bs-`.
Preserve the epic grouping and the dependency edges. After seeding, this file is
historical; **Beads becomes the source of truth** and this document is not
updated.

Dependency notation: `E3 ← E2` means E3 is blocked by E2.

---

## E1 — Foundation

*No dependencies. Start here.*

- Solution and project layout: `src/Bosun/`, `tests/Bosun.Tests/`, `Bosun.sln`
- Target `net10.0-windows`, `UseWPF=true`, nullable enabled, warnings as errors
- Generic Host bootstrap; WPF `App` with `ShutdownMode=OnExplicitShutdown` and no
  `StartupUri`
- Serilog to a rolling file under `%LOCALAPPDATA%\Bosun\logs\` plus debug sink
- `.editorconfig`, `Directory.Build.props`
- Verify `dotnet build` and `dotnet test` pass in CI

## E2 — Configuration layer ← E1

- TOML schema per `docs/CONFIG-SCHEMA.md`, bound with Tomlyn
- `IHostConfigStore`: load, validate, watch-and-reload
- Validation: duplicate host names, drive-letter collisions, malformed drive
  letters, missing identity files, `mode = "persistent"` without a drive letter
- Invalid config must be rejected wholesale — never partially applied. Keep
  running on the last valid config and surface the error.
- Unit tests: valid parse, each validation failure, reload on change

## E3 — rclone rc client ← E2

- `RcloneProcessService`: start `rclone rcd` on a configurable loopback port,
  health-check via `core/version`, restart on death, stop cleanly on exit
- `IRcloneClient` typed wrapper: `config/create`, `config/get`, `mount/mount`,
  `mount/unmount`, `mount/listmounts`, `operations/list`
- Ensure an sftp remote exists per configured host, derived from config
- WinFsp presence detection at startup with an actionable message
- Integration tests against a real `rclone rcd` where feasible; otherwise a fake

## E4 — Probe engine ← E2

- Shallow probe: TCP connect with timeout
- Deep probe: `operations/list` root, depth 1 — proves auth and SFTP subsystem
- Backoff ladder with reset triggers (network change, resume, manual retry)
- `IProbe` fakeable for tests

## E5 — Mount supervisor ← E3, E4

**The core of the project. Build and test this before any UI.**

- State machine exactly as specified in `docs/ARCHITECTURE.md` §4
- Single-threaded async loop; per-host transitions serialised through one channel
- Reconciliation against `mount/listmounts` every tick
- Adopt-or-clear existing mounts on startup (crash recovery)
- Idle-unmount timer for on-demand hosts
- Forced-unmount escalation when a clean unmount does not confirm
- **Extensive unit tests on the transition table.** Every transition, every
  guard, every timeout path. This is where correctness lives.

## E6 — System event integration ← E5

- `PowerModeChanged` Suspend → unmount all, bounded time budget
- `PowerModeChanged` Resume → re-probe all, bypass backoff
- `NetworkAddressChanged` → reset backoff, force-probe mounted hosts
- Test with real sleep/resume and a real network change. Manual test protocol
  documented in `docs/OPERATIONS.md`.

## E7 — Windows Terminal fragment writer ← E2

- Verify the current fragment directory for Store vs unpackaged Terminal against
  Microsoft documentation before implementing — done, recorded in ADR-006's
  amendment
- Serialise host list to fragment schema: name, commandline, colour scheme, tab
  colour. **Not icon** — declined (`bs-9fs`); tab colour already differentiates
  hosts and no config field exists for it
- tmux variant: `ssh -t <host> tmux new -A -s <session>`
- Optional reconnect wrapper script generation
- Rewrite on config change; never touch `settings.json`
- Test: fragment is valid JSON, Terminal loads it, `wt -w 0 new-tab -p "<host>"`
  opens a session

## E8 — Session monitor ← E1

- Enumerate `ssh.exe` via `Process.GetProcessesByName`
- Resolve command lines via CIM to correlate process → configured host
- `GetExtendedTcpTable` P/Invoke for socket state
- All of it behind `ISessionMonitor`, faked in tests
- Isolated in one file; this is the ugliest code in the project

## E9 — Tray UI ← E5, E7, E8

- `H.NotifyIcon` tray with aggregate status in the icon
- Context menu: per-host mount/unmount, open terminal, open in Explorer
- Status window: table of hosts — state, drive, session count, last probe
- Actions post commands to the supervisor channel; UI never mutates state
- Colour coding matching per-host config
- Balloon notification on unexpected unmount (this is the user-visible
  consequence of ADR-005 and must not be silent)

## E10 — Packaging and release ← E9

- `dotnet publish` single-file self-contained win-x64
- **`PublishTrimmed` must remain off** — trimming is unsupported for WPF
- Autostart via `shell:startup` shortcut, toggleable in the UI
- `release.yml` builds on tag and attaches the exe to a GitHub Release
- First-run experience: detect missing WinFsp / rclone / config, guide to setup

## E11 — Documentation ← E10

- `docs/OPERATIONS.md`: prerequisites, rclone config, manual test protocols,
  failure-mode runbook (initial version committed; extend as things are learned)

---

## Recommended order

E1 → E2 → (E3, E4, E7, E8 in parallel) → E5 → E6 → E9 → E10 → E11

E5 is the critical path and the highest-risk component. Do not let UI work start
before its transition table is under test.
