# ADR-024 spike: Claude Code in a Bosun-owned pseudo console (bs-4xw.3)

Throwaway tooling. It is not part of `Bosun.sln` and nothing here ships. Delete the folder when the
spike is done.

**The question.** ADR-024 plans for Bosun to start Claude Code in a hidden terminal it owns (a
pseudo console, ConPTY), so that Bosun can see prompts such as "Trust this folder?" and answer them
when you approve, even while the workstation is locked. This spike checks that Claude Code behaves
normally there:

1. Its screen works in a pseudo console.
2. The trust prompt and the "Enable Remote Control?" prompt can be seen and answered.
3. The session is reachable from the Claude app through Remote Control.
4. All of that still works with the workstation locked.
5. Whether a session link is printed, and how the session's name shows in the app.

**How this works.** You run the steps. The probe writes logs. I read the logs and decide pass or
fail. "Done" means only that you did the step; you do not judge the result. Each step has an
**Expected** line saying what you should see and what to send back.

**Already checked without Claude Code (2026-11-07).** A fake program printing the same prompts ran
in the probe with the workstation locked. Both prompts were detected and answered, and a link was
picked up. So the probe's own mechanics work; what is untested is Claude Code itself.

## What this touches on your machine

- Starts Claude Code with `--remote-control`. That registers Remote Control sessions named
  `bosun-spike-1` to `bosun-spike-4` on your Claude account. They end when the probe quits.
- Claude Code uses your subscription as normal and records trust for the test folders in its own
  configuration, as it does for any folder you trust. The probe never edits any settings file.
- Writes logs to `%LOCALAPPDATA%\BosunSpike\conpty\<time>\`: `events.jsonl` (what was detected) and
  `raw.log` (everything Claude Code printed, **including your conversation text**). Delete them after
  I have read them.
- Test folders go in `%LOCALAPPDATA%\BosunSpike\conpty-work\`.
- Never runs Bosun, never mounts anything.
- Sessions use your normal Claude Code permission settings.

## Part 0. Setup

Open a **new, plain Terminal tab** (not one running Claude Code), then:

```powershell
dotnet build C:\Users\Barry\Development\bosun\spikes\adr024-conpty\ConPtyProbe -c Release
$P = "C:\Users\Barry\Development\bosun\spikes\adr024-conpty\ConPtyProbe\bin\Release\net10.0-windows\ConPtyProbe.exe"
$W = "$env:LOCALAPPDATA\BosunSpike\conpty-work"
```

**Expected:** the build ends with `0 Warning(s)` and `0 Error(s)`. Set `$P` and `$W` again in any new
tab you use.

## Step 1. Watch it work (plain folder, screen shown)

```powershell
& $P --dir "$W\one" --name bosun-spike-1 --mirror
```

Answer any prompts with the keyboard as you normally would (the trust question, then
"Enable Remote Control?" with `y` if it appears). Then, on your phone, open the Claude app's Code
section, find `bosun-spike-1`, and send "say hello". Back at the desk, press **Ctrl+]** then **q**
to quit.

**Expected:** Claude Code's usual screen appears in this tab; a trust question for the folder
appears; the session shows up on the phone and answers there, and the reply also appears in the tab.
**Send back:** "done", the `Run folder` line the probe printed first, whether the session appeared
on the phone, and the name it showed under.

## Step 2. Hidden, answered automatically (new git repository)

```powershell
New-Item -ItemType Directory "$W\two" | Out-Null; git -C "$W\two" init
& $P --dir "$W\two" --name bosun-spike-2 --auto-trust --auto-rc
```

On the phone, open `bosun-spike-2` and send "what folder are you in?". Then type `quit` in the tab
and press Enter.

**Expected:** no Claude Code screen in the tab, only event lines such as `trust_prompt` and
`answered`, possibly `url`. The session appears on the phone and replies with the `two` folder.
If no event lines appear within 30 seconds, type `quit` anyway; the log will show why.
**Send back:** "done", the `Run folder` line, and whether the phone got a reply.

## Step 3. Hidden, with the workstation locked

```powershell
New-Item -ItemType Directory "$W\three" | Out-Null; git -C "$W\three" init
& $P --dir "$W\three" --name bosun-spike-3 --auto-trust --auto-rc --start-delay 30
```

Within 30 seconds, press **Win+L** to lock. Wait one minute. On the phone, open `bosun-spike-3`
and send "what folder are you in?". Wait for the reply. Then unlock, type `quit` in the tab and
press Enter.

**Expected:** the session appears on the phone and replies while the workstation is locked.
**Send back:** "done", the `Run folder` line, and whether the phone got a reply while locked.

## Step 4 (optional). Pick up the step 2 conversation

```powershell
& $P --dir "$W\two" --name bosun-spike-4 --auto-trust --auto-rc --extra "--continue"
```

On the phone, open `bosun-spike-4`. Then type `quit` in the tab and press Enter.

**Expected:** the session on the phone shows the step 2 conversation. No trust question this time,
since the folder was trusted in step 2.
**Send back:** "done", the `Run folder` line, and whether the earlier conversation was there.

## Cleanup

1. Make sure every probe has quit (`quit`, or Ctrl+] then q in step 1).
2. After I have read the logs, delete `%LOCALAPPDATA%\BosunSpike\conpty\` and
   `%LOCALAPPDATA%\BosunSpike\conpty-work\`.
3. The `bosun-spike-*` sessions may stay listed in the Claude app for a while after they end; they
   can be ignored or archived there.

## Probe reference

`ConPtyProbe --help`-style usage is printed on a bad option. Headless commands while it runs:
`enter`, `y`, `n`, `1`, `2`, `3`, `esc`, `up`, `down`, `type <text>` (types and presses Enter),
`send <keys>` (`\r` Enter, `\e` Esc), `quit`. `--cmd "<command line>"` runs something other than
Claude Code; it exists only for testing the probe.
