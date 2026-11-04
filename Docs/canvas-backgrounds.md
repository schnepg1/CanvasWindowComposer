# Canvas backgrounds

Backgrounds are large image surfaces in canvas coordinates. They move and zoom
with the camera and stay behind application content. Their dimensions are not
limited to a monitor or to an image's pixel dimensions.

## Create a place

Open **Backgrounds…** from the tray or the canvas toolbar's **More** menu.
Choose **Fireplace study**, **Bright workspace**, **Nature**, or **Abstract**
(the picker may use shorter names), or import your own PNG, JPEG or BMP.
Imports copy into the user's writable application data by default. Enable the
external-link option only when the source file will remain accessible.

Select a background, name it and choose **Free background** or a project area.
A project background follows the area's bounds, including group moves, nested
area moves, resizing and area undo/redo. Resizing the area never scales its apps.
Detach to keep the current bounds; deleting the area also detaches the background.
Removing an image surface leaves the project, apps and source image intact.

The editor provides world X/Y/width/height, Fill/Fit/Tile, crop alignment, opacity,
darkening, stacking order and locking. **Fill** preserves proportions and crops;
**Fit** shows the whole image; **Tile** repeats at the source's native pixel size.
Stacking order applies only among backgrounds, always below apps and annotations.

Backgrounds start locked. **Edit on canvas** unlocks a free background and shows
its frame and corner handles. Drag its frame to move it or a corner to resize it;
hold **Shift** to preserve its aspect ratio. The interior does not intercept apps.
Middle-drag or Space-drag still pans. **Esc** cancels an active drag; a second Esc
leaves background editing. Lock it again in the editor when finished.
Project-linked frames are edited by moving/resizing their project area instead.

Use the editor's **Undo/Redo** for background changes, or Ctrl+Z/Ctrl+Y while a
background is selected for canvas editing. Background history is separate from
the existing app/annotation/area history and is scoped to each virtual desktop.
The most recent 50 background edits remain available during the app session.
While the picker is open, typing and Escape belong to that window. Escape hides
it and retains its draft. Save or reload unfinished edits before installing an update.

For a first room: add the study background, make it several screens wide, place
two app or folder links there, then press **Ctrl+1** to save a camera bookmark.
Pan away and press **1** to return to the room. This also works at other zooms.

## Persistence and assets

`%APPDATA%\CanvasWindowComposer\backgrounds.json` stores version-1 placements,
with atomic writes and a backup. Missing files load as an empty scene. Invalid
documents are protected from overwrite; valid backup recovery preserves the bad
primary. Failed saves keep the in-memory edits and surface an error/retry action.
Other workspace documents and existing item identities are unchanged.

Managed image files live in `Assets\Backgrounds` beneath that writable data
directory, using a SHA-256 identity for deduplication. Built-in assets ship under
the installation's `Assets\Backgrounds`; updates never write user imports there.
External links retain their absolute source path. Missing, inaccessible or corrupt
images show a recoverable placeholder; locate/replace or retry from the editor.
Reimporting an original repairs a damaged managed copy. Assets are retained when
surfaces are deleted so undo and the saved backup remain usable.

PNG/JPEG/BMP imports are validated before copying, limited to 64 MiB and 32
megapixels. Asynchronous decoding uses two workers, 4096-pixel normal and
1024-pixel low-quality longest-edge limits, frozen detached pixels and a bounded
128 MiB cache. Under memory pressure, large images are demoted to smaller cached
versions, then a compact tier sized to retain the bounded working set. Visible
images remain available without an idle reload loop.
Offscreen images are culled; tile size is independent of decode
quality. Idle camera frames do not continuously invalidate desktop artwork.

## Rendering and verification boundaries

The desktop uses transparent, click-through, nonactivating WPF surfaces attached
to Explorer's wallpaper host, behind its icons and normal windows. The original
wallpaper is never changed. Surfaces hide during overview, app disable and exit;
the existing reconciliation tick checks shell restarts without an animation loop.
Each monitor projects the shared scene with its physical origin and DPI.

The overview reuses the same painter in its existing WPF annotation composition.
Because that WPF surface is above DWM thumbnails, native window rectangles are
excluded before painting backgrounds; placeholders, labels and editing controls
then paint above them. This keeps live app content clear. Transparent window
borders/shadows may show the underlying wallpaper within their excluded rectangle;
fully unified GPU composition remains a separate renderer change.

Model, persistence, asset, gesture, editor and offscreen WPF rendering tests cover
the main behaviour. Actual Explorer attachment, desktop-to-overview transitions,
input pass-through, shell recovery and mixed-DPI monitor behaviour require a Windows
desktop pass. A Linux cross-build does not establish these native behaviours.

Explorer wallpaper-host attachment uses the shell's WorkerW/Progman convention.
If that host is unavailable, desktop surfaces hide and the picker reports the
problem; overview backgrounds remain usable. Shell versions and mixed-DPI
setups need native verification. No FPS or Surface Pro 4 performance claim is made.

The application currently has no workspace transfer/export bundle. Background
assets are not automatically collected for transfer to another machine; copy
managed assets and the backgrounds sidecar together, and relocate external links.
