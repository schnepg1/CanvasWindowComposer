# ARCHITECTURE PLAN: Crash-Resilient Workspace Autosave

**Target Branch:** `agent/workspace-autosave-durability` -> `main`
**Task Identifier:** `workspace-autosave-durability`
**Status:** PROPOSED — awaiting owner review before any code is written.
**Triage:** `[TRIAGE: MULTI-AGENT]` — spans `WorkspaceService` (deferral/threading), persistence, and tray; native-adjacent gating; verification needs the serialized runner.

---

## 1. Scope & Non-Goals

### Goal
Reduce workspace data loss when Canvas terminates without a managed shutdown (native AV, off-thread exception, `taskkill /f`, VS Stop Debugging), and make save failures visible instead of silent.

### Non-goals (explicitly rejected)
- **No write-ahead journal.** The in-memory `Document` is authoritative and `WorkspacePersistence.Save()` is fingerprint-gated, so the journal would touch every mutation path in `WorkspaceService`, `ZoneService`, `TeamPodService`, `WorkflowService` for a worst case of a few seconds. Blast radius not justified.
- **No `session.json` clean-shutdown marker.** `Docs/desktop-handoff-writer-audit.md` already establishes that on committed session end "the app cannot rely on that callback to persist last-minute changes" (`TrayApp.cs:262-263` subscribes `SessionEnded`). A marker flipped in `ShutdownPhase.SaveWorkspace` would report "Canvas crashed" on normal Windows shutdown/logoff. Revisit only if a false-positive-free derivation is found; the Phase 0 log covers the diagnostic need.
- **No live snapshot reload into a running session.** Restoring `Document` in place requires re-running binding/card reconciliation — native rebinding, i.e. exactly the code the user is crashing in. Restore happens at next start.
- **No changes to `Capture()` internals, placement lane semantics, or any `WindowManager` native path.**

---

## 2. Decisions Carried In From Review

| # | Decision | Basis |
| :--- | :--- | :--- |
| D1 | Diagnose before building. Phase 0 adds measurement; Phase 2b is gated on its output. | The "minutes lost" premise is unsupported: the deferral path returns *before* `_nextCapture` advances (`WorkspaceService.cs:83-90`), so the cadence resumes on the first free tick. Loss is bounded by one continuous interaction, not by a dead timer. |
| D2 | Never fsync inside `DeferPeriodicMaintenance`. | `Save()` -> `SavedState()` + fingerprint serialize + full store serialize + `Flush(flushToDisk: true)` + `File.Replace` (`WorkspacePersistence.cs:47-49`, `WorkspaceStore.cs:47-59`). `DeferPeriodicMaintenance` includes `_visualFrames.IsDrawing` (`OverviewManager.GesturePreviews.cs:32`), i.e. timer dispatch inside a render frame. |
| D3 | Split the camera commit from the disk write. | `Capture()` sets `Document.Cameras[_desktop]` as its last statement (`WorkspaceService.Capture.cs:21`); that one assignment is free and can run ungated. |
| D4 | Fix the `_canSave == false` hole before anything else. | A failed load sets `_canSave = false` and every later `Save()` returns `false` silently (`WorkspacePersistence.cs:33-38, 42`). The app then runs indefinitely without persisting and signals only once. Worse than a transient IO error. |
| D5 | Snapshots, not a shorter interval, are the real net for `TerminateProcess`. | `taskkill /f` and VS Stop Debugging run no managed code. Worst case = snapshot cadence. Hence 30-60 s, not 2 min. |
| D6 | Store work is unit-testable without the native suite. | `Tests/WorkspaceHarness.cs` provides `MemoryWorkspaceStore` with `Saves`, `SaveError`, `LoadError`; `Tests/WorkspaceBoundaryTests.cs` already drives `WorkspacePersistence` against a fake store. |

### Verified current state

