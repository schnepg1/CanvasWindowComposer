# Canvas Window Composer

> **Note:** This project is in very early development. Expect rough edges, bugs, and breaking changes.

Turns your Windows desktop into an infinite, pannable, zoomable canvas. Middle-click drag to pan all windows; open the overview to navigate with a mouse or trackpad.

![Panning](Docs/canvasdesktop-pan.gif)

![Alt+S Search](Docs/canvasdesktop-alts.gif)

## Features

- **Pan** — Middle-click drag on desktop, or Alt+middle-click anywhere
- **Inertia** — Fling to keep sliding, smooth deceleration
- **Zoom** — Pinch, Ctrl+scroll, or use + / - in the overview
- **Interact at any live-thumbnail zoom** — Right-click a live window and choose **Interact with window** to use its mouse controls, typing, and keyboard shortcuts. **Esc** returns to canvas navigation. Experimental; see [window interaction](Docs/window-interaction.md).
- **Fuzzy search** — Alt+S to find and jump to any window
- **Minimap** — Click or drag to navigate; right-click the minimap or use the tray menu to keep it visible
- **Virtual desktops** — Independent canvas per desktop
- **Auto-focus** — Camera follows focused windows
- **App switching** — Alt+Tab and taskbar selections center the selected app while keeping Canvas view and its zoom. Fly-to animation defaults on; disable it in Settings → Interaction for immediate navigation. Outside Canvas view, Alt+Tab jumps to the app at desktop size. Automatic proximity restores never request navigation.
- **Off-screen hiding** — Windows hidden when panned out of view
- **System tray** — Toggle, reset, exit
- **Low quality mode** — Settings → Rendering offers a simple grid and cached previews or title cards during movement. Live previews return after settling. Applies immediately; normal quality is the default.
- **Project areas** — Draw named, colored zones, assign saved places, move groups, resize boundaries, undo/redo layouts, and jump back to projects. Areas save per virtual desktop. See [project areas](Docs/project-areas.md).
- **Project navigator** — Keep a compact panel beside the canvas: search areas, members, or paths, expand member lists, jump to areas, focus open members, or reopen closed ones. Open it with **P** in the overview or from the tray.

## Controls

Open **Controls** in the top-right corner of canvas mode for the full reference.

Camera bookmarks work like strategy-game control groups: press **Ctrl + a number (0–9)** to save your current canvas view, then press **that number** to jump back instantly. Saving again replaces that slot. Both the number row and numeric keypad work. Bookmarks include zoom, survive restarts, and are separate for each Windows virtual desktop. Empty slots leave your view in place. When typing in a note, changing a numeric setting, or interacting with a live app, the app keeps its normal keys; click empty canvas to use camera bookmarks again.

Choose **Arrange** to move items or **Interact** to use a live app on the canvas.

| Input | Action |
|---|---|
| Middle-click drag on desktop | Pan all windows |
| Alt + middle-click drag anywhere | Pan (works over windows) |
| Alt + Q | Toggle overview (map-view) |
| Scroll down on empty desktop | Open overview and zoom out |
| Space + left-drag or middle-drag in overview | Look around the canvas |
| Left-drag empty canvas / Ctrl+click an item | Select a group / add or remove an item from the selection |
| Ctrl + 0–9 in canvas mode | Save or replace a camera bookmark (position and zoom) |
| 0–9 in canvas mode | Jump to a saved camera bookmark |
| Two-finger scroll in overview | Zoom vertically or pan horizontally |
| Shift + scroll in overview | Pan horizontally |
| Pinch in overview | Zoom around the pointer (Windows precision touchpad) |
| Right-click a live thumbnail > Interact with window | Use the app at the current zoom, with mouse and real keyboard focus |
| Esc while interacting | Return to canvas navigation, keeping the overview open |
| Scroll, Alt + scroll, or + / - in overview | Zoom around the pointer |
| Alt + S | Fuzzy window search |
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

**Pan and zoom share one camera, the overview adds a second on top.** In *panning* mode the overlay is click-through (`WS_EX_TRANSPARENT`), so middle-click drag keeps driving the real canvas camera and all the thumbnails reflow in real time — including windows that would otherwise be clipped off-screen. In *zooming* mode (Alt+Q / scroll down on empty desktop) click-through goes off and an HLSL shader draws an adaptive grid, scale marks, and a nebula parallax; a second *overview camera* decouples from the canvas camera so you can zoom out further than the real screen would allow for a map-level view. Scrolling zooms the overview; Ctrl+scroll pans vertically. Clicking a thumbnail (or arrow-keys + Enter) recenters the canvas on that window and closes the overlay.

## Config

