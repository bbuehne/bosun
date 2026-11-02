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
mounted_deep_probe_interval_seconds = 300   # deep (SFTP-level) probe cadence while Mounted; see ADR-016
suspend_unmount_timeout_seconds = 3   # Windows allows ~2 s for PBT_APMSUSPEND; see below
start_with_windows      = true
```

`rclone_config_path` may contain `%VAR%` environment-variable tokens. They are kept
literal in the bound config (so a config rewrite writes back what you wrote) and
expanded at use by `GlobalConfig.ResolveRcloneConfigPath`, the single place that
expansion happens (bs-3ip).

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

  [hosts.prod-web.session]            # the whole table is optional; so is every key in it
  autostart    = false                  # open a WT tab when Bosun starts at Windows login (its own autostart); not on manual starts or restarts
  reconnect    = true                   # wrap in the reconnect loop
  tmux         = true
  tmux_session = "main"
  tab_color    = "#7A1F1F"
  color_scheme = "Campbell"

  [hosts.prod-web.probe]
  interval_seconds = 60                 # 0 = never poll (on-demand default)
  deep_probe       = true               # verify SFTP before mounting
```

### Required and optional fields

A host with only the "required" rows below is valid. A missing required field is a parse error
that names the field (`hosts.<key>.probe.deep_probe is required`) and rejects the whole config.

| Table | Field | Required? | Default when absent |
|---|---|---|---|
| `[hosts.<key>]` | `display_name`, `hostname`, `port`, `user`, `identity_file` | **Required** | none |
| `[hosts.<key>.mount]` | table itself, and `mode` | **Required** | none |
| `[hosts.<key>.mount]` | `drive`, `remote_path` | Required when `mode != "none"` (validation) | none |
| `[hosts.<key>.mount]` | `vfs_cache_mode` | Optional | `writes` |
| `[hosts.<key>.mount]` | `network_mode`, `idle_unmount_seconds` | Optional | none bound; absent is not an error (only an explicit `network_mode = false` is rejected, I7) |
| `[hosts.<key>.probe]` | table itself, `interval_seconds`, `deep_probe` | **Required** | none: probe cadence affects safety (ADR-011), so it is a conscious choice per host |
| `[hosts.<key>.session]` | the whole table | Optional | all session defaults below |
| `[hosts.<key>.session]` | `autostart` | Optional | `false` |
| `[hosts.<key>.session]` | `reconnect` | Optional | `true` |
| `[hosts.<key>.session]` | `tmux` | Optional | `false` |
| `[hosts.<key>.session]` | `tmux_session` | Optional; required (validation) when `tmux = true` | none |
| `[hosts.<key>.session]` | `tab_color` | Optional | absent: Windows Terminal's own default |
| `[hosts.<key>.session]` | `color_scheme` | Optional | absent: Windows Terminal's own default |

Absent colours are not written as empty strings: the generated Terminal profile omits the
`tabColor` / `colorScheme` key. An empty or whitespace-only value in `hosts.toml` is treated
the same as absent. The defaults are applied in the config layer (`ConfigParser`), so nothing
downstream sees a null for the booleans.

When the host editor saves, `HostConfigTomlWriter` writes only the `[session]` values that
differ from these defaults, and leaves the `[session]` table out entirely when nothing
differs. A hand-written minimal host therefore stays minimal after a save; re-reading what was
written gives the same configuration. This also means the defaults are part of the schema: a
future change to one would change the meaning of files saved by an earlier Bosun.

## Claude Code sessions

```toml
[claude_sessions]
enabled = false   # restore Claude Code sessions after a reboot or a Terminal exit; ADR-021
```

Not per host: a Claude Code session is not tied to a configured SSH host.

## Field semantics