- Workspace file: `%APPDATA%\CanvasWindowComposer\workspace.json` (`WorkspaceComposition.cs:13` -> `AppConfig.ConfigDir`), atomic write + `.bak` (`WorkspaceStore.cs:44-61`), load falls back to `.bak` and preserves the corrupt file (`WorkspaceStore.cs:31-40`).
- Log file: `%LOCALAPPDATA%\CanvasWindowComposer\Logs\canvas_debug.log`, append-only via `TrayApp.TryLog` (`TrayApp.cs:473-482`).
- Tick: 500 ms (`TrayApp.cs:10`), `_workspace.Tick(cameraMoving: _overview.DeferPeriodicMaintenance)` (`TrayApp.cs:304`).
- Graceful save: `ShutdownPhase.SaveWorkspace` -> `_workspace.Stop(save: _lifecycle.StartupCompleted)` (`TrayApp.cs:80`); `Dispose(bool)` -> `Shutdown()` -> `_lifecycle.Shutdown()` (`TrayApp.cs:423-427`).
- `WorkspaceService.Status` = `_persistence.LastError ?? _status ?? LoadWarning ?? ambiguous` — read on demand only; nothing subscribes.

---

## 3. Phase 0 — Measurement (no behaviour change)

**Goal:** establish whether deferral is actually the loss the user sees, before touching the tick path.

**Changes**
- `WorkspacePersistence.Save()`: on an actual write, append one log line via `TrayApp.TryLog` (never throw): `workspace saved items=<n> zones=<n> cameraChanged=<bool> elapsedMs=<n>`. Log only when the fingerprint changed, so a no-op save is silent.
- `WorkspaceService.RunPeriodicMaintenance`: log once per deferral *episode* (entry only, matching the existing `_periodicMaintenanceDeferred` latch), including the deferral reason (`cameraMoving` vs placement-lane busy) and elapsed ms since the episode began.

**Acceptance criteria**
- `canvas_debug.log` shows a save line no older than ~3 s during ordinary interaction.
- A deliberate continuous pan/drag for 60 s produces an observable gap whose length equals the interaction, confirming or refuting D1.
- Zero new writes when nothing changed.

**Tests:** none required (logging only). Manual probe: watch `Get-Item $env:APPDATA\CanvasWindowComposer\workspace.json | % LastWriteTime` while panning.
**Risk:** LOW. **Est:** ~40 LOC.
**Exit gate:** record the measured worst-case gap in this document before Phase 2 starts; use it to decide whether Phase 2b is needed.

---

## 4. Phase 1 — Make save failure visible (D4)

**Goal:** an unsaved session must never be silent again, including the permanently-disabled-after-bad-load case.

**Changes**
- `WorkspacePersistence`: add `bool CanSave` and a `SaveFailed`/`SaveSucceeded` transition callback (or a single `SaveStateChanged(SaveOutcome)` event). Emit **only on transition** — `LastError` is cleared on success (`WorkspacePersistence.cs:52`), so track `_failing` to avoid balloon spam.
- `WorkspaceService`: surface the transition as an event the tray can consume; keep `Status` semantics unchanged for the existing `SaveNow` failure dialog.
- `TrayApp`: on first failure, one `ShowBalloonTip` (pattern already used at `TrayApp.cs:182, 191, 199`) naming `_persistence`'s error and the log path; on repeated failure, append `" - autosave failing"` to `_trayIcon.Text` (base text at `:240`, currently rewritten only on the Enabled toggle at `:322-324` — move that into a helper so both paths compose it); clear on recovery.
- If `CanSave == false` (load failed), surface it as a failure episode at startup rather than a passive `Status` string.

**Acceptance criteria**
- Removing write permission on the config directory (or pointing the store at a locked file) triggers exactly one balloon and a changed tray label, and clears both once writes succeed again.
- A load failure produces a visible, persistent signal; the "never overwrite the user's files on load failure" invariant is preserved (`_canSave` stays `false`, no save is attempted).

