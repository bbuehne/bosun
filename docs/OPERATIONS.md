# Operations & Runbook

## Prerequisites

Run `scripts/dev-setup.ps1`. It installs WinFsp, rclone, and uv, and prints the
Graphify and Beads setup commands.

## Configuration

Your configuration and your logs are both under `%LOCALAPPDATA%\Bosun` (ADR-012
Decision 4):

```
%LOCALAPPDATA%\Bosun\hosts.toml   -- your configuration
%LOCALAPPDATA%\Bosun\logs\        -- rolling daily logs, capped at 200 MB (see Logs)
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

## Claude Code session restore

Off by default (ADR-021). To turn it on:

1. Set `enabled = true` under `[claude_sessions]` in `hosts.toml` and save. Bosun
   writes its Claude Code plugin to `%LOCALAPPDATA%\Bosun\claude-plugin\`. If the
   window does not show tracking as on, restart Bosun.
2. Install the plugin once, from any shell:

   ```powershell
   claude plugin marketplace add "$env:LOCALAPPDATA\Bosun\claude-plugin"
   claude plugin install bosun-claude-sessions@bosun
   ```

3. In Claude Code, run `/config` and set **Enable Remote Control for all
   sessions** to `true` (`remoteControlAtStartup`), so restored sessions reconnect
   to your phone and the Desktop app on their own.
4. In Windows Terminal, set **Startup > When Terminal starts** to anything other
   than "Open windows from a previous session". Otherwise Terminal reopens the
   same tabs as plain shells beside the ones Bosun restores.

New sessions are tracked from then on. Sessions already running when you
installed the plugin are not, until they are restarted. The window lists tracked
sessions, and says so if a Claude Code session in Terminal is not reporting, or if
the plugin is out of date after a Bosun update.

What is restored: a session killed by a reboot, shutdown, or logoff reopens when
Bosun starts; a session killed because Terminal exited reopens the next time you
launch Terminal. Closing your last tab, or Terminal's only window, counts as
Terminal exiting, the same way Terminal's own session restore treats it.

What is not: a session you ended (`/exit`, Ctrl+D), a tab you closed while
Terminal kept running, a session you opened and never typed into, `claude -p`
runs, and sessions in an elevated Terminal window.

To turn it off, set `enabled = false`; Bosun stops restoring and leaves its
records alone. To remove it entirely, also run
`claude plugin uninstall bosun-claude-sessions@bosun` and delete
`%LOCALAPPDATA%\Bosun\claude-sessions\` and `%LOCALAPPDATA%\Bosun\claude-plugin\`.

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

### Claude Code session restore (ADR-021)

Setup for all of these: `[claude_sessions] enabled = true`, the plugin installed,
`remoteControlAtStartup` on, Terminal's own session restore off. "Three sessions"
means three tabs, in different folders, each running `claude` with at least one
prompt sent. "Wait" means at least one minute, so Bosun has classified at least
once.

#### C1: Restart with sessions open  *(the acceptance test for ADR-021)*
1. Three sessions open. Restart Windows.
2. **Pass:** after login, one Terminal window opens with three tabs, each in its
   original folder, each showing its resumed conversation. Each appears in the
   Claude app as connected through Remote Control. One toast says three sessions
   were restored.

#### C2: Restored sessions survive a second restart
1. Straight after C1, without typing into any restored tab, restart again.
2. **Pass:** the same three sessions are restored again.

#### C3: Shut down and power on
1. Three sessions open. Use **Shut down** (Fast Startup on, the Windows default).
   Power on.
2. **Pass:** as C1.

#### C4: Log off and back on
1. Three sessions open. Sign out. Sign back in.
2. **Pass:** as C1.

#### C5: Terminal exits underneath sessions
1. Three sessions open. End `WindowsTerminal.exe` from Task Manager.
2. **Pass:** nothing opens on its own. Launch Terminal: the three sessions are
   restored into that window.

#### C6: Deliberate ends are not restored
1. Four sessions open. In one, type `/exit`. Close a second with its tab's X while
   Terminal stays open. Open a fifth tab, run `claude`, and send nothing. Wait.
2. Restart Windows.
3. **Pass:** only the two untouched, prompted sessions are restored.

#### C7: Closing the only window
1. One Terminal window with two sessions. Close the window with its X.
2. **Pass:** nothing opens on its own. Launch Terminal: both sessions are restored
   (the accepted limitation in ADR-021 Decision 5).

#### C8: Non-interactive runs are not restored
1. In a tab, run a loop of `claude -p "say hi"` calls. Restart mid-loop.
2. **Pass:** no tab opens for any of them.

#### C9: Bosun restarts while sessions run
1. Three sessions open. Use **Restart Bosun** from the tray.
2. **Pass:** no new tabs open; the window still lists the three sessions.

#### C10: Tracking not installed
1. Uninstall the plugin and empty `%LOCALAPPDATA%\Bosun\claude-sessions\`. With
   `enabled = true`, start a session in Terminal. Wait.
2. **Pass:** the window says tracking is not reporting and names the plugin as
   the likely cause.

## Failure triage

| Symptom | First check |
|---|---|
| Explorer hangs on a drive | Was a mount left up while the host went away? Check the supervisor log for a missed unmount. This is an ADR-005 violation and a bug. |
| Drive never appears | Deep probe failing. Try `rclone lsd <remote>:` manually. |
| Drive appears then vanishes | Expected on probe failure — check whether the host is genuinely reachable. If the host row says `mount failed ...; last error: P: is presented as a Fixed drive instead of a network drive`, the drive appeared and Bosun unmounted it on purpose: rclone mounted it but did not honour `--network-mode` (Invariant I7), which would make Explorer misbehave. Check the rclone version and its `mount/mount` handling of `network_mode`; Bosun retries on the mount-retry ladder, so the drive may flash up again until the cause is fixed. Other reasons seen here: `does not exist (drive type NoRootDirectory)` (rclone reported success but no drive letter is there) and `could not verify that P: is a network drive` (the drive type could not be read; the message carries the exception). |
| Terminal profiles missing | Terminal reports **nothing** when a fragment fails to parse — no toast, no UI surface, no default log entry; it skips that fragment and loads the rest. Profiles appearing in the dropdown is the only confirmation available. Check `%LOCALAPPDATA%\Microsoft\Windows Terminal\Fragments\Bosun\bosun.json` exists and is valid JSON, then restart Terminal. Bosun's own log records each write. (The Store-vs-unpackaged path distinction is a red herring — see ADR-006's amendment.) |
| Profile opens but the connection fails | Bosun connects with the host's own `hostname`, `port`, `user` and `identity_file` (ADR-013 Amendment 1), so check those first. Reproduce exactly what the profile runs: `ssh -i "<identity_file>" -p <port> <user>@<hostname>`. `Could not resolve hostname` means the `hostname` field is wrong, not that an `ssh_config` alias is missing — none is needed. If a Terminal profile still runs the old `ssh <host-key>` form, its fragment predates the amendment; restart Bosun to rewrite it. |
| `ssh: no key found` / `failed to parse private key file` | `identity_file` points at a **public** key (`.pub`) or a PuTTY `.ppk`. rclone and ssh both need the OpenSSH-format private half. Convert a `.ppk` with PuTTYgen → *Conversions* → *Export OpenSSH key*; export from Bitvise via *Client key manager* → *Export* → OpenSSH format. |
| Profile lost its colours or font after a rename | Terminal derives profile identity from the GUID, and Bosun derives that GUID from the host's **config key**, not `display_name` (ADR-013). Renaming `display_name` should be safe; renaming the TOML key is what creates a new profile. |
| Terminal profiles duplicated | Something wrote to `settings.json`. Invariant I5 violation. |
| Files fail to save from an editor | `vfs_cache_mode` below `writes`. Validation should have caught this. |
| The health banner is showing, or a drive is stuck | Use the repair buttons in the banner, or **Repair** in the tray menu (ADR-020). *Restart rclone* replaces `rclone rcd` and reconciles every host. *Unmount all & re-probe* drops every mounted drive and brings persistent hosts back after a fresh check (it does not park hosts or un-park ones you unmounted). *Restart Bosun* starts a fresh Bosun and shows its window; it does not count against the watchdog's restart limit. Each asks first if a drive would disconnect. Use **Copy diagnostics** before restarting if you want to keep the evidence. |
| "Bosun restarted itself" notice | The watchdog found the mount supervisor stalled and restarted Bosun (the window stays hidden for that). The notice gives the time and reason; **Dismiss** removes it, or it goes after 24 hours. The log has the details. |

### Repair actions: expected result

Do this from the tray (**Repair** submenu), then once more from the banner button if the banner is showing.
Do it with at least one persistent host mounted, and note its drive letter first. Every confirmation below
is the same dialog. If it is shown correctly, none of these should happen: it flashes and closes by itself,
or it sits behind another window. (A fault of that kind, bs-3hx, was the reason the dialog was rebuilt.)

**The dialog (all three).** Within about a second of the click the Bosun main window opens if it was hidden
(or comes to the front if it was open), and a "Bosun - <repair name>" dialog appears centred over it. It
stays up until you answer. It lists the mounted drive letters. **No** is the default button, so Enter means
No. Esc also means No. Closing it any other way (Alt+F4) is "no answer".

**Restart rclone**
- Press **Yes**.
- Log (`%LOCALAPPDATA%\Bosun\logs\`, newest file) shows, in order:
  `Repair: restarting rclone at the user's request; N mounted drive(s) will be dropped and reconciled from scratch`,
  then `Repair: rclone restarted and is healthy; every host is being reconciled from scratch`.
