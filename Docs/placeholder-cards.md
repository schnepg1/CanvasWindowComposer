# Readable placeholder cards

Saved places show a large bold title, fitted and wrapped to the available space.
The card's **Rename…** action provides a shorter custom title when
an application's caption is too long. Secondary details yield space on small
cards without changing their saved bounds. Open, linking, startup selection,
renaming, and placement still use the existing workspace commands.

Right-click a distant rectangle, a detailed title card, or a saved live preview
to use the same saved-window menu. **Rename…** changes only Canvas's saved name;
the application's native caption remains its own. Blank names restore the
automatic caption. Custom names update both card representations immediately and
survive tab/document caption changes and restarts. Chrome entries also offer
**Chrome profile…**; see [Chrome profiles](chrome-profiles.md).

Explorer folder places have a vector folder watermark. Other cards request a
256-pixel icon from the saved application's executable, with its associated icon
and a generic application symbol as fallbacks. Icons are loaded when the symbol
changes, never during painting, and disposed with the card. Missing application
files do not prevent cards from displaying.

The overview draws cached snapshots of card controls on its annotation surface.
`DrawToBitmap` renders the controls even when Windows has never painted their
off-screen form, avoiding white DWM thumbnails until first focus. Snapshots are
created only for cards entering the view and reused until their content or size
changes. They follow pan/zoom and are clipped around higher windows to preserve
thumbnail z-order. Rendering never activates a card or launches its saved app.

Automatic minimization is an opt-in policy (`EnableAutomaticMinimization`). Outside
the overview it follows the main canvas viewport. While the overview is open,
its independent camera, zoom, and monitor bounds drive proximity instead.
Eligible running windows park after 1.5 seconds beyond the viewport's 512-pixel
departure margin. In detailed overview mode they restore without activation when
visible or after 150 ms within the 256-pixel approach margin. In simple overview mode,
eligible apps may park even when visible in the map and are not restored merely
because the wide view includes them. Zooming out never maximizes applications;
native sizes and saved placements remain independent of the overview zoom.

In simple overview mode, running apps use info-card proxies. Minimized apps and folders
also have cards at every zoom. A live card replaces its application in overview
ordering, with one saved identity, geometry, and project membership. Moving or
resizing the proxy updates the live application's saved place without restoring
it. Leaving the overview or zooming back in removes unnecessary live proxies.
Automatically parked windows retain their cached capture during restoration.

User-minimized windows stay minimized until an explicit **Restore**, navigator
activation, or overview double-click. Live-card activation restores/focuses the
existing validated HWND, never launches a duplicate process. Closed cards keep
their **Open** action and are never launched just by proximity or zoom changes.

In simple overview mode, the card is a simple rectangle, without a bitmap of its
controls. A separate WPF annotation draws crisp title badges in
display coordinates, following each card's current projected position. Text stays
between 22 and 32 logical pixels rather than shrinking with the DWM thumbnail.
Badges support the overview's existing drag and double-click navigation. In detailed
mode, the full info card's own title is shown, without a duplicate badge.
There is no card bitmap generation or DWM card thumbnail in the distant view.

Overlapping badges yield to hovered, keyboard-selected, or dragged cards. Labels
also avoid project headers and the overview instruction strip. Extremely small
cards hide their labels unless prioritized. This keeps distant views readable;
hover a card or select it with the arrow keys to prioritize its title.

The Direct3D grid, DWM thumbnail engine, native window management, and workspace
file format are unchanged. The wider WinForms-to-WPF Fluent migration is a
separate next step.

The isolated renderer in `Tools/ZonePreview` produces
`placeholder-cards-preview.png` from the actual card controls and annotation
drawing. Automated checks cover zoom limits, collision priority, off-screen
culling, negative monitor origins, DPI scaling, custom names, and desktop
switching. Physical multi-monitor interaction still requires a live check.

## Overview transitions

The first overview view uses 50% zoom to select a representation. Thereafter,
zooming below 45% enters the simple-card mode; zooming back to 55% exits it.
The same latch controls card rendering and automatic parking, so the 45–55%
band cannot repeatedly minimize/restore apps or flip their titles.

Visible cards transition over 150 ms with smoothstep easing. Card geometry
follows the current camera and drag position throughout. A running app's native
thumbnail is retained underneath its incoming card only for the fade, then
unregistered. Restoration keeps the old card through the existing cached restore
allowance, then fades it away after live DWM registration on all relevant passes.
Registration is not a guarantee that a third-party app has painted fresh pixels.
Manually minimized windows that have no remaining native/cached pixels show an
opaque card immediately rather than fading over an empty surface.

Full-card and floating titles fade sequentially through a plain rectangle to
avoid duplicate text. Frozen outgoing card snapshots are borrowed only during
transitions and released afterward. Distant views create no new card bitmaps.
Only visible saved identities have transition state; panning away, leaving the
overview, switching desktop, or disposal releases it. The UI animation timer runs
while a transition is active, independently of camera inertia. If live-thumbnail
registration or its initial update fails, a covered source retries every 250 ms
until ready or no longer visible. A normal idle overview has no timer activity.
