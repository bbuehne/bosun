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
| `mount.vfs_cache_mode` | `writes` is the floor. Validated as an **allow-list** of `{writes, full}`, compared case-insensitively — anything else, including a typo or a differently-cased spelling of a rejected value (`Off`, `OFF`), is a validation error. Absent is not an error: it is defaulted to `writes` in the config layer (`ConfigParser`), so nothing downstream ever sees a null. |
| `mount.network_mode` | Must be `true`. Present as a field for debugging only. An explicit `false` is a validation error. |
| `mount.idle_unmount_seconds` | `0` = never (on-demand only). Negative is a validation error. |
| `probe.interval_seconds` | Polling cadence **while the host is idle** (`Ready` / `Unreachable`). `0` means never poll while idle — the sensible default for on-demand hosts (ADR-008). It does **not** disable probing while the host is `Mounted`; see `mounted_probe_interval_seconds` and ADR-011. |
| `global.mounted_probe_interval_seconds` | Upper bound on the probe interval for a host in `Mounted`. A mounted host is always probed: at `interval_seconds` when that is greater than zero and smaller than this value, otherwise at this value. Bounds worst-case unmount latency to `mounted_probe_interval_seconds × failures_before_unmount` (default 3 minutes). |
| `session.reconnect` | Wraps the ssh invocation in a loop that retries on exit code 255 (dropped) but exits cleanly on 0 (user typed `exit`). |
| `session.tmux` | Strongly recommended. Reconnect gives a *new shell* otherwise — scrollback and working directory are lost. tmux makes reconnection resume the actual session. |

## Validation rules

**Principle:** any `[global]` value that feeds a safety decision carries a
documented range, and validation rejects outside it. The rules below stop the
holes found so far; the principle is what stops the next field arriving
unguarded. `failures_before_unmount` had no rule at all until this pass — `0`
validated cleanly and, depending on how the comparison against it was written,
meant either "unmount on the very next probe tick" or, the direction actually
observed in `MountSupervisor`, "never unmount" — a silent Invariant I2
violation reachable from a config file, not a code bug. Every numeric
`[global]` field, and every per-host field that gates a safety decision (mount
floor, unmount trigger), gets the same treatment: a range, in this table, with
a validation rule enforcing it.

Reject the whole config, keep the previous one, and surface the error if any of:

- duplicate host key (see note below — this cannot reach `ConfigValidator`) or
  duplicate `display_name` (compared **case-insensitively**: `"Prod"` and
  `"prod"` collide, because `wt -p` resolution is undocumented on a tie and
  a differently-cased duplicate is the same hazard as an exact one)
- two hosts claiming the same drive letter
- `mode != "none"` with no `drive` or no `remote_path`
- `drive` not matching `^[D-Z]:$` (A–C reserved)
- `vfs_cache_mode`, when present, not one of `{writes, full}` (**allow-list**,
  compared case-insensitively — not a deny-list of `{off, minimal}`; see the
  field semantics table above)
- `network_mode` explicitly `false` (Invariant I7 requires `--network-mode` on
  every mount; `null`/absent is not rejected — only an explicit `false`)
- `idle_unmount_seconds` negative
- `identity_file` path does not resolve to an existing file
- `backoff_seconds` empty or containing non-positive values
- `probe.interval_seconds` negative
- `global.mounted_probe_interval_seconds` zero or negative — a mounted host must
  always have a probe cadence (ADR-011). Absent is **not** an error: it defaults
  to `60`, consistent with every other `[global]` field.
- `global.failures_before_unmount` less than `1` — below `1` a mounted host is
  never unmounted on probe failure (Invariant I2). `1` is the boundary and is
  valid: it means unmount on the very next consecutive failure.
- `global.probe_timeout_seconds` zero or negative
- `global.rclone_rc_port` outside `1`–`65535`
- `global.suspend_unmount_timeout_seconds` zero or negative — Invariant I8
  requires everything unmounted within this window before the machine sleeps.

**Note on "duplicate host key":** this cannot actually reach `ConfigValidator`.
Tomlyn rejects a TOML document that redefines a table (`[hosts.jump]` appearing
twice) at **parse** time, before binding — see
`ConfigParserTests.Toml_DuplicateHostKey_IsRejectedRatherThanLastOneWinning`.
It is listed here for completeness of the invariant ("no two hosts share an
identity"), not because `ConfigValidator` contains, or should ever grow, code
looking for it — that condition cannot occur by the time a `BosunConfig`
exists to validate.
