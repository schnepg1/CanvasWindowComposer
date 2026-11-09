# Agent Guide

> [!NOTE]
> The original upstream repository (`oreyg/CanvasWindowComposer`) has been archived. This project is actively maintained and developed on the fork under `schnepg1/CanvasWindowComposer`; all development, branches, and Pull Requests must always target the `schnepg1` repository.

Read this file before working in this repository. It covers how to isolate
concurrent agent work and how to run the test suite safely.

## 1. Mandatory Entrypoint: Phase 0 Reconnaissance & Triage

Before creating worktrees, writing code, or running builds, **every agent must execute Phase 0**:

### Step 1: Codebase Reconnaissance (Read-Only)
- Inspect the repository to locate symbols, callers, interfaces, and existing tests relevant to the user's request.
- Assess architectural layers touched (`Source/` WPF UI, `Modules/`, `Native/` Win32/D3D interop, `Tests/`).
- **Do not modify code or start builds during reconnaissance.**

### Step 2: Dynamic Blast Radius Assessment
Evaluate the task against these complexity criteria (no hardcoded keywords):
1. **Architectural Footprint**: Does the change span multiple subsystems, modify public interfaces, or touch more than 2–3 files?
2. **Native & Concurrency Risk**: Does it touch unmanaged Win32 handles (HWND, hooks, DWM thumbnails, DirectX), multi-threading dispatchers (`Dispatcher.Invoke`), or shared state?
3. **Verification Friction**: Does verification require running serialized desktop integration suites (`Tools\Run-Tests.ps1` under the machine-wide named mutex) rather than fast in-memory unit tests?
4. **Merge & Regression Hazard**: Is there significant risk of regression or merge conflicts with active target branches (e.g. `window_linking_workflows` or `main`)?

### Step 3: Triage Decision Gate
- **Route A: Solo Execution (`[TRIAGE: SOLO]`):**
  - Triggered **only if all** answers above are **NO**.
  - Execute directly within a single isolated worktree following the standard lifecycle (implement -> test -> review -> merge -> clean).
- **Route B: Multi-Agent Orchestration (`[TRIAGE: MULTI-AGENT]`):**
  - Triggered **if any** answer above is **YES**.
  - Consult the team catalog in [`.agents/teams/teams.yaml`](.agents/teams/teams.yaml) and workflow transitions in [`.agents/teams/workflow.yaml`](.agents/teams/workflow.yaml).

---

## 2. Multi-Agent Team Orchestration & Role Catalog

For complex tasks (`[TRIAGE: MULTI-AGENT]`), the work is divided across specialized roles located in [`.agents/skills/`](.agents/skills/):

| Role | Skill Directory | Core Mandate |
| :--- | :--- | :--- |
| **Architect** | [`.agents/skills/architect-planning`](.agents/skills/architect-planning) | Read-only analysis; drafts `ARCHITECTURE_PLAN.md` with atomic checklists and native test requirements. |
| **Programmer** | [`.agents/skills/worktree-developer`](.agents/skills/worktree-developer) | Creates isolated worktree, writes code/tests, builds with locked dependencies, runs serialized tests. |
| **Reviewer** | [`.agents/skills/code-reviewer`](.agents/skills/code-reviewer) | Cold-context review of diff vs. spec; audits WPF dispatcher and Win32 handle safety; gives `APPROVED` or `CHANGES_REQUESTED`. |
| **Merger** | [`.agents/skills/branch-merger`](.agents/skills/branch-merger) | Verifies reviewer approval, synchronizes target branch, executes merge under named mutex. |
| **Conflict Resolver** | [`.agents/skills/conflict-resolver`](.agents/skills/conflict-resolver) | Exception-handler role; reconciles 3-way git conflicts, rebuilds, tests, and finalizes commit. |
| **Branch Cleaner** | [`.agents/skills/worktree-cleaner`](.agents/skills/worktree-cleaner) | Verifies running `CanvasDesktop` or `dotnet` processes are stopped before removing worktree and branch. |

### Environment Adaptation:
- **Sub-Agent Capable Environments (Google Antigravity, OpenCode):**
  The entry agent acts as **Mission Orchestrator**, using `invoke_subagent` to delegate to each role in sequence with clean, uncluttered contexts.
- **Single-Agent Environments (Codium / Windsurf, Claude Code, Cursor, Aider):**
  The agent must enforce the same state machine sequentially:
  1. Produce `ARCHITECTURE_PLAN.md` and pause for user review.
  2. Implement and test within an isolated worktree.
  3. Generate a formal self-audit diff review before touching the merge step.
  4. Perform clean post-merge cleanup.

