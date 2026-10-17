# Uses a disposable CI account; never run installer smoke tests against a real user's profile.
$ErrorActionPreference = 'Stop'
if ($env:CI -ne 'true') { throw 'Run this script only in an ephemeral CI Windows account.' }
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$temp = Join-Path ([IO.Path]::GetTempPath()) ('canvas-package-test-' + [Guid]::NewGuid().ToString('N'))
$install = Join-Path $temp 'installation with spaces'
$files = @(
    (Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'CanvasWindowComposer\config.ini'),
    (Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'CanvasWindowComposer\workspace.json'),
    (Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'CanvasWindowComposer\annotations.json'),
    (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'CanvasWindowComposer\AI\chatgpt.dat')
)
$created = @()
function Run-Checked([string]$Exe, [string]$Arguments) {
    $p = Start-Process -FilePath $Exe -ArgumentList $Arguments -PassThru
    if (!$p.WaitForExit(120000)) { throw "Timed out: $Exe" }
    if ($p.ExitCode) { throw "Failed: $Exe (exit $($p.ExitCode))" }
}
function Wait-Report([string]$File, [string]$Version) {
    for ($i=0; $i -lt 100 -and !(Test-Path -LiteralPath $File); $i++) { Start-Sleep -Milliseconds 100 }
    if (!(Test-Path -LiteralPath $File)) { throw "Installed app did not produce $File" }
    $report = Get-Content -Raw -LiteralPath $File | ConvertFrom-Json
    if (!$report.AppVersion.StartsWith($Version)) { throw "Wrong installed version: $($report.AppVersion)" }
    if (!$report.RuntimeDirectory.StartsWith($install, [StringComparison]::OrdinalIgnoreCase)) { throw 'Installed app used a global runtime.' }
}
try {
    New-Item -ItemType Directory -Path $temp | Out-Null
    foreach ($file in $files) {
        if (Test-Path -LiteralPath $file) { throw "CI fixture collision: $file" }
        New-Item -ItemType Directory -Force -Path (Split-Path $file -Parent) | Out-Null
        Set-Content -LiteralPath $file -Value 'canvas upgrade preservation fixture'
        $created += $file
    }
    & "$PSScriptRoot/Build-Release.ps1" -Version '0.0.1-preview.1' -Channel preview -OutputDirectory 'artifacts/smoke-first'
    $setup = @(Get-ChildItem (Join-Path $root 'artifacts/smoke-first') -Filter '*Setup.exe')[0].FullName
    $first = Join-Path $temp 'first.json'
    Run-Checked $setup "--silent --installto `"$install`""
    # Silent Setup deliberately does not launch the app. Use its stable launcher.
    Run-Checked (Join-Path $install 'Update.exe') "start -- --diagnostics-file `"$first`""
    Wait-Report $first '0.0.1-preview.1'
    & "$PSScriptRoot/Build-Release.ps1" -Version '0.0.2-preview.1' -Channel preview -OutputDirectory 'artifacts/release'
    $package = @(Get-ChildItem (Join-Path $root 'artifacts/release') -Filter '*-full.nupkg')[0].FullName
    $second = Join-Path $temp 'second.json'
    Run-Checked (Join-Path $install 'Update.exe') "apply --silent --package `"$package`" -- --diagnostics-file `"$second`""
    Wait-Report $second '0.0.2-preview.1'
    foreach ($file in $files) {
        if ((Get-Content -Raw -LiteralPath $file).Trim() -ne 'canvas upgrade preservation fixture') { throw "Update modified user data: $file" }
    }
    Run-Checked (Join-Path $install 'Update.exe') 'uninstall --silent'
    foreach ($file in $files) { if (!(Test-Path -LiteralPath $file)) { throw "Uninstall removed user data: $file" } }
    Write-Host 'Fresh installation, bundled runtime, upgrade, restart and user-data preservation passed.'
} finally {
    # Only our own files and installation; no real user's data is deleted.
    foreach ($file in $created) { Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath (Join-Path $install 'Update.exe')) {
        Run-Checked (Join-Path $install 'Update.exe') 'uninstall --silent'
    }
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
