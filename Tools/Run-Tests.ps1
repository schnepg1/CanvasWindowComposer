# Machine-wide serialized Canvas test runner.
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Filter,
    [switch]$NoBuild,
    [switch]$Smoke,
    [switch]$NoSmoke,
    [switch]$NoPrompt,
    [switch]$AllowRunningApp,
    [string]$ResultsDirectory
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $root

$nativeFilter = 'Category=NativeRendering|Category=NativeParking|Category=NativePlacement|Category=NativePreview'
$unitFilter = 'Category!=NativeRendering&Category!=NativeParking&Category!=NativePlacement&Category!=NativePreview'

function Invoke-DotNet([string[]]$Arguments) {
    Write-Host ('dotnet ' + ($Arguments -join ' ')) -ForegroundColor Cyan
    $previous = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & dotnet @Arguments
        $code = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previous
    }
    if ($code -ne 0) { throw "dotnet failed with exit code $code." }
}

if ([string]::IsNullOrWhiteSpace($ResultsDirectory)) {
    $ResultsDirectory = Join-Path $root ('TestResults\run-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $PID)
} else {
    $ResultsDirectory = [IO.Path]::GetFullPath($ResultsDirectory)
}

$mutex = $null
$acquired = $false
$failures = @()
$started = Get-Date
try {
    try {
        $mutex = New-Object System.Threading.Mutex($false, 'Global\CanvasWindowComposer.Tests')
    } catch {
        $mutex = New-Object System.Threading.Mutex($false, 'Local\CanvasWindowComposer.Tests')
    }
    Write-Host 'Waiting for other Canvas test runs on this machine...' -ForegroundColor DarkGray
    try {
        $acquired = $mutex.WaitOne([TimeSpan]::FromMinutes(60))
    } catch [System.Threading.AbandonedMutexException] {
        $acquired = $true
    }
    if (-not $acquired) { throw 'Timed out waiting for another Canvas test run on this machine.' }

    if (-not $AllowRunningApp) {
        $preflight = Join-Path $PSScriptRoot 'Test-Preflight.ps1'
        $preflightArgs = @('-NoLogo', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $preflight)
        if ($NoPrompt) { $preflightArgs += @('-Mode', 'Fail') }
        & powershell.exe @preflightArgs
        if ($LASTEXITCODE -ne 0) { throw 'CanvasDesktop is running. Close it and run the tests again.' }
    }

    if (-not $NoBuild) {
        Invoke-DotNet @('build', 'CanvasDesktop.slnx', '-c', $Configuration, '--nologo')
    }

    $skipPreflight = '-p:CanvasSkipTestPreflight=true'
    if ($Filter) {
        $filteredResults = Join-Path $ResultsDirectory 'filtered'
        try {
            Invoke-DotNet @('test', 'CanvasDesktop.slnx', '-c', $Configuration, '--no-build', '--nologo',
                '--filter', $Filter, '--results-directory', $filteredResults, '--logger', 'trx', $skipPreflight)
        } catch { $failures += 'filtered tests' }
    } else {
        $unitResults = Join-Path $ResultsDirectory 'unit'
        Write-Host "`n=== Unit tests (native categories excluded) ===" -ForegroundColor Green
        try {
            Invoke-DotNet @('test', 'CanvasDesktop.slnx', '-c', $Configuration, '--no-build', '--nologo',
                '--filter', $unitFilter, '--results-directory', $unitResults, '--logger', 'trx', $skipPreflight)
        } catch { $failures += 'unit tests' }

        $nativeResults = Join-Path $ResultsDirectory 'native'
        Write-Host "`n=== Native desktop tests (serialized) ===" -ForegroundColor Green
        try {
            Invoke-DotNet @('test', 'Tests\CanvasDesktop.Tests.csproj', '-c', $Configuration, '--no-build', '--nologo',
                '--filter', $nativeFilter, '--results-directory', $nativeResults, '--logger', 'trx', $skipPreflight)
        } catch { $failures += 'native tests' }
    }

    $autoHotkey = 'C:\Program Files\AutoHotkey\v2\AutoHotkey64.exe'
    if ($Smoke -and -not $NoSmoke) {
        if (-not (Test-Path -LiteralPath $autoHotkey)) {
            Write-Warning 'AutoHotkey v2 was not found; skipping the smoke test.'
        } else {
            Write-Host "`n=== Smoke test (AutoHotkey) ===" -ForegroundColor Green
            $smokeLog = Join-Path $root 'Tests\smoke_test.log'
            if (Test-Path -LiteralPath $smokeLog) { Remove-Item -LiteralPath $smokeLog -Force }
            & $autoHotkey (Join-Path $root 'Tests\smoke_test.ahk')
            $smokeExit = $LASTEXITCODE
            if (Test-Path -LiteralPath $smokeLog) { Get-Content -LiteralPath $smokeLog }
            if ($smokeExit -ne 0) { $failures += 'smoke test' }
        }
    }
} catch {
    $failures += $_.Exception.Message
} finally {
    if ($acquired) { $mutex.ReleaseMutex() }
    if ($mutex) { $mutex.Dispose() }
}

$elapsed = (Get-Date) - $started
Write-Host "`nResults: $ResultsDirectory" -ForegroundColor Cyan
Write-Host ('Elapsed: {0:hh\:mm\:ss}' -f $elapsed) -ForegroundColor Cyan
if ($failures.Count -gt 0) {
    Write-Host ('FAILED: ' + ($failures -join '; ')) -ForegroundColor Red
    exit 1
}
Write-Host 'All requested tests passed.' -ForegroundColor Green
exit 0
