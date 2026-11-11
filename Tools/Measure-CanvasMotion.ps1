#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PresentMonPath,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][ValidateSet('Pan','Zoom','Flight','DesktopHandoff')][string]$Scenario,
    [Parameter(Mandatory)][ValidateRange(20,1000)][double]$RefreshHz,
    [ValidateRange(30,180)][int]$Seconds = 30,
    [ValidateRange(0,60)][int]$DelaySeconds = 5
)

$ErrorActionPreference = 'Stop'
$provider = [guid]'854023e4-c25a-4841-8d6e-7307c236be53'
$invariant = [Globalization.CultureInfo]::InvariantCulture
$frequency = [Diagnostics.Stopwatch]::Frequency
$budgetMs = 1000.0 / $RefreshHz

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

$allOperations = @(Get-WinEvent -Path $etl -Oldest | Where-Object {
    $_.ProviderId -eq $provider -and $_.Id -eq 1 -and $_.Version -eq 0 -and $_.ProcessId -eq $app.Id
} | ForEach-Object { Read-Operation $_ } | Sort-Object StartQpc)
if ($allOperations.Count -gt 0) { $allOperations | Export-Csv -LiteralPath (Join-Path $out 'operations.csv') -NoTypeInformation }
$operations = @($allOperations.Where({ $_.StartQpc -ge $first -and $_.StartQpc -le $last }))

$cameraFrames = @($operations.Where({ $_.Operation -in 'OverviewMotionFrameUi','OverviewSettledFrameUi' }))
$gaps = for ($i = 1; $i -lt $cameraFrames.Count; $i++) {
    if ($cameraFrames[$i].Operation -eq 'OverviewMotionFrameUi' -and $cameraFrames[$i-1].Operation -eq 'OverviewMotionFrameUi') {
        1000.0 * ($cameraFrames[$i].StartQpc - $cameraFrames[$i-1].StartQpc) / $frequency
    }
}
$display = @($presents | Group-Object SwapChainAddress | ForEach-Object {
    $displayGaps = foreach ($row in $_.Group) {
        $ms = 0.0
        if ($row.Dropped -eq '0' -and [double]::TryParse($row.MsBetweenDisplayChange,
            [Globalization.NumberStyles]::Float, $invariant, [ref]$ms) -and $ms -gt 0) { $ms }
    }
    [ordered]@{
        SwapChain = $_.Name
        Presents = $_.Count
        Dropped = @($_.Group.Where({ $_.Dropped -eq '1' })).Count
        DisplayGapMs = Summarize $displayGaps
    }
})
$summary = [ordered]@{
    Scenario = $Scenario
    ProcessId = $app.Id
    RefreshHz = $RefreshHz
    RequestedSeconds = $Seconds
    ObservedPresentSpanSeconds = ($last - $first) / $frequency
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
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $out 'summary.json') -Encoding utf8
if ($operations.Count -eq 0) { Write-Warning 'No instrumented Canvas operation events matched this capture.' }
Write-Output (Join-Path $out 'summary.json')
