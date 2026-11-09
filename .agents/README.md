# Multi-Agent Orchestration & Teamwork Rig

A company-agnostic, vendor-neutral multi-agent software engineering rig designed for **CanvasWindowComposer**.

For automatic task pod registration, live status badges, and movable saved containers,
see [Agent team pods](../Docs/team-pods.md). Agents report through
[`Set-TeamTelemetry.ps1`](scripts/Set-TeamTelemetry.ps1) using a shared state path.

This rig coordinates specialized AI agent roles across isolated git worktrees, enforces strict peer review loops, integrates automated merges with intelligent conflict resolution, and guarantees repository hygiene in compliance with [`AGENTS.md`](../AGENTS.md).

---

## 1. Architecture Overview

Modern AI pair programming and multi-agent coordination requires strict isolation, deterministic role contracts, and robust state management. This rig decouples role capabilities from vendor-specific agent platforms (Google Antigravity, Gemini CLI, OpenAI Codex, OpenCode, Claude Code), providing a unified schema:

```mermaid
flowchart TD
    User([User Request / Task]) --> S1[Stage 1: Architect / Planner]
    S1 -->|ARCHITECTURE_PLAN.md| S2A[Stage 2a: Programmer / Worktree Developer]
    
    subgraph DevReviewLoop ["Development & Review Loop (Max 3-5 iterations)"]
        S2A -->|Build & Serialized Tests| S2B{Tests Pass?}
        S2B -- No --> S2A
        S2B -- Yes --> S2C[Stage 2b: Code Reviewer]
        S2C -->|Verdict| S2D{Review Verdict}
        S2D -- CHANGES_REQUESTED -->|REVIEW_REPORT.md| S2A
    end

    S2D -- APPROVED --> S3[Stage 3: Merger / Branch Merger]
    
    subgraph MergePhase ["Merge & Verification Phase"]
        S3 -->|git merge --no-ff| S3A{Clean Merge?}
        S3A -- No: Conflicts --> S3B[Stage 3b: Merge Conflict Resolver]
        S3B -->|Semantic Resolution & Run-Tests.ps1| S3C{Tests Pass?}
        S3C -- Yes --> S3D[Finalize Merge Commit]
        S3A -- Yes --> S3E[Post-Merge Test Verification]
        S3C -- No --> S3F[Escalate to Human]
        S3D --> S4[Stage 4: Branch Cleaner]
        S3E --> S4
    end

    subgraph CleanupPhase ["Worktree & Process Hygiene"]
        S4 -->|Verify Processes & Locks| S4A[Stop Running Canvas/dotnet]
        S4A -->|git worktree remove| S4B[Remove Worktree]
        S4B -->|git branch -d| S4C[Delete Merged Branch]
    end

    S4C --> Done([Task Completed Successfully])
```

---

## 2. Role Catalog

All roles are formally defined in [`.agents/teams/teams.yaml`](./teams/teams.yaml) with strict input/output contracts, permissions, and tool allocations:

| Role | Name | Primary Goal | Workspace Mode | Core Tools |
| :--- | :--- | :--- | :--- | :--- |
| **1. Architect** | System Architect & Planner | Analyzes system architecture, inspects codebase, writes `ARCHITECTURE_PLAN.md` with atomic checklists. | `inherit` (read-only) | `read_file`, `search_files`, `codebase_explorer` |
| **2. Programmer** | Worktree Production Developer | Implements code in an isolated worktree, compiles with locked dependencies, runs serialized tests under named mutex. | `isolated-worktree` (`..\CanvasWindowComposer-<task>`) | `edit_file`, `write_file`, `run_command`, `git_cli`, `dotnet_cli` |
| **3. Reviewer** | Code Quality & Invariants Reviewer | Audits diffs, checks WPF dispatcher safety, unmanaged Win32/D3D resource disposal, and issues `APPROVED` or `CHANGES_REQUESTED`. | `inherit` (read-only diff) | `git_diff`, `read_file`, `write_artifact` |
| **4. Merger** | Automated Branch Merger | Synchronizes target branch, validates reviewer approval, runs tests under named mutex, and executes clean merge. | `target-checkout` (`window_linking_workflows`) | `git_cli`, `test_runner` |
| **5. Conflict Resolver** | Merge Conflict Resolving Specialist | Resolves three-way merge markers intelligently, reconciles conflicting logic, rebuilds, tests under mutex, and completes commit. | `merge-workspace` | `edit_file`, `git_cli`, `dotnet_cli`, `test_runner` |
| **6. Branch Cleaner** | Worktree & Branch Cleaner | Checks for running `CanvasDesktop` or `dotnet` processes, removes git worktree cleanly, and prunes merged branch. | `inherit` (repository root) | `process_query`, `git_cli`, `filesystem_tools` |