**Tests (unit, `MemoryWorkspaceStore`)**
- `SaveFailureEscalatesOnceAndClearsOnRecovery` — set `SaveError`, tick twice, assert one failure notification; clear `SaveError`, assert recovered.
- `LoadFailureDisablesAutosaveAndReports` — `LoadError` set, assert `CanSave == false`, `Saves == 0`, and a reported failure.
- Existing `WorkspaceTests.UnreadableFiles_DoNotSilentlyBecomeEmptyWorkspace` must still pass unchanged.

**Risk:** LOW. **Est:** ~90 LOC + ~60 test LOC.
**Dependency:** none. Safe to land alone.

---

## 5. Phase 2 — Split camera commit from disk write (D2, D3)

**Goal:** remove the stale-camera window without putting an fsync in the render path.

**Changes**
- `WorkspaceService.Tick`: before the deferral return, when `!_switching` and the camera actually differs, assign `Document.Cameras[_desktop] = new WorkspaceCamera { ... }` (compare X/Y/Zoom first to avoid a per-tick allocation). No disk write, no `SynchronizeCopyTargets`, no placement lane.
- Guard the commit against desktop handoff: skip while `_switching` or while `DesktopHandoffPending` is set, so the old desktop key is not overwritten with the new desktop's camera. `BeforeSwitch` (`WorkspaceService.cs:178`) remains the authority for the handoff boundary.
- Leave the disk write exactly where it is (inside the placement-lane scope). Do **not** introduce a background writer in this phase.
- Extract the camera capture into one helper used by both `Capture()` and `Tick` so the two cannot drift.

**Acceptance criteria**
- `Document.Cameras` is current at all times, including mid-pan and mid-drag, verified through the harness.
- No disk write occurs while `cameraMoving` is true (assert via `MemoryWorkspaceStore.Saves`).
- No new save is produced when the camera and document are unchanged.

**Tests (unit)**
- `CameraCommitTracksLiveCameraWhileDeferred` — advance the harness camera with `Tick(cameraMoving: true)` and assert `Document.Cameras` follows while `Saves` stays flat.
- `CameraCommitDoesNotRunDuringDesktopSwitch` — assert no cross-desktop camera write.
- `UnchangedCameraDoesNotProduceASave` — guards the fingerprint gate.

**Risk:** MEDIUM-LOW — the only threading-sensitive edit, and it writes shared state from the tick path. Bounded to one assignment + one guard.
**Est:** ~50 LOC + ~70 test LOC.

---

## 5b. Phase 2b — Background single-writer disk queue (conditional)

**Gate:** only if Phase 0 shows continuous-interaction gaps long enough to matter (proposed threshold: > 30 s recurring).

**Changes**
- Introduce a single-writer queue that owns `_lastSaved` and serializes `WorkspaceStore.Save` off the UI thread. `WorkspaceStore.Save` uses a fixed `FilePath + ".tmp"` with `FileShare.None` (`WorkspaceStore.cs:47`), so **two concurrent writers collide** — one writer, no exceptions.
- Keep `SaveNow()`, the update handoff (`TrayApp.cs:402`), and `Stop(save:)` synchronous or explicitly awaited so they can still report success/failure to the user.
- Fingerprint must advance only after the store call succeeds (preserve the current `WorkspacePersistence.cs:50` invariant).

**Risk:** MEDIUM — new concurrency on a persistence path. Deferred unless measurement demands it.
**Est:** ~120 LOC.

---

## 6. Phase 3 — Last-gasp save on unmanaged shutdown paths

**Goal:** convert off-thread exceptions and `Environment.Exit` paths from "log only" into "log + persist".

**Changes**
- `WorkspacePersistence`: retain the last successfully serialized payload string (already computed as `current` at `:48`). Add `SaveLastKnownGood()` that writes that retained buffer through the store, with no serializer involvement.
- `WorkspaceService.SaveForCrash()`: best-effort, hard-bounded: single-shot latch, `try/catch` everything, no UI, no placement lane, no `Changed` events. Prefer the retained buffer (no torn read from `Document` being mutated by a live UI thread); fall back to a normal save only when no buffer exists.
- `Program`: register `AppDomain.CurrentDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException`, and `AppDomain.CurrentDomain.ProcessExit` -> `TrayApp.TryLog` the stack, then `_workspace.SaveForCrash()`. Register once; unregister/ignore after `_lifecycle.IsStopping`.
- `ProcessExit` budget on Windows is short — do the log and the single write, nothing else.

