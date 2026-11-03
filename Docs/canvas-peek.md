# Canvas Peek

Hover a running window below 80% canvas zoom for one second to expand a readable
preview. It works over live thumbnails, detailed cards and simple icon cards,
including when card tiers are disabled. While the pointer dwells on a card, a
bar sweeps around its border once as the preview arms. Use the window/card
context menu's **Preview window (Canvas Peek)** command for an explicit preview
at any zoom. Saved launchers with no running window never launch on hover.

Peek activation is independent of the zoom tiers: hovering keeps a running
source awake and restores one that automatic minimization has parked, so the
preview loads live at any zoom-out level. User-minimized windows stay minimized;
their preview falls back to the unavailable status card.

The preview expands from the original tile into the monitor's available work
area, preserving aspect ratio and leaving canvas context visible. Its source
tile stays outlined. The camera and saved window bounds remain unchanged.
Low-quality mode, reduced motion and Windows animation preferences disable the
morph. Only one expanded preview is held at a time.

## Preview, work, return

- A passive preview never takes focus. Move into it to read it; a 400 ms grace
  period lets the pointer cross the gap. Leaving both source and preview closes it.
- Choose **Work here**, or press **Enter** while a passive live preview is open.
  In Interact mode, clicking preview content also enters Work here; that first
  click is consumed rather than operating an unknown source control.
- Work here stays open when the pointer leaves. Typing, numbers, clipboard
  shortcuts and Escape belong to the application. Wheel input over the preview,
  including modified and horizontal wheel, is forwarded to the application.
- Choose **Return** to resume the same canvas view. Clicking the canvas outside
  an interactive peek also returns; that exit click does not start a canvas drag.
  Escape dismisses passive previews, while the application's Escape remains
  available in Work here.
- Owned foreground dialogs temporarily replace the source in the expanded view.
  Complete or cancel the dialog to return to its parent. Return is disabled
  while that dialog is open. A dialog that cannot be hosted safely hands off to
  the native desktop instead of leaving it behind the overlay with Escape blocked.
- **Open app** explicitly opens the native desktop-size view when a preview is
  unavailable or an app needs native interaction. This action intentionally
  leaves the overview and uses the existing app-navigation behavior.

Pan/zoom, tools, menus, overview exit, display changes and source loss cancel
passive previews. External app focus releases Work here without pulling focus
back. Displayed running sources are temporarily excluded from automatic
minimization; this does not change their saved exclusion preference. Hover does
not restore apps that are already minimized or automatically parked: those
previews clearly show **Preview unavailable** and offer **Open app**.

## Settings

Settings > Zoom & cards contains **Hover to preview windows (Canvas Peek)** and
**Preview hover delay**, with a 300–3000 ms range and a 1000 ms default. Changes
are applied on saving. Reset zoom defaults resets these controls too.

## Implementation and compatibility

`CanvasPeekHover` owns stationary dwell, and `CanvasPeekLayout` owns monitor
placement, aspect fit and morph interpolation. `CanvasPeekWindow` owns one
nonactivating DWM thumbnail destination, accessible native action buttons and
the preview chrome. `OverviewManager.Peek` owns the lifecycle and uses the
existing `OverviewWindowInteraction` for pointer mapping and real keyboard
focus. Source windows are never moved, resized or reparented to make a peek.
The native thumbnail, hover timer, parking lease and input hook are released
when their owning session ends. DWM errors fail closed; rejected pointer input
returns to a passive preview with a visible Open app fallback.

This feature inherits the [experimental interaction bridge's compatibility
limits](window-interaction.md#compatibility). Native popup menus, raw-input
controls, apps that query physical cursor position, elevated apps and external
drag-and-drop can require **Open app**. A successful posted mouse message does
not establish support for every application. Native controls and owned-dialog
tests do not replace manual VS Code, JetBrains and browser validation.

Automated regressions cover dwell/jitter/cancellation, negative monitor origins,
mixed DPI placement, aspect fit, live-only rendering settings, running cards,
launcher safety, camera/bounds preservation, wheel/input failure, Escape
ownership, owned dialogs, source lifetime and DWM cleanup. Windows tests save
preview/working chrome images alongside their test results. Manual validation
should additionally exercise actual editor prompts, browser menus and scrolling,
IME/composition, multiple monitors and native dialogs under normal and reduced
motion. No real-app GPU/FPS benchmark is claimed.
