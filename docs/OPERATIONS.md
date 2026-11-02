# Operations & Runbook

## Prerequisites

Run `scripts/dev-setup.ps1`. It installs WinFsp, rclone, and uv, and prints the
Graphify and Beads setup commands.

### Where Bosun looks for rclone

Bosun looks for `rclone.exe` on `PATH` first. If `PATH` does not have it, Bosun
tries these places, in this order, and logs which one it used
(`Using rclone from <source>: <path>`):

1. `%LOCALAPPDATA%\rclone\rclone.exe`
2. `%ProgramFiles%\rclone\rclone.exe`
3. winget: `%LOCALAPPDATA%\Microsoft\WinGet\Links\rclone.exe`, then
   `%LOCALAPPDATA%\Microsoft\WinGet\Packages\Rclone.Rclone_*\rclone-*\rclone.exe`,
   then the same package path under `%ProgramFiles%\WinGet\Packages` (machine-wide
   installs). The `*` parts are a source/hash and a version that winget chooses.
4. scoop: `%SCOOP%\shims\rclone.exe`, or `%USERPROFILE%\scoop\shims\rclone.exe`
5. Chocolatey: `%ChocolateyInstall%\bin\rclone.exe`, or
   `%ProgramData%\chocolatey\bin\rclone.exe`

The reason for the fallbacks: a program only reads `PATH` when it starts. If rclone
was installed (and added to `PATH`) after Bosun, or after the Explorer session that
launched Bosun, started, Bosun cannot see the change even though `rclone.exe` is in
a standard place. The search is repeated on every start attempt, so **Restart
rclone** in the Repair menu, or restarting Bosun, finds an rclone installed in
one of the places above without a sign-out. An rclone installed somewhere else is
found only through `PATH`, and a restarted Bosun inherits `PATH` from the program
that launched it (usually Explorer), so after changing `PATH` sign out and back in.

If rclone is in none of these places and not on `PATH`, put it on `PATH` or in
one of the folders above. There is no `rclone_path` setting yet.

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

These cannot be automated and must be run by hand before any release. They are
written for the hardware the project is actually used on: a Windows **desktop**
(no lid, no dock, never moves between networks) with the hosts on the same LAN.
The two failure modes that matter in practice are an **overnight sleep** (T1) and a
**network interruption** (T2). They are the tests behind `CLAUDE.md` §7 items 4 and 5.

Two rules for using this section:

- **"Done" means performed, not passed.** Record that a test was performed, and
  separately whether it passed. A test that could not be performed validly (the
  machine never slept, the kill did not take) is `VOID`, not `PASS`. Use the results
  table at the end of the tests and leave a cell empty until the run has happened.
- **Everything here was derived from the code and the ADRs, not from a run.** The
  "Expected" lines say what the design predicts. Nothing in this section has been
  observed on the real machine yet. When what you see differs from an Expected
  line, that difference is the finding; do not bend the line to match.

### Before you start

