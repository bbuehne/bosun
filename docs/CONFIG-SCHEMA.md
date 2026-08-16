# Configuration Schema

`config/hosts.toml` — **gitignored**. `config/hosts.example.toml` is the
committed template and must contain no real hostnames, usernames, or key paths.

## Global

```toml
[global]
rclone_rc_port          = 5572        # loopback only
rclone_config_path      = "%APPDATA%\\rclone\\rclone.conf"
probe_timeout_seconds   = 5
failures_before_unmount = 3
backoff_seconds         = [5, 15, 30, 60, 300]   # holds at last value
mounted_probe_interval_seconds = 60   # ceiling for probing while Mounted; see ADR-011
suspend_unmount_timeout_seconds = 8   # Windows will not wait forever
start_with_windows      = true
```

## Per host

```toml
[hosts.prod-web]
display_name  = "Prod Web"
hostname      = "prod-web.example.internal"
port          = 22
user          = "barry"
identity_file = "~/.ssh/id_ed25519"

  [hosts.prod-web.mount]
  mode                 = "persistent"   # persistent | on-demand | none
  drive                = "P:"
  remote_path          = "/srv/www"
  vfs_cache_mode       = "writes"       # minimum; "full" also permitted
  network_mode         = true           # must be true — see Invariant I7
  idle_unmount_seconds = 0              # 0 = never; on-demand only

  [hosts.prod-web.session]
  autostart    = false                  # open a WT tab at login
  reconnect    = true                   # wrap in the reconnect loop
  tmux         = true
  tmux_session = "main"
  tab_color    = "#7A1F1F"
  color_scheme = "Campbell"

  [hosts.prod-web.probe]
  interval_seconds = 60                 # 0 = never poll (on-demand default)
  deep_probe       = true               # verify SFTP before mounting
```

## Field semantics

| Field | Notes |
|---|---|
| `mount.mode` | `persistent` mounts automatically once probing succeeds. `on-demand` rests in `Ready` and mounts only on user action. `none` disables mounting entirely; terminal features still apply. |
| `mount.drive` | Required when `mode != "none"`. Must be a free letter with a colon. Collisions are a validation error, not something to auto-resolve. |
| `mount.vfs_cache_mode` | `writes` is the floor. `off` and `minimal` are rejected at validation — they break editors and Office. |
| `mount.network_mode` | Must be `true`. Present as a field for debugging only. |
| `probe.interval_seconds` | Polling cadence **while the host is idle** (`Ready` / `Unreachable`). `0` means never poll while idle — the sensible default for on-demand hosts (ADR-008). It does **not** disable probing while the host is `Mounted`; see `mounted_probe_interval_seconds` and ADR-011. |
| `global.mounted_probe_interval_seconds` | Upper bound on the probe interval for a host in `Mounted`. A mounted host is always probed: at `interval_seconds` when that is greater than zero and smaller than this value, otherwise at this value. Bounds worst-case unmount latency to `mounted_probe_interval_seconds × failures_before_unmount` (default 3 minutes). |
| `session.reconnect` | Wraps the ssh invocation in a loop that retries on exit code 255 (dropped) but exits cleanly on 0 (user typed `exit`). |
| `session.tmux` | Strongly recommended. Reconnect gives a *new shell* otherwise — scrollback and working directory are lost. tmux makes reconnection resume the actual session. |

## Validation rules

Reject the whole config, keep the previous one, and surface the error if any of:

- duplicate host key or duplicate `display_name`
- two hosts claiming the same drive letter
- `mode != "none"` with no `drive` or no `remote_path`
- `drive` not matching `^[D-Z]:$` (A–C reserved)
- `vfs_cache_mode` in `{off, minimal}`
- `identity_file` path does not resolve to an existing file
- `backoff_seconds` empty or containing non-positive values
- `probe.interval_seconds` negative
- `global.mounted_probe_interval_seconds` zero or negative — a mounted host must
  always have a probe cadence (ADR-011). Absent is **not** an error: it defaults
  to `60`, consistent with every other `[global]` field.
