# E13a spike (bs-vqh.1), assumption 6: edits the plugin's hooks.json in place so the next hook run
# logs a different plugin_tag. The log then shows which copy of hooks.json Claude Code actually used.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Tag,
    # Also bump plugin.json "version" (only needed if the plugin turns out to load from a cache copy).
    [string]$Version
)
$ErrorActionPreference = 'Stop'
$hooks = Join-Path $PSScriptRoot 'plugin\hooks\hooks.json'
$text = Get-Content -Raw -Path $hooks
$new = [regex]::Replace($text, '"--plugin-tag",\s*"[^"]*"', ('"--plugin-tag", "' + $Tag + '"'))
[System.IO.File]::WriteAllText($hooks, $new, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "hooks.json now uses --plugin-tag $Tag ($hooks)"

if ($Version) {
    $manifest = Join-Path $PSScriptRoot 'plugin\.claude-plugin\plugin.json'
    $m = Get-Content -Raw -Path $manifest
    $m2 = [regex]::Replace($m, '"version":\s*"[^"]*"', ('"version": "' + $Version + '"'))
    [System.IO.File]::WriteAllText($manifest, $m2, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "plugin.json version is now $Version"
}