---

## 3. Parallel agents: use a git worktree

Never let two agents build or test in the same working copy. `bin/`, `obj/`,
`TestResults/` and the launcher's publish directories are shared state; concurrent
`dotnet build` or `dotnet test` runs there fail with file locks and stale binaries.

Each agent gets its own worktree and branch:

```powershell
# from the primary checkout
git worktree list
git worktree add "..\CanvasWindowComposer-<task>" -b "agent/<task>" HEAD
```

Then run every command inside that worktree. Do not edit or test the primary
checkout while another agent is using it.

Rules:

- One agent per worktree. Do not share a worktree between sessions.
- New untracked files (local config, logs) are not copied into a new worktree.
- Native and smoke tests still share the logged-in Windows desktop across
  worktrees. Run them through `Tools\Run-Tests.ps1`; it serializes runs with a
  machine-wide mutex.
- Finish by committing on the agent branch, then remove the worktree:
  `git worktree remove "..\CanvasWindowComposer-<task>"`. The branch remains.
- Never run `git worktree remove` while a process is still running from it.

## 4. Testing

Preferred entry point (works from any worktree):

```powershell
Tests\run_tests.bat -Configuration Release
# or directly
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\Run-Tests.ps1 -Configuration Release
```

The runner:

- holds a machine-wide named mutex so only one Canvas test run executes at a time;
- prompts you to close a running `CanvasDesktop` before testing;
- writes each run to `TestResults\run-<timestamp>-<pid>\` so concurrent runs cannot
  overwrite each other's TRX/coverage output;
- runs non-native tests first and the native desktop tests in a second phase.

Useful switches:

| Switch | Meaning |
| --- | --- |
| `-Filter <expr>` | Run one VSTest filter instead of the two standard phases |
| `-NoBuild` | Skip the build step (use after a successful build) |
| `-Smoke` | Also run the AutoHotkey smoke test (opt-in, needs AHK v2 + admin) |
| `-NoSmoke` | Force-disable smoke even when `-Smoke` was given |
| `-NoPrompt` | Fail immediately if Canvas is running instead of prompting |
| `-AllowRunningApp` | Skip the preflight; only use for filtered, non-native tests |
| `-ResultsDirectory <path>` | Override the per-run results directory |

Direct `dotnet test` also works, but it bypasses the runner's mutex. Never run
two direct runs at the same time, especially not with native categories. An
MSBuild preflight runs before VSTest and prompts when Canvas is running. Skip
that preflight only in trusted automation:

```powershell
dotnet test Tests/CanvasDesktop.Tests.csproj -c Release -p:CanvasSkipTestPreflight=true
```

### Native and desktop tests

These tests create real HWNDs, D3D/DWM thumbnails and inspect the foreground
window. They must never overlap across processes, even in different worktrees.
Categories:

- `NativeRendering`
- `NativeParking`
- `NativePlacement`
- `NativePreview`

The runner isolates them with:

```text
Category!=NativeRendering&Category!=NativeParking&Category!=NativePlacement&Category!=NativePreview
```

If you run a custom filter that includes any of these categories, do not start a
second test run until it finishes.

### Canvas must not be running

Tests fail or become flaky when a Canvas instance is active: global hooks, the
overlay and window manipulation interfere with native assertions. Close it from
the tray menu, or run `CanvasDesktop.exe --exit` for a clean shutdown. A forced
kill bypasses window restoration.

### Smoke test

`Tests\smoke_test.ahk` is legacy, requires AutoHotkey v2 and administrator
rights, and kills any running Canvas. It is therefore opt-in (`-Smoke`) and must
never run concurrently with anything else.

### CI

`.github/workflows/windows.yml` restores with locked mode, builds Release and
runs the solution's tests. CI runners are isolated, so the preflight is skipped
automatically when `CI`, `TF_BUILD` or `GITHUB_ACTIONS` is set.

## 5. Build and launch

```powershell
dotnet restore CanvasDesktop.slnx --locked-mode
dotnet build CanvasDesktop.slnx -c Release --no-restore
```

`Start-Canvas.cmd` builds from source, publishes to `publish-workspace` and
launches the app. Options: `--build`, `--diagnose`, `--no-launch`, `--no-dialog`.
Exit a running instance through the tray menu before rebuilding.
