# E13a spike: observe real hook and Terminal behaviour (bs-vqh.1)

Throwaway tooling for the assumptions listed at the end of ADR-021 (`docs/DECISIONS.md`). It is not
part of `Bosun.sln` and nothing here ships. Delete the folder when the spike is done.

**How this works.** You perform the steps and run the tools. The tools write logs. The agent reads the
logs and decides pass or fail. So when a step says "done", it means only that you did it. You do not
judge the result. Each step has an **Expected** line saying what you should see on screen and what to
send back.

## What this touches on your machine

- Writes logs to `%LOCALAPPDATA%\BosunSpike\` (`hooks.jsonl`, `session-ending.jsonl`, `wt-race.jsonl`).
  Your prompt text is **not** logged (the logger replaces it with its length). Folder paths and session
  IDs are logged.
- Steps 1.x to 2.x load the plugin for one session with `--plugin-dir`. Nothing is installed.
- Step 3.x installs the plugin into your Claude Code user configuration, then removes it (3.5). Do 3.5
  as soon as 3.4 is done.
- Step 1.4 may need Remote Control auto-connect on. Note its current value first so you can put it back.
- The tools never run Bosun, never mount anything, and never touch `settings.json` themselves.
- If anything looks wrong (hook errors on screen, a session misbehaving), stop and go to **Cleanup**.

## Contents

| Folder or file | What it is |
|---|---|
| `HookLogger/` | The hook. Appends one JSON line per hook run. `--mark "text"` adds a marker line. `--report` summarises the log per assumption. |
| `plugin/` | The plugin to load with `--plugin-dir`. Its `.claude-plugin/` folder at `spikes/e13a/` makes the same files a one-plugin marketplace named `bosun-e13a`. |
| `plugin-slowend/` | A second plugin with one deliberately slow `SessionEnd` hook (assumption 11). |
| `SessionEndingProbe/` | Console app for assumption 8. `--report` summarises its log. |
| `wt-race.ps1` | Assumption 7. |
| `build.ps1`, `set-plugin-tag.ps1` | Helpers. |

## Part 0. Setup

Open a Terminal tab. Set these variables in **every new tab** you use (edit the first line):

```powershell
$S = "C:\path\to\bosun\spikes\e13a"
$L = "$S\plugin\helper\HookLogger.exe"
```

### 0.1 Build

```powershell
pwsh -File "$S\build.ps1"
```

**Expected:** the last lines read `Build OK.` followed by three paths. `$L` exists
(`Test-Path $L` prints `True`). Report back: `done`, or the error text if the build failed.

### 0.2 Validate the plugins (read-only check)

```powershell
claude plugin validate "$S\plugin"
claude plugin validate "$S\plugin-slowend"
claude plugin validate "$S"
```

**Expected:** each ends with `Validation passed` (a warning about a missing field is fine). Report back:
the full output of the three commands, especially any line starting `Validation failed` or a warning
about the hooks.

### 0.3 Check your Claude install type and Fast Startup

```powershell
Get-Command claude -All | Format-Table CommandType, Source -AutoSize
reg query "HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power" /v HiberbootEnabled
claude --version
```

**Expected:** the first command lists where `claude` comes from. A native install shows `claude.exe`
(for example under `~\.local\bin`). An npm install shows `claude.cmd` or `claude.ps1`. `HiberbootEnabled`
is `0x1` if Fast Startup is on (needed for step 5.3) and `0x0` if off. Report back: all three outputs.

## Part 1. Everyday behaviour (assumptions 2, 3, 4, 5, 10, 11)

In each step, open the Claude session in a **new Terminal tab** so the chain to `WindowsTerminal.exe` is
the normal one.

### 1.1 Start a session, use it, then `/clear`

```powershell
& $L --mark "1.1 start, prompt, clear"
claude --plugin-dir "$S\plugin"
```

In Claude: type `say hi`, wait for the reply, type `/clear`, then type `say hi again`. Leave the session
open.

**Expected:** Claude behaves normally. No hook error text appears on screen. In another tab,
`& $L --report` shows, under `1.1`: a `SessionStart source=startup`, a `UserPromptSubmit`, a
`SessionEnd reason=clear`, and a second `SessionStart source=clear`. Sections 2, 3 and 4 of the report
list `WT_SESSION=present`, a chain ending in `WindowsTerminal.exe`, and a hook parent of `claude.exe`
(or `node.exe` for npm). Report back: `done`, plus anything that looked different on screen.

### 1.2 Three ways to end a session

Do this three times, each in a new tab, with a different ending. Mark each one first.

```powershell
& $L --mark "1.2a /exit"
claude --plugin-dir "$S\plugin"      # type: say hi    then: /exit
& $L --mark "1.2b Ctrl+D"
claude --plugin-dir "$S\plugin"      # type: say hi    then: press Ctrl+D on an empty prompt
& $L --mark "1.2c Ctrl+C twice"
claude --plugin-dir "$S\plugin"      # type: say hi    then: press Ctrl+C twice
```

**Expected:** each session exits back to the shell prompt within about two seconds, with no error text.
In the report, each ending has a `SessionEnd` with a `reason`, and `terminal_alive_at_session_end=true`.
Report back: `done`, and whether any exit felt slow or showed text.

### 1.3 Does `--resume` keep the session id (assumption 10)

```powershell
& $L --mark "1.3 resume"
& $L --report | Select-String 'session_id='
```

Copy a session id from the 1.2a session. Then:

```powershell
claude --plugin-dir "$S\plugin" --resume <that-id>
```

In Claude: type `say hi`, then `/exit`.

**Expected:** Claude reopens the earlier conversation (you see the earlier messages). In the report,
section "ASSUMPTION 10" shows `source=resume` for the new start. Report back: `done`, and whether the
earlier messages were shown.

### 1.4 Remote Control on resume (assumption 5)

Skip this step if you do not use Remote Control. Otherwise:

1. In Claude run `/config` and note "Enable Remote Control for all sessions". If it is not `true`, set it
   to `true` for this step and write down the original value.
2. `& $L --mark "1.4 remote control"`, then `claude --plugin-dir "$S\plugin"`. Type `say hi`. Check your
   phone or claude.ai/code: this session should be listed.
3. Type `/exit`. Then `claude --plugin-dir "$S\plugin" --resume <id of this session>` in a new tab.
4. On the phone or claude.ai/code, send a message to the resumed session.

**Expected:** after step 3 the resumed session shows a Remote Control connected indicator in its header
or status line, and the phone/app lists it (the same entry or a new one; note which). The message you
send from the phone appears in the terminal and Claude replies. If instead you see "Previous session is
unavailable", report that text exactly. Report back: what you saw in each of steps 2 and 4, and whether
it was the same or a new entry on the phone. Put the setting back to its original value afterwards.

### 1.5 SessionEnd time budget (assumption 11)

```powershell
& $L --mark "1.5a slow SessionEnd"
claude --plugin-dir "$S\plugin" --plugin-dir "$S\plugin-slowend"
```

In Claude: type `say hi`, then `/exit`. Count roughly how many seconds the tab takes to return to the
prompt.

Then repeat with a longer allowed time:

```powershell
& $L --mark "1.5b slow SessionEnd, env budget 6000"
$env:CLAUDE_CODE_SESSIONEND_HOOKS_TIMEOUT_MS = "6000"
claude --plugin-dir "$S\plugin" --plugin-dir "$S\plugin-slowend"
# say hi, then /exit
Remove-Item Env:CLAUDE_CODE_SESSIONEND_HOOKS_TIMEOUT_MS
```

**Expected:** the slow hook sleeps 4 seconds and logs a line every 0.25 s. The report's last section
lists those lines. If Claude Code cancels it at about 1.5 s, the lines stop near `slept_ms=1500` with no
final record. In 1.5b they should run to `slept_ms=4000`. Report back: the two rough exit times you
counted. The agent reads the cut-off point from the log.

## Part 2. Closing Terminal different ways (assumptions 1, 3)

Save your other work first: 2.3 closes every Terminal window. For each step below, start from a fresh
window.

Setup for 2.1 and 2.2: open one new Terminal window with three tabs. In each tab run
`claude --plugin-dir "$S\plugin"` and type `say hi`. Keep one extra tab free for the report.

### 2.1 Close one tab with its X

```powershell
& $L --mark "2.1 close tab with X"
```

Click the X on one of the three Claude tabs. Wait 10 seconds. Then `& $L --report` in the free tab.

**Expected:** the tab closes at once. Report back: `done`, and whether Terminal showed any
confirmation question. In the report, that session's claude process has a `SessionEnd` or it does not;
either way the agent reads which.

### 2.2 Close the whole window with its X

```powershell
& $L --mark "2.2 close window with X"
```

Click the X on the window's title bar (the window still holding two Claude tabs and the free tab).
Confirm if Terminal asks. Wait 10 seconds, then open a new tab elsewhere and run `& $L --report`.

**Expected:** the window closes. Report back: `done`, and whether Terminal asked "close all tabs?".

### 2.3 Kill `WindowsTerminal.exe` in Task Manager

Open a new window with two Claude tabs (as above) and a free tab. Then:

```powershell
& $L --mark "2.3 kill WindowsTerminal.exe"
```

Open Task Manager, **Details** tab, right-click `WindowsTerminal.exe`, choose **End task**. This closes
every Terminal window. Wait 10 seconds, open a new Terminal, and run `& $L --report`.

**Expected:** all Terminal windows vanish at once. Report back: `done`.

### 2.4 Elevated window (only if you ever run Terminal as administrator)

Skip if you never do. Otherwise: right-click Terminal, **Run as administrator**, accept the prompt, then:

```powershell
& $L --mark "2.4 elevated"   # the variables $S and $L must be set in this admin tab too
claude --plugin-dir "$S\plugin"    # type: say hi    then: /exit
```

**Expected:** Claude starts and exits normally. In the report, that start shows `elevated=True`, a chain
ending in `WindowsTerminal.exe`, and an image path for it (or `open_error_win32=5` if the path could
not be read). Report back: `done` and whether Claude started normally.

### 2.5 npm install (only if 0.3 showed `claude.cmd`)

Skip if you have only the native `claude.exe`. Do not install the npm version for this test. Otherwise:

```powershell
& $L --mark "2.5 npm claude"
claude --plugin-dir "$S\plugin"    # type: say hi    then: /exit   (this uses the claude.cmd shim)
```

**Expected:** Claude starts and exits normally. The report shows a chain with `cmd.exe` and `node.exe`
before `WindowsTerminal.exe`, and the hook's parent as `node.exe`. Report back: `done`.

## Part 3. Does the plugin load in place or from a copy (assumption 6)

The plugin must be installed for this. It runs on every Claude Code session you start until you finish
3.5.

### 3.1 Install

```powershell
& $L --mark "3.1 install"
claude plugin marketplace add "$S"
claude plugin install bosun-e13a-spike@bosun-e13a
claude plugin list
```

**Expected:** `Successfully added marketplace: bosun-e13a`, then `Successfully installed plugin:
bosun-e13a-spike@bosun-e13a (scope: user)`, and `claude plugin list` shows it as enabled. Report back:
the output of the three commands.

### 3.2 Start a session with no flag

```powershell
& $L --mark "3.2 installed plugin session A"
claude
```

In Claude type `say hi`. **Leave this session A running.** In another tab: `& $L --report`.

**Expected:** a `SessionStart` for session A with `plugin_tag=v1`. Section "ASSUMPTION 6" says either
`NOT in the plugin cache (in place)` or `PLUGIN CACHE COPY`, and shows the `CLAUDE_PLUGIN_ROOT` path.
Report back: `done`.

### 3.3 Edit the plugin while session A runs

```powershell
& $L --mark "3.3 edit tag to v2"
pwsh -File "$S\set-plugin-tag.ps1" -Tag v2
```

In session A type `say hi again`. Then open a new tab and start session B: `claude`, type `say hi`, and
`/exit`.

**Expected:** the script prints `hooks.json now uses --plugin-tag v2`. The report shows the plugin tag
that session A's second prompt logged and the tag session B logged. Report back: `done`.

### 3.4 If session B still logged `v1`, update the plugin

Do this only if the report shows session B with `plugin_tag=v1`. Otherwise skip.

```powershell
& $L --mark "3.4 update"
pwsh -File "$S\set-plugin-tag.ps1" -Tag v3 -Version 0.0.2
claude plugin update bosun-e13a-spike@bosun-e13a
```

Start a new session `claude`, type `say hi`, and `/exit`. In the old session A, run `/reload-plugins`
and type `say hi`.

**Expected:** `claude plugin update` prints a result line (updated, or already at the latest version).
The report shows which tag the new session and session A (after `/reload-plugins`) logged. Report back:
the update command's output.

### 3.5 Uninstall now

Exit session A with `/exit`, then:

```powershell
claude plugin uninstall bosun-e13a-spike@bosun-e13a
claude plugin marketplace remove bosun-e13a
claude plugin list
claude plugin marketplace list
```

**Expected:** `Successfully uninstalled plugin`, a line confirming the marketplace was removed, and
neither `bosun-e13a` nor `bosun-e13a-spike` in the two listings. Report back: the output of the four
commands. Then reset the tag so the repo stays clean: `pwsh -File "$S\set-plugin-tag.ps1" -Tag v1 -Version 0.0.1`.

## Part 4. The `wt.exe -w 0` race (assumption 7)

`wt-race.ps1` waits for a **new** `WindowsTerminal.exe`. When one appears it immediately runs
`wt.exe -w 0 new-tab --title E13A-PROBE cmd /k echo probe ...` and logs what happened. It does nothing
until you open Terminal.

It must run outside Terminal, because you will close every Terminal window. A `conhost.exe` window is
a plain console that does not belong to Terminal:

```powershell
Start-Process conhost.exe -ArgumentList 'powershell.exe','-NoExit','-File',"`"$S\wt-race.ps1`""
```

