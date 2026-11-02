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

# Finds rclone.exe the way src/Bosun/Rclone/Process/RcloneExecutableResolver.cs does: PATH first,
# then the places the common installers put it. A shell started before rclone was installed does
# not have it on PATH yet, so Get-Command alone would report "missing" and install it a second time.
# Keep the order and the locations in step with that class. Returns the full path, or $null.
function Find-Rclone {
    $onPath = Get-Command rclone -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($onPath) { return $onPath.Source }

    $localAppData = $env:LOCALAPPDATA
    $programFiles = $env:ProgramFiles

    # Scoop honours %SCOOP% as its root, otherwise %USERPROFILE%\scoop. Chocolatey honours
    # %ChocolateyInstall%, otherwise %ProgramData%\chocolatey.
    $scoopRoot = $env:SCOOP
    if (-not $scoopRoot -and $env:USERPROFILE) { $scoopRoot = Join-Path $env:USERPROFILE 'scoop' }
    $chocolateyRoot = $env:ChocolateyInstall
    if (-not $chocolateyRoot -and $env:ProgramData) { $chocolateyRoot = Join-Path $env:ProgramData 'chocolatey' }

    $candidates = @()
    if ($localAppData) { $candidates += Join-Path $localAppData 'rclone\rclone.exe' }
    if ($programFiles) { $candidates += Join-Path $programFiles 'rclone\rclone.exe' }
    if ($localAppData) { $candidates += Join-Path $localAppData 'Microsoft\WinGet\Links\rclone.exe' }

    # winget unpacks the portable zip to Packages\Rclone.Rclone_<source>_<hash>\rclone-v<version>-windows-<arch>\.
    # Both folder names carry a version or hash, so expand them; newest-looking name first, as the
    # C# resolver does. User packages, then machine packages.
    $wingetRoots = @()
    if ($localAppData) { $wingetRoots += Join-Path $localAppData 'Microsoft\WinGet\Packages' }
    if ($programFiles) { $wingetRoots += Join-Path $programFiles 'WinGet\Packages' }
    foreach ($root in $wingetRoots) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        $packages = Get-ChildItem -LiteralPath $root -Directory -Filter 'Rclone.Rclone_*' -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending
        foreach ($package in $packages) {
            $extracts = Get-ChildItem -LiteralPath $package.FullName -Directory -Filter 'rclone-*' -ErrorAction SilentlyContinue |
                Sort-Object Name -Descending
            foreach ($extract in $extracts) {
                $candidates += Join-Path $extract.FullName 'rclone.exe'
            }
        }
    }

    if ($scoopRoot) { $candidates += Join-Path $scoopRoot 'shims\rclone.exe' }
    if ($chocolateyRoot) { $candidates += Join-Path $chocolateyRoot 'bin\rclone.exe' }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }

    return $null
}

Write-Host "`n=== Bosun prerequisites ===`n" -ForegroundColor Cyan

if (Test-Path 'C:\Program Files (x86)\WinFsp') {
    Write-Host "[ok]   WinFsp present"
} else {
    Write-Host "[..]   Installing WinFsp" -ForegroundColor Yellow
    winget install --id WinFsp.WinFsp -e --accept-source-agreements --accept-package-agreements
}

$rclonePath = Find-Rclone
if ($rclonePath) {
    Write-Host "[ok]   rclone present ($rclonePath)"
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