---

## 3. Modular Team Constellations

Rather than enforcing a single rigid workflow, [`.agents/teams/teams.yaml`](./teams/teams.yaml) and [`.agents/teams/workflow.yaml`](./teams/workflow.yaml) support multiple configurable **Team Constellations**:

### 1. `feature-lifecycle-team` (Default)
- **Roles**: Architect, Programmer, Reviewer, Merger, Conflict Resolver, Branch Cleaner.
- **Workflow**: `feature-lifecycle`
- **Use Case**: Major features, cross-cutting refactoring, new UI/native capabilities (e.g. Window Linking Workflows).
- **Review Loop**: Iterates between Programmer and Reviewer up to 5 times (default 3) until full approval.

### 2. `quick-bugfix-team`
- **Roles**: Programmer, Reviewer, Merger, Branch Cleaner.
- **Workflow**: `quick-bugfix`
- **Use Case**: Focused defect repairs, flaky test stabilization, minor parameter tuning with pre-existing reproduction test cases.
- **Path**: Bypasses heavy architectural planning and launches directly in an isolated worktree.

### 3. `research-spike-team`
- **Roles**: Architect, Programmer.
- **Workflow**: `research-spike`
- **Use Case**: Performance benchmarking, new Win32 API feasibility studies, prototype spikes.
- **Path**: Experiments in an isolated branch without merging to production branches.

### 4. `conflict-rescue-team`
- **Roles**: Conflict Resolver, Merger, Branch Cleaner.
- **Workflow**: `conflict-rescue`
- **Use Case**: Salvaging stale branches or resolving merge conflicts when an upstream branch has rebased or advanced significantly.

---

## 4. Repository Invariants Compliance ([`AGENTS.md`](../AGENTS.md))

This rig strictly enforces every operational rule required by the CanvasWindowComposer repository:

### A. Git Worktree Isolation
```powershell
# From primary checkout:
git worktree list
git worktree add "..\CanvasWindowComposer-<task>" -b "agent/<task>" HEAD
```
- **Rule**: Never allow two agents to build or test in the same working copy. `bin/`, `obj/`, `TestResults/`, and `publish-workspace` are shared mutable state; concurrent dotnet operations in the same folder will fail due to file locks and stale outputs.

### B. Machine-Wide Named Mutex Test Runner
```powershell
# Preferred entry point inside any worktree:
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\Run-Tests.ps1 -Configuration Release -NoPrompt
```
- **Rule**: Native and desktop tests (`Category=NativeRendering|NativeParking|NativePlacement|NativePreview`) manipulate real HWNDs, D3D surfaces, and foreground windows. `Tools\Run-Tests.ps1` serializes runs across all worktrees using the `Global\CanvasWindowComposer.Tests` mutex.

### C. Clean Canvas Shutdown Before Testing
- **Rule**: If Canvas is running, global hooks and overlay windows interfere with native assertions. Cleanly exit via:
  ```powershell
  CanvasDesktop.exe --exit
  ```
  Never force-kill (`taskkill /F`), as forced termination bypasses window restoration.

### D. Safe Worktree Pruning
- **Rule**: *"Never run `git worktree remove` while a process is still running from it."* The Branch Cleaner verifies that all `dotnet`, `testhost`, and `CanvasDesktop` processes have terminated before calling `git worktree remove`.

