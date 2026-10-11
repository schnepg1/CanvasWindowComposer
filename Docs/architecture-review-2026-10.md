# Canvas Window Composer: engineering review

Reviewed 11 October 2026. Scope: this local working copy, including the recent
persistent-workspace additions and the exit recovery implemented with this review.
This is a source/build/test assessment, not a penetration test, GPU benchmark,
or certification across every Windows build and monitor configuration.

Follow-up: the .NET 10 upgrade and normal-user launch/startup hardening have now
been implemented. See [modernization-2026-10.md](modernization-2026-10.md). The
observations below describe the reviewed baseline, not the upgraded runtime state.

## Decision

Keep the foundation and modernize it incrementally. The main risks are lifecycle,
privilege boundaries, recovery and window identity, not the choice of C# or native
Windows APIs. A rewrite would have to solve these same problems again.

The preferred target stack is C#/.NET 10 LTS, Win32 through generated interop,
DWM thumbnails, the existing D3D11 renderer, and a lightweight Windows UI shell.
Keep WinForms initially. WPF is an optional replacement for a richer settings or
workspace UI, not a necessary replacement for the window-management backend.
Electron/Tauri would still require native integration for this app's core purpose;
I do not recommend that migration without a separate compelling product need.
Use the coding model for bounded changes with regression tests, rather than a
framework rewrite based on which model is available.

## Stack assessment

- C# plus Win32 is a sensible Windows-only choice: direct HWND access with managed
  application logic and testable native adapters.
- DWM live thumbnails are a good match for previews. D3D11 remains a reasonable
  renderer here; moving to D3D12 is not justified without measured benefit.
- WinForms is adequate for tray menus, lightweight cards and settings. Modern
  styling/accessibility should be evaluated separately from renderer performance.
- .NET 8 is approaching end of support: 10 November 2026. .NET 10 LTS is supported
  through 14 November 2028. The installed SDK is 8.0.202 and desktop runtime is
  8.0.3, so merely retaining the existing installation is not an acceptable long-term
  maintenance plan. Upgrade the SDK/runtime and validate on .NET 10 next.
- NuGet's production dependency audit, including transitive packages, reported no
  known vulnerable packages. This does not establish absence of vulnerabilities
  or cover Windows/the installed runtime. The outdated-package check found
  CsWin32 0.3.360 available versus this project's 0.3.106; update with interop tests.

Sources: [Microsoft .NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core),
[Windows Forms overview](https://learn.microsoft.com/en-us/dotnet/desktop/winforms/overview/),
[DWM thumbnail API](https://learn.microsoft.com/en-us/windows/win32/dwm/thumbnail-ovw).

## Architecture and modularity

The folder structure has useful responsibilities: Core, System, Application,
Overlays and Workspace. Canvas separates world/camera calculations from native
movement. IWindowApi, IInputRouter, IClock, IScreens and IVirtualDesktops enable
fakes and focused tests. Overview state, camera, input, thumbnails and rendering
are already separated. Manual dependency construction in TrayApp is reasonable
at this scale; a dependency-injection framework is not a prerequisite.

However, these are folder boundaries in one production assembly, not enforced
dependency boundaries. Core still uses native window handles and WindowManager
coordinates numerous responsibilities. WorkspaceService directly combines matching,
native state, discovery, persistence scheduling, launching and WinForms cards. That
recently added subsystem is a functional prototype, not finished product architecture.

Recommended boundaries as the code grows:

1. Core: canvas geometry/state, placement policies and workspace matching; no UI
   framework or native calls. Introduce stable window identity deliberately.
2. Windows integration: enumeration, placement, input hooks, virtual desktops,
   Explorer integration and renderer/native resource ownership.
3. Application/UI: lifecycle orchestration, workspace commands and forms. Separate
   workspace storage, matching, launching and placeholder presentation behind small
   interfaces; expose view models/data rather than forms from workflow logic.

Extract incrementally; avoid adding layers that do not yet have an independent
responsibility. Atomic, validated, versioned JSON with backup recovery is sufficient
for today's layout data. A database is not necessary merely to modernize the stack.

## Prioritized work

### Before broader distribution

1. Upgrade to patched .NET 10 LTS; pin the SDK, align the solution/build tooling,
   update dependencies and add Windows CI for build/tests. The installed .NET 8 SDK
   predates this repo's .slnx format, hence the current explicit-project commands.
2. Review elevation. app.manifest requires administrator rights for the whole app;
   Program grants Everyone process-termination access. Workspace launches from
   that elevated process can inherit elevated privileges. Persisted executable/
   target data therefore deserves explicit trust and launch rules. Prefer a
   normal-user shell and a narrowly scoped elevated helper only where required.
3. Define recovery and shutdown as first-class lifecycle operations. This change
   fixes normal exit ordering and detaches WindowManager/DesktopStateCache events,
   but does not make crashes/power loss recoverable. Review remaining thread,
   COM, FileSystemWatcher and renderer ownership, bounded waits and startup failures.
4. Strengthen identity matching. Duplicate app titles are ambiguous, file/document
   identity is generally not inferable, and existing workspace bindings compare
   executable paths rather than a full process identity. Handle reuse within the
   same process also needs consideration. Prefer app-specific adapters and a
   user-confirmed ambiguous-match path over silent guesses.

### Next quality improvements

- Split WorkspaceService before adding substantially more workspace features.
- Add structured diagnostics and visible recovery/save/launch errors. Several
  native failures currently return false or are swallowed without useful tracing.
- Add real integration coverage: monitor removal, mixed DPI, docking, sleep/wake,
  Explorer restart, unresponsive apps, duplicate titles, virtual-desktop deletion,
  shutdown/reboot, GPU/device loss and interrupted/corrupt saves.
- Profile CPU/GPU/memory and pan latency with realistic window counts before
  changing render APIs or thread strategy. Existing fake tests are not benchmarks.
- Establish a maintained fork/release process; the archived upstream is not a
  source of ongoing fixes. Retain its MIT notice when redistributing.

## Implemented in this review

Normal tray/CLI exit and Windows session ending now save the workspace first,
remove placeholders, stop projection/input ownership, and bring enrolled real
windows into physical monitor working areas. Already-contained windows stay put;
off-screen windows go to the nearest area (largest overlap takes precedence), and
oversized bounds are reduced where the application permits it. Native minimized/
maximized state and placement flags are retained. Previously visited desktops are
included without moving windows between desktops. Closed/recycled handles from a
different process are ignored. Ordinary virtual-desktop switching keeps its
existing behaviour.

This is usable-screen recovery, not an exact rewind to the desktop arrangement
before Canvas started. Recovery does not close applications. Saved world positions
and camera are never replaced with the temporary exit positions; the next Canvas
start reapplies the saved layout. Forced termination cannot execute cleanup.

Validation: 181 passing tests, including monitor geometry, shutdown detachment,
native-state routing, async projection shutdown, and a save/recover/restart test.
A live clean-exit/restart smoke check preserved all 19 saved entries, changed
none of their saved bounds, and preserved the camera. Multi-monitor geometry is
tested with fakes; a real reboot and full hardware/topology matrix were not tested.
The local publish/startup installation is updated; a pre-update binary/workspace
rollback copy is retained under the ignored publish-before-exit-restore directory.

The runtime/framework and package upgrades above are recommendations, not changes
silently bundled into this behaviour fix.
