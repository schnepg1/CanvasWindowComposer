# Capture a transient shell/window problem before restarting Canvas or Explorer.
[CmdletBinding()]
param(
    [ValidateRange(0, 60)][int]$Seconds = 15,
    [ValidateRange(100, 5000)][int]$IntervalMilliseconds = 500,
    [string]$OutputPath,
    [switch]$IncludeTitles
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not ('CanvasDesktop.WindowStateSnapshot' -as [type])) {
    Add-Type -Path (Join-Path $root 'Source\System\WindowStateSnapshot.cs')
}
if (-not $OutputPath) {
    $directory = Join-Path $env:LOCALAPPDATA 'CanvasWindowComposer\Logs'
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $OutputPath = Join-Path $directory ('windows-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '.jsonl')
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
Write-Host "Capturing for $Seconds seconds. Reproduce the taskbar problem now."
Write-Host "Report: $OutputPath"
$writer = [IO.StreamWriter]::new($OutputPath, $false, [Text.UTF8Encoding]::new($false))
$elapsed = [Diagnostics.Stopwatch]::StartNew()
try {
    do {
        $snapshot = [CanvasDesktop.WindowStateSnapshot]::Capture($IncludeTitles.IsPresent)
        $writer.WriteLine(($snapshot | ConvertTo-Json -Depth 8 -Compress))
        $writer.Flush()
        if ($elapsed.Elapsed.TotalSeconds -ge $Seconds) { break }
        Start-Sleep -Milliseconds $IntervalMilliseconds
    } while ($true)
} finally { $writer.Dispose() }
Write-Host 'Capture complete. No windows or Explorer settings were changed.'
