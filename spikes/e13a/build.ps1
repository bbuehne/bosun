# E13a spike (bs-vqh.1): builds the hook logger into both plugin folders and the SessionEnding probe.
# Safe to run: it only calls `dotnet publish`. It does not touch Claude Code, Terminal or Bosun.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$loggerOut = Join-Path $root 'HookLogger\bin\publish'
$probeOut = Join-Path $root 'SessionEndingProbe\bin\publish'

dotnet publish (Join-Path $root 'HookLogger\HookLogger.csproj') -c Release -o $loggerOut --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'HookLogger publish failed' }

dotnet publish (Join-Path $root 'SessionEndingProbe\SessionEndingProbe.csproj') -c Release -o $probeOut --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'SessionEndingProbe publish failed' }

foreach ($plugin in 'plugin', 'plugin-slowend') {
    $dest = Join-Path $root "$plugin\helper"
    if (Test-Path $dest) { Remove-Item -Recurse -Force $dest }
    New-Item -ItemType Directory -Path $dest | Out-Null
    Copy-Item -Path (Join-Path $loggerOut '*') -Destination $dest -Recurse
}

Write-Host ''
Write-Host 'Build OK.'
Write-Host "  Logger (plugin):   $(Join-Path $root 'plugin\helper\HookLogger.exe')"
Write-Host "  Logger (slow end): $(Join-Path $root 'plugin-slowend\helper\HookLogger.exe')"
Write-Host "  Probe:             $(Join-Path $probeOut 'SessionEndingProbe.exe')"