1. **Which Bosun.** Run the Bosun you use day to day, as yourself, started the way
   you normally start it. Do not run any of this from a worktree under
   `.claude\worktrees\`: a Bosun started there drives the same real rclone port, the
   same drive letters and the same Terminal fragment file.
2. **Which host.** Any LAN host that Bosun can already mount over SFTP with a key.
   Check first that `rclone lsd bosun-<key>:` works (see *rclone remotes*). The
   tests below call it "the test host". Keep its address and key out of anything
   you commit (Invariant I9); the commands below read them from variables.
3. **Which settings.** T1 to T4, T7, T8, T10 and T11 need the test host configured
   `mode = "persistent"`. T5 and T9 need it (or a second host) configured
   `mode = "on-demand"`. To change a setting: quit Bosun from the tray (**Exit**),
   edit `%LOCALAPPDATA%\Bosun\hosts.toml`, start Bosun. Put every value back when
   the test is done.
4. **Write down your effective numbers.** The expected timings below are
   formulas in these values. Read them from `hosts.toml` (defaults in brackets) and
   write them in the results notes:

   | Symbol | Meaning | Default |
   |---|---|---|
   | `I` | Mounted shallow-probe interval: `min(probe.interval_seconds, global.mounted_probe_interval_seconds)`; just `mounted_probe_interval_seconds` when `probe.interval_seconds = 0` | 60 s |
   | `N` | `global.failures_before_unmount` | 3 |
   | `U` | shallow worst case, `I × N` | 180 s |
   | `D` | `global.mounted_deep_probe_interval_seconds` | 300 s |
   | `2D` | deep-probe worst case (ADR-016: two consecutive failures) | 600 s |
   | `P` | `global.probe_timeout_seconds` | 5 s |
   | `B` | `global.suspend_unmount_timeout_seconds`, the suspend budget | 3 s |
   | rc port | `global.rclone_rc_port` | 5572 |

5. **Open a PowerShell window and paste this block.** It sets the variables the
   steps use and defines two helpers. The helpers are read-only except that `Test-BosunDrive`
   starts a background job.

   ```powershell
   $H       = '<address of the test host>'
   $Key     = '<the host key: the name in [hosts.<key>]>'
   $Drive   = 'P:'                                  # the host's mount.drive
   $Scratch = "$Drive\bosun-protocol-scratch"       # every test writes only here
   $Log     = "$env:LOCALAPPDATA\Bosun\logs"

   # Log: print log entries whose MESSAGE matches a regex, each with its timestamp.
   # (Each entry is two lines: a header with time, level and source, then the message.)
   function Get-BosunLog {
       param([Parameter(Mandatory)][string]$Pattern, [string]$Dir = $Log)
       Get-ChildItem -Path $Dir -Filter 'bosun-*.log' | Sort-Object Name | ForEach-Object {
           $header = ''
           foreach ($line in Get-Content -LiteralPath $_.FullName) {
               if ($line -match '^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3} ') { $header = $line }
               elseif ($line -match $Pattern) { '{0} | {1}' -f $header, $line }
           }
       }
   }

   # Drive check: ask whether a path answers within a time limit, without hanging this window.
   function Test-BosunDrive {
       param([Parameter(Mandatory)][string]$Path, [int]$TimeoutSeconds = 10)
       $job = Start-Job -ArgumentList $Path -ScriptBlock {
           param($p)
           Get-ChildItem -LiteralPath $p -ErrorAction Stop | Select-Object -First 5 | Out-Null
           'ok'
       }
       if (Wait-Job $job -Timeout $TimeoutSeconds) {
           $result = Receive-Job $job -ErrorAction SilentlyContinue
           Remove-Job $job -Force
           if ($result -eq 'ok') { 'ANSWERED' } else { 'ERROR (the path returned an error, e.g. the drive letter is absent)' }
       } else {
           "NO ANSWER within $TimeoutSeconds s (the job is left running; close this PowerShell window)"
       }
   }
   ```

6. **Make the scratch folder, once**, after the host is `Mounted`:
   `New-Item -ItemType Directory $Scratch`. It is created under the mount's
   `remote_path` on the host, so use a host where one empty folder there is
   acceptable. **Every test writes only inside `$Scratch`.** Cleanup at the very end:
   delete the files you created (`Get-ChildItem $Scratch` lists them), then
   `Remove-Item $Scratch` without `-Recurse`, so it refuses if anything unexpected
   is in it.
7. **Know the risk.** T1, T2, T3 and T7 exist to catch a wedged drive letter, and a
   wedged drive letter can hang File Explorer for the whole session. Save your
   work and close Explorer windows you care about first. If Explorer does hang,
   two recoveries, neither verified on this machine: end `rclone.exe` in Task
   Manager (WinFsp should drop the drive letter and Bosun should start rcd again),
   or use **Repair > Restart rclone** from the tray.
8. **Check sleep works.** `powercfg /a` should list `Standby (S3)` as available. If
   the machine uses modern standby (`Standby (S0 Low Power Idle)`) the suspend
   events may arrive differently; the Bosun log lines in T1 and T8 are the
   evidence of what actually happened, and a run with no
   `System event: power suspend` line is `VOID`.

### Shared steps

The tests refer to these by name.

**Log. Read the log.** Entries are two lines each, so a plain `Select-String` on the
message loses the timestamp. Use the helper: `Get-BosunLog 'transitioned'` lists
every state change with its time; `Get-BosunLog 'suspend|resume|network address'`
lists the system events. A day's log is `%LOCALAPPDATA%\Bosun\logs\bosun-yyyyMMdd.log`,
so an overnight test spans two files; the helper reads them all in order. To see
warnings and errors with their messages:

```powershell
Select-String -Path "$Log\bosun-*.log" -Pattern '\[(WRN|ERR|CRT|FTL)\]' -Context 0,1
```

**Drive check. Does the drive answer?** `Test-BosunDrive $Scratch`. It returns `ANSWERED`,
`ERROR` (the letter is absent) or `NO ANSWER`. Do not use `Get-PSDrive`, `Get-Volume`,
`dir P:` or `.IsReady` / `.TotalSize` on a mount you suspect is dead: they read the
volume and can hang this window. Listing drive types is safe, because it does not
read the volume (it is what Bosun itself does):

```powershell
[IO.DriveInfo]::GetDrives() | ForEach-Object { '{0} {1}' -f $_.Name, $_.DriveType }
```

A healthy Bosun mount reads `Network` (`P:\ Network`). `Fixed` is a bug (Invariant I7).

**Mounts. What does rclone think is mounted?** Three ways, in order of preference:

1. The Bosun window: the host row reads `P: is mounted`, and *Copy diagnostics*
   (the Bundle shared step) writes `rclone-mounts.json` and a `RCLONE MOUNTS` section in
   `summary.txt`, which lists rclone's own mounts next to the logical drives.
2. The rc port. The default is **5572** on `127.0.0.1`; confirm who holds it with
   `Get-NetTCPConnection -LocalPort 5572 -State Listen | Select LocalAddress,LocalPort,OwningProcess`
   (substitute your `rclone_rc_port`).
3. A hand-typed call: `rclone rc --url :5572 --user bosun --pass <password> mount/listmounts`.
   **You cannot normally do this.** Bosun generates a random rc password on each
   launch, passes it to rcd in the `RCLONE_RC_PASS` environment variable, and never
   writes it down; without it the call fails with HTTP 401. If you really need it,
   Sysinternals Process Explorer shows the environment of `rclone.exe`
   (Properties > Environment). That is reading Bosun's secret, and it changes at
   every Bosun start, so prefer ways 1 and 2. The reply, when it works, is
   `{"mountPoints":[{"Fs":"bosun-<key>:<remote_path>","MountPoint":"P:","MountedOn":"..."}]}`.

**Window. The Bosun window and tray.** The window row for a host reads, for example,
`P: is mounted`, `P: unmounting...`, `P: is not mounted -- host unreachable after 3
consecutive failed probes`, or `P: is not mounted -- on-demand; mount it from the
tray when you need it`. The tray icon is green when healthy, amber when degraded,
red when something is faulted. After a drive is taken down for a reason that is not
a user unmount and not a sleep, the tray shows a notification that starts
`<display name> was unmounted` and ends with the reason (the transition's trigger text).

**Bundle. Copy diagnostics.** Tray menu > *Copy diagnostics*, or the button in the window.
Explorer opens on `%LOCALAPPDATA%\Bosun\diagnostics\bosun-diagnostics-<yyyyMMdd-HHmmss>.zip`.
Open `summary.txt` first. `hosts.json` holds the supervisor's in-memory transition
history (see *Reading the transition log*). See T14 for what the bundle should contain.

**Timing.** Write down wall-clock times to the second at every action. Log lines
carry millisecond timestamps with your local offset; compare against your own notes.
`Get-Date -Format 'HH:mm:ss.fff'` gives the current time in the same shape.

### Suggested order

Quick tests first, the overnight one last, so a mistake in the setup does not cost a
night: T11, T14, T4, T5, T9, T8, T10, T3, T2, T7, T6, then T1. T12 and T13 cannot be
run as things stand (see them). The whole set takes a few hours of attended time plus
the overnight sleep.

### T1 — Overnight sleep, host still reachable  *(CLAUDE.md §7 item 4)*

The desktop sleeps for hours with a mount up. The host stays on the LAN, so its TCP
port 22 keeps answering after wake: the shallow probe cannot tell a healthy mount
from one whose SSH channel died in the night. Bosun's defences are the unmount on
suspend (Invariant I8) and, if that was missed, the forced shallow-and-deep probe on
resume (ADR-017) and the recurring deep probe (ADR-016).

1. In the evening, with the test host `Mounted`: run the drive check (expect `ANSWERED`), then
   write a marker: `Set-Content "$Scratch\t1-before.txt" (Get-Date -Format o)`.
   Leave the test host powered on and on the network. Do not unplug anything.
   **Expected:** the window row reads `P: is mounted`; the drive check prints `ANSWERED`.
2. Start > Power > Sleep. Note the time. Leave it until morning.
3. In the morning, wake the machine from the keyboard and note the time. **Do not
   open the drive.** Within a few seconds press Win+E and open *This PC*, then open
   a local folder (for example Documents) and go back.
   **Expected:** Explorer opens and responds at every click, no "Not responding" in a
   title bar, no freeze longer than a couple of seconds. The drive letter may be
   present or absent at this moment; either is acceptable here.
4. Confirm the machine really slept:
   `Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='Microsoft-Windows-Kernel-Power'; Id=42,107; StartTime=(Get-Date).AddDays(-1)} | Select TimeCreated,Id,Message`
   (42 is "entering sleep", 107 is "resumed from sleep"; modern standby logs
   different events). Then `Get-BosunLog 'System event: power|system (suspend|resume) event'`.
   **Expected:** a `System event: power suspend` line before the sleep, a
   `System event: power resume` line after the wake, and the Kernel-Power times
   agree with yours. No suspend line means the machine did not sleep as far as
   Bosun can tell: mark the run `VOID`.
5. Read the suspend side: `Get-BosunLog "$Key|suspend"`, around the time you slept.
   **Expected (route A, the clean one):**
   - `Trigger: system suspend event -- issuing SuspendAsync (Invariant I8 budget: 3s)`
   - `Host <key> transitioned Mounted -> Draining (trigger: system suspend)`
   - `Unmount confirmed for <key> at P: (reason: system suspend)`
   - `Host <key> transitioned Draining -> Disabled (trigger: drain completed: system suspend)`
   - `Trigger: system suspend event -- drain confirmed within the 3s budget`

   If instead you see `the 3s suspend budget (Invariant I8) elapsed before the
   drain confirmed`, the suspend unmount was best-effort and may not have finished
   (route B). That is allowed by the design but is a recorded observation: note it,
   and expect the resume side to carry the recovery (next step).
6. Read the resume side: `Get-BosunLog "$Key|resume|network address|Forced deep probe"`.
   **Expected, route A:**
   - `Trigger: system resume event -- issuing ResumeAsync`
   - `transitioned Disabled -> Probing (trigger: resume: previously enabled)`
   - `transitioned Probing -> Ready (trigger: initial probe succeeded (resume: previously enabled))`
   - `transitioned Ready -> Mounting (trigger: persistent tier: auto-mount on becoming Ready)`
   - `transitioned Mounting -> Mounted (trigger: mount/mount succeeded)`

   It is also acceptable if the network was not up yet at the instant of wake: the
   first probe then fails (`Probing -> Unreachable (trigger: initial probe failed:
   ... (resume: previously enabled))`) and a later `network address changed` event
   or the backoff ladder brings the host to `Ready` and then `Mounted`.

   **Expected, route B (the mount survived the sleep without being unmounted):**
   instead of the `Disabled -> Probing` chain, either nothing (the forced shallow
   and deep probes on resume found the mount healthy, which is a pass), or
   `Forced deep probe failed for mounted host <key> immediately after resume` followed by
   `transitioned Mounted -> Draining (trigger: deep probe failed immediately after resume: SSH channel confirmed dead post-transition ...)`,
   then the same `Disabled -> Probing -> Ready -> Mounting -> Mounted` chain.
7. About a minute after the wake: run the drive check, read the marker back
   (`Get-Content "$Scratch\t1-before.txt"`), write a second one
   (`Set-Content "$Scratch\t1-after.txt" (Get-Date -Format o)`).
   **Expected:** the drive letter is either absent (still remounting) or the drive check prints
   `ANSWERED`. The drive check printing `NO ANSWER` while the letter is present is a failure.
   Ten minutes after the wake: window row `P: is mounted`, the drive check `ANSWERED`, marker intact.
8. Look for anything unexpected from the whole night with the warnings-and-errors
   command in the Log shared step.
   **Expected:** no `[ERR]` or `[CRT]` entries between the suspend and ten minutes
   after the wake; every `[WRN]` is explained by the chain above.

**PASS** when all of: Explorer stayed responsive (step 3); the suspend and resume
events are in the log (step 4); a minute after wake the drive was absent or
`ANSWERED` (step 7); ten minutes after wake the host was `Mounted`, the drive check `ANSWERED` and
the marker intact (step 7); no unexplained error (step 8).
**FAIL** on any of: Explorer hung; the letter present with `NO ANSWER` a minute after
wake; not `Mounted` ten minutes after wake while the test host was reachable; Bosun
no longer running. Copy the matching log lines into the results notes either way.

### T2 — Network interruption shorter than the unmount threshold  *(CLAUDE.md §7 item 5)*

The link is lost for less than `U = I × N` (180 s by default), then restored. The
interruption is too short to trip the shallow-failure threshold and long enough to
possibly kill the SSH channel under the mount. Use **30 s**. Run both variants.

**Variant A: the desktop's own link goes down.**

1. Test host `Mounted`; the drive check prints `ANSWERED`. Marker: `Set-Content "$Scratch\t2a-before.txt" (Get-Date -Format o)`.
2. Pull the desktop's network cable (or turn the adapter off in Settings > Network
   & internet). Note the time. Do not touch the drive.
3. Ten to fifteen seconds in, press Win+E, open *This PC*, then a local folder.
   **Expected:** Explorer responds throughout.
4. At 30 s, restore the link. Note the time.
5. Read what Bosun saw: `Get-BosunLog "$Key|network address|Forced deep probe"`.
   Two routes, depending on whether Windows raised an address-change event for
   your link loss. Record which one happened.

   **Route A1: Bosun saw the link go down** (what the design expects when your own link drops; not yet seen on this machine).
   **Expected:** shortly after the link drops (the event is debounced for 0.75 s,
   at most 5 s):
   - `System event: network address changed (settled after debounce, or capped)`
   - `Trigger: network address change event -- issuing NetworkChangedAsync`
   - `Forced deep probe failed for mounted host <key> immediately after network change (...)`
   - `transitioned Mounted -> Draining (trigger: deep probe failed immediately after network change: SSH channel confirmed dead post-transition ...)`
   - `Unmount confirmed for <key> ...`, then `Draining -> Disabled`

   The drive letter is gone within roughly 15 s of the link loss (debounce, a
   shallow probe and a deep probe, each bounded by `P`, then the unmount),
   with the tray notification `<display name> was unmounted ...`. **This is the
   design working, not a failure:** ADR-017 treats a failed deep probe right after a
   network change as conclusive, so the drive is taken down even though 30 s is
   below `U`. After the link returns, expect another `network address changed` line,
   then `Unreachable -> Ready (trigger: probe succeeded (network change: bypass backoff))`
   (or `Probing -> Ready`), `Ready -> Mounting (trigger: persistent tier: auto-mount on becoming Ready)`,
   `Mounting -> Mounted`.

   **Route A2: no network-change line at all** (Windows did not raise one).
   **Expected:** at most `Mounted probe failed for <key> (...): 1/3 consecutive failures`
   (or 2/3, never 3/3 if the interruption was under `U`), and no drain from the
   shallow probe. The mount then either kept working (pass) or has a dead channel
   under it, which only the deep probe can find: T7's rule applies, so it must be
   drained within `2D` of the link returning. Do not browse the drive while you wait.
6. Five minutes after the link returned: window row, then the drive check, then the marker.
   **Expected:** the row reads `P: is mounted`, the drive check prints `ANSWERED`,
   `t2a-before.txt` reads back intact.

**Variant B: the interruption is somewhere the desktop cannot see.** Pull the cable
between your switch and the test host (or the host's own cable), for 30 s, so the
desktop's link never drops.

7. Repeat steps 1, 3 and 4 with that cable (marker `t2b-before.txt`).
   **Expected:** no `network address changed` line (the desktop saw nothing). The
   shallow probe sees at most a failure or two: `Mounted probe failed for <key>
   (...): 1/3 consecutive failures`, then silence once the host answers again.
8. After the cable is back, wait up to `2D` (10 minutes by default), watching only
   the window row and the log. Then the drive check and the marker.
   **Expected, either of:** the mount kept working, so no further warnings and the drive check
   prints `ANSWERED`; **or** the channel died, so warnings
   `Deep probe failed for mounted host <key> (...): 1/2 consecutive deep-probe
   failures`, then `2/2`, then `Mounted -> Draining (trigger: 2 consecutive
   deep-probe failures while mounted ...)`, then a remount, all within `2D` of the
   cable coming back.

**PASS** when, for both variants: Explorer stayed responsive wherever you tested it;
no present-but-unanswering drive outlasted `2D` after the link returned; and five
minutes after the link returned (ten for variant B if a drain was needed) the host
was `Mounted`, the drive check `ANSWERED` and the marker intact. **FAIL** otherwise. Which of A1
or A2 happened, and whether B's mount survived, go in the notes: they are findings
about your network, not pass or fail.

### T3 — Host dies while mounted

1. Test host `Mounted`; the drive check prints `ANSWERED`. Power the host off, or pull the host's
   own network cable. (If the host is a NAS with its own web interface, a clean
   shutdown is fine. Do not unplug the desktop's cable: that is T2.) Note the time `t0`.
2. At about `t0 + 20 s` open *This PC* and a local folder, then run `dir C:\` in
   another window.
   **Expected:** responsive. This is the claim under test: while the host is gone and
   the drive is still listed, only things that touch the drive may wait.
3. Watch `Get-BosunLog "$Key"` (rerun it as you wait).
   **Expected:** on each mounted-probe interval, one more
   `Mounted probe failed for <key> (<outcome>): 1/3 consecutive failures`, then `2/3`,
   then `3/3`; then
   `transitioned Mounted -> Draining (trigger: 3 consecutive mounted-probe failures (threshold 3))`,
   `Unmount confirmed for <key> ...`, `Draining -> Disabled (trigger: drain completed: 3 consecutive ...)`,
   then `Disabled -> Probing (trigger: auto re-enable after drain)` and
   `Probing -> Unreachable (trigger: initial probe failed: ... (auto re-enable after drain))`.
   The tray shows the `<display name> was unmounted ...` notification. The window
   row reads `P: is not mounted -- host unreachable after N consecutive failed probes`.
   The drive letter is gone from the drive-type listing (see the Drive check shared step).
4. Note when the `Mounted -> Draining` line was logged and when the letter
   disappeared.
   **Expected:** `Draining` within `I × N + P` of `t0` (180 s + 5 s by default); the
   letter gone within about 15 s after that.
5. Power the host back on. Wait.
   **Expected:** a persistent host keeps polling on the backoff ladder
   (5, 15, 30, 60, then 300 s), so the drive is back within about 5 minutes of the
   host answering: `Unreachable -> Ready (trigger: probe succeeded (...))`,
   `Ready -> Mounting`, `Mounting -> Mounted`. Marker check:
   `Set-Content "$Scratch\t3-after.txt" (Get-Date -Format o)`.

**PASS** when step 2 stayed responsive, step 4's bound held, and step 5 brought the
drive back unaided within 5 minutes. **FAIL** if Explorer hung, the drive outlived
the bound, or no notification fired.

### T4 — Drag and drop

1. Make a throwaway file: `$f="$env:TEMP\bosun-t4.bin"; $b=New-Object byte[] 5MB; (New-Object Random).NextBytes($b); [IO.File]::WriteAllBytes($f,$b); (Get-FileHash $f).Hash`.
   Note the hash.
2. In Explorer, open `%TEMP%` and the scratch folder on the mounted drive side by
   side. Drag `bosun-t4.bin` into the scratch folder.
   **Expected:** the copy completes with no error dialog; the file appears at 5,242,880 bytes.
3. Make a local folder `%TEMP%\bosun-t4-back`. Drag the file from the scratch folder
   into it.
   **Expected:** completes; then
   `Get-FileHash "$Scratch\bosun-t4.bin", "$env:TEMP\bosun-t4-back\bosun-t4.bin"` shows the
   same hash as step 1 for both.
4. Editing in place (Invariant I6): in Notepad create `$Scratch\t4-note.txt`, type a
   line, save, close, reopen.
   **Expected:** saves without an error; the line is still there.
5. `Get-BosunLog 'transitioned'` for the period of the test.
   **Expected:** no new transition for the host: ordinary file work does not
   unmount anything.
6. Clean up: delete `bosun-t4.bin` and `t4-note.txt` from `$Scratch`, and the two
   local temp items.

**PASS** when both hashes match, the note saves, and step 5 shows no transition.
**FAIL** otherwise.

### T5 — Idle unmount

Needs an on-demand host: set `mode = "on-demand"` and `idle_unmount_seconds = 120`
for the test host (or a second host), restart Bosun. Then `T = 120` below.

1. Tray > the host > **Mount**. Note the time.
   **Expected:** `transitioned Ready -> Mounting (trigger: user/persistent mount request)`,
   `Mounting -> Mounted (trigger: mount/mount succeeded)`; the tray item for the host
   now offers **Unmount** and **Open in Explorer**.
2. Leave the drive completely alone, with no Explorer window on it, for `T` seconds.
   **Expected:** about `T` after the mount,
   `transitioned Mounted -> Draining (trigger: idle timeout (120s without activity))`,
   then `Unmount confirmed ...`, `Draining -> Disabled (trigger: drain completed: idle timeout ...)`,
   `Disabled -> Probing (trigger: auto re-enable after drain)`, `Probing -> Ready`.
   The tray shows a `<display name> was unmounted — idle timeout ...` notification
   (an idle unmount counts as unexpected for notification purposes). The window row
   reads `P: is not mounted -- on-demand; mount it from the tray when you need it`.
3. Check the letter is gone (drive-type listing in the drive check) and that **Mount** is offered again.

**PASS (T5)** when the unmount happens within about `T ± 15 s` of the mount and the
host ends in `Ready`, not `Unreachable` and not parked. **FAIL** otherwise.

**T5b: activity should postpone it (spec conformance; expected to FAIL today).**
`docs/ARCHITECTURE.md` §4 rule 7 says the idle unmount comes after the period
*without filesystem activity*. Reading the code (`MountSupervisor.ArmIdleUnmountTimer`;
`RecordActivityAsync` has no caller in production), the timer starts when the drive
is mounted and nothing restarts it, so it should fire `T` seconds after the mount
even while you are using the drive. To check:

4. Mount again. At about 80 s, write a file: `Set-Content "$Scratch\t5b.txt" (Get-Date -Format o)`.
   Wait to 130 s after the mount.
   **Expected by the spec:** still `Mounted` at 130 s, unmounting about 120 s after the
   write. **Expected from the code:** `Mounted -> Draining (trigger: idle timeout ...)` at
   about 120 s after the mount. Record which happened. If it is the second, that
   is a divergence between spec and code: report it, do not edit the Expected line.

### T6 — Crash recovery

`ADR-020` binds the `rclone.exe` that Bosun starts to Bosun's life with a Job
Object, so killing Bosun should take its rclone, and the drive letters, down with it.
The adopt-or-clear path (`startup: adopted existing mount`) is for an rcd that
survived, which the Job Object is meant to prevent.

1. Persistent host `Mounted`. Note the PIDs: `Get-Process Bosun,rclone | Select Id,ProcessName`.
2. Task Manager > Details > select `Bosun.exe` > **End task**. Note the time.
   **Expected:** within a few seconds `Get-Process rclone` finds nothing; the letter is
   gone from the drive-type listing in the drive check; Explorer stays responsive. An `rclone.exe`
   still running after 15 s is a failure of ADR-020 Decision 1: note its PID and stop.
3. Start Bosun again.
   **Expected:** `Using rclone from ...`, `rclone rcd is healthy on loopback port 5572`
   (your port), then the host goes through its normal chain to `Mounted`.
   `Get-BosunLog 'transitioned.*Mounting'` shows exactly one `Ready -> Mounting` for
   the host since the restart: no duplicate mount attempts. No
   `startup: adopted existing mount` line appears, because nothing survived.
4. The drive check prints `ANSWERED`; the window row reads `P: is mounted`.

**PASS** when no `rclone.exe` outlived Bosun, there are no orphaned drive letters, and
the restart mounted the host exactly once. **FAIL** otherwise. (If an adoption line
*does* appear, record it: it means rcd survived, which is itself worth reporting.)

### T7 — Dead mount on a live host: the deep probe catches it  *(ADR-016; bs-510)*

The TCP port stays reachable, so the shallow probe keeps passing, while the SSH
channel underneath the mount is dead. The recurring deep probe is the only thing that
can see this. Its worst case is `2D` (10 minutes with defaults): two consecutive deep
failures, one per `D`.

1. **Shorten the clock for the test.** Quit Bosun, set
   `mounted_deep_probe_interval_seconds = 60` in `[global]`, start Bosun. Now
   `2D = 120 s`. (Run it once at the default 300 afterwards if you want the 10-minute
   figure itself; it is the same procedure with a longer wait.)
2. Test host `Mounted`; the drive check prints `ANSWERED`. Wait one full `D` so a deep probe has run.
3. **Kill the channel while port 22 stays reachable.** Which method works depends on
   what the test host is, which this document cannot know. In order of preference:

   **Method A: block rclone only, on the desktop (needs an elevated PowerShell).**
   Bosun's shallow probe is a TCP connect from `Bosun.exe`; the mount's traffic comes
   from `rclone.exe`. A rule scoped to `rclone.exe` silences the channel and leaves
   the probe untouched:

   ```powershell
   $rc = (Get-Process rclone).Path
   New-NetFirewallRule -DisplayName 'Bosun protocol T7 (remove after the test)' -Direction Outbound -Action Block -Program $rc -Protocol TCP -RemoteAddress $H -RemotePort 22
   ```

   This models the nasty case: packets vanish and nothing resets the connection.
   Not verified here: whether Windows applies a new block rule to a flow that is
   already established. The evidence in step 5 tells you whether it did.

   **Method B: close the one connection, from Windows.** Sysinternals TCPView
   (`winget install Microsoft.Sysinternals.TCPView`), run as administrator: find
   the `rclone.exe` row to `<host>:22` and use *Close Connection*. This resets that one
   connection and leaves port 22 alone. Caveat: rclone's SFTP backend may reconnect
   by itself on the next operation, in which case the mount heals and there is nothing
   to detect (`VOID`, not `PASS`).

   **Method C: kill the session on the host.** Find rclone's local port:
   `Get-NetTCPConnection -RemoteAddress $H -RemotePort 22 -State Established | Where-Object { (Get-Process -Id $_.OwningProcess).ProcessName -eq 'rclone' } | Select LocalPort`.
   On the host, find the `sshd` process for that connection (`sudo ss -tnp 'sport = :22'`
   and match the client port) and kill that per-connection process, not the listening
   `sshd`: killing the listener drops port 22, which turns this into T3. Whether that
   is possible depends on the host: on a stock OpenSSH server the per-connection process
   is a child shown as `sshd: <user>@notty`; a NAS may run a different SSH server and
   it may not be possible at all. Restarting the SSH service usually does *not* end
   existing sessions, so it is not a way to do this. Same reconnect caveat as B.
4. Note the time `t0`. Do **not** open the drive in Explorer while you wait.
5. Confirm the signature. `Get-BosunLog "$Key|Mounted probe|Deep probe"`.
   **Expected:** after `t0` there are no `Mounted probe failed` lines (the shallow
   probe still passes; if there are, your method took port 22 down and this is a T3
   run, `VOID` for T7). Within `D` of `t0`:
   `Deep probe failed for mounted host <key> (<outcome>: <detail>): 1/2 consecutive deep-probe failures -- shallow TCP probes may still be succeeding ...`.
   No such line within `D` + a few seconds means the channel is not dead: `VOID`.
6. Wait for the second deep tick.
   **Expected:** `... 2/2 consecutive deep-probe failures ...`, then
   `transitioned Mounted -> Draining (trigger: 2 consecutive deep-probe failures while mounted (threshold 2): SSH channel confirmed dead (ADR-016))`,
   `Unmount confirmed for <key> ...`, `Draining -> Disabled`. The tray shows the
   `<display name> was unmounted ...` notification. The letter is gone from the
   drive-type listing (see the Drive check shared step).
7. Time it. **Expected:** the `Mounted -> Draining` line is between `D` and `2D` after
   `t0` (it cannot be sooner without a network-change or resume event, whose trigger
   text would then say `immediately after`). The documented bound is `2D`. Because each
   failing deep probe itself takes up to `P` seconds, accept `2D + 2P` before calling it a
   failure; if you need the allowance, note it, because the ADR states the bound
   without it.
8. Undo the cause (Method A: `Remove-NetFirewallRule -DisplayName 'Bosun protocol T7*'`,
   then check `Get-NetFirewallRule -DisplayName 'Bosun protocol T7*'` finds nothing; B and
   C need nothing). **Expected:** the host re-probes, `Mounting -> Mounted` returns, the drive check
   prints `ANSWERED`.
9. Restore `mounted_deep_probe_interval_seconds` (remove the line or set 300) and restart Bosun.

**PASS** when the drive was taken down by the deep probe (trigger text of step 6), at
`t0 + D` to `t0 + 2D` (with the step 7 allowance), Explorer stayed responsive wherever
you tested it, and step 8 brought the drive back. **FAIL** when the drive was still
present at `t0 + 2D + 2P` with the signature of step 5 in place. **VOID** when step 5's
signature never appeared.

### T8 — Short sleep: suspend budget, and nothing mounts while suspended  *(bs-bv0; Invariant I8)*

A daytime check of the same suspend path T1 uses, with a one-minute sleep.

1. Persistent host `Mounted`. Open an Explorer window on the mounted drive (the
   unmount has to get past it). Note the time. Start > Power > Sleep. Wait at least
   60 s. Wake.
2. `Get-BosunLog 'suspend event|transitioned|Unmount confirmed|resume event'`.
   **Expected:** the five suspend lines listed in T1 step 5, in that order. Subtract
   the timestamp of `issuing SuspendAsync` from that of `drain confirmed within the 3s
   budget`: it must be **under `B` (3 s)**. Write the figure down.
   A `the 3s suspend budget (Invariant I8) elapsed before the drain confirmed`
   warning is a failure of this test, whatever happens afterwards.
3. Nothing mounts while suspended. List the transitions between the `issuing
   SuspendAsync` line and the `Trigger: system resume event` line.
   **Expected:** only the drain (`Mounted -> Draining`, `Draining -> Disabled`). **No
   `-> Mounting` transition** between those two timestamps. If something
   tried, the good evidence is `Not mounting <key> (<trigger>): the system is
   suspended (Invariant I8); staying ...`.
   Limit of this test: it can only show that nothing mounted. It cannot make a mount
   attempt land in the seconds between the suspend notice and the machine actually
   sleeping, which is the case `bs-bv0` closes; that is covered by unit tests only.
4. After the wake: persistent host remounts.
   **Expected:** the resume chain of T1 step 6; the drive check `ANSWERED` within 2 minutes.
5. Optional second run, same checks, with a larger local copy in progress into
   `$Scratch` when you press Sleep (copy a throwaway file you made; delete it
   afterwards). It is the case most likely to stretch the unmount past `B`.

**PASS** when the budget held, no mount happened inside the suspend window, and the
remount followed. **FAIL** otherwise.

### T9 — An on-demand drive you mounted comes back after wake  *(ADR-022; bs-hrg, bs-ane)*

Needs an on-demand host (as in T5; set `idle_unmount_seconds = 0` so the idle timer
cannot interfere). Parts A to D are runnable; part E is not, as explained.

**Part A: it comes back.**

1. Tray > the host > **Mount**; wait for `Mounted`. Sleep for at least 60 s. Wake.
2. **Expected:** with no click from you, within about a minute of wake the log shows
   `transitioned Disabled -> Probing (trigger: resume: previously enabled)`,
   `Probing -> Ready (trigger: initial probe succeeded (resume: previously enabled))`,
   `Ready -> Mounting (trigger: remount after resume: user-mounted before suspend (ADR-022))`,
   `Mounting -> Mounted (trigger: mount/mount succeeded)`; the row reads `P: is mounted`;
   the drive check `ANSWERED`. The idle timer counts from this new mount.

**Part B: a drive that was not mounted does not appear.** Host on-demand and `Ready`
(not mounted). Sleep, wake. **Expected:** no `-> Mounting` line for the host; the row
reads `P: is not mounted -- on-demand; mount it from the tray when you need it`.

**Part C: a drive you unmounted stays unmounted.** Mount it, then tray > **Unmount**.
Sleep, wake. **Expected:** it stays unmounted; no `-> Mounting` for the host.

**Part D: if the first probe after wake fails, it is forgotten.**

3. Mount the host. Power the **host** off (do not unplug the desktop's cable: a network
   change would drain the mount by another cause first and the record would never be
   set). Within a minute, sleep. Wake with the host still off.
   **Expected:** `Probing -> Unreachable (trigger: initial probe failed: ... (resume: previously enabled))`
   and `Not remounting <key> after resume: its first post-resume probe failed (...); the
   record is dropped and the host stays unmounted until the user mounts it (ADR-022)`.
4. Power the host on and wait 3 minutes.
   **Expected:** it does **not** mount by itself: an on-demand host that is
   `Unreachable` is not polled, and the record is gone. A click on **Mount** mounts it
   normally.

**Part E: forgotten if rclone is not back within 10 minutes. Not runnable by hand as
things stand.** The record is kept only while something local is in the way (rcd not
answering `core/version` at the resume, or mounting unavailable), and it expires
`RemountRecordWaitLimit` (10 minutes) after the resume, with the log line
`Dropping the remount record for <key> (...): it waited 00:10:00 after the resume without
the obstacle clearing, so the host is not remounted automatically ...`. Nothing in the
code lets you hold rclone unavailable across a resume on purpose. Options, none verified:

- Freeze `rclone.exe` just before sleeping with Sysinternals `pssuspend`, wake, wait
  past 10 minutes, then resume it. Hazard: a frozen rcd holds a live WinFsp mount
  that Explorer can hang on, and the suspend unmount cannot complete against a
  frozen rcd (it would show up as the `B` budget warning). Not recommended on a
  machine you are using.
- Leave this part to the unit tests and record it `NOT RUN` with that reason.
- Ask for a fault-injection or shortened-limit switch in a debug build; that is a
  code change and a new issue.

**PASS (A to D)** when each part matches its Expected lines. **FAIL** otherwise.

### T10 — Network-change debounce is capped at 5 s  *(bs-u4a item 2)*

Windows raises `NetworkAddressChanged` several times for one physical change, and
Bosun coalesces them: it waits for 750 ms of quiet, but never longer than **5 s**
after the first event of a burst. Without the cap a link flapping faster than 750 ms
would defer the re-check for as long as the flapping lasts.

The log does not record the raw events, only the public one, so the test is: make a
sustained burst by hand and read the timestamps of the public events.

1. Open an **elevated** PowerShell. This loop adds and removes a throwaway
   documentation-range address (`192.0.2.77`, reserved for examples) on your active
   adapter about 40 times. It changes no route and nothing else. Run it only when
   you are content for Windows to raise address-change events for about 12 s:

   ```powershell
   $if = (Get-NetAdapter | Where-Object Status -eq 'Up' | Select-Object -First 1).ifIndex
   $start = Get-Date; 'start ' + $start.ToString('HH:mm:ss.fff')
   1..40 | ForEach-Object {
       New-NetIPAddress -InterfaceIndex $if -IPAddress 192.0.2.77 -PrefixLength 32 | Out-Null
       Remove-NetIPAddress -InterfaceIndex $if -IPAddress 192.0.2.77 -Confirm:$false
       Start-Sleep -Milliseconds 100
   }
   'end   ' + (Get-Date).ToString('HH:mm:ss.fff')
   ```

   Not verified here: that each add or remove raises its own event, or how long each
   takes. If the loop does not produce events closer together than 750 ms, the result
   is `VOID`, not `PASS`.
2. `Get-BosunLog 'network address change'` and compare with `start` and `end`.
   **Expected:** the first `System event: network address changed (settled after
   debounce, or capped)` line is stamped **between about 4.5 and 6.5 s after `start`**,
   while the loop is still running, and further such lines follow at roughly 5 s
   intervals until the loop ends (each fired burst starts a fresh 5 s), with a last one
   about 0.75 s after the final event. Each is followed by
   `Trigger: network address change event -- issuing NetworkChangedAsync`. A persistent
   host that is `Mounted` stays `Mounted` throughout (the forced probes pass).
3. Read the pattern.
   **Expected:** more than one line while the loop runs is the cap working. A line within
   1.5 s of `start`, followed by lines about a second apart, means the events were not a
   continuous burst: `VOID`.

**PASS** when a line appears at roughly 5 s after `start` and not only after `end`.
**FAIL** when nothing is logged until after `end` (the cap is not applied).

### T11 — The first mount reaches `Mounted` on the first try  *(bs-dtk)*

After `mount/mount` returns, Bosun reads the drive's type and keeps the mount only if
it is `Network`. The open question is whether the letter exists by the time rclone
answers: if not, the read says `NoRootDirectory` and the mount is torn down.

1. Note the time. For a persistent host: restart Bosun. For an on-demand host: tray >
   **Mount**.
2. `Get-BosunLog "$Key"` since that time.
   **Expected:** `Ready -> Mounting`, then `Mounting -> Mounted (trigger: mount/mount
   succeeded)`, with **no** `Mounting -> Draining` between them. The window row reads `P: is mounted`.
3. Look for the failure strings:
   `Get-BosunLog 'NoRootDirectory|presented as a|could not verify that|reports DriveType|Could not read the drive type'`.
   **Expected:** no output newer than step 1.
4. `[IO.DriveInfo]::GetDrives() | ForEach-Object { '{0} {1}' -f $_.Name, $_.DriveType }`.
   **Expected:** `P:\ Network`. In Explorer, *This PC* lists the drive under *Network
   locations*, not under *Devices and drives*.
5. Repeat the unmount and mount (tray > **Unmount**, then **Mount**) twice more, with
   steps 2 and 3 each time.

**PASS** when all three mounts reached `Mounted` the first time with none of the
step 3 strings. **FAIL** otherwise. A `NoRootDirectory` line is the specific finding
that `mount/mount` returns before the letter exists; report it with the log lines,
because it would mean the guard needs a short wait.

### T12 — The mount supervisor fails to start: red tray  *(bs-4js)*  — cannot be run as things stand

There is no switch in the product that makes the supervisor fail to start, and no
safe real cause: the only way found in the code is an exception out of
`MountSupervisor.StartAsync`. Options, none verified:

- A deliberate `throw` added to a scratch build. Run it only after quitting your
  real Bosun: it would otherwise share the real rc port, drive letters and Terminal
  fragment. With the supervisor failing to start nothing is probed or mounted, but
  the build still starts a real `rclone rcd`.
- Ask for a debug-only fault-injection switch (a new issue).
- Record `NOT RUN`.

What to check if the state is ever reached, by design:

1. The tray icon is **red**, not green or amber.
2. The window banner reads `The mount supervisor failed to start: <the real
   exception message>` and the host list is empty.
3. The log has `Failed to start the mount supervisor; no host will be probed or mounted
   this session` with the exception.
4. *Copy diagnostics* still works: `hosts.json` reads `unavailable: <reason>`, and
   `startup.json` and `summary.txt` carry the failure message (`supervisor: FAILED TO START`).
5. Nothing is mounted, nothing is retried. **Restart Bosun** from the banner restores normal operation.

### T13 — Bosun survives an exception on the UI thread  *(bs-twz)*  — cannot be run as things stand

Same position as T12: nothing in the product raises a dispatcher exception on demand.
Options: a throwaway `throw` in a window event handler in a scratch build (quit your
real Bosun first), a debug-only switch (a new issue), or `NOT RUN`. By design:

1. The process keeps running; the tray icon turns **red**; the banner reads `A UI error
   occurred; see the log`, naming the exception type and message and saying mount
   supervision is still running.
2. The log has a Critical entry: `Unhandled dispatcher exception (handled; 1 in the last
   60 s, 1 since start). Supervision continues; the tray icon is marked faulted.`
3. Supervision really continues: with a persistent host `Mounted`, T3's step 1 still takes
   the drive down within its bound.
4. The red state stays until Bosun is restarted; the banner offers **Restart Bosun** first.
5. The runaway guard: more than 5 such exceptions within 60 s makes the sixth
   un-handled; the process exits, and the log says `no longer handling them, so Bosun will exit`.

### T14 — Copy diagnostics works

1. Tray > **Copy diagnostics**, then again from the window's button.
   **Expected:** a dialog or Explorer window opens on a new
   `bosun-diagnostics-<yyyyMMdd-HHmmss>.zip` in `%LOCALAPPDATA%\Bosun\diagnostics\`
   each time (the second has a different name). No inline message box flashes and
   disappears.
2. Extract the zip into a scratch folder: `Expand-Archive <zip> -DestinationPath $env:TEMP\bosun-diag`.
   **Expected:** `summary.txt`, `health.json`, `startup.json`, `hosts.json`,
   `processes.json`, `drives.json`, `rclone-mounts.json`, `events.json`, `version.txt`, the
   config with secrets masked, and `logs\` with the last three days of logs.
3. Open `summary.txt`.
   **Expected:** a `DRIVES` line and a `RCLONE MOUNTS (n)` list that agree with each other
   (a mount marked `-- NOT among the logical drives` is a disagreement worth
   reporting); a `supervisor: running` line under `STARTUP`; either `COLLECTION
   PROBLEMS: none` or a list you can account for.
4. Secrets: `Select-String -Path (Get-ChildItem $env:TEMP\bosun-diag -Recurse -File).FullName -Pattern 'RCLONE_RC_PASS|rc-pass|BEGIN OPENSSH PRIVATE KEY'`.
   **Expected:** no private key block; any `RCLONE_RC_*` or `rc-pass` occurrence is
   followed by `***`, never a value.
5. `hosts.json` > `recentTransitions` is newest first and includes the transitions
   from your recent tests, with `from`, `to`, `trigger` and `drainCause`.
6. Delete `$env:TEMP\bosun-diag` (it holds your logs).

The failure path (a write failure shown through the window-owned prompt, `bs-6bc`) is
not provoked here. **PASS** when steps 1 to 5 match. **FAIL** otherwise.

### Results

Leave a cell empty until the run has happened. "Performed on" is the date you ran it;
"Result" is `PASS`, `FAIL`, `VOID` (could not be performed validly) or `NOT RUN`
(with the reason in Notes). Put the log lines that justify the result in Evidence or in
the notes beside it.

| Test | Performed on | Bosun commit | Result | Evidence (log lines, bundle path) | Notes |
|---|---|---|---|---|---|
| T1 Overnight sleep, host reachable | | | | | |
| T2 Network interruption, variant A | | | | | |
| T2 Network interruption, variant B | | | | | |
| T3 Host dies while mounted | | | | | |
| T4 Drag and drop | | | | | |
| T5 Idle unmount | | | | | |
| T5b Activity postpones idle unmount | | | | | |
| T6 Crash recovery | | | | | |
| T7 Dead mount, deep probe | | | | | |
| T8 Short sleep: budget, no mount while suspended | | | | | |
| T9 On-demand drive after wake (parts A to D) | | | | | |
| T9 part E, 10-minute expiry | | | | | |
| T10 Network-change debounce cap | | | | | |
| T11 First mount, drive type | | | | | |
| T12 Supervisor start failure, red tray | | | | | |
| T13 UI-thread exception survived | | | | | |
| T14 Copy diagnostics | | | | | |

`CLAUDE.md` §7 items 4 and 5 are T1 and T2. The ADR-020 recovery paths in `bs-e90`
that are not repeated here (an orphaned rcd, the health banner and Repair buttons,
Restart Bosun from the banner, bounded exit) are checked under that issue; its item 6 is
T1 and T2 and its drive-type note is T11.

### Reading the transition log

The log is the artifact that explains a result after the fact. Two places hold the
transitions:

- **The log files**, `%LOCALAPPDATA%\Bosun\logs\bosun-yyyyMMdd.log`. Complete and
  durable (see *Logs* for rolling and size). Every state change is one entry:

  ```
  2026-11-02 07:00:01.000 -05:00 [INF] Bosun.Supervisor.MountSupervisor
  Host <key> transitioned <From> -> <To> (trigger: <text>)
  ```

  The first line is time, level and source; the second is the message. Use the `Get-BosunLog`
  helper so the time stays attached to the message.
- **The diagnostics bundle**, `hosts.json` > `recentTransitions`: the supervisor's own
  in-memory history, **newest first, the last 300 entries, lost when Bosun restarts**. Each
  entry has `timestampUtc`, `hostKey`, `from`, `to`, `trigger` and `drainCause`. Use it
  when the log is not at hand; for anything older than the current Bosun run, use the files.
  (The window does not list transitions; its row shows only the present state.)

**How to read it.** The states are `Disabled`, `Probing`, `Ready`, `Unreachable`, `Mounting`,
`Mounted`, `Draining` (`docs/ARCHITECTURE.md` §4). The **trigger** is the part that says
why, so read it before the arrow. A healthy host that stays healthy writes almost
nothing: recurring probes run in place and log no transition (rule 10), so a transition
means something changed. Recipe for explaining any result:

1. Find the event that started it, in the system-event lines below, and note its time.
2. Follow that host's transitions from that time. Compare with the Expected chain in the test.
3. Look at `[WRN]` and `[ERR]` entries in the same minute: they carry the probe outcome and detail.
4. A trigger ending in `(<something>)` names the cause that caused the probe or drain.

**System-event lines** (not transitions):

| Line | Meaning |
|---|---|
| `System event: power suspend` / `power resume` | Windows told Bosun the machine is going to sleep / has woken. |
| `Trigger: system suspend event -- issuing SuspendAsync (Invariant I8 budget: 3s)` | The unmount-everything drain starts, with the budget. |
| `... drain confirmed within the 3s budget` | The drain finished in time. |
| `... the 3s suspend budget (Invariant I8) elapsed before the drain confirmed ...` | It did not; resume's re-check is the recovery. |
| `Trigger: system resume event -- issuing ResumeAsync` | Resume handling starts: reconcile with rclone, force-probe, re-enable. |
| `System event: network address changed (settled after debounce, or capped)` | One debounced network change (quiet for 750 ms, or 5 s after the first raw event). |
| `Trigger: network address change event -- issuing NetworkChangedAsync` | Same handling as resume's re-check. |
| `Re-derivation after <trigger> skipped: rclone rcd did not answer core/version ...` | rclone was not answering, so nothing was reconciled or drained that time. |
| `Mounted probe failed for <key> (<outcome>): n/N consecutive failures` | A shallow (TCP) probe of a mounted host failed. |
| `Deep probe failed for mounted host <key> (...): n/2 consecutive deep-probe failures ...` | The SFTP-level probe of a mounted host failed (ADR-016). |
| `Forced deep probe failed for mounted host <key> immediately after <resume or network change> ...` | The same probe forced by a transition; one failure is enough (ADR-017). |
| `Not mounting <key> (...): the system is suspended (Invariant I8) ...` | A mount was held back because the machine is suspending. |

**Triggers on a transition** (`<x>` is filled in at run time; wording may change, so the
code's typed `drainCause` in `hosts.json` is the reliable field for scripts):

| Transition | Trigger text | Meaning |
|---|---|---|
| `Disabled -> Probing` | `startup` | Bosun started. |
| | `resume: previously enabled` | Waking after the suspend drain. |
| | `auto re-enable after drain` | A drain finished; probing again. |
| | `mount-retry backoff elapsed` | Retrying a failed mount, paced. |
| | `repair: re-probe after unmount all`, `repair: re-probe` | A Repair action. |
| | `config change: <x>` | `hosts.toml` changed. |
| `Probing -> Ready` / `Unreachable` | `initial probe succeeded (<why>)` / `initial probe failed: <outcome> (<why>)` | The one-shot probe after enabling. |
| `Unreachable -> Ready`, `Ready -> Unreachable` | `probe succeeded (<why>)` / `probe failed: <outcome> (<why>)` | A recurring probe changed the result. `<why>` is `periodic idle probe`, `network change: bypass backoff`, `resume: bypass backoff`, `user retry now`, or `user mount request: immediate probe (ADR-014 rule 8)`. |
| `Ready -> Mounting` | `persistent tier: auto-mount on becoming Ready` | A persistent host mounts itself. |
| | `user/persistent mount request` | A tray or window Mount. |
| | `remount after resume: user-mounted before suspend (ADR-022)` | An on-demand drive returning after wake. |
| | `resume: persistent host held back while suspended`, `mounting became available` | A mount that was held back is now allowed. |
| `Mounting -> Mounted` | `mount/mount succeeded` | The mount worked and read back as a network drive. |
| `Mounting -> Draining` | `deep probe failed entering Mounting: <detail>`, `mount/mount failed: <x>`, `... is presented as a <type> drive ...`, `... does not exist (drive type NoRootDirectory) ...`, `could not verify that <drive> is a network drive ...` | The mount was refused or torn down (Invariants I1 and I7). |
| `Mounted -> Draining` | `system suspend` | I8: unmount before sleep. |
| | `user unmount` | You asked. The host is parked and will not remount by itself. |
| | `<n> consecutive mounted-probe failures (threshold <N>)` | The host stopped answering (T3). |
| | `<n> consecutive deep-probe failures while mounted (threshold 2): SSH channel confirmed dead (ADR-016)` | T7. |
| | `deep probe failed immediately after <resume or network change>: SSH channel confirmed dead post-transition (ADR-017 ...)` | T1 route B, T2 variant A. |
| | `idle timeout (<n>s without activity)` | T5. |
| | `reconciliation: mount missing from listmounts` | rclone no longer lists a mount Bosun believed in. |
| | `repair: unmount all and re-probe`, `config change: <x>` | A Repair, or a `hosts.toml` change. |
| `Draining -> Disabled` | `drain completed: <the drain's reason>` | The unmount was confirmed. |
| `Disabled -> Mounted` | `startup: adopted existing mount at <drive> ... crash recovery` | Bosun found a surviving mount of the right host at start (T6). |

