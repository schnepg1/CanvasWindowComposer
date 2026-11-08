param(
    [string]$Event = "PreToolUse"
)

# Read stdin JSON payload from Antigravity lifecycle hook runner
$inputJson = ""
if ([Console]::IsInputRedirected) {
    $inputJson = [Console]::In.ReadToEnd()
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$agentsDir = Split-Path -Parent $scriptDir
if (Test-Path (Join-Path $agentsDir "teams")) {
    $stateFile = Join-Path $agentsDir "teams\state.json"
} else {
    $repoRoot = Split-Path -Parent $agentsDir
    $stateFile = Join-Path $repoRoot ".agents\teams\state.json"
}

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
            if ($block -match '(?m)^[ \t]+mutex_name:\s*(.+)$') {
                $val = $Matches[1].Trim()
                if ($val.StartsWith('"') -and $val.EndsWith('"')) {
                    $val = $val.Substring(1, $val.Length - 2).Replace('\\', '\')
                } elseif ($val.StartsWith("'") -and $val.EndsWith("'")) {
                    $val = $val.Substring(1, $val.Length - 2)
                }
                $config.MutexName = $val
            }
            if ($block -match '(?m)^[ \t]+test_command_pattern:\s*(.+)$') {
                $val = $Matches[1].Trim()
                if (($val.StartsWith('"') -and $val.EndsWith('"')) -or ($val.StartsWith("'") -and $val.EndsWith("'"))) {
                    $val = $val.Substring(1, $val.Length - 2)
                }
                $config.TestCommandPattern = $val
            }
            if ($block -match '(?m)^[ \t]+cvl_template:\s*(.+)$') {
                $val = $Matches[1].Trim()
                if (($val.StartsWith('"') -and $val.EndsWith('"')) -or ($val.StartsWith("'") -and $val.EndsWith("'"))) {
                    $val = $val.Substring(1, $val.Length - 2)
                }
                $config.CvlTemplate = $val
            }
        }
    }
    return $config
}

$yamlCandidate = Join-Path (Split-Path -Parent $stateFile) "teams.yaml"
if (-not (Test-Path $yamlCandidate)) {
    $yamlCandidate = Join-Path $agentsDir "teams\teams.yaml"
}
$telemetryConfig = Get-TelemetryConfigFromYaml $yamlCandidate

$universalTestPattern = "Run-Tests|dotnet test|pytest|python -m unittest|npm test|yarn test|pnpm test|cargo test|go test|mvn test|gradlew? test"
$testPattern = if ($telemetryConfig.TestCommandPattern) { $telemetryConfig.TestCommandPattern } else { $universalTestPattern }

function Read-JsonWithRetry([string]$path, [int]$maxRetries = 3) {
    for ($i = 0; $i -lt $maxRetries; $i++) {
        try {
            if (Test-Path $path) {
                $raw = Get-Content -Raw $path -ErrorAction Stop
                return ($raw | ConvertFrom-Json)
            }
            return $null
        } catch {
            Start-Sleep -Milliseconds (50 * ($i + 1))
        }
    }
    return $null
}

function Write-JsonWithRetry([string]$path, [string]$content, [int]$maxRetries = 3) {
    for ($i = 0; $i -lt $maxRetries; $i++) {
        try {
            $dir = Split-Path -Parent $path
            if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
            [System.IO.File]::WriteAllText($path, $content, [System.Text.Encoding]::UTF8)
            return
        } catch {
            Start-Sleep -Milliseconds (50 * ($i + 1))
        }
    }
}

