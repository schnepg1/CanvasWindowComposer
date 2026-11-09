param(
    [string]$Event = 'PreToolUse',
    [string]$TaskId = $env:CANVAS_TASK_ID,
    [string]$TaskName = $env:CANVAS_TASK_NAME,
    [string]$Role = 'programmer'
)

# Read the lifecycle payload; only this invocation's task and role are updated.
$inputJson = if ([Console]::IsInputRedirected) { [Console]::In.ReadToEnd() } else { '' }
try {
    $state = if ($Event -eq 'Stop') { 'Done' } else { 'Working' }
    $detail = if ($Event -eq 'Stop') { 'Phase finished' } else { 'Executing tool' }
    if ($Event -ne 'Stop' -and $inputJson) {
        $parsed = $inputJson | ConvertFrom-Json
        $command = $parsed.toolArguments.CommandLine
        if ($command -match 'Run-Tests|dotnet test') { $state = 'Testing'; $detail = 'Running test suite' }
        elseif ($parsed.toolName -match 'edit_file|write_file') { $detail = 'Modifying source code' }
        elseif ($parsed.toolName -match 'read_file|search_files') { $detail = 'Investigating source' }
    }
    $arguments = @{ TaskId = $TaskId; Name = $TaskName; Role = $Role; State = $state; Detail = $detail }
    if ($env:CANVAS_TEAM_STATE_PATH) { $arguments.StatePath = $env:CANVAS_TEAM_STATE_PATH }
    & (Join-Path $PSScriptRoot 'Set-TeamTelemetry.ps1') @arguments
} catch { [Console]::Error.WriteLine('Team telemetry update failed: ' + $_.Exception.Message) }
# Required lifecycle-hook protocol response.
[Console]::Out.Write('{}')
