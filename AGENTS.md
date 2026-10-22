# Agent Guide

Read this file before working in this repository. It covers how to isolate
concurrent agent work and how to run the test suite safely.

## Parallel agents: use a git worktree

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
  worktrees. Run them through `Tools\RunCanvasTests.ps1`; it serializes runs with a
  machine-wide mutex.
- Finish by committing on the agent branch, then remove the worktree:
  `git worktree remove "..\CanvasWindowComposer-<task>"`. The branch remains.
- Never run `git worktree remove` while a process is still running from it.

## Testing

Preferred entry point (works from any worktree):

```powershell
Tests\run-tests.bat -Configuration Release
# or directly
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\RunCanvasTests.ps1 -Configuration Release
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

## Build and launch

```powershell
dotnet restore CanvasDesktop.slnx --locked-mode
dotnet build CanvasDesktop.slnx -c Release --no-restore
```

`Start-Canvas.cmd` builds from source, publishes to `publish-workspace` and
launches the app. Options: `--build`, `--diagnose`, `--no-launch`, `--no-dialog`.
Exit a running instance through the tray menu before rebuilding.
