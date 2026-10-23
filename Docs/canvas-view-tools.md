# Canvas view module and future drawing tools

The canvas view now has a UI-neutral module in
`Modules/CanvasView/CanvasDesktop.CanvasView.csproj` targeting `net10.0`.
It has no package, WinForms, WPF, Win32 or application dependency.
The application references the module; dependencies do not point back into the
executable. This refactor prepares drawing support without adding arrows or notes.

## Current boundaries

| Component | Responsibility |
| --- | --- |
| `CanvasViewTransform` | Floating-point world, virtual-screen, monitor-local physical pixel and DIP conversion. Shared by input, layout and painting; separate from native window placement rules. |
| `ICanvasTool` / `CanvasPointer` | Tool interaction with normalized button, modifier, surface and world-coordinate data. No UI event arguments or native handles. |
| `CanvasToolController` | Exclusive tool/window/pan gesture ownership, initiating surface/button, cancellation and clearing ownership before commit callbacks. |
| `ZoneCanvasTool` | Adapts the existing pure draw/move/resize engine to the tool contract, with detached zone/member data and commit events. No forms or service dependencies. |
| `OverviewManager.Tools` | WinForms conversion, native capture, selected tool/layer attachment and common Escape policy. |
| `OverviewAnnotationSurface` | Explicit painting order: placeholder cards/labels, areas, attached drawing layers, then HUD. |
| Annotation painters / snapshot | WPF rendering and hit-layout data, independent of native window ownership. Existing DWM thumbnail and grid lifecycles remain in the overview host. |

The host can attach a tool and its WPF layers with `SetCanvasTool`. Passing null
restores the project-area tool. Beginning a new project area restores that tool
explicitly. Pointer updates refresh the scene using the cached card snapshot,
without rebuilding card transitions and occluders for each preview movement.
Hidden/disposed overviews clear the cached image references.

Tools get first opportunity to consume pointer-down. A consumed gesture keeps its
initiating surface and button until completion or cancellation; other buttons
cannot start a competing pan/window drag. The host alone acquires/releases
native capture. Capture loss, overview exit, tool switching, display rebuild and
desktop switching cancel pending gestures. A first Escape cancels an armed/active
interaction; another closes the overview. Global hotkey and form events use the
same operation. Wheel and keyboard camera navigation are blocked during a gesture;
external camera changes cancel it before replacing the transform.

In interactive overview, Windows wheel messages from two-finger trackpad
scrolling pan the camera on either axis. A precision touchpad pinch, delivered
by Windows as Ctrl+wheel, zooms around the pointer. Ctrl+wheel and Alt+wheel
also zoom with a mouse, and + / - zoom from the keyboard. Windows reserves some three-
and four-finger gestures for system actions; these are not intercepted.

## Recommended feature stack

Continue with **C#/.NET 10**, the new **UI-neutral canvas-view module**, and
**WPF drawing layers** above the existing **DWM/D3D11** window view.
This provides native Windows integration, vector arrows, text layout, per-monitor
DPI support and a focused text editor using APIs already deployed in the app.
A browser renderer or new UI framework would introduce another composition/input
boundary without solving a current requirement.

| Feature | Recommended implementation |
| --- | --- |
| Arrows, connectors and selection handles | Immutable world-space points/styles in the drawing domain; WPF `StreamGeometry`/`DrawingContext` painting through a drawing layer. Keep handles readable in DIPs. |
| Text notes | Immutable note ID, desktop ID, world position/bounds, text and style. WPF `FormattedText` for display, a focused `TextBox` editor hosted by the Windows adapter for input/IME. |
| Tool state and undo | C# command-based drawing document with one edit per completed gesture. Keep previews transient. Route undo by editing context initially; add a combined chronological history only if product behavior requires it. |
| Persistence | Versioned `System.Text.Json` drawing data through a host adapter, reusing the existing atomic-save/backup/error approach. Define migration and finite-coordinate/text/point-count limits before enabling saves. |
| Window-attached connectors | Stable workspace item IDs and a bounds adapter; never persistent HWND identities. Define closed-app/placeholder and desktop-switch behavior. |
| Testing | xUnit for geometry/tools/document/history, WPF offscreen rendering for layers, and Windows native tests/smoke checks for capture, focus, IME and DWM ordering. |

The future drawing domain should own drawing IDs, geometry, styles and desktop
scope independently of `Canvas.Windows`, `WorldRect` window state and workspace
matching. Put its headless code beside or within the neutral module. WPF painters
and editor adapters stay in the Windows host; extract a Windows presentation
project only when it has enough independent functionality to justify one.

