# Agent team pods

An agent or orchestrator registers a stable task ID on its first telemetry update.
Canvas reads registrations; it does not start agents or infer tasks from chat text.
The shipped CVL team template creates seven role cards and nine connectors inside
one **AI Mission Control: <task name>** project area. Repeated updates reuse the
saved pod. Each card stores its task and role identity independently of its label.

## Register and report progress

Run from a worktree, using one shared state path for Canvas and every agent:

```powershell
$env:CANVAS_TEAM_STATE_PATH = 'C:\Projects\CanvasWindowComposer\.agents\teams\state.json'
.agents\scripts\Set-TeamTelemetry.ps1 -TaskId task-window-linking-01 `
  -Name 'Feature: Window Linking' -Role architect -State Working -Detail 'Planning window links'
.agents\scripts\Set-TeamTelemetry.ps1 -TaskId task-window-linking-01 `
  -Role programmer -State Working -Detail 'Editing WindowManager.cs'
.agents\scripts\Set-TeamTelemetry.ps1 -TaskId task-window-linking-01 `
  -Role reviewer -State Testing -Detail 'Running mutex tests'
.agents\scripts\Set-TeamTelemetry.ps1 -TaskId task-window-linking-01 `
  -Role orchestrator -State Done -Detail 'Phase complete' -TaskStatus Done
```

Set `CANVAS_TEAM_STATE_PATH` before starting Canvas too. Without it, Canvas checks
existing `.canvas/state.json` and `.agents/teams/state.json` files in its working
and application directories, then `%APPDATA%\CanvasWindowComposer\Teams\state.json`.
In a repository containing `.agents`, the fallback is `.agents/teams/state.json`.
The helper defaults to the state file beside the repository's team templates.
State is local runtime data and is ignored by git. Writers share a named mutex
and atomically replace the file, preserving other tasks and global legacy roles.
Before replacing the file, the helper enforces the reader's limits: 64 task
registrations, 64 roles per task or global role map, and 1 MiB of UTF-8 data.
Completed and archived tasks count toward the limit. At capacity, existing tasks
can still update; adding another task reports an error and preserves the valid
file. Remove an obsolete registration explicitly before registering a new task.

For lifecycle hooks, set `CANVAS_TASK_ID` and optionally `CANVAS_TASK_NAME`, or pass
`-TaskId`, `-TaskName`, and `-Role` to `telemetry-hook.ps1`. A Stop event finishes
that task's specified role; it does not finish every other agent's phase.

```json
{
  "teams": {
    "task-window-linking-01": {
      "taskId": "task-window-linking-01",
      "name": "Feature: Window Linking",
      "status": "Active",
      "activeRole": "programmer",
      "roles": {
        "programmer": { "state": "Working", "detail": "Editing WindowManager.cs" }
      }
    }
  }
}
```

The `teams` key must match `taskId` when supplied. Each task has independent roles:
`orchestrator`, `architect`, `programmer`, `reviewer`, `merger`, `conflict-resolver`,
and `branch-cleaner`. States are `Idle`, `Working`, `Testing`, `Blocked`, `Done`,
and `Error`. A registered active role without a report defaults to Working;
other unreported roles show Idle. A machine-wide test mutex can only override
legacy global role cards because it cannot identify which task owns the tests.
Malformed or incomplete writes retain the last valid telemetry snapshot. Polling
also discovers files created after startup and recovers missed watcher events.

## Move and keep a pod

Open the interactive canvas, including at 100% zoom. Use the project navigator to
find **AI Mission Control: <task name>**, then drag its outlined header. Cards,
connectors, nested areas, and member windows move in one undoable transaction.
Task identity keeps cards linked even after they are renamed or moved outside
the frame. The frame is saved in `workspace.json`; cards, connectors, and their
identities are saved in `annotations.json`. Telemetry never resets their positions.
Pods stay on the desktop where first created and remain after task completion.
The normal desktop view hides canvas overlays; enter the interactive canvas to
see and manipulate project borders.

Status headers use the installed [WPF-UI](https://github.com/lepoco/wpfui) 4.3.0
Badge control: cyan Working, amber Testing, emerald Done, orange Blocked, red
Error, and gray Idle. Details truncate within the card width, with reserved header
space on task cards. Legacy unbound cards show the pill just above their artwork.
Pills and text follow canvas zoom and DPI; glow animation respects reduced motion
and pauses during camera motion.
