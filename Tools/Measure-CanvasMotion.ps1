#Requires -Version 7.0
[CmdletBinding(DefaultParameterSetName = 'Capture')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Capture')][string]$PresentMonPath,
    [Parameter(Mandatory, ParameterSetName = 'Capture')][string]$OutputDirectory,
    [Parameter(Mandatory, ParameterSetName = 'Analyze')][string]$AnalyzeDirectory,
    [Parameter(Mandatory, ParameterSetName = 'Capture')][ValidateSet('Pan','Zoom','Flight','DesktopHandoff')][string]$Scenario,
    [Parameter(Mandatory, ParameterSetName = 'Capture')][ValidateRange(20,1000)][double]$RefreshHz,
    [Parameter(ParameterSetName = 'Capture')][ValidateRange(30,180)][int]$Seconds = 30,
    [Parameter(ParameterSetName = 'Capture')][ValidateRange(0,60)][int]$DelaySeconds = 5
)

$ErrorActionPreference = 'Stop'
$provider = [guid]'854023e4-c25a-4841-8d6e-7307c236be53'
$invariant = [Globalization.CultureInfo]::InvariantCulture
$frequency = [Diagnostics.Stopwatch]::Frequency

function Summarize([double[]]$Values) {
    $sorted = @($Values | Sort-Object)
    if ($sorted.Count -eq 0) { return $null }
    [ordered]@{
        Count = $sorted.Count
        P50 = $sorted[[math]::Ceiling(.50 * $sorted.Count) - 1]
        P95 = $sorted[[math]::Ceiling(.95 * $sorted.Count) - 1]
        P99 = $sorted[[math]::Ceiling(.99 * $sorted.Count) - 1]
        Max = $sorted[-1]
        OverOneRefresh = @($sorted.Where({ $_ -gt $budgetMs })).Count
        OverTwoRefreshes = @($sorted.Where({ $_ -gt 2 * $budgetMs })).Count
    }
}

function Read-Operation($event) {
    # Get-WinEvent can expose raw EventSource data when the provider manifest
    # is absent from the registry. Decode only the exact version-0 layout.
    $xml = [xml]$event.ToXml()
    $data = @($xml.Event.EventData.Data)
    if ($data.Count -eq 5) {
        $operation = $data[0].'#text'
        $start = [long]::Parse($data[1].'#text', $invariant)
        $duration = [double]::Parse($data[2].'#text', $invariant)
        $status = [int]$data[3].'#text'
        $items = [int]$data[4].'#text'
    } else {
        $hex = [string]$xml.Event.ProcessingErrorData.EventPayload
        if ($hex -notmatch '^(?:[A-Fa-f0-9]{2})+$') { throw 'Missing operation payload' }
        $bytes = [Convert]::FromHexString($hex)
        $offset = 0
        while ($offset + 1 -lt $bytes.Length -and ($bytes[$offset] -ne 0 -or $bytes[$offset + 1] -ne 0)) {
            $offset += 2
        }
        if ($bytes.Length -ne $offset + 26) { throw 'Unexpected operation payload size' }
        $operation = [Text.Encoding]::Unicode.GetString($bytes, 0, $offset)
        $start = [BitConverter]::ToInt64($bytes, $offset + 2)
        $duration = [BitConverter]::ToDouble($bytes, $offset + 10)
        $status = [BitConverter]::ToInt32($bytes, $offset + 18)
        $items = [BitConverter]::ToInt32($bytes, $offset + 22)
    }
    [pscustomobject]@{ Operation = $operation; StartQpc = $start; DurationMs = $duration; Status = $status; ItemCount = $items; ThreadId = $event.ThreadId }
}

