# Canvas Window Composer

> **Note:** This project is in very early development. Expect rough edges, bugs, and breaking changes.

Turns your Windows desktop into an infinite, pannable, zoomable canvas. Middle-click drag to pan all windows; open the overview to navigate with a mouse or trackpad.

![Panning](Docs/canvasdesktop-pan.gif)

![Alt+S Search](Docs/canvasdesktop-alts.gif)

## Features

- **Pan** — Middle-click drag on desktop, or Alt+middle-click anywhere
- **Inertia** — Fling to keep sliding, smooth deceleration
- **Zoom** — Pinch, Ctrl+scroll, or use + / - in the overview
- **Fuzzy search** — Alt+S to find and jump to any window
- **Minimap** — Canvas overview, fades after inactivity
- **Virtual desktops** — Independent canvas per desktop
- **Auto-focus** — Camera follows focused windows
- **Off-screen hiding** — Windows hidden when panned out of view
- **System tray** — Toggle, reset, exit
- **Project areas** — Draw named, colored zones, assign saved places, move groups, resize boundaries, undo/redo layouts, and jump back to projects. Areas save per virtual desktop. See [project areas](Docs/project-areas.md).
- **Project navigator** — Keep a compact panel beside the canvas: search areas, members, or paths, expand member lists, jump to areas, focus open members, or reopen closed ones. Open it with **P** in the overview or from the tray.

## Controls

| Input | Action |
|---|---|
| Middle-click drag on desktop | Pan all windows |
| Alt + middle-click drag anywhere | Pan (works over windows) |
| Alt + Q | Toggle overview (map-view) |
| Alt + scroll on desktop | Open overview |
| Two-finger scroll in overview | Pan vertically or horizontally |
| Pinch in overview | Zoom around the pointer (Windows precision touchpad) |
| Ctrl + scroll, Alt + scroll, or + / - in overview | Zoom without a pinch gesture |
| Alt + S | Fuzzy window search |
| Shift + left-drag in overview | Draw a project area |
| Drag an area corner in overview | Resize its boundary without moving members |
| Ctrl+Z / Ctrl+Y (or Ctrl+Shift+Z) in overview | Undo / redo an area move or resize |
| N in overview | Draw the next project area with a normal left-drag |
| Z in overview | Open the project area manager |
| P in overview | Open the compact project navigator |
| Enter in navigator search / member tree | Jump to the selected area / jump or focus/reopen the selected row |
| Ctrl+F / Esc in navigator | Focus search / clear search, then hide the panel |
| Drag an area's name in overview | Move the area and its assigned, unpinned places |
| Tray menu > Enabled | Toggle the canvas on/off |
| Tray menu > Refresh | Unclip and redraw all windows |

## How it works

**The overview is a fake desktop made of live DWM thumbnails.** When you pan or press Alt+Q, a borderless form per monitor comes up with a D3D11 swap chain. Instead of rendering window contents ourselves, we call `DwmRegisterThumbnail` for the desktop wallpaper (Progman/WorkerW), every canvas-managed window, and the taskbar(s) — DWM then composites live thumbnails onto the form. We only push destination rects when the camera moves; the thumbnails stay in sync at the source window's own frame rate, with no pixel copy. The real windows stay parked wherever `SetWindowRgn` clipped them — the overview is a view on top of that state, not a replacement for it.

**Pan and zoom share one camera, the overview adds a second on top.** In *panning* mode the overlay is click-through (`WS_EX_TRANSPARENT`), so middle-click drag keeps driving the real canvas camera and all the thumbnails reflow in real time — including windows that would otherwise be clipped off-screen. In *zooming* mode (Alt+Q / Alt+scroll) click-through goes off and an HLSL shader draws an adaptive grid, scale marks, and a nebula parallax; a second *overview camera* decouples from the canvas camera so you can zoom out further than the real screen would allow for a map-level view. Scrolling pans the overview; pinching or Ctrl+scroll zooms it. Clicking a thumbnail (or arrow-keys + Enter) recenters the canvas on that window and closes the overlay.

## Config

