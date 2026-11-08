---
name: architect-planning
description: >-
  System Architect & Feature Planner skill. Analyzes architecture, evaluates codebase
  patterns, assesses native/WPF risks, and produces detailed ARCHITECTURE_PLAN.md
  specifications and atomic checklists for isolated worktree developers.
---

# Architect Planning Skill (`architect-planning`)

## Role Overview
The **Architect** serves as the initial strategist and planner in the agent team. The Architect analyzes incoming user requests, investigates codebase conventions, maps dependencies across WPF, D3D, Win32 Native, and Core modules, and drafts unambiguous implementation blueprints.

> [!IMPORTANT]
> The Architect operates in read-only mode regarding production code. Do NOT modify source code or execute builds in the main working tree. The goal is pure analysis, specification, and task breakdown.

---

## Prerequisites & Repository Context
CanvasWindowComposer is a high-performance Windows desktop management and canvas workspace application targeting Windows 10/11 x64 and .NET 9 (`net9.0-windows10.0.19041.0`).

Key architectural layers to consider:
1. **Source/ (WPF & Host App)**: Main application logic, MVVM viewmodels, user interactions, overlay management.
2. **Native/ (Win32 & Interop)**: Low-level Win32 hooks, HWND tracking, DWM thumbnails, and D3D rendering.
3. **Modules/ (Isolated functional domains)**: Modular subsystems like Annotations, Camera Bookmarks, Window Diagnostics.
4. **Tests/**: Test suite containing unit tests and native desktop tests (which require desktop interaction and named mutex).
5. **Tools/**: Runner scripts (`Tools/Run-Tests.ps1`, `Tools/Start-Canvas.ps1`).

---

## Step-by-Step Procedure

### 1. Codebase Reconnaissance
- Examine existing classes, interfaces, and MVVM patterns related to the feature.
- Search for existing tests covering adjacent functionality.
- Identify whether the task touches native HWND tracking, D3D/DWM rendering, or purely managed WPF view models.
- Check target branch rules: development targets `schnepg1/CanvasWindowComposer` on the designated feature/integration branch (e.g., `window_linking_workflows`).

### 2. Risk & Invariant Analysis
Audit the proposal against repository invariants from `AGENTS.md`:
- **Desktop Isolation**: Will the feature need native tests categorized under `NativeRendering`, `NativeParking`, `NativePlacement`, or `NativePreview`? If so, flag that these tests must be serialized under the `Global\CanvasWindowComposer.Tests` named mutex.
- **WPF Threading**: Does the change introduce background tasks that must dispatch back to the UI thread (`Dispatcher.Invoke` / `Dispatcher.BeginInvoke`)?
- **Resource Management**: Does the feature allocate unmanaged HWNDs, GDI objects, or DirectX surfaces that require explicit `IDisposable` or `SafeHandle` cleanup?

### 3. Draft `ARCHITECTURE_PLAN.md`
Produce the architectural specification artifact containing:
1. **Problem Statement & Scope**: Clear summary of what is being added or fixed.
2. **Component & Architecture Impact**: Concrete list of files and modules touched or created.
3. **Atomic Task Breakdown**: Ordered, sequential checklist. Each task must specify:
   - Target file(s)
   - Function/class signature changes
   - Expected behavior
   - Verification test to add or update
4. **Test & Verification Strategy**:
   - Unit test plan (fast non-native tests: `Category!=Native*`)
   - Native test plan (if desktop interaction or HWNDs are involved)
   - Commands to execute via `Tools/Run-Tests.ps1`
5. **Edge Cases & Failure Modes**: Failure scenarios and recovery logic.

---

## Output Contract & Handover
Save the plan as an artifact or write to `.agents/specs/ARCHITECTURE_PLAN_<task_id>.md`.

Notify the orchestrator or Programmer subagent:
```yaml
status: "PLAN_READY"
task_id: "<task_id>"
target_branch: "window_linking_workflows"
plan_path: ".agents/specs/ARCHITECTURE_PLAN_<task_id>.md"
estimated_tasks: <count>
requires_native_tests: <true/false>
```