if ($PSCmdlet.ParameterSetName -eq 'Analyze') {
    $out = (Resolve-Path -LiteralPath $AnalyzeDirectory).Path
    $original = Get-Content -Raw -LiteralPath (Join-Path $out 'summary.json') | ConvertFrom-Json
    $Scenario = $original.Scenario
    $RefreshHz = $original.RefreshHz
    $Seconds = $original.RequestedSeconds
    $frequency = $original.QpcFrequency
    $processId = $original.ProcessId
    $csv = Join-Path $out 'presents.csv'
    $operationsCsv = Join-Path $out 'operations.csv'
    $allOperations = @()
    if (Test-Path -LiteralPath $operationsCsv) {
        $allOperations = @(Import-Csv -LiteralPath $operationsCsv | ForEach-Object {
            $_.StartQpc = [long]::Parse($_.StartQpc, $invariant)
            # Early PowerShell captures on some systems wrote decimal commas.
            $_.DurationMs = [double]::Parse($_.DurationMs.Replace(',', '.'), $invariant)
            $_
        } | Sort-Object StartQpc)
    }
    $summaryName = 'motion-summary.json'
} else {
    $app = Get-Process -Name CanvasDesktop
    if (@($app).Count -ne 1) { throw 'Run exactly one Canvas Desktop instance.' }
    $exe = (Resolve-Path -LiteralPath $PresentMonPath).Path
    $out = [IO.Path]::GetFullPath($OutputDirectory)
    if (Test-Path -LiteralPath $out) { throw "Use a new output directory: $out" }
    New-Item -ItemType Directory -Path $out | Out-Null
    $etl = Join-Path $out 'operations.etl'
    $csv = Join-Path $out 'presents.csv'
    $session = 'CanvasMotion-' + [Guid]::NewGuid().ToString('N')
    & logman start "$session-operations" -p "{$provider}" 0xffffffffffffffff 4 -o $etl -ets | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not start the operation trace.' }
    try {
        Write-Host "Start $Scenario in $DelaySeconds seconds and repeat for $Seconds seconds."
        $arguments = @('--process_id', $app.Id, '--session_name', "$session-presents", '--delay', $DelaySeconds,
            '--timed', $Seconds, '--terminate_after_timed', '--no_console_stats', '--no_track_input', '--v1_metrics', '--qpc_time', '--output_file', $csv)
        & $exe @arguments
        if ($LASTEXITCODE -ne 0) { throw "PresentMon failed with exit code $LASTEXITCODE." }
    } finally {
        & logman stop "$session-operations" -ets | Out-Null
    }
    $processId = $app.Id
    $allOperations = @(Get-WinEvent -Path $etl -Oldest | Where-Object {
        $_.ProviderId -eq $provider -and $_.Id -eq 1 -and $_.Version -eq 0 -and $_.ProcessId -eq $processId
    } | ForEach-Object { Read-Operation $_ } | Sort-Object StartQpc)
    if ($allOperations.Count -gt 0) {
        $allOperations | Export-Csv -LiteralPath (Join-Path $out 'operations.csv') -NoTypeInformation
    }
    $summaryName = 'summary.json'
}

if (!(Test-Path -LiteralPath $csv)) { throw 'No Canvas presentation samples were written.' }
$presents = @(Import-Csv -LiteralPath $csv)
if ($presents.Count -eq 0) { throw 'No Canvas presentation samples were recorded.' }
$first = [long]::MaxValue
$last = [long]::MinValue
foreach ($present in $presents) {
    $qpc = [long]::Parse($present.QPCTime, $invariant)
    $first = [Math]::Min($first, $qpc)
    $last = [Math]::Max($last, $qpc)
}

