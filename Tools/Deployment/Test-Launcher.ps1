# Run on an ephemeral Windows CI runner. No GUI and no application hooks.
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$ps = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
$launcher = Join-Path $root 'Tools\Start-Canvas.ps1'
$temp = Join-Path ([IO.Path]::GetTempPath()) ('canvas-launcher-test-' + [Guid]::NewGuid().ToString('N'))
$originalPath = $env:PATH
$originalNoPause = $env:CANVAS_NO_PAUSE
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    # Exercise the actual command file without building or opening the app, including double-click startup.
    $commandRoot = Join-Path $temp 'command launcher with spaces'
    New-Item -ItemType Directory -Path (Join-Path $commandRoot 'Tools') | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'Start-Canvas.cmd') -Destination $commandRoot
    Set-Content -LiteralPath (Join-Path $commandRoot 'Tools\Start-Canvas.ps1') -Value @'
[CmdletBinding()]
param([switch]$Build, [switch]$NoLaunch, [switch]$Diagnose, [switch]$NoDialog)
Write-Output ("Build={0};NoLaunch={1};Diagnose={2};NoDialog={3}" -f [bool]$Build, [bool]$NoLaunch, [bool]$Diagnose, [bool]$NoDialog)
if ($Build -and $Diagnose) { exit 23 }
exit 0
'@
    $env:CANVAS_NO_PAUSE = '1'
    Push-Location $commandRoot
    try {
        foreach ($case in @(
            @{ Command = 'Start-Canvas.cmd'; Expected = 'Build=False;NoLaunch=False;Diagnose=False;NoDialog=False'; ExitCode = 0 },
            @{ Command = 'Start-Canvas.cmd --build --no-launch --no-dialog'; Expected = 'Build=True;NoLaunch=True;Diagnose=False;NoDialog=True'; ExitCode = 0 },
            @{ Command = 'Start-Canvas.cmd --diagnose'; Expected = 'Build=False;NoLaunch=False;Diagnose=True;NoDialog=False'; ExitCode = 0 },
            @{ Command = 'Start-Canvas.cmd -Build -NoLaunch -NoDialog'; Expected = 'Build=True;NoLaunch=True;Diagnose=False;NoDialog=True'; ExitCode = 0 },
            @{ Command = 'Start-Canvas.cmd --build --diagnose'; Expected = 'Build=True;NoLaunch=False;Diagnose=True;NoDialog=False'; ExitCode = 23 }
        )) {
            $ErrorActionPreference = 'Continue'
            $output = & "$env:SystemRoot\System32\cmd.exe" /d /c $case.Command 2>&1 | Out-String
            $code = $LASTEXITCODE
            $ErrorActionPreference = 'Stop'
            if ($code -ne $case.ExitCode -or !$output.Contains($case.Expected)) {
                throw "Command-file argument handling failed for '$($case.Command)' (exit $code): $output"
            }
        }
    } finally { Pop-Location }
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
    Write-Host 'Command-file startup with no arguments, options and exit codes, missing/wrong SDK, source rebuild and stale-file cleanup passed.'
} finally {
    $env:PATH = $originalPath
    $env:CANVAS_NO_PAUSE = $originalNoPause
    Remove-Item -LiteralPath $temp -Recurse -Force
}
