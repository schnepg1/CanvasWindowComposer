# Runs on Windows PowerShell 5.1, independently of the app's .NET 10 dependency.
$ErrorActionPreference = 'Stop'
$Build = $false
$NoLaunch = $false
$Diagnose = $false
$NoDialog = $false
$NoSelect = $false
$ForceSelect = $false
$SelectedBranch = $null
$root = Split-Path $PSScriptRoot -Parent
$logDirectory = Join-Path $env:LOCALAPPDATA 'CanvasWindowComposer\Logs'
$log = $null
$stage = $null
$lock = $null

function Write-Detail([string]$message) {
    Write-Host $message
    if ($log) { Add-Content -LiteralPath $log -Value $message }
}

function Invoke-DotNet([string[]]$Arguments) {
    Write-Detail ('dotnet ' + ($Arguments -join ' '))
    $previous = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $script:dotnet @Arguments 2>&1 | ForEach-Object { Write-Detail "$_" }
        $code = $LASTEXITCODE
    } finally { $ErrorActionPreference = $previous }
    if ($code -ne 0) { throw "dotnet failed (exit $code). See the complete output below." }
}

try {
    New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
    $log = Join-Path $logDirectory ('startup-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '.log')
    Write-Detail 'Canvas development launcher - builds from source checkout.'
    Write-Detail "Log: $log"

    $invalid = @()
    $i = 0
    while ($i -lt $args.Count) {
        $token = [string]$args[$i]
        if ($token -match '^[/-]{1,2}([A-Za-z][A-Za-z-]*)(?:=(.*))?$') {
            $name = ($Matches[1] -replace '-', '').ToLowerInvariant()
            $raw = $Matches[2]
            $enabled = if ($raw -and $raw -match '^(?i:false|0|no|off)$') { $false } else { $true }
            switch ($name) {
                'build' { $Build = $enabled }
                'diagnose' { $Diagnose = $enabled }
                'nolaunch' { $NoLaunch = $enabled }
                'nodialog' { $NoDialog = $enabled }
                'select' { $ForceSelect = $true }
                'noselect' { $NoSelect = $true }
                { $_ -in 'branch', 'worktree' } {
                    if ($raw) { $SelectedBranch = $raw }
                    elseif ($i + 1 -lt $args.Count -and $args[$i + 1] -notmatch '^[/-]') {
                        $i++; $SelectedBranch = [string]$args[$i]
                    } else { $ForceSelect = $true }
                }
                default { $invalid += $token }
            }
        } else {
            $invalid += $token
        }
        $i++
    }

    if ($invalid.Count -gt 0) {
        throw "Unknown option(s): $($invalid -join ', '). Supported options: --build, --diagnose, --no-launch, --no-dialog, --branch <name>, --select, --no-select."
    }

    if ([Environment]::OSVersion.Platform -ne 'Win32NT') { throw 'Canvas requires Windows 10/11.' }

    # Discover git worktrees
    $worktrees = @()
    try {
        $wtLines = @(& git worktree list 2>$null)
        foreach ($line in $wtLines) {
            if ($line -match '^(\S.*?)\s+([0-9a-fA-F]+)\s+\[([^\]]+)\]') {
                $wtPath = $Matches[1].Trim()
                $wtBranch = $Matches[3].Trim()
                $normWt = ($wtPath -replace '[\\/]', '\').TrimEnd('\')
                $normRoot = ($root -replace '[\\/]', '\').TrimEnd('\')
                $worktrees += [PSCustomObject]@{
                    Path = $wtPath
                    Branch = $wtBranch
                    Commit = $Matches[2]
                    IsCurrent = $normWt -eq $normRoot
                }
            }
        }
    } catch { }

    if ($SelectedBranch) {
        $match = $worktrees | Where-Object { $_.Branch -like "*$SelectedBranch*" -or $_.Path -like "*$SelectedBranch*" } | Select-Object -First 1
        if ($match) {
            $root = $match.Path
            Write-Detail "Selected worktree: $($match.Branch) ($($match.Path))"
        } else {
            Write-Detail "No worktree matched '$SelectedBranch'. Using current worktree: $root"
        }
    } elseif ($worktrees.Count -gt 1 -and !$NoSelect -and !$NoLaunch -and !$Diagnose -and [Environment]::UserInteractive) {
        $defaultIdx = 1
        for ($w = 0; $w -lt $worktrees.Count; $w++) {
            if ($worktrees[$w].IsCurrent) { $defaultIdx = $w + 1; break }
        }

        Write-Host ""
        Write-Host "==========================================================" -ForegroundColor Cyan
        Write-Host "       Canvas Window Composer - Worktree Launcher         " -ForegroundColor Cyan
        Write-Host "==========================================================" -ForegroundColor Cyan
        Write-Host "Select which branch / worktree to start:`n"
        for ($w = 0; $w -lt $worktrees.Count; $w++) {
            $wt = $worktrees[$w]
            $prefix = if ($wt.IsCurrent) { "* [$($w + 1)]" } else { "  [$($w + 1)]" }
            $color = if ($wt.IsCurrent) { "Green" } else { "White" }
            $tag = if ($wt.IsCurrent) { " (current)" } else { "" }
            Write-Host "$prefix $($wt.Branch)$tag" -ForegroundColor $color
            Write-Host "      $($wt.Path)" -ForegroundColor DarkGray
        }
        Write-Host "----------------------------------------------------------" -ForegroundColor DarkCyan
        Write-Host "Enter selection [1-$($worktrees.Count)] (Press Enter for [$defaultIdx]): " -NoNewline -ForegroundColor Yellow
        $resp = Read-Host
        $chosenIdx = $defaultIdx - 1
        if (![string]::IsNullOrWhiteSpace($resp)) {
            $parsed = 0
            if ([int]::TryParse($resp.Trim(), [ref]$parsed) -and $parsed -ge 1 -and $parsed -le $worktrees.Count) {
                $chosenIdx = $parsed - 1
            }
        }
        $selected = $worktrees[$chosenIdx]
        $root = $selected.Path
        Write-Host "`nLaunching: $($selected.Branch) ($($selected.Path))`n" -ForegroundColor Green
        Write-Detail "Target repository: $root ($($selected.Branch))"
    } else {
        Write-Detail "Repository: $root"
    }

    Set-Location -LiteralPath $root
    $required = (Get-Content -Raw -LiteralPath (Join-Path $root 'global.json') | ConvertFrom-Json).sdk.version
    $script:dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
    if (!$script:dotnet) { throw "The .NET SDK is missing. This launcher compiles Canvas and requires SDK $required (or a compatible patch in the same SDK feature band). Installing only the Desktop Runtime is insufficient. Download: https://dotnet.microsoft.com/download/dotnet/10.0 - alternatively install a self-contained Canvas release." }

    Write-Detail "Required SDK: $required (global.json)"
    $ErrorActionPreference = 'Continue'
    $sdkOutput = @(& $script:dotnet --version 2>&1)
    $sdkExit = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    $sdkOutput | ForEach-Object { Write-Detail "$_" }
    if ($sdkExit -ne 0) {
        & $script:dotnet --list-sdks 2>&1 | ForEach-Object { Write-Detail "$_" }
        throw "A compatible .NET SDK could not be selected. Install SDK $required or a compatible patch in the same feature band: https://dotnet.microsoft.com/download/dotnet/10.0 . A runtime-only installation cannot build this checkout."
    }

    Invoke-DotNet -Arguments @('--list-runtimes')
    $runtimes = & $script:dotnet --list-runtimes
    if (!($runtimes -match '^Microsoft.WindowsDesktop.App 10\.')) { throw 'The .NET 10 Windows Desktop Runtime is missing for this dotnet architecture. Install it from https://dotnet.microsoft.com/download/dotnet/10.0 or use a self-contained Canvas release.' }
    if ($Diagnose) { Write-Detail 'Dependency diagnostics completed.'; exit 0 }

    # If Canvas is already running, gracefully exit it so the new instance can start without single-instance mutex conflict.
    $runningProcesses = @(Get-Process CanvasDesktop -ErrorAction SilentlyContinue)
    if ($runningProcesses.Count -gt 0) {
        foreach ($proc in $runningProcesses) {
            Write-Detail "Canvas is currently running (PID $($proc.Id) from $($proc.Path)). Shutting down cleanly..."
            Write-Host "Canvas is currently running. Shutting down existing instance cleanly..." -ForegroundColor Yellow
            try {
                if ($proc.Path -and (Test-Path -LiteralPath $proc.Path)) {
                    & $proc.Path --exit 2>&1 | Out-Null
                    if ($proc.WaitForExit(8000)) {
                        Write-Host "Existing instance closed cleanly." -ForegroundColor Green
                    }
                }
            } catch { }
        }
    }

    $destination = Join-Path $root 'publish-workspace'
    # One publisher per checkout. Never overwrite a running build or use stale output after failure.
    $lock = [IO.File]::Open((Join-Path $root '.canvas-launch.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    foreach ($process in @(Get-Process CanvasDesktop -ErrorAction SilentlyContinue)) {
        if ($process.Path -and $process.Path.StartsWith($destination + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Canvas is still running from this checkout. Exit through its tray menu, then run Start-Canvas.cmd again. Your running app was left intact.'
        }
    }

    $stage = Join-Path $root ('publish-launch-' + [Guid]::NewGuid().ToString('N'))
    # Always publish from current sources; MSBuild still performs incremental compilation.
    Invoke-DotNet -Arguments @('publish', 'CanvasDesktop.csproj', '-c', 'Release', '--self-contained', 'false', '-o', $stage)
    if (!(Test-Path -LiteralPath (Join-Path $stage 'CanvasDesktop.exe'))) { throw 'Publishing did not produce CanvasDesktop.exe.' }
    if (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Recurse -Force }
    Move-Item -LiteralPath $stage -Destination $destination
    $stage = $null

    if (!$NoLaunch) {
        $process = Start-Process -FilePath (Join-Path $destination 'CanvasDesktop.exe') -WorkingDirectory $destination -PassThru
        if ($process.WaitForExit(3000) -and $process.ExitCode -ne 0) {
            throw "Canvas exited during startup (exit $($process.ExitCode)). Check the .NET Desktop Runtime and the application log at $logDirectory\canvas_debug.log."
        }
        Write-Detail 'Canvas started. Its controls are available from the system tray.'
    }
    exit 0
} catch {
    if ($lock) { $lock.Dispose(); $lock = $null }
    $report = "Canvas could not start.`r`n`r`n$($_.Exception.Message)`r`n`r`n"
    if ($log -and (Test-Path -LiteralPath $log)) { $report += "Log: $log`r`n`r`n" + (Get-Content -Raw -LiteralPath $log) }
    [Console]::Error.WriteLine($report)
    if ($log) { Add-Content -LiteralPath $log -Value "`r`n$($_ | Out-String)" -ErrorAction SilentlyContinue }
    if (!$NoDialog) {
        try {
            Add-Type -AssemblyName System.Windows.Forms
            $form = New-Object Windows.Forms.Form
            $form.Text = 'Canvas - Startup problem'; $form.Width = 820; $form.Height = 540; $form.StartPosition = 'CenterScreen'
            $details = New-Object Windows.Forms.TextBox
            $details.Multiline = $true; $details.ReadOnly = $true; $details.ScrollBars = 'Both'; $details.Dock = 'Fill'; $details.Text = $report
            $copy = New-Object Windows.Forms.Button
            $copy.Text = 'Copy details'; $copy.Dock = 'Bottom'; $copy.Height = 36
            $copy.Add_Click({ try { [Windows.Forms.Clipboard]::SetDataObject($details.Text, $true, 5, 100) } catch { $copy.Text = 'Select text and press Ctrl+C' } })
            $form.Controls.Add($details); $form.Controls.Add($copy)
            $form.ShowDialog() | Out-Null; $form.Dispose()
        } catch { [Console]::Error.WriteLine('Select and copy the console output or open the startup log.') }
    }
    exit 1
} finally {
    if ($stage -and (Test-Path -LiteralPath $stage)) { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue }
    if ($lock) { $lock.Dispose() }
}