A default `config.ini` is written to `%APPDATA%\CanvasWindowComposer\` on first run. Every flag is commented out at its default — uncomment and change its value to configure a feature. Changes are picked up live; no restart. Open the folder via **Tray menu > Open Config Directory**.

| Flag | Default | Effect when `true` |
|---|---|---|
| `DisableSearch` | `false` | Don't register the Alt+S fuzzy-search hotkey |
| `EnableAutomaticMinimization` | `false` | Experimental: capture eligible off-screen windows, then minimize them after a delay. Restore without activation on approach, using cached overview previews during restoration. Failed captures retain existing placement behavior. Real-application compatibility validation remains pending |
| `DisableAltPan` | `false` | Disable Alt + middle-click drag to pan over windows |
| `DisableGreedyDraw` | `true` | Skip `SetWindowRgn` clipping of off-screen windows. Keeps Alt-Tab / taskbar thumbnails live at the cost of render work for windows panned out of view |
| `DisableMouseCurve` | `false` | Send raw HID deltas to the canvas (1 count = 1 pixel) instead of applying Windows' pointer-acceleration curve. Use if you've turned off "Enhance pointer precision" and want linear pan |
| `DisableZoomHotkey` | `false` | Don't register Alt+Q for the overview. The overview is still reachable by starting a pan |

## Install and update

Use a self-contained Windows x64 installer from a published release when available.
It includes .NET; users do not need Git, the SDK or a separate runtime. Open Updates
from the tray to check, download and restart safely. Preview and stable channels
are separate. See [startup and distribution](Docs/startup-and-distribution.md) for
release preparation, signing, migration and support logs.

## Development requirements

- Windows 10/11
- .NET SDK selected by `global.json` (10.0.401 or a compatible patch in that feature band)
- Built-in Windows PowerShell 5.1 for `Start-Canvas.cmd`

## Build

### Local persistent-workspace extension

This working copy includes automatic workspace saves, clickable placeholders for
closed apps, Explorer folder targets, a saved-workspace manager, and opt-in startup
restoration. See [LOCAL-SETUP.md](LOCAL-SETUP.md) for usage and current limitations.
Run `Start-Canvas.cmd`; exit any older running instance first. This development
launcher rebuilds current sources on each run, so a successful `git pull` is used.
Use `Start-Canvas.cmd --diagnose` for dependency checks and copyable startup logs.

On a normal exit, the canvas layout is saved before real windows are brought
back into physical monitor working areas. Starting Canvas again reapplies the
saved canvas layout. Forced termination cannot run this recovery.

Canvas runs without administrator rights. Sign-in startup is per-user, and saved
applications are never automatically launched from an elevated Canvas process.
Windows may block normal-user Canvas from moving elevated applications.
See [LOCAL-SETUP.md](LOCAL-SETUP.md) for launch-target rules and migration details.
Development boundaries and refactor validation are documented in
[workspace architecture](Docs/workspace-architecture.md).
Startup rollback and shutdown ownership are covered in
[lifecycle refactor notes](Docs/lifecycle-refactor.md).
Canvas tool hosting, annotation layers, and the recommended stack for future
arrows and text notes are described in [canvas view tools](Docs/canvas-view-tools.md).

Duplicate-window matching now keeps uncertain saved places as placeholders, with
manual linking and optional names. VS Code can reopen separate project targets;
opt-in full-path title matching is described in the
[window identity and matching notes](Docs/identity-and-matching.md).

Build and test on .NET 10:

```powershell
dotnet restore CanvasDesktop.slnx --locked-mode
dotnet build CanvasDesktop.slnx -c Release --no-restore
dotnet test Tests/CanvasDesktop.Tests.csproj -c Release --no-build --no-restore
```

```bash
# C# app
dotnet build

# Installer (optional)
Install\build-installer.bat 1.13.0-preview.1 preview
```

## Author

**Devnova** — [github.com/oreyg](https://github.com/oreyg)

## License

[MIT](LICENSE)

### Startup reveal

On launch, Canvas reveals the restored workspace with a 3.5-second intro:
centered zoom-out to lightweight cards, a softly tracked app title, a short curved
camera flight across nearby windows, and a gentle return to the saved view.
The overview remains interactive afterward. The flight never updates the saved
camera or uses its temporary view to automatically minimize or restore apps.

Click, scroll, or press a key to skip. The skip gesture is consumed through its
release so it cannot accidentally open a window or start a pan. Settings → Startup
lets you disable the intro or choose a short stationary title fade. Windows'
animation preference also selects the reduced-motion version automatically.

Configuration keys: `EnableStartupIntro=true`, `ReduceStartupMotion=false`.

### Canvas annotations

In canvas overview, use the toolbar to draw arrows, rectangles and ellipses, or
click **Text** to add a note. Use **Select** to move/resize drawings, double-click
notes to edit, and **Windows** to return to window navigation. Shift constrains
shapes/directions; Delete removes a selection. Ctrl+Z/Y undo/redo annotation edits
while a drawing/selection tool is active. Notes save with Ctrl+Enter; Escape cancels
editing. Drawings save automatically per virtual desktop and return after restart.
Notes open in a card preview with matching colors, proportions, typography and wrapping.
Save with Ctrl+Enter or cancel with Esc. Selected drawings have larger corner/endpoint
grips; notes and shapes also resize from their sides. Grip sizes stay readable as you zoom.

See [canvas tools](Docs/canvas-view-tools.md#canvas-annotations-v1) for details.

### Spatial selection

Select windows, annotations and project tiles together with rectangle selection, Ctrl-click or a configurable radius around the camera. Drag the group with ghost previews and one-step undo, or open AI Compose to review the selection as context. Click a project name to include its assigned windows and spatial contents. See [spatial selection](Docs/spatial-selection.md).

### AI Canvas Composer

Click **AI Compose** in overview to turn a prompt and explicitly checked canvas context into
an editable diagram or text rewrite. Choose an **OpenAI API key** or **ChatGPT plan**.
Continue with ChatGPT opens browser sign-in, then offers saved accounts and their
available models. API-key usage is billed separately; the connection never changes
automatically. Inspect the translucent preview, then Apply or discard it. A complete
proposal is one undo step. See [AI connections](Docs/ai-connections.md) for setup.
ACP remains a future adapter.
See [AI composer design and roadmap](Docs/ai-canvas-composer.md).