$budgetMs = 1000.0 / $RefreshHz
$ticksPerMs = $frequency / 1000.0
$operations = @($allOperations.Where({ $_.StartQpc -ge $first -and $_.StartQpc -le $last }))
$cameraFrames = @($operations.Where({ $_.Operation -in 'OverviewMotionFrameUi','OverviewSettledFrameUi' }))
$motionIntervals = [Collections.Generic.List[object]]::new()
$gaps = [Collections.Generic.List[double]]::new()
$activeInterval = $null
foreach ($frame in $cameraFrames) {
    if ($frame.Operation -eq 'OverviewSettledFrameUi') {
        $activeInterval = $null
        continue
    }
    if ($null -eq $activeInterval) {
        $activeInterval = [pscustomobject]@{ First = $frame.StartQpc; Last = $frame.StartQpc }
        $motionIntervals.Add($activeInterval)
        continue
    }
    $gaps.Add(($frame.StartQpc - $activeInterval.Last) / $ticksPerMs)
    $activeInterval.Last = $frame.StartQpc
}
$motionIntervals = @($motionIntervals.Where({ $_.Last -gt $_.First }))
$motionTicks = 0L
foreach ($interval in $motionIntervals) { $motionTicks += $interval.Last - $interval.First }

function Test-InMotion([double]$Start, [double]$End) {
    $low = 0
    $high = $motionIntervals.Count - 1
    while ($low -le $high) {
        $middle = $low + (($high - $low) -shr 1)
        $interval = $motionIntervals[$middle]
        if ($Start -lt $interval.First) { $high = $middle - 1 }
        elseif ($Start -gt $interval.Last) { $low = $middle + 1 }
        else { return $End -le $interval.Last }
    }
    return $false
}

$display = @($presents | Group-Object SwapChainAddress | ForEach-Object {
    $displayGaps = [Collections.Generic.List[double]]::new()
    $motionDisplayGaps = [Collections.Generic.List[double]]::new()
    foreach ($row in $_.Group) {
        $ms = 0.0
        if ($row.Dropped -ne '0' -or ![double]::TryParse($row.MsBetweenDisplayChange,
            [Globalization.NumberStyles]::Float, $invariant, [ref]$ms) -or $ms -le 0) { continue }

        $displayGaps.Add($ms)
        $untilDisplayed = 0.0
        if (![double]::TryParse($row.MsUntilDisplayed,
            [Globalization.NumberStyles]::Float, $invariant, [ref]$untilDisplayed)) { continue }

        $end = [long]::Parse($row.QPCTime, $invariant) + $untilDisplayed * $ticksPerMs
        if (Test-InMotion ($end - $ms * $ticksPerMs) $end) { $motionDisplayGaps.Add($ms) }
    }
    [ordered]@{
        SwapChain = $_.Name
        Presents = $_.Count
        Dropped = @($_.Group.Where({ $_.Dropped -eq '1' })).Count
        DisplayGapMs = Summarize $displayGaps
        DisplayGapDuringMotionMs = Summarize $motionDisplayGaps
    }
})
$summary = [ordered]@{
    Scenario = $Scenario
    ProcessId = $processId
    RefreshHz = $RefreshHz
    RequestedSeconds = $Seconds
    ObservedPresentSpanSeconds = ($last - $first) / $frequency
    ObservedMotionSeconds = $motionTicks / $frequency
    MotionIntervalCount = $motionIntervals.Count
    QpcFrequency = $frequency
    FirstQpc = $first
    LastQpc = $last
    OperationCount = $operations.Count
    CameraMotionStartGapMs = Summarize $gaps
    OperationWallMs = @($operations | Group-Object Operation | ForEach-Object {
        [ordered]@{ Operation = $_.Name; WallMs = Summarize $_.Group.DurationMs }
    })
    DisplayPerSwapChain = $display
}
$summaryPath = Join-Path $out $summaryName
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryPath -Encoding utf8
if ($operations.Count -eq 0) { Write-Warning 'No instrumented Canvas operation events matched this capture.' }
if ($summary.ObservedMotionSeconds -lt 30 -and $Scenario -ne 'DesktopHandoff') {
    Write-Warning ('Detected {0:F1} seconds of motion. Aim for at least 30 seconds of active movement when comparing runs.' -f $summary.ObservedMotionSeconds)
}
Write-Output $summaryPath
