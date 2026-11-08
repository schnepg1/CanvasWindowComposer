---
name: conflict-resolver
description: >-
  Merge Conflict Resolving Specialist skill. Intervenes when automated merge encounters
  git conflicts. Analyzes three-way conflict markers, reconciles differences preserving
  both upstream changes and new features, compiles, runs tests under named mutex, and
  finalizes the merge.
---

# Conflict Resolver Skill (`conflict-resolver`)

## Role Overview
The **Conflict Resolver (Merge Conflict Resolving Programmer)** is a specialized engineering role called into action when an automated merge fails due to conflicting changes between the agent branch and the target branch.

Unlike a naive merge or blind checkout, the Conflict Resolver possesses deep understanding of:
1. Three-way merge markers (`<<<<<<< HEAD`, `=======`, `>>>>>>>`).
2. C# semantics, namespace imports, class members, and WPF XAML declarations.
3. Solution and project file structure (`.slnx`, `.csproj`, `packages.lock.json`).
4. CanvasWindowComposer invariants from `AGENTS.md`.

---

## Conflict Resolution Invariants
- **Never Blindly Overwrite**: Do not execute blind `git checkout --ours` or `git checkout --theirs` on functional code. Both upstream fixes and agent feature additions must be reconciled.
- **Solution File Hygiene**: In `.slnx` or `.csproj` conflicts, ensure all Project GUIDs, PackageReferences, and Compile items from both sides are preserved without duplicates.
- **Lockfile Discipline**: If `packages.lock.json` conflicts, restore using `dotnet restore CanvasDesktop.slnx --force-evaluate` or update package dependencies deterministically.
- **Mandatory Test Verification**: A conflict resolution is NEVER complete until `Tools\Run-Tests.ps1` runs and passes under the named mutex `Global\CanvasWindowComposer.Tests`.

---

## Step-by-Step Procedure

### 1. Initiate Merge for Conflict Resolution
```powershell
$targetBranch = "window_linking_workflows"
$sourceBranch = "agent/window-linking-feature"

# Checkout target branch
git checkout $targetBranch

# Initiate merge without automatic commit to inspect conflicts
git merge --no-ff --no-commit $sourceBranch
```

### 2. Identify Conflicted Files
```powershell
# List all unmerged files
git status --porcelain | Select-String "^(UU|AA|UD|DU)"
```

### 3. Analyze and Resolve Conflicted Markers
For each conflicted file:
1. Locate the conflict blocks:
   ```text
   <<<<<<< HEAD (Target branch - e.g., window_linking_workflows)
   // Upstream updates
   =======
   // Agent feature updates
   >>>>>>> agent/window-linking-feature
   ```
2. **Semantic Reconciliation**:
   - Merge imports / `using` directives at the top of the file, removing duplicates.
   - For method or member conflicts: incorporate upstream logic changes (e.g. bugfixes, null checks) into the new feature code.
   - In XAML files: ensure XML namespaces, resources, and grid row/column definitions merge without syntax errors or duplicate keys.
3. Remove all conflict markers (`<<<<<<<`, `=======`, `>>>>>>>`).
4. Stage the resolved file:
   ```powershell
   git add <resolved-file-path>
   ```

### 4. Build and Compile Validation
Inside the resolved tree:
```powershell
# Check restore
dotnet restore CanvasDesktop.slnx --locked-mode

# Build Release configuration
dotnet build CanvasDesktop.slnx -c Release --no-restore
```
If compilation fails, inspect the compiler errors, adjust the integrated code, and rebuild until exit code is 0.

### 5. Execute Serialized Test Suite
Verify that the resolution introduced no semantic regressions:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\Run-Tests.ps1 -Configuration Release -NoPrompt
```

### 6. Finalize Merge Commit
Once all tests pass cleanly:
```powershell
git commit -m "Merge branch '$sourceBranch' into $targetBranch (resolved conflicts)"
```

### 7. Handover or Escalation
- **Success**: Hand off to **Branch Cleaner** for worktree and branch removal.
- **Unresolvable Conflict**: If conflicts represent incompatible architectural rewrites that cannot be safely unified:
  ```powershell
  git merge --abort
  ```
  Produce a conflict escalation report detailing conflicting symbols and notify the human developer.
