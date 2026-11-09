param(
    [ValidatePattern('^$|^[a-zA-Z0-9][a-zA-Z0-9._-]{0,199}$')][string]$TaskId = '',
    [ValidateLength(0, 100)][string]$Name = "",
    [ValidateSet('orchestrator', 'architect', 'programmer', 'reviewer', 'merger', 'conflict-resolver', 'branch-cleaner')]
    [string]$Role = 'orchestrator',
    [ValidateSet('Idle', 'Working', 'Testing', 'Blocked', 'Done', 'Error')][string]$State = 'Working',
    [ValidateLength(0, 1000)][string]$Detail = "",
    [ValidateSet('', 'Active', 'Done', 'Archived')][string]$TaskStatus = '',
    [string]$StatePath = $env:CANVAS_TEAM_STATE_PATH
)

$ErrorActionPreference = 'Stop'
if (-not $StatePath) { $StatePath = Join-Path $PSScriptRoot '..\teams\state.json' }
$StatePath = [IO.Path]::GetFullPath($StatePath)
$directory = [IO.Path]::GetDirectoryName($StatePath)
[IO.Directory]::CreateDirectory($directory) | Out-Null
# All agents writing the shared file use the same lock across their worktrees.
$sha = [Security.Cryptography.SHA256]::Create()
try { $hash = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($StatePath.ToUpperInvariant()))).Replace('-', '') }
finally { $sha.Dispose() }
$mutex = [Threading.Mutex]::new($false, "Local\CanvasWindowComposer.Telemetry.$hash")
$held = $false
$temporary = "$StatePath.$([Guid]::NewGuid().ToString('N')).tmp"
try {
    try { $held = $mutex.WaitOne(10000) } catch [Threading.AbandonedMutexException] { $held = $true }
    if (-not $held) { throw 'Timed out waiting to update team telemetry.' }
    $root = if (Test-Path -LiteralPath $StatePath) { Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
    if ($TaskId) {
        if (-not $root.teams) { $root | Add-Member -NotePropertyName teams -NotePropertyValue ([pscustomobject]@{}) -Force }
        $teamProperty = $root.teams.PSObject.Properties[$TaskId]
        if ($teamProperty) { $team = $teamProperty.Value }
        else {
            $team = [pscustomobject]@{ taskId = $TaskId; name = $TaskId.Substring(0, [Math]::Min(100, $TaskId.Length)); status = 'Active'; activeRole = ''; roles = [pscustomobject]@{} }
            $root.teams | Add-Member -NotePropertyName $TaskId -NotePropertyValue $team
        }
        foreach ($field in @('taskId', 'name', 'status', 'activeRole', 'roles')) {
            if (-not $team.PSObject.Properties[$field]) {
                $value = switch ($field) { 'taskId' { $TaskId } 'name' { $TaskId.Substring(0, [Math]::Min(100, $TaskId.Length)) } 'status' { 'Active' } 'roles' { [pscustomobject]@{} } default { '' } }
                $team | Add-Member -NotePropertyName $field -NotePropertyValue $value
            }
        }
        if ($team.taskId -ne $TaskId) { throw 'Task key and taskId must match.' }
        if ($Name) { $team.name = $Name }
        if ($TaskStatus) { $team.status = $TaskStatus }
        if ($State -in @('Working', 'Testing', 'Blocked', 'Error')) { $team.activeRole = $Role }
        elseif ($team.activeRole -eq $Role) { $team.activeRole = '' }
    } else {
        # Keep old hook configurations working without inventing a task identity.
        $team = $root
        if (-not $team.roles) { $team | Add-Member -NotePropertyName roles -NotePropertyValue ([pscustomobject]@{}) -Force }
    }
    $report = [pscustomobject]@{ state = $State; detail = $Detail; timestamp = [DateTimeOffset]::UtcNow.ToString('o'); worktree = (Get-Location).Path }
    $team.roles | Add-Member -NotePropertyName $Role -NotePropertyValue $report -Force
    $root | Add-Member -NotePropertyName updatedAt -NotePropertyValue ([DateTimeOffset]::UtcNow.ToString('o')) -Force
    [IO.File]::WriteAllText($temporary, ($root | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
    if (Test-Path -LiteralPath $StatePath) { [IO.File]::Replace($temporary, $StatePath, [NullString]::Value) }
    else { [IO.File]::Move($temporary, $StatePath) }
}
finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
    if ($held) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
