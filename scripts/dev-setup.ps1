#requires -Version 5.1
<#
.SYNOPSIS
  Installs Bosun's development and runtime prerequisites.
.DESCRIPTION
  Idempotent. Safe to re-run. Reports what is already present.
#>

$ErrorActionPreference = 'Stop'

function Test-Command($name) {
    return [bool](Get-Command $name -ErrorAction SilentlyContinue)
}

Write-Host "`n=== Bosun prerequisites ===`n" -ForegroundColor Cyan

if (Test-Path 'C:\Program Files (x86)\WinFsp') {
    Write-Host "[ok]   WinFsp present"
} else {
    Write-Host "[..]   Installing WinFsp" -ForegroundColor Yellow
    winget install --id WinFsp.WinFsp -e --accept-source-agreements --accept-package-agreements
}

if (Test-Command rclone) {
    Write-Host "[ok]   rclone present"
} else {
    Write-Host "[..]   Installing rclone" -ForegroundColor Yellow
    winget install --id Rclone.Rclone -e --accept-source-agreements --accept-package-agreements
}

if (Test-Command wt) {
    Write-Host "[ok]   Windows Terminal present"
} else {
    Write-Host "[!!]   Windows Terminal not found - install from the Store" -ForegroundColor Red
}

if (Test-Command dotnet) {
    Write-Host "[ok]   dotnet present ($(dotnet --version))"
} else {
    Write-Host "[!!]   .NET SDK not found - install the .NET 10 SDK" -ForegroundColor Red
}

if (Test-Command uv) {
    Write-Host "[ok]   uv present"
} else {
    Write-Host "[..]   Installing uv" -ForegroundColor Yellow
    winget install --id astral-sh.uv -e --accept-source-agreements --accept-package-agreements
}

Write-Host "`n=== Agent tooling ===" -ForegroundColor Cyan
Write-Host @"

Graphify (knowledge graph):
    uv tool install graphifyy      # package is graphifyy, command is graphify
    graphify install
    graphify claude install        # run from inside the repo
    graphify hook install          # rebuild on commit, not on session start

Beads (work queue):
    See https://github.com/steveyegge/beads for current install instructions.
    Then, from inside the repo:
        bd quickstart
    Set the issue prefix to 'bs-' and seed from docs/WORK-BREAKDOWN.md.
    Do not assume flag syntax - check 'bd --help' for the installed version.

"@ -ForegroundColor Gray

Write-Host "Next: copy config\hosts.example.toml to config\hosts.toml and edit.`n" -ForegroundColor Cyan
