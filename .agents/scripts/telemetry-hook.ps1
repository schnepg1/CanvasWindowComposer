param(
    [string]$Event = "PreToolUse"
)

# Read stdin JSON payload from Antigravity lifecycle hook runner
$inputJson = ""
if ([Console]::IsInputRedirected) {
    $inputJson = [Console]::In.ReadToEnd()
}

$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$stateFile = Join-Path $repoRoot ".agents\teams\state.json"

try {
    $state = @{
        roles = @{}
        updatedAt = [DateTimeOffset]::UtcNow.ToString("o")
    }

    if (Test-Path $stateFile) {
        $existing = Get-Content -Raw $stateFile | ConvertFrom-Json
        if ($existing.roles) {
            foreach ($prop in $existing.roles.PSObject.Properties) {
                $state.roles[$prop.Name] = @{
                    state = $prop.Value.state
                    detail = $prop.Value.detail
                    timestamp = $prop.Value.timestamp
                }
            }
        }
    }

    $toolName = ""
    $command = ""
    if ($inputJson) {
        try {
            $parsed = $inputJson | ConvertFrom-Json
            if ($parsed.toolName) { $toolName = $parsed.toolName }
            if ($parsed.toolArguments -and $parsed.toolArguments.CommandLine) {
                $command = $parsed.toolArguments.CommandLine
            }
        } catch {}
    }

    if ($Event -eq "PreToolUse") {
        if ($toolName -eq "run_command") {
            if ($command -match "Run-Tests|dotnet test") {
                $state.roles["programmer"] = @{ state = "Testing"; detail = "Running test suite"; timestamp = [DateTimeOffset]::UtcNow.ToString("o") }
                $state.roles["reviewer"] = @{ state = "Testing"; detail = "Validating test results"; timestamp = [DateTimeOffset]::UtcNow.ToString("o") }
            } elseif ($command -match "git merge") {
                $state.roles["merger"] = @{ state = "Working"; detail = "Executing branch merge"; timestamp = [DateTimeOffset]::UtcNow.ToString("o") }
            } else {
                $state.roles["programmer"] = @{ state = "Working"; detail = "Executing command"; timestamp = [DateTimeOffset]::UtcNow.ToString("o") }
            }
        } elseif ($toolName -match "edit_file|write_file") {
            $state.roles["programmer"] = @{ state = "Working"; detail = "Modifying source code"; timestamp = [DateTimeOffset]::UtcNow.ToString("o") }
        } elseif ($toolName -match "read_file|search_files") {
            $state.roles["architect"] = @{ state = "Working"; detail = "Investigating architecture"; timestamp = [DateTimeOffset]::UtcNow.ToString("o") }
        }
    } elseif ($Event -eq "Stop") {
        foreach ($k in @($state.roles.Keys)) {
            if ($state.roles[$k].state -eq "Working" -or $state.roles[$k].state -eq "Testing") {
                $state.roles[$k].state = "Done"
                $state.roles[$k].detail = "Phase finished"
            }
        }
    }

    $stateDir = Split-Path -Parent $stateFile
    if (-not (Test-Path $stateDir)) { New-Item -ItemType Directory -Path $stateDir -Force | Out-Null }
    $state | ConvertTo-Json -Depth 5 | Set-Content -Path $stateFile -Encoding utf8
} catch {}

# Output empty JSON as required by Antigravity hook protocol
[Console]::Out.Write("{}")
