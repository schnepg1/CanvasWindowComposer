[CmdletBinding()]
param(
    [ValidateRange(1, 100)][int]$Count = 8,
    [ValidateRange(1, 20)][int]$Repeats = 3,
    [ValidateRange(1, 60)][double]$Seconds = 5,
    [double[]]$Zooms = @(1, 0.5, 0.25),
    [string]$OutputDirectory = "",
    [switch]$NoDesktopMomentum,
    [switch]$NoCanvasMomentum
)
$ErrorActionPreference = 'Stop'
$culture = [Globalization.CultureInfo]::InvariantCulture
foreach ($zoom in $Zooms) {
    if ([double]::IsNaN($zoom) -or [double]::IsInfinity($zoom) -or $zoom -lt 0.05 -or $zoom -gt 1) {
        throw 'Zooms must be finite values from 0.05 to 1.'
    }
}
if ($Zooms.Count -eq 0) { throw 'Specify at least one zoom.' }
$exe = Join-Path $PSScriptRoot 'bin/Release/net10.0-windows/CanvasDesktop.PerformanceHarness.exe'
if (!(Test-Path -LiteralPath $exe)) { throw 'Build the Release performance harness before running this script.' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot ('results/presentation-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$cases = @(
    foreach ($zoom in $Zooms) {
        foreach ($lowQuality in @($false, $true)) {
            foreach ($flip in @($false, $true)) {
                [pscustomobject]@{ Zoom = $zoom; LowQuality = $lowQuality; Flip = $flip }
            }
        }
    }
)
$rows = [Collections.Generic.List[object]]::new()
function Operation($phase, [string]$name) {
    $found = @($phase.Diagnostics.Operations | Where-Object { $_.Operation -eq $name })
    if ($found.Count) { return $found[0] }
    return [pscustomobject]@{ Calls = 0; TotalMs = 0; MaxMs = 0 }
}
for ($repeat = 0; $repeat -lt $Repeats; $repeat++) {
    # Rotate and reverse the case order to reduce fixed backend/thermal order bias.
    for ($offset = 0; $offset -lt $cases.Count; $offset++) {
        $index = ($offset + $repeat) % $cases.Count
        if ($repeat % 2) { $index = $cases.Count - 1 - $index }
        $case = $cases[$index]
        $backend = if ($case.Flip) { 'FlipDiscard' } else { 'CompatibilityDiscard' }
        $quality = if ($case.LowQuality) { 'Low' } else { 'Normal' }
        $zoomText = $case.Zoom.ToString('R', $culture)
        $file = 'r{0:D2}-{1}-{2}-z{3}.json' -f ($repeat + 1), $backend, $quality, $zoomText.Replace('.', 'p')
        $reportPath = Join-Path $outputPath $file
        $arguments = @('--presentation-only', "--count=$Count", '--repeats=1',
            ('--seconds=' + $Seconds.ToString('R', $culture)), "--overview-zoom=$zoomText", "--output=$reportPath")
        if ($case.Flip) { $arguments += '--flip-rendering' }
        if ($case.LowQuality) { $arguments += '--low-quality' }
        if ($NoDesktopMomentum) { $arguments += '--no-desktop-momentum' }
        if ($NoCanvasMomentum) { $arguments += '--no-canvas-momentum' }
        & $exe @arguments
        if ($LASTEXITCODE -ne 0) { throw "Presentation harness exited with code $LASTEXITCODE for $file." }
        $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        $actual = @($report.RenderingBackends)
        $matched = $actual.Count -gt 0 -and @($actual | Where-Object { $_ -ne $backend }).Count -eq 0
        foreach ($phase in $report.Results) {
            $hostSample = @($phase.Processes | Where-Object { $_.Role -eq 'CanvasHarness' })[0]
            $upload = Operation $phase 'GridUploadAndDraw'
            $present = Operation $phase 'GridPresentCall'
            $annotations = Operation $phase 'OverviewAnnotationFlush'
            $rows.Add([pscustomobject]@{
                Repeat = $repeat + 1; RequestedBackend = $backend; ActualBackends = ($actual -join ','); BackendMatched = $matched
                Quality = $quality; RequestedZoom = $case.Zoom; Phase = $phase.Phase; CameraStartZoom = $phase.CameraStart.Zoom
                DesktopPanMomentum = $report.DesktopPanMomentum; CanvasPanMomentum = $report.CanvasPanMomentum
                MomentumActiveAtEnd = $phase.MomentumActiveAtEnd; ModeAtEnd = $phase.ModeAtEnd
                CpuOneCorePercent = $hostSample.CpuOneCorePercent
                InputPumpP50Ms = $phase.InputPumpMs.P50; InputPumpP95Ms = $phase.InputPumpMs.P95; InputPumpP99Ms = $phase.InputPumpMs.P99
                GridUploadCalls = $upload.Calls; GridUploadMaxMs = $upload.MaxMs; GridPresentMaxMs = $present.MaxMs
                AnnotationFlushCalls = $annotations.Calls; AnnotationFlushMaxMs = $annotations.MaxMs; Report = $file
            })
        }
        if (!$matched) { Write-Warning "Requested $backend; actual backends: $($actual -join ', '). Inspect $file." }
    }
}
$rows | Export-Csv -LiteralPath (Join-Path $outputPath 'summary.csv') -NoTypeInformation
$rows | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $outputPath 'summary.json') -Encoding utf8
Write-Output $outputPath
