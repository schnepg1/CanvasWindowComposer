# Window identity and duplicate matching

Implemented locally on 11 October 2026, after the lifecycle refactor. This is an
incremental change to the existing C# application, not a replacement stack.

## What changed

- `WorkspaceBindings` owns session-only window observations, opaque selection
  tokens, one-to-one slot bindings, forgotten-window state and Explorer wait state.
  A binding includes HWND, PID and process start time, not just the executable.
  A window-manager removal notification retires the token even within the same
  process. Recycled process identities cannot inherit a binding or forgotten state.
- Capture, desktop-switch snapshots, focus, close and manual linking validate the
  runtime identity. A changed identity re-enrolls native geometry only on the
  active desktop; it cannot write the previous window's cached bounds into a slot.
  Inaccessible identities fail closed. No elevation is requested.
- `WorkspaceMatcher` resolves an observation batch before assigning any slots.
  Launch intent, full targets, exact captions and the restricted single-app
  fallback are ranked. Equal-strength competing windows and duplicate saved
  identities are ambiguous, not first-match-wins. A bound duplicate does not make
  the remaining caption/path unique.
- Ambiguity keeps saved slots as **Needs linking** placeholders, without moving
  the uncertain windows or creating extra saved entries for those observations.
  Use **Link window…** in Saved workspace, or right-click a placeholder and select
  **Link open window…**. Linking explicitly moves the selected window to that
  saved place. The application, desktop, visibility, binding and opaque token are
  checked again when the dialog submits; stale selections cannot steal a window.
- Launch intent is limited to HWNDs absent from a pre-launch enumeration. Even
  inaccessible pre-existing HWNDs are excluded. Same-app launches remain serialized.
  This is correlation, not proof that an arbitrary application opened the desired
  document; uncertain/single-instance applications can still require manual linking.

## Multiple VS Code projects

Executable names and PIDs are not project identities. VS Code's title can include
the workspace name, but a folder basename such as `Project` is not enough to tell
`C:\One\Project` from `C:\Two\Project`, and the active editor changes captions.

