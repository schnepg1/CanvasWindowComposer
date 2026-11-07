---
name: branch-merger
description: >-
  Automated Branch Merger skill. Validates reviewer approval, verifies target branch
  freshness, executes clean git merge into target branch, runs post-merge tests under
  named mutex, and routes conflicts to the conflict-resolver.
---

# Branch Merger Skill (`branch-merger`)

## Role Overview
The **Branch Merger** acts as the integration gatekeeper. Once a feature branch has received an `APPROVED` verdict from the Reviewer, the Merger integrates the work into the target branch (e.g., `window_linking_workflows` or `main`).

The Merger ensures that:
1. No unapproved or failing code enters the target branch.
2. The target branch is cleanly synchronized before merging.
3. If git conflicts occur, the merge is safely aborted without leaving dirty conflict state, and execution is routed to the **Conflict Resolver**.
4. The merged codebase compiles cleanly and passes the full serialized test suite.

---

## Pre-Merge Invariants
- **Review Verdict**: Must be strictly `APPROVED`.
- **Target Branch**: Must be clean with no uncommitted changes.
- **Mutex Awareness**: When executing post-merge tests, use `Tools\Run-Tests.ps1` to honor the machine-wide named mutex `Global\CanvasWindowComposer.Tests`.

---

## Step-by-Step Procedure

### 1. Verify Approval & Target Freshness
```powershell
$targetBranch = "window_linking_workflows"
$sourceBranch = "agent/window-linking-feature"

# Ensure we are in the primary workspace checkout
git checkout $targetBranch

# Verify working tree is pristine
$status = git status --porcelain
if ($status) {
    throw "Target branch working tree is dirty. Stash or clean before merging."
}

# Pull upstream changes if tracking remote exists
git pull --ff-only
```

### 2. Execute Git Merge
Attempt a non-fast-forward merge to preserve branch history:
```powershell
# Attempt merge
git merge --no-ff $sourceBranch -m "Merge branch '$sourceBranch' into $targetBranch"
```

### 3. Conflict Detection & Branch Routing
Check the merge status:
- **Case A: Clean Merge (Exit Code 0)**:
  Proceed immediately to Step 4 (Post-Merge Verification).

- **Case B: Git Conflict Detected (Exit Code != 0 or unmerged paths)**:
  1. Abort the broken merge immediately to restore clean state:
     ```powershell
     git merge --abort
     ```
  2. Gather list of conflicting files:
     ```powershell
     # List files that differ between target and source
     git diff --name-only --diff-filter=U
     ```
  3. Hand over execution to the **Conflict Resolver**:
     ```yaml
     status: "ROUTED_TO_CONFLICT_RESOLVER"
     task_id: "<task_id>"
     target_branch: "$targetBranch"
     source_branch: "$sourceBranch"
     reason: "Automated merge encountered git conflicts."
     ```

### 4. Post-Merge Build & Test Verification
Run the solution build and serialized test runner on the target branch:
```powershell
# Restore locked packages
dotnet restore CanvasDesktop.slnx --locked-mode

# Build Release
dotnet build CanvasDesktop.slnx -c Release --no-restore

# Run serialized tests
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\Run-Tests.ps1 -Configuration Release -NoPrompt
```

### 5. Final Handover
If tests pass:
```yaml
status: "MERGE_SUCCESS"
target_branch: "$targetBranch"
merged_branch: "$sourceBranch"
merge_commit: "<commit_sha>"
test_status: "PASSED"
next_step: "NOTIFY_BRANCH_CLEANER"
```
Notify the **Branch Cleaner** to safely remove the worktree and delete the merged agent branch.
