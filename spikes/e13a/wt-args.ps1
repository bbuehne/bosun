# E13a (bs-vqh.1): checks how a real wt.exe treats the argument list WtRestoreArgumentBuilder
# (bs-vqh.6) produces. Unit tests cannot launch wt.exe, so two behaviours are unverified:
#   1. a tab root whose path has spaces (C:\Program Files\PowerShell\7\pwsh.exe) survives intact;
#   2. a ';' in -d and --title, escaped as '\;', arrives as a literal ';' rather than splitting.
# The arguments mirror the builder's output for a pwsh tab, except that the command is
# Get-Location instead of 'claude --resume <id>', so no Claude session is touched. They are passed
# through ProcessStartInfo.ArgumentList with UseShellExecute, exactly as Win32ExternalLauncher does.
# Run with pwsh (PowerShell 7): Windows PowerShell 5 has no ArgumentList.

$ErrorActionPreference = 'Stop'
$dir = Join-Path $env:TEMP 'e13a my;dir'
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$pwsh = (Get-Command pwsh).Source

$arguments = @(
    '-w', '0', 'new-tab',
    '-d', ($dir -replace ';', '\;'),
    '--title', 'e13a my\;dir',
    $pwsh, '-NoExit', '-Command', 'Get-Location'
)

$psi = [System.Diagnostics.ProcessStartInfo]::new('wt.exe')
$psi.UseShellExecute = $true
foreach ($a in $arguments) { $psi.ArgumentList.Add($a) }

Write-Host "Tab root : $pwsh"
Write-Host "Folder   : $dir"
Write-Host "Arguments, one per line:"
$arguments | ForEach-Object { Write-Host "  $_" }
[System.Diagnostics.Process]::Start($psi) | Out-Null
Write-Host "wt.exe started. Look for a new tab titled 'e13a my;dir'."