For a saved VS Code place, choose its project folder (or `.code-workspace` file)
as the reopen target. Canvas now adds `--new-window` to VS Code and VS Code Insiders
launches and passes the target as one escaped argument. No shell wrapper is used.
These options are documented in the [official VS Code CLI reference](https://code.visualstudio.com/docs/configure/command-line).

Canvas automatically captures single local project folders from live VS Code and
VS Code Insiders windows. It reads the local diagnostics channel used by Code's
`--status` command, verifies the pipe server's executable, PID and process start
time, then associates a unique exact window caption with its reported full folder
URI. It does not infer paths from folder basenames or scan project directories.
No extension installation or settings changes are required.

Polling runs in the background with a timeout. Existing saved entries without a
target are upgraded when the window matches unambiguously. Captured folders follow
folder changes in that window; choosing a target manually disables automatic target
updates for that saved place. Old nonempty targets remain manual. At startup,
Canvas briefly waits for already-open Code windows before launching saved projects,
and does not duplicate an unresolved saved project during a diagnostics outage.

Identical captions within one Code process remain ambiguous. Remote/SSH/WSL folders,
network shares and multi-folder workspaces require a manual reopen target. VS Code's
diagnostics protocol is internal, so an incompatible version falls back to manual
selection. Detection has been verified against a live local Code window; multi-window
matching and failure handling are covered with injected diagnostics and windows.

The earlier full-path title adapter is still available as an optional alternative.
Add this to your VS Code **user** settings JSON, preserving other settings:

```json
"window.title": "${dirty}${activeEditorShort}${separator}${rootName}${separator}${appName} [CanvasRoot:${rootPath}]"
```

The opt-in adapter reads that explicit full-path suffix, not a guessed substring
or process command line. VS Code defines `rootPath` in its
[title configuration](https://github.com/microsoft/vscode/blob/main/src/vs/workbench/browser/workbench.contribution.ts)
and supplies it from the workspace in its
[title implementation](https://github.com/microsoft/vscode/blob/main/src/vs/workbench/browser/parts/titlebar/windowTitle.ts).
Canvas has **not** changed your VS Code settings. The suffix exposes the local
workspace path in the window title, so enable it only if that is acceptable.

The title adapter supports local fully-qualified Windows paths. Missing or malformed
markers fall back to live diagnostics, conservative matching or manual linking.

## Optional named launch places

Use **Name…** in Saved workspace, or **Name this saved place…** on a placeholder.
For example, name one slot `VS Code — Canvas` and another `VS Code — Website`, then
choose their respective project folders as targets. Names survive changing native
captions; clearing a name makes the display follow the caption again.

This is the first part of a named-wrapper concept: a persistent slot GUID, a user
label, an explicit executable and a target. A label is not matching evidence and
does not magically create an isolated application instance. No arbitrary shell
commands, custom interpreter wrappers, environment overrides or generic argument
editor have been added. For apps needing different profiles, a later structured
launch-profile feature should use app-specific supported options. VS Code offers
profiles and isolated user-data directories, but those affect settings/extensions
and should be an explicit choice, not a default.
[VS Code profiles and isolation options](https://code.visualstudio.com/docs/configure/command-line).

## Storage and limits

The v1 document gains an optional `Name` string (default empty, maximum 200
characters). Old v1 documents load unchanged. Bounds, slot IDs, camera data and
launch opt-ins keep their existing format. Live bindings and selection tokens
are never serialized. The temporary-area upgrade separately checkpoints pending
adoption identities (HWND, PID, process start time) until those original windows
are captured or retire; these records do not authorize native actions. Older
binaries ignore and may drop names
when saving; use the retained workspace copy when rolling back.

`AutoDetectedTarget` (default false) records whether Canvas owns updates to a Code
folder target. Older documents load without migration, preserving manual choices.

This is not a universal document-identity guarantee or a security boundary.
Missed destroy notifications followed by HWND reuse **inside the same process**
cannot be proven from PID/start time alone. Native operations still have a
query/action race. Window-manager projection/recovery has its own existing PID
tracking; this refactor does not claim to replace every native tracking boundary.
Explorer identity remains a best-effort asynchronous active-tab snapshot, not a
per-tab inventory or an atomic association with HWND lifetime. UI titles and
adapter markers are application-provided metadata, not trusted authentication.

## Verification

Release build and publish pass without warnings/errors. All 292 tests pass,
including 50 new cases for registry ownership, process/PID recycling, duplicate
captions/targets, batch competition, stale commands/selections, launch baselines,
desktop isolation, names, backward-compatible storage and VS Code target parsing
and argument construction. WinForms contract tests exercise successful/stale/empty
link dialogs and ambiguous placeholders. Rendered previews were inspected.

Native process inspection is exercised against the test process. Project-path
matching uses injected windows; this is not an end-to-end multi-project VS Code
or reboot test. Mixed-DPI, monitor removal, forced termination, remote CI and
installer execution remain outside this checkpoint.

The build was published to `publish-identity-workspace` and activated in
`publish-workspace` after graceful shutdown. The previous application and workspace
are retained in `publish-before-identity-20261011`. Both the activation session and
a controlled UI-idle start/exit cycle retained all 20 saved items, every bounds
field and the camera, with application exit code 0. The app was started again and
reports .NET 10.0.12, non-elevated, with sign-in startup still enabled. No cleanup
error log was produced. Active/staged DLL SHA-256:
`64AD894A3E8750EDF70FA6A4CD967152126A2EB77C243F1CA60C4B1A65E5E58A`.

Next refactor: extract placement decisions into a pure planner, followed by a
repeatable Windows integration harness for topology/DPI, Explorer tabs, real
multi-project editors and crash/recovery behaviour. Structured native-operation
results and diagnostics should accompany that work.