try {
    $state = @{
        updatedAt = [DateTimeOffset]::UtcNow.ToString("o")
        activeTaskId = "default"
        roles = @{}
        teams = @{}
    }

    $existing = Read-JsonWithRetry $stateFile
    if ($existing) {
        if ($existing.updatedAt) { $state.updatedAt = $existing.updatedAt }
        if ($existing.activeTaskId) { $state.activeTaskId = $existing.activeTaskId }
        if ($existing.roles) {
            foreach ($prop in $existing.roles.PSObject.Properties) {
                $state.roles[$prop.Name] = @{
                    state = $prop.Value.state
                    detail = $prop.Value.detail
                    timestamp = $prop.Value.timestamp
                }
            }
        }
        if ($existing.teams) {
            foreach ($tProp in $existing.teams.PSObject.Properties) {
                $tObj = $tProp.Value
                $teamRoles = @{}
                if ($tObj.roles) {
                    foreach ($rProp in $tObj.roles.PSObject.Properties) {
                        $teamRoles[$rProp.Name] = @{
                            state = $rProp.Value.state
                            detail = $rProp.Value.detail
                            timestamp = $rProp.Value.timestamp
                        }
                    }
                }
                $state.teams[$tProp.Name] = @{
                    name = $tObj.name
                    conversationId = $tObj.conversationId
                    worktree = $tObj.worktree
                    activeRole = $tObj.activeRole
                    status = if ($tObj.status) { $tObj.status } else { "Active" }
                    updatedAt = $tObj.updatedAt
                    roles = $teamRoles
                }
            }
        }
    }

    $toolName = ""
    $command = ""
    $convId = ""
    $targetPath = ""

    if ($inputJson) {
        try {
            $parsed = $inputJson | ConvertFrom-Json
            if ($parsed.conversationId) { $convId = $parsed.conversationId }
            if ($parsed.toolCall) {
                if ($parsed.toolCall.name) { $toolName = $parsed.toolCall.name }
                if ($parsed.toolCall.args) {
                    if ($parsed.toolCall.args.CommandLine) { $command = $parsed.toolCall.args.CommandLine }
                    if ($parsed.toolCall.args.Path) { $targetPath = $parsed.toolCall.args.Path }
                }
            } elseif ($parsed.toolName) {
                $toolName = $parsed.toolName
                if ($parsed.toolArguments -and $parsed.toolArguments.CommandLine) {
                    $command = $parsed.toolArguments.CommandLine
                }
                if ($parsed.toolArguments -and $parsed.toolArguments.Path) {
                    $targetPath = $parsed.toolArguments.Path
                }
            }
        } catch {}
    }

    # Match or determine target team
    $targetTaskId = $state.activeTaskId
    if ($convId) {
        foreach ($tKey in $state.teams.Keys) {
            if ($state.teams[$tKey].conversationId -eq $convId) {
                $targetTaskId = $tKey
                break
            }
        }
    }

    # Ensure target team entry exists
    if (-not $state.teams.ContainsKey($targetTaskId)) {
        $state.teams[$targetTaskId] = @{
            name = "Team $targetTaskId"
            conversationId = $convId
            worktree = ""
            activeRole = "orchestrator"
            status = "Active"
            updatedAt = [DateTimeOffset]::UtcNow.ToString("o")
            roles = @{}
        }
    }

    $targetTeam = $state.teams[$targetTaskId]

    function Set-TeamRoleState([string]$role, [string]$rState, [string]$rDetail) {
        $now = [DateTimeOffset]::UtcNow.ToString("o")
        $targetTeam.roles[$role] = @{ state = $rState; detail = $rDetail; timestamp = $now }
        $targetTeam.activeRole = $role
        $targetTeam.updatedAt = $now
        $state.roles[$role] = @{ state = $rState; detail = $rDetail; timestamp = $now }
    }

    if ($Event -eq "PreToolUse" -or $Event -eq "PostToolUse") {
        if ($toolName -eq "run_command") {
            if ($command -match $testPattern) {
                $testDetail = if ($telemetryConfig.MutexName) { "Running test suite under named mutex" } else { "Running test suite" }
                Set-TeamRoleState "programmer" "Testing" $testDetail
                Set-TeamRoleState "reviewer" "Testing" "Validating test results"
            } elseif ($command -match "git merge") {
                Set-TeamRoleState "merger" "Working" "Executing branch merge"
            } elseif ($command -match "git worktree remove") {
                Set-TeamRoleState "branch-cleaner" "Working" "Pruning worktree"
            } elseif ($command -match "git checkout|git worktree add") {
                Set-TeamRoleState "programmer" "Working" "Configuring isolated worktree"
            } else {
                Set-TeamRoleState "programmer" "Working" "Executing command: $command"
            }
        } elseif ($toolName -match "edit_file|write_file") {
            if ($targetPath -match "ARCHITECTURE_PLAN") {
                Set-TeamRoleState "architect" "Working" "Authoring architecture plan"
            } else {
                Set-TeamRoleState "programmer" "Working" "Modifying source code"
            }
        } elseif ($toolName -match "read_file|search_files|search_web") {
            if ($targetTeam.activeRole -eq "reviewer") {
                Set-TeamRoleState "reviewer" "Working" "Reviewing code changes"
            } else {
                Set-TeamRoleState "architect" "Working" "Investigating codebase architecture"
            }
        }
    } elseif ($Event -eq "Stop") {
        foreach ($k in @($targetTeam.roles.Keys)) {
            if ($targetTeam.roles[$k].state -eq "Working" -or $targetTeam.roles[$k].state -eq "Testing") {
                $targetTeam.roles[$k].state = "Done"
                $targetTeam.roles[$k].detail = "Phase finished"
            }
        }
        if ($targetTeam.roles.ContainsKey("branch-cleaner") -and $targetTeam.roles["branch-cleaner"].state -eq "Done") {
            $targetTeam.status = "Completed"
        }
        foreach ($k in @($state.roles.Keys)) {
            if ($state.roles[$k].state -eq "Working" -or $state.roles[$k].state -eq "Testing") {
                $state.roles[$k].state = "Done"
                $state.roles[$k].detail = "Phase finished"
            }
        }
    }

    $state.updatedAt = [DateTimeOffset]::UtcNow.ToString("o")
    $jsonOutput = $state | ConvertTo-Json -Depth 6
    Write-JsonWithRetry $stateFile $jsonOutput
} catch {}

if ($Event -eq "PreToolUse") {
    [Console]::Out.Write('{"decision":"allow"}')
} else {
    [Console]::Out.Write('{}')
}