A default `config.ini` is written to `%APPDATA%\CanvasWindowComposer\` on first run. Every flag is commented out at its default — uncomment and change its value to configure a feature. Changes are picked up live; no restart. Open the folder via **Tray menu > Open Config Directory**.

Right-click a saved window or its overview card and choose **Exclude from auto-minimize** to keep that window from being automatically minimized. The choice is saved with the workspace place and can be changed even while the app is closed.

| Flag | Default | Effect when `true` |
|---|---|---|
| `DisableSearch` | `false` | Don't register the Alt+S fuzzy-search hotkey |
| `EnableAutomaticMinimization` | `false` | Experimental: capture eligible off-screen windows, then minimize them after a delay. Restore without activation on approach, using cached overview previews during restoration. Failed captures retain existing placement behavior. Real-application compatibility validation remains pending |

| `DisableAltPan` | `false` | Disable Alt + middle-click drag to pan over windows |
| `DisableGreedyDraw` | `true` | Skip `SetWindowRgn` clipping of off-screen windows. Keeps Alt-Tab / taskbar thumbnails live at the cost of render work for windows panned out of view. When clipping is enabled, off-screen thumbnails may be blank or incomplete |
| `DisableMouseCurve` | `false` | Send raw HID deltas to the canvas (1 count = 1 pixel) instead of applying Windows' pointer-acceleration curve. Use if you've turned off "Enhance pointer precision" and want linear pan |
| `DisableZoomHotkey` | `false` | Don't register Alt+Q for the overview. The overview is still reachable by starting a pan |
| `EnableShortcutZoomOut` | `true` | Zoom out immediately when Alt+Q opens canvas view; toggle in the zoom settings popup |
| `ShortcutZoomOutAmount` | `0.15` | Fraction to reduce the current zoom by (0–0.80); adjust with the zoom popup slider. 0.15 changes 100% zoom to 85% |
| `LowQualityMode` | `false` | Simple grid without nebula/decorations; cached previews or title cards during pan/zoom/window dragging. Change through Settings → Rendering for immediate application |
| `ShowTaskbarInCanvas` | `true` | Keep the real Windows taskbar visible and clickable while panning and zooming. Toggle in Settings > Rendering; applies immediately. Disable Windows taskbar auto-hide for permanent visibility |
| `AnimateWindowNavigation` | `true` | Animate app selection from Alt+Tab or the taskbar within Canvas view. Disable in Settings > Interaction for an immediate jump. Applies immediately; Windows animation preferences are respected |

Performance changes, validation limits and the remaining optimization roadmap are
tracked in [the canvas performance work log](Docs/canvas-performance.md).

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
New, unplaced windows collect in a [temporary area](Docs/temporary-area.md)
without leaving saved placeholders. Existing layouts are adopted on upgrade;
move a temporary window out of the area or choose **Keep this place** to save it.
Web resource placeholders save a URL, browser (Chrome, Edge or Firefox), and an
optional profile. Use **Add web resource…** from canvas tools, the tray, or Saved
Workspace; see [web resources](Docs/web-resources.md) for setup and recovery.
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

For agent work and native desktop tests, use the serialized runner:
`Tests\run_tests.bat -Configuration Release`. It waits for other test runs,
prompts you to close a running Canvas, and isolates results per run. See
[AGENTS.md](AGENTS.md).

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

On launch, Canvas reveals the restored workspace with a smooth camera tour:
a gradual takeoff to lightweight cards, a softly tracked app title, wide sideways
sweeps over project areas and window clusters, and rolling rises and dips before
landing. The duration grows with the number of areas and the distance between them.
Empty canvases still get a 12-second sweep.
The overview remains interactive afterward. The flight never updates the saved
camera or uses its temporary view to automatically minimize or restore apps.

Click, scroll, or press any key to end the tour with a short, smooth landing.
The landing gesture is consumed through its release so it cannot accidentally
open a window or start a pan. Settings → Startup
lets you disable the intro or choose a short stationary title fade. Windows'
animation preference also selects the reduced-motion version automatically.

Configuration keys: `EnableStartupIntro=true`, `ReduceStartupMotion=false`.

### On-canvas tutorial

On the first tutorial-enabled startup, notes and arrows are placed beside your visible
windows and the intro lands at a readable welcome note. The optional guided tour
introduces moving, zooming, arranging windows and making a note, one action at a time.
Every exercise is optional; **Show note** brings you back if you lose your place.
Open **Controls** at the top right of canvas mode (or from the tour) for the full reference.
Use **Tray → Tutorial** to hide the notes, return to them, or resume/restart the tour.
Progress is saved; **Skip tour** keeps the notes available.
Tutorial notes save separately from your own drawings.
See [on-canvas tutorial](Docs/canvas-tutorial.md) for behaviour and limitations.

### Canvas annotations

In canvas overview, use the toolbar to draw arrows, rectangles and ellipses, or
click **Text** to add a note. Use **Select** to move/resize drawings, double-click
notes to edit, and choose **Arrange** at the top right to move windows. Shift constrains
shapes/directions; Delete removes a selection. Ctrl+Z/Y undo/redo annotation edits
while a drawing/selection tool is active. Click outside a note or press Ctrl+Enter
to finish editing; Escape cancels changes. Drawings save automatically per virtual
desktop and return after restart.
The compact dock keeps Select, Shapes, Text, Undo and Redo in reach; use More for
editing, deletion, saving, nearby selection and AI Compose. The main Shapes button
reuses your last shape, while its arrow opens the shape chooser. Color, stroke and
text controls appear above the dock for the current tool or selected annotation.
The dock sits at the bottom of the screen, or immediately above the Windows taskbar
when it is shown. Hover feedback respects reduced motion and low quality settings.
Choose **Text**, click on the canvas and start typing directly in the note. Notes
keep their wrapping width and grow vertically as you type. A nearby toolbar controls
text size, color, bold/italic, alignment and background. Click outside or choose
**Done** to save; **Esc** cancels. Clearing an existing note and finishing removes it,
with undo available.
After editing, **Select** is active: drag the note to move it, drag a **corner to
scale the text and box together**, or drag a side to change its wrapping width.
Text sizes range from 8 to 4096 canvas units, so category headings can remain readable
when zoomed out. Double-click a note or press Enter on a selection to edit it again.
Grip sizes stay readable as you zoom.

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