| Field | Notes |
|---|---|
| `mount.mode` | `persistent` mounts automatically once probing succeeds. `on-demand` rests in `Ready` and mounts only on user action. `none` disables mounting entirely; terminal features still apply. |
| `mount.drive` | Required when `mode != "none"`. Must be a free letter with a colon. Collisions between hosts that mount are a validation error, not something to auto-resolve. A `mode = "none"` host claims no letter, so an optional leftover `drive` on it never collides (its format is still checked). |
| `mount.vfs_cache_mode` | `writes` is the floor. Validated as an **allow-list** of `{writes, full}`, compared case-insensitively — anything else, including a typo or a differently-cased spelling of a rejected value (`Off`, `OFF`), is a validation error. Absent is not an error: it is defaulted to `writes` in the config layer (`ConfigParser`), so nothing downstream ever sees a null. |
| `mount.network_mode` | Must be `true`. Present as a field for debugging only. An explicit `false` is a validation error. |
| `mount.idle_unmount_seconds` | `0` = never (on-demand only). Negative is a validation error. Otherwise the drive unmounts this many seconds after the last **activity**, not after mounting: activity is a change in rclone's VFS counters (cache bytes or files, uploads running or queued, directory or file listings), sampled about once per `global.mounted_probe_interval_seconds`. It never unmounts while uploads are running, queued or have failed, or while the VFS cannot be read (the unmount is skipped and logged, and the clock restarts). **Known limit:** re-browsing cached folders and re-reading cached files are invisible to rclone and do not keep the drive up. See `ARCHITECTURE.md` §4 rule 7. |
| `probe.interval_seconds` | Polling cadence **while the host is idle** (`Ready` / `Unreachable`). `0` means never poll while idle — the sensible default for on-demand hosts (ADR-008). It does **not** disable probing while the host is `Mounted`; see `mounted_probe_interval_seconds` and ADR-011. |
| `global.mounted_probe_interval_seconds` | Upper bound on the probe interval for a host in `Mounted`. A mounted host is always probed: at `interval_seconds` when that is greater than zero and smaller than this value, otherwise at this value. Bounds worst-case unmount latency to `mounted_probe_interval_seconds × failures_before_unmount` (default 3 minutes). |
| `global.mounted_deep_probe_interval_seconds` | Independent, slower cadence at which a `Mounted` host is also DEEP-probed (`operations/list`, over loopback HTTP to `rclone rcd` — never by touching the drive letter itself). A shallow (TCP) probe only proves the host's port answers; it cannot detect a mount whose SSH channel died while the host stayed reachable (sleep/resume, a network blip shorter than `failures_before_unmount × mounted_probe_interval_seconds`). Deep-probe failures accumulate on their own counter (`ConsecutiveDeepProbeFailures`, kept structurally separate from the shallow probe's `ConsecutiveMountedFailures` — see ADR-016) and drain after a fixed 2 consecutive failures, deliberately fewer than `failures_before_unmount`: a dead mount confirmed by `operations/list` is stronger evidence than a TCP timeout. A deep-probe success resets its own counter. Worst case from a mount's last good deep probe to the start of its drain is `2 × mounted_deep_probe_interval_seconds + 2 × probe_timeout_seconds`, because the next probe is scheduled only after the previous one returns and a failing probe can take the whole timeout: 610 s with the defaults (300 and 5), not 600. |
| `session.autostart` / `session.reconnect` / `session.tmux` | Optional (bs-aoq). Defaults `false` / `true` / `false`; see "Required and optional fields". `autostart = true` opens a Windows Terminal tab for the host, but only when Bosun itself starts at Windows login (the Run-key launch, `--autostart`, ADR-018). A manual start, a watchdog restart, a "Restart Bosun" and a config reload open nothing. All autostart hosts open as tabs of one `wt -w 0` window, after the Terminal fragment is written, and independent of `mount.mode` (a `none` host gets its tab) and of rclone (bs-e28). |
| `session.tab_color` / `session.color_scheme` | Optional (bs-aoq). Absent means Windows Terminal's own default, and the fragment profile omits the key. |
| `global.suspend_unmount_timeout_seconds` | How long the suspend handler waits for the unmount-everything drain before handing control back to Windows, and the deadline for a forced unmount of any host draining at that moment (Invariant I8; ARCHITECTURE.md §3 and §4 rule 5). Default `3` (`bs-ajh`). Microsoft documents that "the system allows approximately two seconds for an application to handle this notification" ([PBT_APMSUSPEND](https://learn.microsoft.com/en-us/windows/win32/power/pbt-apmsuspend)); `3` leaves one second of headroom over that figure. A larger value only blocks `SystemEvents`' single notification thread for longer than Windows will wait, delaying every other subscriber in the process. Whatever the drain did not finish is recovered on resume by re-derivation (ADR-017). |
| `session.reconnect` | Wraps the ssh invocation in a loop that retries on exit code 255 (dropped) but exits cleanly on 0 (user typed `exit`). Capped at 5 attempts (`FragmentProfileGenerator.MaxReconnectAttempts`, bs-ew1) — an uncapped loop cannot distinguish "connection keeps dropping" from "`ssh.exe` cannot be launched at all" (`cmd.exe`'s own not-recognised exit code, 9009, is ≥ 255 and so satisfies the same retry condition), and would otherwise spin forever with nothing surfaced. After the cap, the tab prints a message with the last exit code and exits. This cap is a maintainer recommendation on an open decision, not settled — easily reverted to unbounded if preferred. |
| `session.tmux` | Strongly recommended. Reconnect gives a *new shell* otherwise — scrollback and working directory are lost. tmux makes reconnection resume the actual session. |
| `claude_sessions.enabled` | `true` makes Bosun generate its Claude Code plugin, watch session records, and restore sessions lost to a reboot, shutdown, logoff, or Terminal exit (ADR-021). Absent, whether the field or the whole `[claude_sessions]` table, means `false`: turning it on leads to a plugin the user installs into their own Claude Code configuration, so it is never on by default. `HostConfigTomlWriter` must round-trip this table (ADR-019). |

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
- two hosts claiming the same drive letter. A host with `mode = "none"` claims
  **no** letter (it mounts nothing), so a leftover `drive = "N:"` on a
  shell-only host does not collide with another host that mounts N:. Its
  letter's *format* is still validated (the `drive` pattern rule below), because
  that rule is unconditional. The host editor's drive-letter picker applies the same
  rule
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
- `global.mounted_deep_probe_interval_seconds` zero or negative — a mounted host
  must always have a deep-probe cadence too, not just a shallow one (ADR-016).
  Absent is **not** an error: it defaults to `300`, consistent with every other
  `[global]` field.
- `global.failures_before_unmount` less than `1` — below `1` a mounted host is
  never unmounted on probe failure (Invariant I2). `1` is the boundary and is
  valid: it means unmount on the very next consecutive failure.
- `global.probe_timeout_seconds` zero or negative
- `global.rclone_rc_port` outside `1`–`65535`
- `global.suspend_unmount_timeout_seconds` zero or negative — Invariant I8
  requires everything unmounted within this window before the machine sleeps.
- a host key whose derived rclone remote name (`bosun-<key>`) rclone would reject
  (rule `invalid-rclone-remote-name`, bs-3ip). The rule is rclone's own config-name
  grammar, `configNameRe` in
  [`fs/fspath/path.go`](https://github.com/rclone/rclone/blob/master/fs/fspath/path.go):
  `[\w\p{L}\p{N}.+@]+(?:[ -]+[\w\p{L}\p{N}.+@-]+)*` — letters, digits, `_` `-` `.`
  `+` `@` and space, not starting with `-` or space, not ending with space. Because
  of the `bosun-` prefix, every TOML *bare* key (`[A-Za-z0-9_-]`) is always legal;
  the rule bites on quoted keys such as `[hosts."a:b"]`, `[hosts."trailing "]` or
  `[hosts.""]`. Applies to every host regardless of `mount.mode`, since a remote is
  provisioned for each.
- `session.tmux = true` with a null, absent or empty `session.tmux_session` — otherwise
  `FragmentProfileGenerator` silently falls back to a shared default session name
  (`"main"`), which is defence-in-depth, not something authors should rely on.

**Note on "duplicate host key":** this cannot actually reach `ConfigValidator`.
Tomlyn rejects a TOML document that redefines a table (`[hosts.jump]` appearing
twice) at **parse** time, before binding — see
`ConfigSchemaConformanceTests.Toml_DuplicateHostKey_IsRejectedRatherThanLastOneWinning`.
It is listed here for completeness of the invariant ("no two hosts share an
identity"), not because `ConfigValidator` contains, or should ever grow, code
looking for it — that condition cannot occur by the time a `BosunConfig`
exists to validate.
