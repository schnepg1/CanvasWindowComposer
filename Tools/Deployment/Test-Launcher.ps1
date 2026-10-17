# Run on an ephemeral Windows CI runner. No GUI and no application hooks.
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$ps = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
$launcher = Join-Path $root 'Tools\Start-Canvas.ps1'
$temp = Join-Path ([IO.Path]::GetTempPath()) ('canvas-launcher-test-' + [Guid]::NewGuid().ToString('N'))
$originalPath = $env:PATH
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    $env:PATH = $temp
    $ErrorActionPreference = 'Continue' # Expected native stderr must remain copyable text on PS 5.1.
    $output = & $ps -NoProfile -ExecutionPolicy Bypass -File $launcher -Diagnose -NoDialog 2>&1 | Out-String
    $ErrorActionPreference = 'Stop'
    if ($LASTEXITCODE -eq 0 -or $output -notmatch 'SDK is missing' -or $output -notmatch 'runtime' -or $output -notmatch 'Log:') { throw "Missing-SDK diagnostics were not actionable: $output" }
    Set-Content -LiteralPath (Join-Path $temp 'dotnet.cmd') -Value '@echo off', 'echo Incompatible SDK', 'exit /b 1'
    $ErrorActionPreference = 'Continue'
    $output = & $ps -NoProfile -ExecutionPolicy Bypass -File $launcher -Diagnose -NoDialog 2>&1 | Out-String
    $ErrorActionPreference = 'Stop'
    if ($LASTEXITCODE -eq 0 -or $output -notmatch 'compatible .NET SDK' -or $output -notmatch '10.0.401') { throw "Wrong-SDK diagnostics failed: $output" }
    $env:PATH = $originalPath
    Push-Location $root
    try {
        $env:CANVAS_NO_PAUSE = '1'
        & "$env:SystemRoot\System32\cmd.exe" /c 'Start-Canvas.cmd --build --no-launch --no-dialog'
        if ($LASTEXITCODE) { throw 'The command-file development launcher failed.' }
        Set-Content -LiteralPath (Join-Path $root 'publish-workspace\obsolete-dependency.dll') -Value 'stale'
        & $ps -NoProfile -ExecutionPolicy Bypass -File $launcher -NoLaunch -NoDialog
        if ($LASTEXITCODE -or (Test-Path -LiteralPath (Join-Path $root 'publish-workspace\obsolete-dependency.dll'))) { throw 'The launcher reused stale publish output.' }
    } finally { Pop-Location }
    Write-Host 'Missing/wrong SDK, command-file options, source rebuild and stale-file cleanup passed.'
} finally {
    $env:PATH = $originalPath
    Remove-Item -LiteralPath $temp -Recurse -Force
}
