# E13a spike (bs-vqh.1), ADR-021 assumption 7:
# "wt.exe -w 0 new-tab ..., issued just after a new WindowsTerminal.exe appears, lands in that new window."
#
# This script does NOTHING until a WindowsTerminal.exe process that did not exist when it started
# appears. You launch Terminal by hand. It then runs `wt.exe -w 0 new-tab` at once, and logs what it saw.
# Plain Windows PowerShell 5.1 compatible. Output: %LOCALAPPDATA%\BosunSpike\wt-race.jsonl
[CmdletBinding()]
param(
    [int]$PollMs = 10,
    [int]$GiveUpMinutes = 30,
    [int]$FollowUpSeconds = 20
)
$ErrorActionPreference = 'Stop'

$dir = $env:BOSUN_SPIKE_DIR
if (-not $dir) { $dir = Join-Path $env:LOCALAPPDATA 'BosunSpike' }
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$log = Join-Path $dir 'wt-race.jsonl'
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Write-Log([hashtable]$record) {
    $record['ts_utc'] = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
    $line = ($record | ConvertTo-Json -Compress -Depth 6) + "`n"
    [System.IO.File]::AppendAllText($log, $line, $utf8)
}

function Get-TerminalPids {
    $ids = @()
    foreach ($p in [System.Diagnostics.Process]::GetProcessesByName('WindowsTerminal')) { $ids += $p.Id; $p.Dispose() }
    return $ids
}

$token = 'E13A-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$baseline = @(Get-TerminalPids)
Write-Log @{ kind = 'waiting'; baseline_terminal_pids = $baseline; token = $token }
Write-Host "Waiting for a NEW WindowsTerminal.exe (existing ones: $($baseline -join ', '))."
Write-Host 'Launch Terminal by hand now. Nothing is run until it appears.'

$clock = [System.Diagnostics.Stopwatch]::StartNew()
$newPid = $null
while ($clock.Elapsed.TotalMinutes -lt $GiveUpMinutes) {
    foreach ($id in (Get-TerminalPids)) {
        if ($baseline -notcontains $id) { $newPid = $id; break }
    }
    if ($newPid) { break }
    Start-Sleep -Milliseconds $PollMs
}
if (-not $newPid) {
    Write-Log @{ kind = 'gave_up'; minutes = $GiveUpMinutes }
    Write-Host 'No new Terminal appeared. Gave up.'
    return
}

$detectedAt = [DateTime]::UtcNow
$startUtc = $null
try {
    $np = [System.Diagnostics.Process]::GetProcessById($newPid)
    $startUtc = $np.StartTime.ToUniversalTime()
} catch { }
$lag = $null
if ($startUtc) { $lag = [math]::Round(($detectedAt - $startUtc).TotalMilliseconds, 1) }
Write-Log @{ kind = 'new_terminal_detected'; terminal_pid = $newPid; terminal_start_utc = $(if ($startUtc) { $startUtc.ToString('o') } else { $null }); detection_lag_ms = $lag }

# The race itself: issue wt.exe immediately, with no settling delay.
Write-Log @{ kind = 'wt_invoke'; args = "-w 0 new-tab --title E13A-PROBE cmd /k echo probe $token" }
$wt = Start-Process -FilePath 'wt.exe' -ArgumentList @('-w', '0', 'new-tab', '--title', 'E13A-PROBE', 'cmd', '/k', "echo probe $token") -PassThru
$invokedAt = [DateTime]::UtcNow
Write-Log @{ kind = 'wt_invoked'; wt_launcher_pid = $wt.Id; ms_after_detection = [math]::Round(($invokedAt - $detectedAt).TotalMilliseconds, 1) }
Write-Host "Detected Terminal pid $newPid and ran wt.exe. Now watching for the probe tab..."

# Follow up: find the probe tab's cmd.exe and walk up to the WindowsTerminal.exe that owns it.
function Get-OwnerTerminalPid([int]$startPid) {
    $cur = $startPid
    for ($i = 0; $i -lt 16 -and $cur; $i++) {
        $proc = Get-CimInstance Win32_Process -Filter "ProcessId = $cur" -ErrorAction SilentlyContinue
        if (-not $proc) { return $null }
        if ($proc.Name -eq 'WindowsTerminal.exe') { return [int]$proc.ProcessId }
        $cur = [int]$proc.ParentProcessId
    }
    return $null
}

$deadline = [DateTime]::UtcNow.AddSeconds($FollowUpSeconds)
$found = $false
while ([DateTime]::UtcNow -lt $deadline -and -not $found) {
    $hit = Get-CimInstance Win32_Process -Filter "Name = 'cmd.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like "*$token*" } | Select-Object -First 1
    if ($hit) {
        $found = $true
        $owner = Get-OwnerTerminalPid ([int]$hit.ProcessId)
        $titles = @()
        foreach ($p in [System.Diagnostics.Process]::GetProcessesByName('WindowsTerminal')) {
            $titles += @{ pid = $p.Id; main_window_title = $p.MainWindowTitle }
            $p.Dispose()
        }
        Write-Log @{
            kind = 'probe_tab_found'
            probe_cmd_pid = [int]$hit.ProcessId
            owner_terminal_pid = $owner
            owner_is_the_new_terminal = ($owner -eq $newPid)
            terminal_pids_now = @(Get-TerminalPids)
            terminal_window_titles = $titles
            ms_after_detection = [math]::Round(([DateTime]::UtcNow - $detectedAt).TotalMilliseconds, 1)
        }
        Write-Host "Probe tab found. Owner Terminal pid: $owner (new Terminal was $newPid)."
    } else {
        Start-Sleep -Milliseconds 100
    }
}
if (-not $found) {
    Write-Log @{ kind = 'probe_tab_not_found'; seconds_waited = $FollowUpSeconds; terminal_pids_now = @(Get-TerminalPids) }
    Write-Host "No probe tab appeared within $FollowUpSeconds s."
}
Write-Host 'Done. Now look at your screen: which Terminal window holds the tab titled E13A-PROBE?'
