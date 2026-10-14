# Smoke Test Checklist

Run through before each release. Test on the installed version, not debug.

## Startup
- [ ] App launches without errors
- [ ] Tray icon appears in system tray
- [ ] Right-click tray shows context menu

## Panning
- [ ] Middle-click drag on desktop pans the canvas
- [ ] Alt+middle-click drag on a window pans from anywhere
- [ ] Cursor moves freely during pan (not locked)
- [ ] Release drag — inertia continues smoothly, then stops
- [ ] Minimap appears during pan, fades after stopping

## Overview Map
- [ ] Alt+scroll on desktop opens overview
- [ ] Alt+Q opens overview
- [ ] Overview shows live window thumbnails at correct positions
- [ ] Grid and X marks render in overview background
- [ ] Scroll wheel zooms overview (capped at 120%)
- [ ] Drag to pan overview
- [ ] Double-click window — main canvas centers on it, overview closes
- [ ] Double-click minimized window — restores and centers
- [ ] Arrow keys cycle through windows, camera follows
- [ ] Enter on selected window — navigates and closes
- [ ] Escape closes overview without jumping
- [ ] No camera jump after closing overview (foreground suppression)

## Search
- [ ] Alt+S opens search overlay
- [ ] Recent windows shown on open
- [ ] Typing filters by title and process name
- [ ] Arrow keys navigate results
- [ ] Enter navigates to selected window
- [ ] Escape closes search

## Window Management
- [ ] New windows are discovered and tracked
- [ ] Manually moving a window updates its canvas position
- [ ] Off-screen windows are clipped (no visible artifacts)
- [ ] Clipped windows restore when panned back on-screen

## Alt-Tab Integration
- [ ] Alt-Tab shows correct thumbnails for clipped windows
- [ ] Releasing Alt-Tab re-clips off-screen windows

## Virtual Desktops
- [ ] Switch to another desktop — canvas state resets
- [ ] Switch back — previous canvas state restored
- [ ] Minimap shows briefly on desktop switch

## Window Focus
- [ ] Clicking an off-screen window in taskbar centers canvas on it
- [ ] Minimizing a window does NOT cause camera jump
- [ ] Closing a window does NOT cause camera jump

## Exit
- [ ] Tray menu Exit restores all windows to original positions
- [ ] App terminates cleanly (no orphan processes)

### Card transitions

- Open overview near 100%, zoom slowly below 45%, then back above 55%.
  Watch for the subtle 150 ms fade with stable geometry and one title treatment.
- Rock between 46% and 54% in each direction: no representation flip or restore churn.
- Reverse direction mid-fade, pan/drag during it, and test overlapping cards
  across monitors with different DPI: no position jumps or incorrect overlap.
- With automatic parking enabled, zoom out and wait for parking; approach at
  detailed zoom. The card persists through restore, then fades to the app without activation.
- Minimize manually and restore using its card. Already minimized apps with no
  usable underlay should display an opaque card promptly. Test several real apps;
  DWM registration does not certify that each app has repainted its contents.
- Leave overview or change desktops during a fade; reopen. No old cards or
  titles should linger, and idle overview should have no animation timer activity.

### Startup intro

- Launch with several saved app/folder cards and open windows. Check the initial
  reveal, centered pullback, crisp title, level curved flight, and soft landing.
  Saved positions and native app sizes should not change during the flight.
- Enable automatic minimization; launch with both manually minimized and closed
  apps. The flight must not restore or launch them, or park other apps along its route.
- Skip with Enter (hold through key-repeat), arrows, Esc, left/double click,
  wheel, and Alt+middle drag (hold and move for over a second). The same gesture
  must not open a card, move the saved camera, or start inertia afterward.
- Open on a saved zoom below 100%; finish or skip, then close the untouched
  overview. Saved camera must be unchanged. After intentional navigation,
  normal overview-to-desktop synchronization must still work.
- Test empty layouts, multiple monitors/negative origins/mixed DPI, and both
  presentation backends. Verify DWM thumbnails stay visible while layered alpha
  changes and all monitors reveal together. Cancel during display/desktop change,
  disable Canvas, or quit: no overlay, filter, timer or parking guard may remain.
- Settings: disable startup intro and restart; then enable reduced startup motion
  or disable Windows animations. The latter uses a short stationary fade, with
  no camera flight or expanding title tracking. Rendering errors must skip the intro.
