# Operations & Runbook

## Prerequisites

Run `scripts/dev-setup.ps1`. It installs WinFsp, rclone, and uv, and prints the
Graphify and Beads setup commands.

## rclone remotes

Bosun creates its own sftp remotes from `hosts.toml` via `config/create`, so you
do not normally hand-edit `rclone.conf`. To verify one manually:

```powershell
rclone lsd bosun-example-nas:
```

If that fails, Bosun's mounts for that host will fail too. Fix it at the rclone
layer first.

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
| Terminal profiles duplicated | Something wrote to `settings.json`. Invariant I5 violation. |
| Files fail to save from an editor | `vfs_cache_mode` below `writes`. Validation should have caught this. |

## Logs

`%LOCALAPPDATA%\Bosun\logs\` — rolling daily. Every state transition is logged
with the host, from-state, to-state, and trigger. When filing a bug, this is the
part that matters.