For a first feature PR, implement free-positioned arrows, then text-note display
and editing. Suspend overview shortcuts while an editor owns focus and handle
Escape inside editing before closing the view. Decide explicitly whether annotations
are visible only in interactive overview or also over the live desktop: the
current annotations hide with the overview and panning remains click-through.

Use retained `DrawingVisual` layers and viewport culling if measurements justify
them for larger documents. Consider SkiaSharp only if profiling identifies a WPF
rendering limitation or a cross-platform product requirement emerges. Neither
SkiaSharp nor a spatial index is required to start with arrows and text notes.

## Verification

The existing project-area geometry/history and placeholder pixel/occlusion tests
are retained. New checks cover the standalone assembly dependency boundary,
fractional geometry, negative monitor origins, mixed per-axis DPI, exclusive
gesture ownership, secondary buttons, stale releases, cancellation, reentrant and
failed commits, tool switching, desktop cancellation, real overview Escape routing,
layer ordering/detachment and detached scene collections.

Run the existing locked restore, Release build, tests and publish workflow on
Windows. Native capture across physical monitors, mixed-DPI display changes,
physical Escape hotkeys and future text IME behavior still merit manual hardware
smoke checks; offscreen rendering does not verify those interactions.

## Canvas annotations v1

The overview toolbar exposes a unified Select control (combining window navigation,
group selection, element movement, and handle resizing) alongside Arrow, Rectangle, Ellipse and Text modes. Drawing modes own left clicks even above
window previews; middle drag remains camera navigation. Select moves annotations
and exposes corner/endpoint handles. Shift constrains arrows to 45-degree steps
and shapes to equal sides. Color and stroke apply to the selection and new items;
the Text menu applies size, emphasis, alignment and card fill. Delete removes the
selected item. Double-click a note in Select mode, or press
Enter with a note selected, to edit it.

Text is plain multiline Unicode. Click in Text mode to place the default card, or
drag to draw a card at the chosen size; a drag below the minimum still produces a
usable card. A separate focused native editor handles typing, clipboard and IME,
while annotation painting stays passive. The editor previews the card and changes
its text size (8–96 canvas units) with a numeric control, preset links or Ctrl +/-
along with left/center/right alignment (Ctrl+L/E/R), bold/italic emphasis (Ctrl+B/I)
and the card fill (solid, tinted or outline). All style changes are saved with the
note. The annotation toolbar applies text size, emphasis, alignment and fill to the
selected item and to new notes. Ctrl+Enter or Save commits the complete text edit;
Escape or closing the editor cancels it. Global canvas input is drained while
editing or drawing. A first Escape cancels a gesture or editor; an idle
drawing mode returns to the unified Select mode on Escape, an active selection is
cleared on Escape, and pressing Escape with nothing selected closes overview.
Annotations are visible only in interactive overview, not on the live desktop or
during the startup flight.

`AnnotationDocument` owns immutable world geometry and bounded, desktop-local
undo/redo. Pointer previews do not enter storage/history. Ctrl+Z/Y and
Ctrl+Shift+Z operate on annotation history while an annotation mode is active;
Windows mode retains project-area history. Toolbar Undo/Redo always target
annotations. TextBox undo remains local while typing. Selection handles and hit
slop use DIPs; annotation geometry and text size follow canvas zoom.

`annotations.json` in the existing configuration directory stores a separate
version-1 document with desktop GUIDs and annotation GUIDs. Saves happen only on
completed edits and use a temporary file, disk flush, atomic replacement and
backup. Invalid primary data can recover from backup while preserving the damaged
file. If neither copy can load, saving stays disabled to preserve user data.
The toolbar shows a warning and its Save action retries saves and displays the
status. Limits: 2,000 items, 10,000 characters per note, 16 MiB file, finite
coordinates within ±10,000,000, stroke widths 1–12, text sizes 8–96, and one of
three alignments, four emphasis styles and three card fills. Version-1 files
without these style fields load with left alignment, normal emphasis and a solid
card. History retains 100 edits per desktop for the current session. No HWND
identities are persisted.

Hardware smoke checklist: draw across a monitor boundary; test a negative-origin
monitor at a different DPI; resize both ends of an arrow; edit a Persian/IME note;
press Escape locally/globally; switch desktops during capture/editing; restart to
verify saved geometry; verify Windows mode still opens preview windows on double
click. Automated coverage includes offscreen rendering and native overview input,
but does not substitute for these physical-device checks.