If no console window opens, tell me, and use Win+R instead:
`conhost.exe powershell.exe -NoExit -File "<full path to wt-race.ps1>"`.

### 4.1 Run the race

1. The console window prints `Waiting for a NEW WindowsTerminal.exe`.
2. Close **all** Terminal windows, so `WindowsTerminal.exe` exits.
3. Wait about 5 seconds. Then open Terminal from the Start menu, as you normally would.

**Expected:** the console window prints `Detected Terminal pid ... and ran wt.exe`, then `Probe tab
found`. On screen, the Terminal window you just opened holds two tabs: your normal one and one titled
`E13A-PROBE` that shows `probe`. Report back: which window the `E13A-PROBE` tab appeared in (the new
one, an older one, or a brand-new third window), and whether an extra empty window appeared. Then send
`%LOCALAPPDATA%\BosunSpike\wt-race.jsonl`.

Repeat 4.1 two more times with the machine idle, then once with something busy running (for example a
large build), and tell me which run was which. Close the `E13A-PROBE` tabs and the console window
afterwards.

## Part 5. Logoff, restart, shutdown (assumptions 1 and 8)

Each step ends your Windows session. Save your work first. The probe records `SessionEnding` against the
life of `WindowsTerminal.exe`, `claude.exe` and `node.exe`. It is started outside Terminal, in the same
`conhost.exe` way as Part 4.