**Acceptance criteria**
- An exception thrown on a thread-pool thread (deliberately, in a dev build) leaves a newer `workspace.json` and a stack line in `canvas_debug.log`.
- Handlers never throw, never deadlock, and never run twice.
- Graceful exit is unchanged (no double save, no new failure path in `Stop`).

**Tests (unit)**
- `SaveForCrashWritesRetainedPayloadWithoutSerializing` — mutate `Document` into an unvalidatable state after a good save, assert the crash path still writes the last good bytes and does not throw.
- `SaveForCrashIsSingleShotAndSilent`.

**Risk:** MEDIUM — runs during process teardown; must be provably non-throwing and bounded.
**Est:** ~80 LOC + ~60 test LOC.

---

## 7. Phase 4 — Rolling snapshots + restore on next start (D5)

**Goal:** a recoverable generation older than the crash, so a bad capture cannot destroy both live generations.

**Changes**
- New `WorkspaceSnapshots` service (own file, `Source/Workspace/`): copy the *on-disk* `workspace.json` to `%APPDATA%\CanvasWindowComposer\Snapshots\workspace-<utc>-<pid>.json`. Copying the validated file rather than re-serializing keeps the snapshot as trustworthy as the file it came from.
- Skip the copy when the newest snapshot is byte-identical (dedupe by length + mtime, or content hash).
- Prune to newest 30 **and** a hard 32 MB total cap — `Validate` permits up to 16 MB and 2000 items (`WorkspaceStore.cs:29`), so a count alone is not a bound.
- Take a snapshot on: the 60 s timer, `SaveNow()`, and the shutdown save phase. Never from a failed save, and never from a document whose load failed.
- Tray: "Restore workspace snapshot..." listing timestamp + item/zone counts (counts read via `IWorkspaceStore.Load` on a read-only instance; a corrupt or unreadable snapshot must be listed as unavailable, not throw).
- Restore = copy the chosen snapshot over `workspace.json`, first preserving the current file as `workspace.pre-restore-<utc>.json`, then shut down via the path that does **not** save (`Stop(save: false)`) and relaunch. Live in-process reload is explicitly out of scope (see §1).
- Add the snapshot columns/paths to `Docs/` (see Phase 6).

**Acceptance criteria**
- After 3 minutes of use, ≥ 3 snapshots exist with distinct timestamps; the count and byte caps both hold.
- A snapshot taken, then a deliberately corrupted `workspace.json` + `.bak`, restores to the snapshot's content and leaves the pre-restore copy on disk.
- No snapshot is created from a failed load or a failed save.

**Tests (unit, real `WorkspaceStore` on a temp dir — pattern of `Tests/WorkspaceTests.cs`)**
- `SnapshotsPruneByCountAndBySize`
- `SnapshotOfCorruptWorkspaceIsNeverTaken`
- `RestorePreservesCurrentFileAsPreRestore`
- `SnapshotListingReportsUnreadableEntriesWithoutThrowing`

**Risk:** MEDIUM — new on-disk artifacts and a restore path that mutates the user's workspace file. Mitigated by: snapshot-only-on-success, pre-restore backup, no live reload, restart to apply.
**Est:** ~200 LOC + ~120 test LOC.
**Open question:** whether a relaunch helper exists. If not, exit cleanly with an explicit "restart Canvas" instruction rather than inventing process-spawn logic.

---

## 8. Phase 5 — Configuration knob (optional, last)

- `AppConfig`: add `WorkspaceSnapshotIntervalSeconds` using the existing `GetDouble`/`GetBool` parse helpers (`AppConfig.cs:173-204`), clamped 10-600, default 60; add a commented sample entry in the ini template near `:409`.
- Do **not** add a 500 ms *disk* interval knob — with Phase 2 in place that is 4x the fsync churn for no gain. The knob governs snapshot cadence only.

