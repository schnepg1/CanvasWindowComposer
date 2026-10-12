# Readable placeholder cards

Saved places show a large bold title, fitted and wrapped to the available space.
The card's **Name this saved place…** action provides a shorter custom title when
an application's caption is too long. Secondary details yield space on small
cards without changing their saved bounds. Open, linking, startup selection,
renaming, and placement still use the existing workspace commands.

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

Automatic minimization is a separate, opt-in policy (`EnableAutomaticMinimization`).
It parks eligible running windows after 1.5 seconds beyond the main canvas
viewport's 512-pixel departure margin, and restores them without activation when
they enter the viewport or remain within its 256-pixel approach margin for 150 ms.
The independent zoomed overview camera does not drive this policy; new parking
is suspended in zooming mode. Automatically parked applications use cached image
previews, while closed saved applications and folders use these info cards.
User-minimized windows are not automatically restored by the parking policy.

Below 80% overview zoom, a separate WPF annotation draws crisp title badges in
display coordinates, following each card's current projected position. Text stays
between 22 and 32 logical pixels rather than shrinking with the DWM thumbnail.
Badges support the overview's existing drag and double-click navigation. They do
not launch an application on their own.

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
