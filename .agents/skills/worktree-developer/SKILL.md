---
name: worktree-developer
description: >-
  Worktree Production Developer skill. Creates isolated git worktrees, writes production
  code and unit/integration tests following Architect plans, compiles with locked
  dependencies, runs serialized tests via Tools/Run-Tests.ps1 under named mutex, and
  iterates with the Reviewer until full approval.
---

# Worktree Developer Skill (`worktree-developer`)

## Role Overview
The **Worktree Developer (Programmer)** is responsible for writing production code and accompanying tests in complete isolation. Following the rules in `AGENTS.md`, developers NEVER build or test directly in the primary checkout because `bin/`, `obj/`, `TestResults/`, and `publish-workspace` are shared state across concurrent processes.

---

## Strict Repository Invariants (from `AGENTS.md`)
1. **Isolated Worktree per Task**:
   Each agent task must have its own worktree and dedicated branch:
   ```powershell
   git worktree list
   git worktree add "..\CanvasWindowComposer-<task>" -b "agent/<task>" HEAD
   ```
2. **Never Share Worktrees**:
   One agent per worktree. Do not share a worktree between sessions or agents.
3. **Canvas Must Not Be Running During Tests**:
   Global hooks, overlay windows, and native desktop interactions interfere with tests.
   Close a running instance via the tray icon or execute:
   ```powershell
   CanvasDesktop.exe --exit
   ```
   Never use forced kill (`taskkill /F`), as that bypasses desktop window restoration.
4. **Machine-Wide Named Mutex Test Runner**:
   Always execute tests through `Tools\Run-Tests.ps1` or `Tests\run_tests.bat`. It acquires `Global\CanvasWindowComposer.Tests` mutex to serialize native tests across all worktrees.

---

## Step-by-Step Procedure

### 1. Worktree & Branch Initialization
From the repository root:
```powershell
$task = "window-linking-feature"
$worktreePath = "..\CanvasWindowComposer-$task"
$branchName = "agent/$task"

# Check existing worktrees
git worktree list

# Create the isolated worktree
git worktree add $worktreePath -b $branchName HEAD

# Change working directory into the worktree
Set-Location -LiteralPath $worktreePath
```

### 2. Dependency Restore and Initial Build
Inside `$worktreePath`:
```powershell
# Restore using locked mode to guarantee deterministic package resolution
dotnet restore CanvasDesktop.slnx --locked-mode

# Build Release configuration
dotnet build CanvasDesktop.slnx -c Release --no-restore
```

### 3. Implement Code and Automated Tests
- Implement the atomic tasks specified in the Architect's `ARCHITECTURE_PLAN.md`.
- Follow clean WPF MVVM architecture and C# conventions:
  - Keep viewmodels decoupled from UI threads (use `IDispatcher` abstractions if applicable).
  - Ensure unmanaged Win32 resources (`HWND`, `HBITMAP`, `D3D` device pointers) implement `IDisposable` or use `SafeHandle`.
- Add comprehensive automated tests under `Tests/`:
  - Unit tests for new logic (tagged without native categories).
  - Integration or native tests if testing physical window placement or thumbnail capture (tagged with appropriate category like `[TestCategory("NativePlacement")]`).

---

### 3.1 WPF Window Ownership & Interop: Preventing "Cannot set Owner to unshown Window"

#### The Issue
When setting ownership between WPF windows or bridging WinForms and WPF, WPF throws:
`System.InvalidOperationException: Cannot set Owner property to a Window that has not been shown previously.`
(German locale: `Die Owner-Eigenschaft kann nicht auf ein "Window" festgelegt werden, das zuvor noch nicht angezeigt wurde.`)

On an STA message loop or marshaled callback, this exception can escape and trigger a modal OS error/JIT dialog that halts automated tests and interrupts the user.

#### Why It Happens
WPF's internal `Window.Owner` setter checks:
```csharp
if (!ownerWindow.IsSourceWindowNull && !ownerWindow.IsVisible)
    throw new InvalidOperationException(SR.Get(SRID.CantSetOwnerToUnshownWindow));
```
This fails when:
1. An owner window's handle is created before it is fully shown (e.g. via `new WindowInteropHelper(owner).Owner = parentHandle` or `interop.EnsureHandle()`). At this point, `!owner.IsSourceWindowNull` is `true`, but `owner.IsVisible` is still `false`.
2. Calling `childWindow.Owner = owner` right before or right after `owner.Show()` before Windows has dispatched `WM_SHOWWINDOW` or pumped the message loop.

#### Required Implementer Patterns

1. **Set ownership in constructor when possible**:
   If both windows are unshown and have not yet created Win32 handles (`IsSourceWindowNull == true` for both), WPF allows `childWindow.Owner = this;` inside the constructor without throwing:
   ```csharp
   _toolbar = new Window { ... };
   try { _toolbar.Owner = this; } catch { }
   ```

2. **Use Win32 HWND fallback if handles already exist**:
   Setting ownership via `WindowInteropHelper.Owner` assigns Win32 `GWLP_HWNDPARENT` directly and bypasses WPF's `IsVisible` guard:
   ```csharp
   if (_childWindow.Owner != this)
   {
       try
       {
           _childWindow.Owner = this;
       }
       catch (InvalidOperationException)
       {
           var ownerHwnd = new WindowInteropHelper(this).Handle;
           if (ownerHwnd != IntPtr.Zero)
               new WindowInteropHelper(_childWindow).Owner = ownerHwnd;
       }
   }
   ```

3. **Never call `childWindow.Owner = this` inside a hot `Show()` path without defensive fallback.**

### 4. Run Test Suite Safely
Run the serialized test runner:
```powershell
# Preferred test entry point
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\Run-Tests.ps1 -Configuration Release -NoPrompt
```

The runner will:
- Acquire `Global\CanvasWindowComposer.Tests` mutex;
- Check that `CanvasDesktop.exe` is not running;
- Run non-native unit tests first;
- Run native desktop tests second;
- Output results to an isolated timestamped directory under `TestResults\run-<timestamp>-<pid>\`.

If any test fails:
1. Review the failure in the test result log.
2. Fix the implementation in your worktree.
3. Re-run `Tools\Run-Tests.ps1 -Configuration Release -NoPrompt` until 100% passing.

### 5. Commit and Submit for Review
Stage and commit changes cleanly on `agent/<task>`:
```powershell
git status
git add -A
git commit -m "feat(window-linking): implement planned window linking workflows and tests"
```

Prepare review payload:
```yaml
task_id: "<task_id>"
branch: "agent/<task>"
worktree_path: "..\CanvasWindowComposer-<task>"
head_commit: "<commit_sha>"
iteration: 1
test_status: "PASSED"
summary: "Implemented window linking logic, attached viewmodels, added 12 unit tests and 2 native integration tests."
```
Invoke or send a message to the **Reviewer**.

### 6. Addressing Review Feedback (Iterative Loop)
When the Reviewer returns `CHANGES_REQUESTED`:
1. Open `REVIEW_REPORT.md` and read all findings (Blockers, Majors, Minors).
2. Apply requested corrections in the worktree.
3. Re-build and re-run `Tools\Run-Tests.ps1 -Configuration Release -NoPrompt`.
4. Commit the fixes:
   ```powershell
   git commit -m "fix(review): address review feedback iteration <n>"
   ```
5. Increment iteration count (up to 3-5 max).
6. Re-submit to Reviewer until `APPROVED` is received.
7. Upon receiving `APPROVED`, notify the **Merger**.
