# Operations & Runbook

## Prerequisites

Run `scripts/dev-setup.ps1`. It installs WinFsp, rclone, and uv, and prints the
Graphify and Beads setup commands.

## Configuration

Your configuration and your logs are both under `%LOCALAPPDATA%\Bosun` (ADR-012
Decision 4):

```
%LOCALAPPDATA%\Bosun\hosts.toml   -- your configuration
%LOCALAPPDATA%\Bosun\logs\        -- rolling daily logs
```

On first run, if `hosts.toml` does not exist yet, Bosun creates the directory and
writes a template with a `[global]` block and every example host commented out —
it configures zero hosts deliberately (a template that tried to mount
`nas.example.internal` on first launch would be worse than no config at all). Add
a `[hosts.<key>]` section and restart Bosun to bring a host up. This is the
expected first-run path, not an error; a first-run window is the intended way to
discover it (E9).

If `hosts.toml` exists but fails to parse or fails validation, Bosun keeps
running with mounting and remote provisioning disabled — the same graceful
degradation as a missing WinFsp — rather than refusing to start. Fix the file and
restart.

A `config/hosts.example.toml` fuller example, with real host archetypes,
ships in the repository for reference — it is not what gets copied to
`%LOCALAPPDATA%\Bosun\hosts.toml` automatically. Full schema:
`docs/CONFIG-SCHEMA.md`.

## rclone remotes

Bosun creates its own sftp remotes from `hosts.toml` via `config/create`, so you
do not normally hand-edit `rclone.conf`. To verify one manually:

```powershell
rclone lsd bosun-example-nas:
```

If that fails, Bosun's mounts for that host will fail too. Fix it at the rclone
layer first.

## Running the tests

```powershell
dotnet test                                                       # the default suite
dotnet test --settings tests/Bosun.Tests/integration.runsettings  # integration tests only
```

The default suite is **safe by construction**: `tests/Bosun.Tests/bosun.runsettings` excludes the
`Integration` category, and the test project applies it via `RunSettingsFilePath`, so a bare
`dotnet test` cannot reach a live `rclone rcd`, WinFsp, a drive letter, a real SFTP host, or the
real Windows Terminal fragment path. Everything in it uses fakes and injected time. CI inherits
the same default.

Integration tests touch real components and can mount real drives — run them deliberately, on a
machine where a wedged Explorer would be an inconvenience rather than a disaster.

Mark such a test with `[Trait(TestCategories.Category, TestCategories.Integration)]`.

> A command-line `--filter` is **ANDed** with the default rather than replacing it, so
> `--filter "Category=Integration"` yields `(Category!=Integration)&(Category=Integration)` and
> silently matches nothing — it looks like a clean run. Use `--settings` as shown above.

## Manual test protocol

These cannot be automated and must be run by hand before any release.

### T1 — Sleep and resume, same network
1. Persistent host mounted, drive visible in Explorer.
2. Sleep the machine. Wait 60s. Resume.
3. **Pass:** drive returns within one probe interval. No Explorer hang at any
   point.

### T2 — Sleep and resume, different network  *(the acceptance test)*
1. Persistent host on the LAN mounted.
2. Close the lid. Move to a different network. Open.
3. **Pass:** the LAN host's drive letter is gone, not wedged. Explorer opens
   instantly. Returning to the LAN restores the drive without intervention.

### T3 — Host dies while mounted
1. Persistent host mounted. Power off the host (or drop its NIC).
2. **Pass:** drive disappears within `interval × failures_before_unmount`.
   Explorer, `dir`, and file dialogs stay responsive throughout. A notification
   fires.

### T4 — Drag and drop
1. Drag a file from a local Explorer window to the mounted drive. Then back.
2. **Pass:** both directions work; the file is intact.

### T5 — Idle unmount
1. On-demand host mounted from the tray. Leave it alone past
   `idle_unmount_seconds`.
2. **Pass:** unmounts cleanly, host returns to Ready.

### T6 — Crash recovery
1. Mounts up. Kill `Bosun.exe` from Task Manager. Restart it.
2. **Pass:** existing mounts are adopted or cleared. No orphaned drive letters,
   no duplicate mount attempts.

## Failure triage

| Symptom | First check |
|---|---|
| Explorer hangs on a drive | Was a mount left up while the host went away? Check the supervisor log for a missed unmount. This is an ADR-005 violation and a bug. |
| Drive never appears | Deep probe failing. Try `rclone lsd <remote>:` manually. |
| Drive appears then vanishes | Expected on probe failure — check whether the host is genuinely reachable. |
| Terminal profiles missing | Fragment path wrong for this Terminal install (Store vs unpackaged). Check the written file exists and is valid JSON. |
| Profile opens but the connection fails | No matching `Host <host-key>` block in `~/.ssh/config`. Bosun emits `ssh <host-key>` deliberately, so that your `ProxyJump` and friends still apply (ADR-013) — but it means the alias has to exist. Test it directly: `ssh <host-key>`. |
| Profile lost its colours or font after a rename | Terminal derives profile identity from the GUID, and Bosun derives that GUID from the host's **config key**, not `display_name` (ADR-013). Renaming `display_name` should be safe; renaming the TOML key is what creates a new profile. |
| Terminal profiles duplicated | Something wrote to `settings.json`. Invariant I5 violation. |
| Files fail to save from an editor | `vfs_cache_mode` below `writes`. Validation should have caught this. |

## Logs

`%LOCALAPPDATA%\Bosun\logs\` — rolling daily. Every state transition is logged
with the host, from-state, to-state, and trigger. When filing a bug, this is the
part that matters.
