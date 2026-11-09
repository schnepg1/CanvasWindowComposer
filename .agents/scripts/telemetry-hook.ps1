param(
    [string]$Event = 'PreToolUse',
    [string]$TaskId = $env:CANVAS_TASK_ID,
    [string]$TaskName = $env:CANVAS_TASK_NAME,
    [string]$Role = 'programmer'
)

# Parse telemetry block from teams.yaml
function Get-TelemetryConfigFromYaml([string]$yamlPath) {
    $config = @{
        MutexName = $null
        TestCommandPattern = $null
        CvlTemplate = $null
    }
    if (Test-Path $yamlPath) {
        $content = Get-Content $yamlPath -Raw -ErrorAction SilentlyContinue
        if ($content -match '(?ms)^telemetry:\s*\r?\n(?<block>.*?(?=^[a-zA-Z0-9_\-]+:|\z))') {
            $block = $Matches['block']
            if ($block -match '(?m)^[ \t]+test_command_pattern:\s*(.+)$') {
                $val = $Matches[1].Trim()
                if (($val.StartsWith('"') -and $val.EndsWith('"')) -or ($val.StartsWith("'") -and $val.EndsWith("'"))) {
                    $val = $val.Substring(1, $val.Length - 2)
                }
                $config.TestCommandPattern = $val
            }
        }
    }
    return $config
}

$yamlCandidate = Join-Path $PSScriptRoot '..\teams\teams.yaml'
$telemetryConfig = Get-TelemetryConfigFromYaml $yamlCandidate
$universalTestPattern = 'Run-Tests|dotnet test|pytest|python -m unittest|npm test|yarn test|pnpm test|cargo test|go test|mvn test|gradlew? test'
$testPattern = if ($telemetryConfig.TestCommandPattern) { $telemetryConfig.TestCommandPattern } else { $universalTestPattern }

# Read the lifecycle payload
$inputJson = if ([Console]::IsInputRedirected) { [Console]::In.ReadToEnd() } else { '' }
try {
    $state = if ($Event -eq 'Stop') { 'Done' } else { 'Working' }
    $detail = if ($Event -eq 'Stop') { 'Phase finished' } else { 'Executing tool' }
    if ($Event -ne 'Stop' -and $inputJson) {
        try {
            $parsed = $inputJson | ConvertFrom-Json
            $toolName = ''
            $command = ''
            $targetPath = ''
            if ($parsed.toolName) {
                $toolName = $parsed.toolName
                if ($parsed.toolArguments) {
                    if ($parsed.toolArguments.CommandLine) { $command = $parsed.toolArguments.CommandLine }
                    if ($parsed.toolArguments.Path) { $targetPath = $parsed.toolArguments.Path }
                }
            } elseif ($parsed.toolCall) {
                $toolName = $parsed.toolCall.name
                if ($parsed.toolCall.args) {
                    if ($parsed.toolCall.args.CommandLine) { $command = $parsed.toolCall.args.CommandLine }
                    if ($parsed.toolCall.args.Path) { $targetPath = $parsed.toolCall.args.Path }
                }
            }

            if ($command -and ($command -match $testPattern)) {
                $state = 'Testing'
                $detail = 'Running test suite'
            } elseif ($command -and ($command -match 'git merge')) {
                $Role = 'merger'
                $detail = 'Executing branch merge'
            } elseif ($command -and ($command -match 'git worktree remove')) {
                $Role = 'branch-cleaner'
                $detail = 'Pruning worktree'
            } elseif ($toolName -match 'edit_file|write_file') {
                if ($targetPath -match 'ARCHITECTURE_PLAN') {
                    $Role = 'architect'
                    $detail = 'Authoring architecture plan'
                } else {
                    $detail = 'Modifying source code'
                }
            } elseif ($toolName -match 'read_file|search_files') {
                if ($Role -eq 'reviewer') {
                    $detail = 'Reviewing code changes'
                } else {
                    $detail = 'Investigating source'
                }
            }
        } catch {}
    }

    $setScript = Join-Path $PSScriptRoot 'Set-TeamTelemetry.ps1'
    if (Test-Path $setScript) {
        $arguments = @{ TaskId = $TaskId; Name = $TaskName; Role = $Role; State = $state; Detail = $detail }
        if ($env:CANVAS_TEAM_STATE_PATH) { $arguments.StatePath = $env:CANVAS_TEAM_STATE_PATH }
        & $setScript @arguments

        # Also replicate to AppData if writing default path
        if (-not $env:CANVAS_TEAM_STATE_PATH -and $env:APPDATA) {
            $appDataTarget = Join-Path $env:APPDATA 'CanvasWindowComposer\Teams\state.json'
            try {
                $appDataArgs = @{ TaskId = $TaskId; Name = $TaskName; Role = $Role; State = $state; Detail = $detail; StatePath = $appDataTarget }
                & $setScript @appDataArgs
            } catch {}
        }
    }
} catch {
    [Console]::Error.WriteLine('Team telemetry update failed: ' + $_.Exception.Message)
}

# Required lifecycle-hook protocol response
if ($Event -eq 'PreToolUse') {
    [Console]::Out.Write('{"decision":"allow"}')
} else {
    [Console]::Out.Write('{}')
}