- Drives: every mounted drive letter disappears at once. Persistent hosts mount again, on their old letters,
  within one probe cycle after a fresh probe passes (up to a minute or so). On-demand hosts stay unmounted
  until you mount them from the tray.
- The banner, if it was showing for an rclone fault, clears.
- Press **No** instead: nothing changes, drives stay up, and the log has
  `Repair: Restart rclone was declined (the user answered No at the confirmation); the repair did not run`.

**Unmount all & re-probe**
- Press **Yes**.
- Log: `Repair: unmount all and re-probe at the user's request; draining N mounted drive(s)`, then
  `Repair: unmount all and re-probe was accepted by the supervisor`. The supervisor's own transition lines
  follow, for each host.
- Drives: every mounted drive letter disappears. Each persistent host is probed again and mounts again on its
  old letter once that probe passes. A host you had unmounted yourself, and on-demand hosts, stay unmounted.
- Press **No** instead: drives stay up, and the log says `... was declined (the user answered No ...)`.

**Restart Bosun**
- This dialog always appears, even with no drive mounted. Press **Yes**.
- Log (the old instance): `Repair: restarting Bosun at the user's request (N mounted drive(s)); this is a manual
  restart and is not counted against the watchdog's limit`. The window and tray icon disappear, then come
  back a few seconds later. The new instance's window opens by itself (it is a manual restart, not an
  autostart). The new process has a different PID in Task Manager.