---

## 5. Cross-Tool Directory Junctions

Different AI coding platforms look for agent skills in different directories:
- **Google Antigravity & Gemini CLI**: `.agents/skills` or `.gemini/skills`
- **OpenCode**: `.opencode/skills`
- **OpenAI Codex**: `.agents/skills` or `.opencode/skills`

To prevent duplication and avoid git branch skew, the rig uses native **Windows NTFS Directory Junctions** (`mklink /J`). Junctions operate on local drives without needing Administrator privileges or Windows Developer Mode.

### Setting Up Junctions
Run the included helper script from the workspace root or script directory:

```powershell
# In PowerShell:
powershell -NoProfile -ExecutionPolicy Bypass -File .agents\scripts\setup-agent-links.ps1

# Or from Windows Command Prompt:
.agents\scripts\setup-agent-links.cmd
```

### Script Options
| Option | Description |
| :--- | :--- |
| *(default)* | Creates or verifies junctions for `.gemini\skills` and `.opencode\skills`. |
| `-Check` | Inspects and displays the current target of each junction without modifying them. |
| `-Remove` | Safely detaches the directory junctions without deleting the original skills. |
| `-Force` | Replaces an existing standard directory with a junction. |

---

## 6. Tool Integration Runbooks

### A. Google Antigravity
1. **Teamwork Mode**:
   Type `/teamwork` in chat to trigger multi-agent pair programming. Specify the desired constellation:
   > "Using the feature-lifecycle-team constellation, implement the window linking workflow per .agents/teams/workflow.yaml."
2. **Subagent Delegation**:
   Antigravity discovers skills automatically via `.agents/skills/`. The parent agent can invoke specialized subagents using the skills as operational guides:
   - Architect creates `.agents/specs/ARCHITECTURE_PLAN.md`.
   - Programmer runs in `..\CanvasWindowComposer-<task>`.
   - Reviewer is invoked with the diff and returns `APPROVED` or `CHANGES_REQUESTED`.
   - Merger executes integration.
   - Cleaner deprovisions the worktree.

### B. OpenCode
With junctions established via `setup-agent-links.ps1`:
1. OpenCode automatically detects the skills in `.opencode/skills/`.
2. When launching a task, instruct OpenCode:
   ```bash
   opencode "Read .agents/teams/teams.yaml and execute the 'worktree-developer' skill for task window-linking-01 on branch agent/window-linking-01."
   ```

### C. OpenAI Codex CLI
1. Codex reads `.agents/teams/teams.yaml` and executes tasks within the designated worktree:
   ```bash
   codex exec --config .agents/teams/workflow.yaml --role programmer --task window-linking-01
   ```
2. For review gates, run the reviewer role:
   ```bash
   codex exec --config .agents/teams/workflow.yaml --role reviewer --task window-linking-01
   ```

---

## 7. Directory Structure

```text
.agents/
├── README.md                           # This architecture and operations guide
├── scripts/
│   ├── setup-agent-links.ps1           # PowerShell NTFS junction configurator
│   └── setup-agent-links.cmd           # Command prompt launcher wrapper
├── skills/
│   ├── architect-planning/
│   │   └── SKILL.md                    # Role 1: System analysis & task breakdown
│   ├── worktree-developer/
│   │   └── SKILL.md                    # Role 2: Worktree isolation & test-driven coding
│   ├── code-reviewer/
│   │   └── SKILL.md                    # Role 3: Diff audit, security & review verdicts
│   ├── branch-merger/
│   │   └── SKILL.md                    # Role 4: Safe target branch merging & testing
│   ├── conflict-resolver/
│   │   └── SKILL.md                    # Role 5: Three-way merge conflict resolution
│   └── worktree-cleaner/
│       └── SKILL.md                    # Role 6: Process-safe worktree & branch cleanup
└── teams/
    ├── teams.yaml                      # Role catalog & constellation templates
    └── workflow.yaml                   # State-machine workflows & transition logic
```
