# Agent team pods

Open the canvas with **Alt+Q**, then choose **AI Teams Mission Control** from the
toolbelt's **More actions** menu. Create a mission at the center of the current
view, or jump to an existing mission. New missions start empty. In **Inspect**,
enter a name and choose **Add agent** for each agent you want. Click an agent
card on the canvas to open that agent directly. Starting Codex is a separate
action on the agent's card.

External agents can still register missions by reporting a stable task ID.
Repeated telemetry updates reuse the saved frame and cards. Missions imported
from the existing external team workflow use its seven-role layout.

## Open an agent terminal

Install Herdr Core (`hc.exe`), the Codex CLI (`codex.cmd`), and Windows Terminal.
Enter an existing local path in **Local working folder**. Use **Save working
folder** to make it the default for the mission. **Start Codex** launches a
background Herdr terminal. Complete any Codex startup prompt in **Open terminal**.
**Watch terminal** attaches read-only. Press **Ctrl+\\** in the viewer to detach.
The role card remains in place and can find the same live Herdr terminal when
Canvas reopens. Herdr reports the states emitted by the running program.

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

Drag the mission's title grip to move the frame and its cards as one undoable
operation. Use **Lock position** in the inspector to keep it anchored. You can
place missions within different project areas. Each mission keeps its own
position and working folder. Closing Mission Control leaves its saved missions
available on the desktop where they were created.

Agent cards use the installed [WPF-UI](https://github.com/lepoco/wpfui) badges.
At a distant zoom level, open the inspector from the Mission Control list.

## Planned terminal and chat views

- Place a standalone agent from the toolbelt and link or unlink it from a
  mission while preserving its identity and any live session.
- Expand an agent card in place into a Fluent shell with its status header and
  a terminal pane connected to the same Herdr session. Prove streaming output,
  resize, and keyboard input in Windows. Open in observation mode and offer
  **Take control** for input. Collapsing the pane detaches the view.
- Host the same agent view in a mission pop-out for Alt+Tab. Canvas and the
  pop-out share agent identity and session state. Opening the pop-out focuses
  an existing window for that mission.
- Add a chat view once a supported runtime exposes structured messages,
  streaming, and tool approval state. Keep terminal access on the same card.
  Speech-to-text can feed the chat's compose field in a later step.