Before each step:

1. Open a Terminal window with two Claude tabs: `claude --plugin-dir "$S\plugin"`, type `say hi` in each.
2. Start the probe and leave its window open:

```powershell
Start-Process conhost.exe -ArgumentList "`"$S\SessionEndingProbe\bin\publish\SessionEndingProbe.exe`""
```

3. `& $L --mark "<the step name below>"`.

After you are back at your desktop, in a **new** Terminal tab, run
`claude --plugin-dir "$S\plugin"`, type `say hi`, `/exit` (this records the new logon session), then:

```powershell
& $L --report
& "$S\SessionEndingProbe\bin\publish\SessionEndingProbe.exe" --report
```

### 5.1 Sign out

Start menu, your name, **Sign out**. Sign back in.

**Expected:** all windows close. Windows may briefly say an app is preventing sign-out; note it if so.
After sign-in, the report's "LOGON SESSIONS" section lists a different `luid` and `logon_time` for the
new session than for the sessions that were running before.
Report back: the two report outputs, and anything Windows showed while signing out.

### 5.2 Restart

Start menu, power button, **Restart**. Sign in. Then the same follow-up commands.

**Expected:** as 5.1. Report back: the two report outputs.

### 5.3 Shut down with Fast Startup on

Only meaningful if 0.3 showed `HiberbootEnabled 0x1`. If it showed `0x0`, still do it and tell me.
Start menu, power button, **Shut down**. Wait 30 seconds. Power on, sign in. Then the same follow-up
commands.

**Expected:** as 5.1. Report back: the two report outputs, and the `HiberbootEnabled` value you saw in
0.3.

## Part 6. Send results back, then clean up

### Send back

```powershell
& $L --report > "$env:USERPROFILE\Desktop\e13a-report.txt"
& "$S\SessionEndingProbe\bin\publish\SessionEndingProbe.exe" --report > "$env:USERPROFILE\Desktop\e13a-probe-report.txt"
Copy-Item "$env:LOCALAPPDATA\BosunSpike\*.jsonl" "$env:USERPROFILE\Desktop\"
```

Give the agent the files from the Desktop plus your **Expected / report back** notes for each step. The
logs hold folder paths and session ids but not your prompt text.

### Cleanup

```powershell
claude plugin uninstall bosun-e13a-spike@bosun-e13a      # fails harmlessly if 3.5 already did it
claude plugin marketplace remove bosun-e13a              # fails harmlessly if 3.5 already did it
claude plugin list
claude plugin marketplace list
Remove-Item -Recurse -Force "$env:LOCALAPPDATA\BosunSpike"
```

**Expected:** neither listing mentions `bosun-e13a`. `Test-Path "$env:LOCALAPPDATA\BosunSpike"` prints
`False`. Also close any leftover `conhost` console windows from Parts 4 and 5, and restore the Remote
Control setting if you changed it in 1.4. Report back: `done`.

## What the Claude Code docs say (read 2026-11-01)

| Topic | Source |
|---|---|
| Hook events, payload, exec form, timeouts, SessionEnd budget | <https://code.claude.com/docs/en/hooks> |
| Plugin manifest, `${CLAUDE_PLUGIN_ROOT}`, name rules | <https://code.claude.com/docs/en/plugins-reference> |
| In-place vs cached plugins, versions and updates | <https://code.claude.com/docs/en/plugins/loading.md> |
| `claude plugin install / uninstall / update / marketplace` | <https://code.claude.com/docs/en/plugins/cli-reference.md> |
| Local marketplace walkthrough | <https://code.claude.com/docs/en/plugin-marketplaces> |
| `--plugin-dir` | <https://code.claude.com/docs/en/plugins/create.md> and the CLI reference above |
| Resume, session ids | <https://code.claude.com/docs/en/sessions.md> |
| Remote Control and resume | <https://code.claude.com/docs/en/remote-control.md> |

The hooks-page quotes were checked against the raw page text (`curl` of the `.md` form). The other
pages' quotes come from the fetch tool's rendering of the page, so re-check one before relying on it.

- **Exec form exists.** "A command hook runs as exec form when `args` is set, and shell form when `args`
  is omitted." Exec form "resolves `command` as an executable on `PATH` and spawns it directly with
  `args` as the argument vector. There is no shell". On Windows it "requires `command` to resolve to a
  real executable such as a `.exe`." (hooks, "Exec form and shell form").
- **`SessionEnd` default budget (assumption 11).** "`SessionEnd` hooks have a default timeout of 1.5
  seconds. It applies when you exit, run `/clear`, or switch sessions with interactive `/resume`."
  Also: "The overall budget rises automatically to match the highest per-hook `timeout` in your settings
  files, up to 60 seconds. ... Timeouts set on plugin-provided hooks don't raise the budget."
  `CLAUDE_CODE_SESSIONEND_HOOKS_TIMEOUT_MS` "set this environment variable in milliseconds to override
  the budget explicitly." (hooks, SessionEnd)
- **`--resume` and `session_id` (assumption 10): not stated.** The sessions page says
  "`claude --resume <session-id>` from any directory" and that "Sessions created with `/branch` or
  `--fork-session` get their own session IDs", but nowhere says a plain resume keeps its id. The hooks
  page says only "`SessionStart` hooks run again on resume with `source` set to `"resume"`, or `"fork"`
  if you added `--fork-session`".
- **In place vs cache (assumption 6).** "Relative-path plugins in a marketplace you added from a local
  path: the plugin loads in place from its path inside the marketplace folder. Your edits to the source
  directory take effect at the next session start or `/reload-plugins`, and you don't need to increase
  the version. The plugin's hook processes ... receive a `CLAUDE_PLUGIN_ROOT` that points at the source
  directory." Every other marketplace plugin is copied to `cache/<marketplace>/<plugin>/<version>/`.
  (loading, "In-place and copied plugins")
- **`--plugin-dir`.** "Load a plugin from a directory or a `.zip` archive of one ... Each flag takes one
  path." It loads in place and is "never copied". A resumed session needs the flag again: "If the
  session depended on ... `--plugin-dir` ... pass them again when you resume."
- **Not stated anywhere I found:** what `SessionEnd` does when the Terminal tab or window is closed, when
  `WindowsTerminal.exe` is killed, or at logoff or shutdown. Assumption 1 stays open.

### Where the docs disagree with, or put pressure on, ADR-021

1. **Plugin `timeout` does not extend the `SessionEnd` budget.**
   ADR Decision 2: "registers `SessionStart`, `UserPromptSubmit` and `SessionEnd` in its
   `hooks/hooks.json`, each with an explicit `timeout`." Decision 3: "within an internal deadline well
   under the hook `timeout`" and "`SessionEnd`'s shared 1.5 second default budget".
   Docs: "Timeouts set on plugin-provided hooks don't raise the budget."
   So a `timeout` on the plugin's `SessionEnd` hook cannot buy the helper more than the shared 1.5 s.
   The helper's internal deadline has to be under 1.5 s, not "well under the hook `timeout`". Whether
   the plugin hook's own `timeout` still keeps that one hook alive past 1.5 s is unclear from the page;
   step 1.5 measures it.
2. **The plugin name draws a validation warning.**
   ADR Decision 2: "The plugin (`bosun-claude-sessions` ...".
   Docs (plugins-reference, `name`): a name that "Has `claude`, `anthropic`, or `anthropics` as a whole
   word anywhere else, such as `mcp-for-claude`" gives a Warning: `Plugin name "<name>" reads as one of
   Anthropic's own`. `claude plugin validate --strict` turns warnings into failures. It is a warning, not
   an error. Something like `bosun-sessions` avoids it.
