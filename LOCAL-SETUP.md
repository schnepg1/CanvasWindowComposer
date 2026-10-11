# Your local Canvas Window Composer setup

The source is cloned into `G:\Coding\Projects\Canvas\CanvasWindowComposer`.
The updated application is published in `publish-workspace` and uses the installed .NET 10
Desktop Runtime. SDK 10.0.401 and runtime 10.0.12 were installed side by side with
the older versions; those were not removed because other apps may use them.
Canvas runs as a normal user and starts at sign-in through the current user's
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` entry named
`CanvasWindowComposer-Local`, not an elevated scheduled task. Disable it using the tray
menu's **Start Canvas at sign-in** option.

Setup verification: Release publish completed with zero warnings and errors.
All 192 unit/integration tests passed on .NET 10, including launch-security rules, workspace save recovery,
close/reopen under a new window handle, placeholder rendering, desktop switches,
and responsiveness during a blocked Explorer folder lookup.
The original build remains in `publish-local` for rollback.
The original workspace activation saved 16 windows, including four Explorer
folder paths. The latest .NET 10 clean-exit/restart check retained all 19 current
saved entries, their bounds and camera; diagnostics confirmed unelevated execution
with startup enabled. A full Windows reboot has not been performed as part of testing.
The pre-upgrade binaries, workspace and legacy task definition are retained in
`publish-before-net10-20261011` for rollback.

## Persistent workspace

The updated app automatically saves window positions/sizes, camera positions,
and application identities every two seconds, and before normal exit/logoff.
State lives in `%APPDATA%\CanvasWindowComposer\workspace.json`, with an atomic
write and a `.bak` backup. Native window handles are never saved to disk.

1. Exit the old Canvas instance through its tray menu, then run `Start-Canvas.cmd`.
2. Arrange your windows on the canvas. Use **Save Workspace Now** for an immediate snapshot.
3. Close a window normally, or use **Saved Workspace > Close to placeholder**.
   Any normal application save prompt still appears. Its saved place becomes a card.
4. Use Alt+Q to find off-screen cards. Navigate to one and click **Open**.
5. In **Saved Workspace**, check **Open on startup** for items you want launched
   automatically. Unchecked items remain placeholders until you open them.
6. **Start Canvas at sign-in** is enabled for automatic restoration after reboot.
   This option controls the current user's normal-user Run entry; uncheck it to
   remove that entry. No administrator permission is required.

Use a card's right-click menu, or the workspace list, to choose a file/folder
launch target or forget a saved place. Forgetting a live window does not close it
and suppresses recapture of that window for the rest of its lifetime in this run.
The placeholder itself can be moved/resized, including through the overview.

Explorer folders are identified by their actual folder path when Shell automation
is available. Explorer tabs are not saved individually. For applications, executable
path and window title are used; unique applications can match even after a title
change. Multiple indistinguishable windows cannot be reliably identified across
restart. Applications that reuse an existing window may need manual positioning.
File targets are user-specified; open documents, browser tabs, terminal sessions,
and unsaved application data are not automatically recovered.

Virtual desktops keep separate cameras and saved items using their Windows desktop
IDs. This version does not recreate deleted desktops or migrate IDs if Windows
changes them. Auto-open for another desktop runs when you visit that desktop.

Placeholders are native lightweight cards, not running copies of the saved apps.
They contain labels and buttons rather than screenshots. They are visible on the
canvas, minimap, overview, and Alt+S search; the workspace list offers another way to find them.

Future versions can be stopped gracefully from a command prompt using
`publish-workspace\CanvasDesktop.exe --exit` before rebuilding. The original
upstream build does not support this command; exit that version through its tray.

## Start and stop

Double-click `Start-Canvas.cmd`, or use the **Canvas Window Composer (Local)**
Start menu shortcut. There is no administrator prompt. The application
runs in the system tray; it does not open a normal main window.

- Middle-click drag on the desktop: pan windows.
- Alt + middle-click drag: pan over a window.
- Alt + Q: overview.
- Alt + scroll: zoom.
- Alt + S: window search.
- Right-click the tray icon > Exit: save the canvas layout, remove placeholders,
  then bring managed windows into the nearest monitor's working area. Windows
  already contained in a monitor stay put. Oversized windows are reduced to fit
  where the application allows it; minimized/maximized states are retained.
  On the next Canvas start, the saved canvas positions and camera are reapplied.
  Windows from previously visited virtual desktops are recovered too, without
  moving them between desktops. Exiting does not close the applications.
- Forced termination, a crash, or a power failure bypasses this exit recovery.
  Use the tray's Exit action or `CanvasDesktop.exe --exit` for a clean shutdown.
- Tray > Open Config Directory: open the live configuration file.

## Continue development

Edit the C# files in `Source`, or open `CanvasDesktop.csproj` in Visual Studio
with the .NET desktop development workload. From PowerShell in this directory:

```powershell
dotnet build CanvasDesktop.csproj
dotnet test Tests/CanvasDesktop.Tests.csproj -c Release
```

To run your changes, first exit the running app through its tray menu, then:

```powershell
.\Start-Canvas.cmd --build
```

`global.json` pins the .NET 10 SDK feature band. Both explicit project paths and
`dotnet build CanvasDesktop.slnx` now work. Restore using
`dotnet restore CanvasDesktop.slnx --locked-mode` for reproducible dependencies.
When intentionally updating packages, regenerate and review both `packages.lock.json`
files. Windows build/test CI is defined in `.github/workflows/windows.yml`; it will
run when this working copy is published to a GitHub repository with Actions enabled.

## Launch security and limitations

Canvas uses `asInvoker` and refuses to open its workspace when started elevated.
The legacy current-user elevated task is migrated to per-user startup on its first
new-build launch, then that migration process exits. Start Canvas normally afterward.
Migration checks the task's user, exact executable path and absence of extra actions/
arguments; it does not remove tasks belonging to other users or installations.

Windows may prevent normal-user Canvas from moving applications started as
administrator. Start those apps normally where possible; a constrained elevated
window-control helper would be separate future work. Canvas no longer changes its
process DACL to grant termination permission to Everyone.

Workspace launches go through a guarded launcher. Applications must be existing
absolute `.exe` paths; optional targets must be existing absolute files/folders,
not command-line options, URLs or shell protocols. Explorer additionally accepts
an exact `::{GUID}` folder identifier. Launches use no shell interpreter, shell
association or `runas` verb. Applications requiring elevation must be started
manually. A chosen `.exe` still controls how it interprets its file argument; this
is not a sandbox. Treat saved executable/target data as trusted user configuration.

The optional NSIS installer now installs per-user without an elevated task.
It remains framework-dependent and requires .NET 10 Desktop Runtime. Uninstall
uses graceful exit and leaves saved workspace data intact. Remove a previous
machine-wide installation separately before switching to the per-user installer.

## Publish changes or contribute

The upstream repository was archived on 25 September 2026 and is read-only.
You can continue development locally or publish a fork. The MIT licence allows
modification and redistribution; retain its copyright and licence notice.
Upstream pull requests need the owner to unarchive the repository first.

For a feature, create a branch with `git switch -c feature/your-feature`.
To publish your work, fork the repository on GitHub, then configure your remotes:

```powershell
git remote rename origin upstream
git remote add origin https://github.com/YOUR-USERNAME/CanvasWindowComposer.git
git push -u origin feature/your-feature
```

No GitHub fork or remote contribution has been created by this local setup.

The optional per-user NSIS installer is built with `Install\build-installer.bat`
when NSIS is installed. No GitHub push or remote CI run was performed by this setup.
