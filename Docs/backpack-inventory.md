# Backpack inventory

While Canvas is running, new unmatched windows try free space inside the working
areas visible through the active camera. Placement preserves window size and
avoids existing windows, saved placeholders, areas, notes, and the backpack HUD.
If the view is full, the window goes into the backpack. It does not spill into
the temporary area. Startup discovery retains the existing temporary-area policy.

Press **I** while Canvas has keyboard focus, click the backpack badge, or choose
**Backpack inventory** in the tray. I remains normal text input in application
interaction and in the backpack's search box. Escape closes an open backpack
before leaving Canvas. The badge and expanded panel sit at the top border,
horizontally centered on the primary working area, and stay fixed while the
camera pans or the temporary drawer opens. Closing the panel keeps its badge
clickable and its contents available for the next opening.

Drag a canvas window onto the open backpack or its folded badge. A selection of
windows can also be collected together. **Put in backpack** is available in each
window's context menu. Drag an inventory tile out onto clear canvas, or
double-click it to find nearby landing space. If there is no clear landing
space, it stays in the backpack with an explanation. Search filters titles and
applications. Right-click a tile to place it, open its application, keep a session
item, or forget a saved entry.

**KEPT** entries were deliberately collected. Their inventory membership survives
restart, and closed applications remain as entries inside the backpack. **SESSION**
entries are automatic overflow: they remain temporary, disappear when closed,
and are excluded from workspace saves. Explicitly unpacking a session item keeps
its new place. **Keep in backpack** makes an automatic entry persistent.

Collection shows a short card flight into the brass backpack badge with a pickup
pulse and sparkle. Background overflow does not activate the panel or pan the
camera. If the collected window had foreground focus, focus transfers to the
backpack so typing cannot be trapped in the hidden source window.
Reduced-motion preferences replace the flight with a brief destination effect.
The closed badge appears briefly for background overflow, and remains available
in Canvas. The panel distinguishes kept entries in brass and session entries in
teal, with a scrollable, searchable grid.

The backpack is scoped to the active Windows virtual desktop. Carrying a window
detaches its former project memberships. Its native HWND remains tracked and is
clipped rather than minimized or closed; live previews cannot reveal it while
carried. Normal Canvas shutdown still restores native desktop windows. The saved
membership determines where a matching window returns on the next launch.
Window search and shell navigation reveal the backpack for carried windows
instead of moving the camera to an invisible source.

The UI is a dedicated Canvas tool window, owned by the visible canvas so focusing
the canvas cannot cover its controls. Like the temporary drawer, it has no
Alt-Tab or taskbar entry. Explorer folders hold launch shortcuts,
not live HWND ownership or canvas drop coordinates, so an ordinary folder cannot
provide this behavior. No application windows are reparented into the UI.

Validation covers visible-space placement, multi-monitor coordinates and HUD
reservations, overflow lifetime, kept membership and restart, closed-app entries,
native failure recovery, preview isolation, project and move-history detachment,
virtual desktops, search navigation, DPI rendering, the shortcut and canvas drop,
and test-owned native clipping and notification windows.