3. **A resume may not simply "connect" Remote Control.**
   ADR Decision 8: "With `remoteControlAtStartup` set to `true` ... every interactive start connects, a
   resume included (Assumption 5)."
   Docs (remote-control): "When you resume a conversation with `claude --resume` or `claude --continue`,
   Claude Code reconnects to the Remote Control session recorded in that conversation." and, for a
   failure, "Claude Code couldn't bring back the previous Remote Control session and stopped instead of
   starting a new one on its own." So a restored session after a reboot may sit with Remote Control off
   and need `/remote-control`. Step 1.4 tests the live case only (resume after a normal exit). Resuming
   a Remote Control session after a reboot is not tested by this sheet.
4. **SessionEnd reason `prompt_input_exit` is not tied to Ctrl+D.**
   ADR Decision 5, row 2: "`/exit`, Ctrl+D, `/logout` | `SessionEnd` reason `prompt_input_exit` or
   `logout`".
   Docs: "`prompt_input_exit` | User exited while prompt input was visible", "`logout` | User logged
   out", "`other` | Other exit reasons". Ctrl+D and a double Ctrl+C are not named. Step 1.2 tests them.

### Gaps, not contradictions

- **Old helper files and running sessions.** Docs say an in-place plugin's edits are read "at the next
  session start or `/reload-plugins`". A session started before Bosun wrote a new `hooks.json` keeps
  the old one, which names the old versioned helper. If Bosun deletes the old file at once, those
  sessions' hooks fail to start. ADR Decision 10 says only "never overwritten in place". Step 3.3 shows
  whether a running session keeps the old hooks.
