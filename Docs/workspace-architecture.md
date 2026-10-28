# Workspace boundaries

Refactored locally on 11 October 2026. This is an incremental separation inside
the existing assembly, not a rewrite or a new dependency-injection framework.
Follow-up: [application lifecycle and resource ownership](lifecycle-refactor.md)
now covers startup rollback and failure-isolated shutdown. Its test count
supersedes the checkpoint recorded below.
The subsequent [identity/matching refactor](identity-and-matching.md) replaces the
first-match and executable-only binding policy described in the historical
remaining-work section below.

## Ownership

| Component | Responsibility |
| --- | --- |
| `WorkspaceModels` / `WorkspaceGeometry` | Version-1 persisted data and geometry constraints; live bindings remain session-only, with a separate checkpoint for outstanding upgrade adoption |
| `WorkspaceMatcher` | Pure matching policy: desktop, executable, target, title, already-bound slots and explicit launches |
| `WorkspaceStore` / `IWorkspaceStore` | JSON format validation, atomic file replacement, backup recovery and preservation of damaged input |
| `WorkspacePersistence` | Load-failure write protection, save-on-change fingerprinting and retryable save errors |
| `WorkspaceService` | Workspace lifecycle: capture, bindings, launch queue, desktop changes and save-before-desktop-recovery ordering |
| `WorkspaceWindowInspector` | Read-only process identity checks, Chrome profile evidence and window metadata discovery |
| `WorkspaceWindowMetadata` | Shared entry-creation and target/profile capture rules for background discovery and context-menu recovery |
| `IWorkspaceCommands` / `WorkspaceItemView` | UI commands addressed by stable IDs; detached, immutable presentation snapshots |
| `WorkspaceForms` / `WorkspaceDialogs` | WinForms controls, menus, file/folder dialogs and UI subscription cleanup |
| Placeholder, identity, launcher and native-action interfaces | Replaceable boundaries for forms, Explorer/process lookup, process startup and focus/close operations |
| `Application/WorkspaceComposition` | Production wiring of the concrete Windows adapters |

The coordinator no longer imports WinForms or JSON serialization, creates forms,
opens dialogs, or calls native focus/close functions directly. A stale UI row
cannot mutate a model or reopen a forgotten entry. Launch failures are status plus
an event; the tray host decides how to display them.

Coordinator capture and launch timeouts now use the existing injectable monotonic
`IClock`, so tests advance time without sleeps or system-clock changes. Explorer's
single-flight background COM polling remains in its Windows identity adapter.

## Maintaining capture and window menus

The 28 October 2026 refactor keeps `WorkspaceService.cs` focused on lifecycle,
desktop changes and persistence ordering. Its partial files group the remaining
responsibilities: `Capture` observes and matches windows, `LiveWindows` validates
bindings and applies native placement, `Cards` owns placeholders and view data,
`Launch` owns launch tracking, `Commands` handles explicit user actions, and
`Settings` edits saved preferences. `Temporary`, `Overview`, `ContextMenu` and
`WebResources` retain their existing focused entry points.

Capture runs in an explicit order: synchronize cards, validate observed runtime
identities, enroll missing native windows, collect observations, resolve the whole
match batch, then finish temporary arrivals and camera state. Keep the batch
boundary: assigning slots while enumerating windows would reintroduce
enumeration-order-dependent matching.

`WorkspaceWindowInspector` reads Windows evidence without changing bindings,
placement or saved data. `WorkspaceWindowMetadata` is the shared description used
to create deliberate and temporary entries, including entries recovered by
right-click. Change target/profile capture policy there rather than duplicating
it in menu recovery or temporary placement. Explicit web-resource URLs and manual
editor targets continue to take precedence over observed captions/navigation.

The overview's `Keyboard` and `Pointer` partials route input, while `WindowMenus`
owns window-menu selection and the interaction/fallback menu builders. Preserve
the clicked HWND before resolving a workspace entry: publishing a new entry can
refresh and reorder the overview. Placeholder menus are borrowed from their
cards; the overview owns its live-window and fallback menu instances.

Regression coverage includes detached-tab menus before periodic capture,
same-profile ambiguity, recycled handles, explicit linking, temporary placement,
saved URLs, and native overview menu/input behavior. Use `Tools/Run-Tests.ps1` to
serialize native checks across worktrees.

## Preserved behaviour

The workspace file schema, backup filenames and saved coordinates have not
changed. Matching retains its previous title/target policy. Explicit file launches
may identify a targeted slot even when its Win32 caption cannot reveal the file;
folder launches still require folder identity. Same-executable launches remain
serialized, and placeholders retain the application's native minimized/maximized
and pinned state while their own windows stay normal and unpinned.

Workspace disposal saves canvas coordinates and removes placeholder HWNDs before
the window manager restores real windows to physical monitor work areas. Physical
desktop recovery must not be persisted as a new canvas layout.

## Historical validation (11 October 2026)

- All 220 tests pass in Release, including the original regressions and 28 new
  cases covering persistence, matching, headless orchestration and UI contracts.
- The previous helper-process identity test now uses an injected fake identity;
  the exit/restart regression still exercises real JSON storage and window-manager
  projection, without racing a newly started process's `MainModule` property.
- Coverage collection succeeds. Reported line coverage is 100% for matching and
  persistence, 94.8% for the coordinator, 100% for the placeholder form and 96.2%
  for the workspace list form. Coverage is not a substitute for native testing.
- Release publish succeeds without warnings/errors. The rendered placeholder
  preview was inspected; command-contract tests exercise its Open action and
  snapshot updates, plus list checkbox/button wiring and disposal unsubscription.

The new production build is staged in `publish-modular-workspace` and copied to
the active `publish-workspace` after graceful shutdown. The previous application
and saved workspace are retained in `publish-before-modularity-20261011`.
The running application reports .NET 10.0.12, non-elevated, with sign-in startup
still enabled; its DLL hash matches the staged publish. A controlled live
exit/start/exit cycle retained all 19 saved entries, all bounds fields and the
camera, and the application was started again afterward. An earlier longer live
session changed one entry's foreground Z-order and recentered the camera (not
its saved X/Y/size), consistent with the existing focus policy; this is why
native integration checks must distinguish interaction from storage regressions.

## Remaining boundaries and next priority

`WorkspaceService` still coordinates the existing `Canvas`, `WindowManager` and
`DesktopStateCache`; these are not yet separate projects. Runtime HWND bindings
and capture remain in the coordinator. This focused change avoids changing native
projection algorithms or adding a framework merely to split files.

Next priority: a repeatable Windows integration harness and recovery policy before
additional features. Cover mixed-DPI and disconnected monitors, multiple same-app
windows, Explorer tabs, minimized/maximized windows and abrupt termination. In
particular, identical captions are still resolved by the previous first-match
policy; a recycled HWND from the same executable is not distinguished by process
start identity. Those need an explicit matching policy and regression tests, not
a silent behavioural change during this refactor.

No real reboot, forced-crash recovery, NSIS installer run or remote CI run is
claimed by this change. Least-privilege restrictions for administrator-run apps
remain as documented in the modernization notes.
