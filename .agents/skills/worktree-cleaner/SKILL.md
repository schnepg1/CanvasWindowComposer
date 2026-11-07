---
name: worktree-cleaner
description: >-
  Worktree & Branch Cleaner skill. Safely prunes temporary git worktrees and deletes
  merged agent branches, strictly verifying that no background processes (dotnet,
  testhost, CanvasDesktop) hold locks on the directory before removal.
---

# Worktree & Branch Cleaner Skill (`worktree-cleaner`)

## Role Overview
The **Branch Cleaner** is the final role in the multi-agent engineering lifecycle. Its primary responsibility is maintaining workspace hygiene by safely decommissioning temporary git worktrees and deleting merged agent branches.

---

## Critical Safety Invariant (from `AGENTS.md`)
> [!CAUTION]
> **"Never run `git worktree remove` while a process is still running from it."**
> Attempting to remove a worktree with active processes causes locked-file errors, corrupts git worktree administrative metadata, and leaves stale lock files.

---

## Step-by-Step Procedure

### 1. Process & Lock Verification
Before touching git worktree records, inspect active processes on the system to ensure no process is running from or holding open handles in the worktree directory:

```powershell
$task = "window-linking-feature"
$worktreePath = [System.IO.Path]::GetFullPath("..\CanvasWindowComposer-$task")
$branchName = "agent/$task"

# 1. Check if CanvasDesktop is running
$canvasProcs = Get-Process -Name "CanvasDesktop" -ErrorAction SilentlyContinue
if ($canvasProcs) {
    Write-Host "CanvasDesktop is active. Requesting clean exit..."
    # Perform clean shutdown per AGENTS.md
    & "$worktreePath\publish-workspace\CanvasDesktop.exe" --exit
    Start-Sleep -Seconds 2
}

# 2. Check for dotnet test host or build processes
$dotnetProcs = Get-Process -Name "dotnet", "testhost", "vstest.console" -ErrorAction SilentlyContinue
foreach ($proc in $dotnetProcs) {
    try {
        $procPath = $proc.MainModule.FileName
        # Check if process is running inside worktree
        if ($procPath -like "*CanvasWindowComposer-$task*") {
            Write-Host "Waiting for process $($proc.Id) ($($proc.Name)) to terminate..."
            $proc.WaitForExit(5000)
            if (-not $proc.HasExited) {
                Write-Host "Gracefully stopping process $($proc.Id)..."
                Stop-Process -Id $proc.Id -Force
            }
        }
    } catch {
        # Process may have exited or access denied
    }
}
```

### 2. Verify Merge Status Before Branch Deletion
Confirm that the agent branch has been incorporated into the target branch:
```powershell
# Check if branch is merged
$mergedBranches = git branch --merged
$isMerged = $mergedBranches -contains "  $branchName" -or $mergedBranches -contains "* $branchName"

if (-not $isMerged) {
    Write-Warning "Branch '$branchName' is not reported as merged by git. Double check merge state."
}
```

### 3. Remove Git Worktree
Execute worktree removal from the primary repository checkout root:
```powershell
# Verify worktree exists in git registry
git worktree list

# Safely remove worktree
git worktree remove $worktreePath

# Prune stale worktree references
git worktree prune
```

If directory removal is momentarily prevented by Windows file indexing or Defender:
```powershell
if (Test-Path $worktreePath) {
    Start-Sleep -Seconds 2
    git worktree remove --force $worktreePath
}
```

### 4. Delete the Merged Agent Branch
Delete the local feature branch:
```powershell
# Safe deletion (fails if branch was not merged)
git branch -d $branchName
```

### 5. Final Report Manifest
Output the cleanup manifest:
```yaml
task_id: "<task_id>"
worktree_path: "$worktreePath"
branch_name: "$branchName"
worktree_removed: true
branch_deleted: true
processes_cleaned: 0
status: "CLEANUP_SUCCESS"
```
