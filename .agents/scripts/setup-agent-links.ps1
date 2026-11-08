# ==============================================================================
# Cross-Tool Windows Junction Setup Script (setup-agent-links.ps1)
# ==============================================================================
# Description:
#   Creates NTFS directory junctions for cross-tool interoperability on Windows.
#   Links .gemini/skills and .opencode/skills directly to .agents/skills so that
#   Google Antigravity, Gemini CLI, OpenCode, and OpenAI Codex tools can share
#   the exact same physical skills directory without file duplication.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File setup-agent-links.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File setup-agent-links.ps1 -Check
#   powershell -NoProfile -ExecutionPolicy Bypass -File setup-agent-links.ps1 -Remove
#   powershell -NoProfile -ExecutionPolicy Bypass -File setup-agent-links.ps1 -Force
# ==============================================================================

[CmdletBinding()]
param(
    [switch]$Check,
    [switch]$Remove,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

# Determine workspace root (two levels up from .agents/scripts)
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$workspaceRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptDir "..\.."))
$targetSkillsDir = Join-Path $workspaceRoot ".agents\skills"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " Agent Cross-Tool Junction Setup (Windows NTFS)" -ForegroundColor Cyan
Write-Host " Workspace Root: $workspaceRoot" -ForegroundColor DarkGray
Write-Host " Target Skills:  $targetSkillsDir" -ForegroundColor DarkGray
Write-Host "==========================================================" -ForegroundColor Cyan

if (-not (Test-Path -LiteralPath $targetSkillsDir)) {
    throw "Target skills directory does not exist: $targetSkillsDir"
}

# Junction definitions: relative link path from workspace root
$links = @(
    @{
        Name = "Gemini / Antigravity"
        ParentDir = ".gemini"
        LinkPath = ".gemini\skills"
    },
    @{
        Name = "OpenCode / Codex"
        ParentDir = ".opencode"
        LinkPath = ".opencode\skills"
    }
)

function Test-IsJunction([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    $item = Get-Item -LiteralPath $Path -Force
    return ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq [System.IO.FileAttributes]::ReparsePoint
}

function Get-JunctionTarget([string]$Path) {
    if (-not (Test-IsJunction $Path)) { return $null }
    try {
        $item = Get-Item -LiteralPath $Path -Force
        return $item.Target
    } catch {
        return $null
    }
}

function Remove-JunctionSafe([string]$Path) {
    if (Test-IsJunction $Path) {
        Write-Host "  Removing junction: $Path" -ForegroundColor Yellow
        # cmd /c rmdir removes the reparse point without touching target files
        cmd /c "rmdir `"$Path`""
    } elseif (Test-Path -LiteralPath $Path) {
        Write-Warning "  Path is a normal directory, not a junction: $Path"
    }
}

# ------------------------------------------------------------------------------
# Action: Check Only
# ------------------------------------------------------------------------------
if ($Check) {
    Write-Host "`nChecking junction status..." -ForegroundColor Green
    foreach ($entry in $links) {
        $fullLinkPath = Join-Path $workspaceRoot $entry.LinkPath
        $isJunc = Test-IsJunction $fullLinkPath
        if ($isJunc) {
            $target = Get-JunctionTarget $fullLinkPath
            Write-Host "  [OK] $($entry.Name) ($($entry.LinkPath)) -> $target" -ForegroundColor Green
        } elseif (Test-Path -LiteralPath $fullLinkPath) {
            Write-Host "  [EXISTS - NOT A JUNCTION] $($entry.Name) ($($entry.LinkPath))" -ForegroundColor Yellow
        } else {
            Write-Host "  [NOT CREATED] $($entry.Name) ($($entry.LinkPath))" -ForegroundColor DarkGray
        }
    }
    return
}

# ------------------------------------------------------------------------------
# Action: Remove Junctions
# ------------------------------------------------------------------------------
if ($Remove) {
    Write-Host "`nRemoving junctions..." -ForegroundColor Yellow
    foreach ($entry in $links) {
        $fullLinkPath = Join-Path $workspaceRoot $entry.LinkPath
        Remove-JunctionSafe $fullLinkPath
    }
    Write-Host "Junction removal complete." -ForegroundColor Green
    return
}

# ------------------------------------------------------------------------------
# Action: Create / Sync Junctions
# ------------------------------------------------------------------------------
Write-Host "`nConfiguring directory junctions..." -ForegroundColor Green

foreach ($entry in $links) {
    $parentPath = Join-Path $workspaceRoot $entry.ParentDir
    $fullLinkPath = Join-Path $workspaceRoot $entry.LinkPath

    # 1. Ensure parent directory exists (.gemini, .opencode)
    if (-not (Test-Path -LiteralPath $parentPath)) {
        Write-Host "  Creating parent directory: $($entry.ParentDir)" -ForegroundColor DarkGray
        New-Item -ItemType Directory -Path $parentPath -Force | Out-Null
    }

    # 2. Check if link already exists
    if (Test-Path -LiteralPath $fullLinkPath) {
        if (Test-IsJunction $fullLinkPath) {
            $target = Get-JunctionTarget $fullLinkPath
            if ($target -and ($target.TrimEnd('\') -eq $targetSkillsDir.TrimEnd('\') -or $target -like "*\.agents\skills*")) {
                Write-Host "  [CURRENT] $($entry.Name) ($($entry.LinkPath)) already points to target." -ForegroundColor DarkGray
                continue
            } else {
                Write-Host "  [UPDATE] Existing junction points to $target. Recreating..." -ForegroundColor Yellow
                Remove-JunctionSafe $fullLinkPath
            }
        } else {
            if ($Force) {
                Write-Host "  [FORCE] Replacing existing folder $($entry.LinkPath) with junction..." -ForegroundColor Yellow
                Remove-Item -LiteralPath $fullLinkPath -Recurse -Force
            } else {
                Write-Warning "Directory '$($entry.LinkPath)' exists and is not a junction. Use -Force to overwrite."
                continue
            }
        }
    }

    # 3. Create NTFS Directory Junction
    Write-Host "  Linking $($entry.LinkPath) -> $targetSkillsDir" -ForegroundColor Cyan
    try {
        New-Item -ItemType Junction -Path $fullLinkPath -Target $targetSkillsDir | Out-Null
        Write-Host "  [SUCCESS] Created junction for $($entry.Name)" -ForegroundColor Green
    } catch {
        Write-Host "  Falling back to cmd mklink /J..." -ForegroundColor DarkGray
        cmd /c "mklink /J `"$fullLinkPath`" `"$targetSkillsDir`""
    }
}

Write-Host "`nAll junctions configured successfully." -ForegroundColor Green
Write-Host "Antigravity, OpenCode, and Codex will now discover skills under .agents/skills directly." -ForegroundColor Cyan
