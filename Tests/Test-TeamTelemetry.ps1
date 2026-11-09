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

    # Capacity rejection must preserve the last valid bytes, including completed tasks.
    $capacityPath = Join-Path $folder 'capacity.json'
    $teams = [ordered]@{}
    foreach ($index in 1..64) { $teams["task-$index"] = @{ taskId = "task-$index"; status = 'Done'; roles = @{} } }
    [IO.File]::WriteAllText($capacityPath, (@{ teams = $teams } | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
    function Assert-RejectedWithoutWrite($path, $arguments, $message) {
        $before = [Convert]::ToBase64String([IO.File]::ReadAllBytes($path))
        $rejected = $false
        try { & $writer @arguments -StatePath $path }
        catch {
            if ($_.Exception.Message -notlike "*$message*") { throw }
            $rejected = $true
        }
        if (-not $rejected) { throw "Expected telemetry rejection: $message" }
        if ($before -ne [Convert]::ToBase64String([IO.File]::ReadAllBytes($path))) { throw 'Rejected update changed the valid state file' }
    }
    Assert-RejectedWithoutWrite $capacityPath @{ TaskId = 'task-65' } '64 registrations'
    & $writer -TaskId task-64 -Role programmer -Detail 'Still updating at capacity' -StatePath $capacityPath
    $snapshot = Get-Content $capacityPath -Raw | ConvertFrom-Json
    if ($snapshot.teams.PSObject.Properties.Name.Count -ne 64 -or $snapshot.teams.'task-64'.roles.programmer.detail -ne 'Still updating at capacity') {
        throw 'Existing task update failed at the registration limit'
    }

    # Unknown preserved fields also count toward the reader's total UTF-8 budget.
    $sizePath = Join-Path $folder 'size.json'
    $padding = ([char]0x00E9).ToString() * ((1048576 - 100) / 2)
    [IO.File]::WriteAllText($sizePath, '{"padding":"' + $padding + '"}', [Text.UTF8Encoding]::new($false))
    Assert-RejectedWithoutWrite $sizePath @{ TaskId = 'size-limit' } '1 MiB'
    $rolesPath = Join-Path $folder 'roles.json'
    $roles = [ordered]@{}
    foreach ($index in 1..64) { $roles["role-$index"] = @{ state = 'Idle' } }
    [IO.File]::WriteAllText($rolesPath, (@{ roles = $roles } | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
    Assert-RejectedWithoutWrite $rolesPath @{ Role = 'programmer' } '64 roles'
    Write-Output 'Team telemetry registration, concurrent writes, legacy compatibility, Stop isolation and reader-limit preservation passed.'
}
finally {
    $env:CANVAS_TEAM_STATE_PATH = $oldStatePath
    $jobs | Remove-Job -Force
    if (Test-Path $folder) { Remove-Item $folder -Recurse -Force }
}
