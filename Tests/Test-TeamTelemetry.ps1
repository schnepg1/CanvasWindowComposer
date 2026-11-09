$ErrorActionPreference = 'Stop'
$writer = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\.agents\scripts\Set-TeamTelemetry.ps1'))
$hook = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\.agents\scripts\telemetry-hook.ps1'))
$folder = Join-Path ([IO.Path]::GetTempPath()) ('canvas-team-script-' + [Guid]::NewGuid().ToString('N'))
$statePath = Join-Path $folder 'state.json'
$jobs = @()
$oldStatePath = $env:CANVAS_TEAM_STATE_PATH
try {
    & $writer -TaskId task-a -Name 'Feature A' -Role architect -Detail 'Planning' -StatePath $statePath
    & $writer -TaskId task-b -Name 'Feature B' -Role programmer -State Testing -Detail 'Testing B' -StatePath $statePath
    & $writer -TaskId task-a -Role programmer -Detail 'Editing A' -StatePath $statePath
    # A legacy update must preserve task registrations.
    & $writer -Role programmer -State Blocked -Detail 'Legacy card' -StatePath $statePath
    $snapshot = Get-Content $statePath -Raw | ConvertFrom-Json
    if ($snapshot.teams.PSObject.Properties.Name.Count -ne 2) { throw 'Expected two teams' }
    if ($snapshot.teams.'task-a'.roles.architect.detail -ne 'Planning') { throw 'Lost architect report' }
    if ($snapshot.teams.'task-b'.roles.programmer.state -ne 'Testing') { throw 'Cross-task corruption' }
    if ($snapshot.roles.programmer.detail -ne 'Legacy card') { throw 'Legacy telemetry was lost' }
    # Simulate independent agent processes writing the same file concurrently.
    $jobs = 1..4 | ForEach-Object {
        Start-Job -ScriptBlock {
            param($writer, $path, $index)
            & $writer -TaskId "parallel-$index" -Role programmer -Detail "Agent $index" -StatePath $path
        } -ArgumentList $writer, $statePath, $_
    }
    $jobs | Wait-Job | Receive-Job -ErrorAction Stop
    $snapshot = Get-Content $statePath -Raw | ConvertFrom-Json
    if ($snapshot.teams.PSObject.Properties.Name.Count -ne 6) { throw 'Concurrent registration lost a task' }
    foreach ($index in 1..4) {
        if ($snapshot.teams."parallel-$index".roles.programmer.detail -ne "Agent $index") { throw 'Concurrent report was lost' }
    }
    $env:CANVAS_TEAM_STATE_PATH = $statePath
    & $hook -Event Stop -TaskId task-a -Role programmer
    $snapshot = Get-Content $statePath -Raw | ConvertFrom-Json
    if ($snapshot.teams.'task-a'.roles.programmer.state -ne 'Done') { throw 'Stop failed to finish this role' }
    if ($snapshot.teams.'task-b'.roles.programmer.state -ne 'Testing') { throw 'Stop changed another task' }
    if ($snapshot.teams.'task-a'.roles.architect.state -ne 'Working') { throw 'Stop changed another role' }
    Write-Output 'Team telemetry registration, concurrent writes, legacy compatibility and Stop isolation passed.'
}
finally {
    $env:CANVAS_TEAM_STATE_PATH = $oldStatePath
    $jobs | Remove-Job -Force
    if (Test-Path $folder) { Remove-Item $folder -Recurse -Force }
}