- Drives: drives disconnect when the old Bosun closes. Persistent hosts mount again once the new Bosun has
  probed them. On-demand hosts stay unmounted.
- No "Bosun restarted itself" notice appears (that is for the watchdog), and the watchdog's restart count does
  not go up.
- Press **No** instead: nothing restarts, and the log says `... was declined (the user answered No ...)`.

**If the log says "was not confirmed (the confirmation closed without an answer)"** the dialog went away
without any button being used. That is the old fault, or something closed the dialog. Nothing ran. Report it
with the log lines around it.

## Logs

`%LOCALAPPDATA%\Bosun\logs\` — rolling daily. Every state transition is logged
with the host, from-state, to-state, and trigger. When filing a bug, this is the
part that matters.

**Level.** The log is at Information. For a more detailed trace, set the
`BOSUN_LOG_LEVEL` environment variable to `Debug` (or `Verbose`) and restart
Bosun:

```
setx BOSUN_LOG_LEVEL Debug       -- then quit and start Bosun again
setx BOSUN_LOG_LEVEL ""          -- back to Information
```

It is read once at startup, from the environment Bosun is launched in, so a Bosun
started at login needs you to sign out and in again after `setx`. A value that is
not a level name is ignored, and the log says so in its first lines. Debug is meant
for a session spent chasing a problem, not for leaving on. The level is an
environment variable and not a `hosts.toml` key because the logger starts before
the configuration is read, and a failure to read it is exactly when you want Debug.
`Microsoft.*` and `System.*` log only warnings and errors at every level.

**Size.** Each day gets its own file, `bosun-yyyyMMdd.log`. A file that reaches
20 MB rolls to `bosun-yyyyMMdd_001.log`, then `_002`, and so on, and Bosun keeps
the newest 10 files. The log directory therefore never holds more than 200 MB. On
an ordinary day that means about ten days of history.

**A fault that keeps happening is logged once.** When something fails on a timer
(rclone not answering, `mount/listmounts` failing, an unmount that cannot get
through), the log shows the first failure in full, then nothing, then a short line
about every ten minutes:

```
rclone rcd start: still failing: rclone rcd started but did not become healthy ... HTTP 401 ... (41 attempts since 09:14)
```

A change in the kind of failure (HTTP 401 becoming connection refused) is logged at
once, and so is recovery (`... working again after 150 failed attempts since 09:14`).
So a long outage reads as a few lines, not thousands. Stack traces appear only for
exceptions Bosun did not expect; a 401, a refused connection or an rc timeout is
described in words.

**What a bundle holds.** *Copy diagnostics* includes every `*.log` file modified in
the last three days, rolled `_001` files included.
