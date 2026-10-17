param([Parameter(Mandatory)][string]$Directory)
$ErrorActionPreference = 'Stop'
foreach ($name in @('CanvasDesktop.exe','CanvasDesktop.dll','CanvasDesktop.Ai.dll','CanvasDesktop.CanvasView.dll','Velopack.dll','coreclr.dll','hostfxr.dll','PresentationFramework.dll','System.Windows.Forms.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $Directory $name))) { throw "Self-contained package is incomplete: $name is missing." }
}
$runtime = Get-Content -Raw -LiteralPath (Join-Path $Directory 'CanvasDesktop.runtimeconfig.json') | ConvertFrom-Json
if ($runtime.runtimeOptions.framework -or $runtime.runtimeOptions.frameworks) { throw 'The published app still requires a globally installed .NET runtime.' }
# Execute only a diagnostic path: no hooks, layouts, graphics or user window movements.
$diagnostics = Join-Path $Directory 'package-diagnostics.json'
$p = Start-Process -FilePath (Join-Path $Directory 'CanvasDesktop.exe') -ArgumentList "--diagnostics-file `"$diagnostics`"" -Wait -PassThru
if ($p.ExitCode -ne 0 -or !(Test-Path -LiteralPath $diagnostics)) { throw 'The published app failed its diagnostic startup.' }
$report = Get-Content -Raw -LiteralPath $diagnostics | ConvertFrom-Json
if ($report.Framework -notmatch '^\.NET 10\.') { throw 'Unexpected runtime in published app.' }
Remove-Item -LiteralPath $diagnostics
Write-Host 'Verified self-contained runtime, application modules and diagnostic startup.'