**Risk:** LOW. **Est:** ~30 LOC + 2 tests.

---

## 9. Phase 6 — Documentation, verification, review, merge

- Update `LOCAL-SETUP.md:24-29`, which currently promises "saves ... every two seconds, and before normal exit/logoff" — qualify it with what is actually guaranteed on an unclean kill.
- Note the snapshots directory and restore procedure in `Docs/`.
- Full verification (see §11), self-audit diff review against this plan, merge, worktree cleanup.

---

## 10. Regression Watchlist

| Test / surface | Why it is at risk | Owning phase |
| :--- | :--- | :--- |
| `WorkspaceTests.ItemViewsSurviveWindowRemovalDuringLiveStateInspection` (~line 306) | Asserts the saved file is unchanged after `Dispose` even though desktop coordinates were recovered afterwards — a comment there says "Save BEFORE recovering desktop coordinates". Phase 2 changes *when* the camera is committed. | 2 |
| `WorkspaceBoundaryTests` (fingerprint/save-count assertions, `Saves == 2`) | Phase 2 must not introduce a save when nothing changed. | 2 |
| `WorkspaceTests.UnreadableFiles_DoNotSilentlyBecomeEmptyWorkspace` | Phase 1 must add visibility without weakening "a failed load never overwrites existing files". | 1 |
| `WorkspaceTests.DamagedPrimary_RecoversBackupAndPreservesCorruptBytes` | Phase 4 must not snapshot a corrupt workspace. | 4 |
| `TrayApp.LogPath` / `ApplicationErrorDialogTests` | `TrayApp.LogPath` is asserted by a test; keep the constant stable. | 0 |
| Tray label composition (`TrayApp.cs:240`, `:322-324`) | Phase 1 moves text ownership into a helper; the Enabled/Disabled toggle must still win. | 1 |

---

## 11. Verification Commands

Per `AGENTS.md`; run from the phase's own worktree.

```powershell
git worktree add "..\CanvasWindowComposer-autosave-durability" -b "agent/workspace-autosave-durability" HEAD
```

Fast unit loop (workspace-focused, no native categories):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\Run-Tests.ps1 -Configuration Release -Filter "FullyQualifiedName~Workspace" -AllowRunningApp
```

Phase exit gate (non-native phases):

```powershell
Tests\run_tests.bat -Configuration Release
```

- Close any running `CanvasDesktop` before the full run; the runner enforces the machine-wide mutex.
- No phase in this plan adds or modifies a `NativeRendering` / `NativeParking` / `NativePlacement` / `NativePreview` test, so no serialized native phase is newly required — the full run covers it.
- Automated tests cannot cover `TerminateProcess`, VS Stop Debugging, or a real native AV. Report those as **manually unverified** rather than passing.

---

## 12. Sequencing & Effort

| Phase | Depends on | Risk | Est. |
| :--- | :--- | :--- | :--- |
| 0 Measurement | — | LOW | ~40 LOC |
| 1 Failure visibility | — | LOW | ~90 + 60 |
| 2 Camera/disk split | 0 (gate) | MED-LOW | ~50 + 70 |
| 2b Background writer | 0 (conditional) | MEDIUM | ~120 |
| 3 Last-gasp save | 1, 2 | MEDIUM | ~80 + 60 |
| 4 Snapshots + restore | 1 | MEDIUM | ~200 + 120 |
| 5 Config knob | 4 | LOW | ~30 |
| 6 Docs/review/merge | all | — | — |

Single worktree, one branch, one commit + self-review per phase, merged at Phase 6 so any phase can be dropped independently. Phase 1 is independent and could ship first on its own branch if the owner wants fast value while Phase 0 data accumulates.

**Value order if scope must shrink:** 1 (silent failure) -> 4 (snapshots) -> 3 (last-gasp) -> 2 -> everything else. Snapshots and the `_canSave` fix cover the named crash modes; the tick rework is a refinement whose necessity Phase 0 decides.