- **`session_title` after the first prompt.** ADR Decision 4: "`UserPromptSubmit` fires on every prompt,
  so the helper returns at once when `prompted` is already set." Docs: "`UserPromptSubmit` hooks also
  receive `session_title` when the session has a custom title", and `/rename` fires no `SessionStart`.
  With the early return, a rename after the first prompt never reaches the record.
- **Transcript lag.** Docs (common input fields): the transcript "file is written asynchronously and may
  lag the in-memory conversation". Row 4 ("transcript is gone") could misread a very recent session if it
  were ever evaluated before row 1, which the ADR order prevents.
- **A resume may fire two `SessionStart` events (unverified).** Third-party issue reports, not the
  official docs, describe a `startup` event with a new id followed by a `resume` event with the original
  id, for example <https://claudeissues.com/issue/24265-bug-resume-fires-two-unlinked-sessionstart-events-with-no-sessionend-for-superse>
  (reported against v2.1.37). If true, Decision 4's "overwrites that process's record" depends on event
  order. Step 1.3's report lists every `SessionStart` per Claude process in order.
- **A Remote Control hint in the hook environment.** The hooks page says `$CLAUDE_CODE_BRIDGE_SESSION_ID`
  "is set during an active Remote Control connection". The logger records it, which gives the helper a
  possible cheap signal. The page also lists `session_title` only on `SessionStart` and `UserPromptSubmit`.

## Known limits of these tools

- The logger is a framework-dependent .NET app. Its `startup_ms` and `wall_ms` are **not** the NativeAOT
  helper's cold start (assumption 9 belongs to the helper's own issue).
- The probe logs every process-set change plus a heartbeat each second, and every 50 ms sample once
  `SessionEnding` has fired, not every sample from the start. This keeps the file small; the 50 ms
  resolution of every change is unchanged.
- The logger records `CLAUDE_*` environment variable **names**, and values only for `CLAUDE_PLUGIN_ROOT`,
  `CLAUDE_PROJECT_DIR`, `CLAUDE_CODE_BRIDGE_SESSION_ID`, `CLAUDE_CODE_REMOTE`, since other
  `CLAUDE_*` variables can hold credentials.
- The Claude Code command line (to check `-p`) is not recorded; ADR-021 Decision 4's `-p` filter is
  outside this spike.
