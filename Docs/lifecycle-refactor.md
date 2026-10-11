# Application lifecycle and resource ownership

Implemented locally on 11 October 2026, following the workspace boundary refactor.
This addresses the first recommended priority; identity/binding and placement
policy refactors remain separate work.

Follow-up: [runtime identity and duplicate matching](identity-and-matching.md)
now implements the binding/matching work. Placement planning remains separate.

## Ownership and order

`ApplicationLifecycle` is a UI-thread-owned cleanup plan. Each acquired resource
is registered immediately, so a later startup exception releases the resources
already acquired. The original startup exception is preserved even if cleanup
or its error reporter also fails. Startup completion is tracked separately from
shutdown state.

Shutdown has explicit, ordered phases:

1. Quiesce: stop observation/input activity and detach foreground/session events.
2. Finalize layout: close overview and release its subscriptions/render resources.
3. Save workspace: capture canvas coordinates, save, and remove placeholders.
4. Stop services: stop virtual-desktop polling/input ownership and detach desktop cache.
5. Recover windows: stop projection and return real windows to physical work areas.
6. Release UI: dispose forms, dispatcher, tray menu and icon resources.

Steps in a phase retain registration order. Each step is attempted once; a
failure is collected and reported without preventing later steps. Shutdown is
safe to call repeatedly or reentrantly. The tray host, context disposal and
handled UI-dispatch exceptions use this same path. `Program` owns/disposes the
application context even when the message loop exits unexpectedly.

Incomplete startup removes placeholders without capturing/saving a partially
restored canvas during rollback. Normal workspace shutdown releases cards and
subscriptions even when capture or saving fails. Save failures are reported to
the lifecycle instead of silently being treated as success. Window recovery
attempts the remaining enrolled windows if an individual window throws.

## Resource fixes

- `AppConfig` owns one watcher, detaches/disposes it, serializes reload/disposal,
  ignores queued post-disposal callbacks, and rejects use after disposal. A
  configurable directory lets tests avoid the user's configuration.
- `ForegroundCoordinator` and `OverviewInputs` explicitly detach external events.
- Minimap/search forms detach canvas, input and desktop subscriptions on disposal.
- Minimap and overview forms release renderers on direct `Dispose`, not only a
  form-closing event. Queued fade/inertia callbacks ignore disposed owners.
- Overview cleanup independently attempts thumbnail, MMCSS and per-pass cleanup
  if its normal hide transition fails.
- The input router cleans up acquired components on construction failure and
  attempts every child disposal. Virtual-desktop disposal is idempotent and
  suppresses queued notifications. A poller that exceeds its existing one-second
  stop wait reports a timeout rather than disposing resources still in use.
- The tray icon's native HICON is cloned into an owned managed icon and its
  temporary native handle is destroyed; the managed image outlives NotifyIcon.

## Verification

All 242 tests pass in Release, including 22 new lifecycle/resource-ownership
cases. They cover every shutdown phase failing, a failing reporter, repeated
and reentrant shutdown, partial acquisition rollback, failed saves/captures,
per-window recovery failures, watcher disposal, event detachment, and direct
renderer ownership cleanup without GPU initialization. The existing layout and
exit/restart regressions remain enabled. Coverage collection and Release publish
also succeed.

The new build is staged at `publish-lifecycle-workspace`; the previous active
build and workspace are retained at `publish-before-lifecycle-20261011`.
The active DLL hash matches the staged build. Live diagnostics report .NET
10.0.12, non-elevated, with sign-in startup enabled. A controlled live
start/exit cycle reached UI idle, exited with application exit code 0 and
retained all 20 currently saved entries, all bounds fields and the camera.
The app was started again afterward. No cleanup-error log was produced.

## Limits

This is best-effort managed cleanup, not a crash-recovery guarantee. Forced
termination, power loss, an unhandled worker-thread failure, a hung native call
or a GPU driver stall can still prevent recovery. Existing projection/render
thread timeout policies are not redesigned here. Cleanup failures use the
existing best-effort local log and set the application's exit code; `--exit`
confirms that the instance stopped, not that every native window operation
succeeded. Some native API return values still need the proposed diagnostic
result-boundary refactor.

Tests of partial startup use controlled acquired resources and headless workspace
fixtures, not injected failures throughout a live native desktop session. A real
reboot, full hardware/topology matrix, remote CI and NSIS installer run are not
claimed. The next code refactor should strengthen runtime bindings and ambiguous
workspace matching, with native integration coverage alongside it.