A worked shape, invented for illustration (not output from a run): a drive that
vanished after a link drop reads as `Mounted -> Draining (trigger: deep probe failed
immediately after network change ...)`, `Draining -> Disabled (trigger: drain
completed: ...)`, `Disabled -> Probing (trigger: auto re-enable after drain)`,
`Probing -> Unreachable (trigger: initial probe failed: ...)`, and later, after the
link is back, `Unreachable -> Ready (trigger: probe succeeded (network change:
bypass backoff))`, `Ready -> Mounting`, `Mounting -> Mounted`. Read it as: network
event, drive taken down on purpose, host unreachable while the link was out, host
back, drive back.

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
| Banner says "rclone was not found" | The message lists every place Bosun looked (PATH first, then the install locations in *Where Bosun looks for rclone*). If rclone is installed somewhere not on that list, add it to `PATH` and sign out and back in. If it was only just installed to a listed place, press **Restart rclone** in the banner (or restart Bosun). Mounting is off until rclone is found; terminal profiles and SSH sessions keep working. The log shows `Using rclone from <source>: <path>` once it is found. |
| Drive never appears | Deep probe failing. Try `rclone lsd <remote>:` manually. |
| Drive appears then vanishes | Expected on probe failure — check whether the host is genuinely reachable. If the host row says `mount failed ...; last error: P: is presented as a Fixed drive instead of a network drive`, the drive appeared and Bosun unmounted it on purpose: rclone mounted it but did not honour `--network-mode` (Invariant I7), which would make Explorer misbehave. Check the rclone version and its `mount/mount` handling of `network_mode`; Bosun retries on the mount-retry ladder, so the drive may flash up again until the cause is fixed. Other reasons seen here: `does not exist (drive type NoRootDirectory)` (rclone reported success but no drive letter is there) and `could not verify that P: is a network drive` (the drive type could not be read; the message carries the exception). |
| Terminal profiles missing | Terminal reports **nothing** when a fragment fails to parse — no toast, no UI surface, no default log entry; it skips that fragment and loads the rest. Profiles appearing in the dropdown is the only confirmation available. Check `%LOCALAPPDATA%\Microsoft\Windows Terminal\Fragments\Bosun\bosun.json` exists and is valid JSON, then restart Terminal. Bosun's own log records each write. (The Store-vs-unpackaged path distinction is a red herring — see ADR-006's amendment.) |
| Profile opens but the connection fails | Bosun connects with the host's own `hostname`, `port`, `user` and `identity_file` (ADR-013 Amendment 1), so check those first. Reproduce exactly what the profile runs: `ssh -i "<identity_file>" -p <port> <user>@<hostname>`. `Could not resolve hostname` means the `hostname` field is wrong, not that an `ssh_config` alias is missing — none is needed. If a Terminal profile still runs the old `ssh <host-key>` form, its fragment predates the amendment; restart Bosun to rewrite it. |
| `ssh: no key found` / `failed to parse private key file` | `identity_file` points at a **public** key (`.pub`) or a PuTTY `.ppk`. rclone and ssh both need the OpenSSH-format private half. Convert a `.ppk` with PuTTYgen → *Conversions* → *Export OpenSSH key*; export from Bitvise via *Client key manager* → *Export* → OpenSSH format. |
| Profile lost its colours or font after a rename | Terminal derives profile identity from the GUID, and Bosun derives that GUID from the host's **config key**, not `display_name` (ADR-013). Renaming `display_name` should be safe; renaming the TOML key is what creates a new profile. |
| Terminal profiles duplicated | Something wrote to `settings.json`. Invariant I5 violation. |
| Files fail to save from an editor | `vfs_cache_mode` below `writes`. Validation should have caught this. |
| The health banner is showing, or a drive is stuck | Use the repair buttons in the banner, or **Repair** in the tray menu (ADR-020). *Restart rclone* replaces `rclone rcd` and reconciles every host. *Unmount all & re-probe* drops every mounted drive and brings persistent hosts back after a fresh check (it does not park hosts or un-park ones you unmounted). *Restart Bosun* starts a fresh Bosun and shows its window; it does not count against the watchdog's restart limit. Each asks first if a drive would disconnect. Use **Copy diagnostics** before restarting if you want to keep the evidence. |
| Red tray icon and a banner reading **The mount supervisor failed to start: …** | Nothing is being probed or mounted this session, and nothing will retry. The text after the colon is the real exception message (the tooltip may cut it short; the banner shows it whole). Typical cause: a startup dependency the program could not build, which is a Bosun bug rather than something wrong with your hosts. Find the full exception in the log (search for `Failed to start the mount supervisor`), then **Restart Bosun**. Terminal profiles still work. The window will list no hosts. **Copy diagnostics** still works: `hosts.json` reads `unavailable: <reason>`, and `startup.json` and `summary.txt` carry the supervisor's failure message. |
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
the last three days, rolled `_001` files included. It also holds `summary.txt` (open
this first), the health issues, `startup.json` (config state and errors, WinFsp, rclone
and the supervisor's own start failure), `hosts.json`, the rclone processes and the rc
port's owner, `drives.json`, `rclone-mounts.json` (rclone's own list of mounts from
`mount/listmounts`, to compare with `drives.json`; only asked when rclone answers),
`events.json`, `version.txt`, the config with secrets masked, and
`bootstrap-failures.log` (the last 64 KiB of `%TEMP%\Bosun\bootstrap-failures.log`, only if
it exists). A section that cannot be collected says why and the rest are still written.
If any could not be, the dialog after the bundle opens says "Diagnostics bundle written with N
collection problems" and lists the first few; `summary.txt` lists them all. A section that is
`unavailable` because the supervisor or rclone is down is the state being diagnosed, not a
collection problem.
