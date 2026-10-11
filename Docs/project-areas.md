# Project areas

Project areas are named, colored rectangles in canvas coordinates. They provide
visual landmarks for projects without moving or copying files. Areas support
drawing, naming, recoloring, saving, removing, navigation, explicit membership,
moving their members together, resizing boundaries, and layout undo/redo.
The compact project navigator searches areas and their saved members, and opens
alongside the canvas for navigation and focus/reopen actions.

## Use

1. Open the overview with **Alt+Q**.
2. Hold **Shift** and drag with the left mouse button to mark an area. Alternatively,
   press **N**, then drag normally. The drag can start over a window thumbnail.
3. The **Project areas** manager opens with the new area selected. Enter a name,
   choose a color or enter `#RRGGBB`, then select **Save changes**.
4. Press **Z** in the overview, or choose **Project Areas…** in the system tray,
   to reopen the manager. Select an area and choose **Jump to area**, double-click
   it, or press Enter while its list row has focus.
5. **Draw an area** opens the overview and arms the next left-drag. **Esc** cancels
   an armed or unfinished gesture; press it again to close the overview. A tiny
   click/drag creates nothing.
6. Select **Choose members…**, check the saved places that belong to this project,
   and select **Save membership**. Filtering the list preserves hidden selections.
   Cancel leaves the saved membership alone. Targets/paths are available as tooltips.
7. In the overview, drag the area's name to move the group. Dashed member outlines
   preview the destination. Release applies and saves the move once; Esc or losing
   mouse capture cancels the preview. The label and list display member counts.
8. Drag a visible corner square to resize the boundary. The opposite corner stays
   fixed, and members stay where they are. Release saves once; Esc or losing mouse
   capture cancels. Handles use fixed screen sizes and larger pointer targets.
   Zoom in if an area is too small to show its handles. Corners clamp at 64 world
   units per side (or the original size for smaller existing areas).
9. In the overview, **Ctrl+Z** undoes an area move or resize. **Ctrl+Y** or
   **Ctrl+Shift+Z** redoes it. The manager also has **Undo** and **Redo** buttons.
10. Press **P** in the overview, choose **Project Navigator…** from the tray, or
    select **Navigator** in the area manager to open the compact project panel.

## Project navigator

The navigator is a resizable, modeless panel. It stays available when you jump to
an area. Expand an area to see its saved members, their current open/placeholder
state, and pinning. Shared members appear under each assigned area. Only areas on
the active virtual desktop are shown; switching desktops refreshes the panel.

Search matches area names, member names, target paths, and executable paths,
ignoring case. Matching an area shows all of its members; matching only a member
shows that member under its area. Counts always describe the full membership.
Search results expand automatically, and clearing search restores remembered
expansion. Unchanged workspace refreshes preserve the existing tree rows, focus,
selection, and scroll position. Loaded hidden panels refresh when shown again.

- Select an area and choose **Jump to area**, or press Enter in search to jump to
  the selected area. Jump uses the area's current saved boundary.
- Select a member and choose **Focus member** for an open app or **Reopen member**
  for a placeholder. Enter or double-click on a tree row performs that row's action.
  Focusing/reopening closes the overview and uses the workspace's existing current
  window bindings, launch queue, target rules, and error reporting.
- Opening/queued members cannot be launched again from this panel. **Needs linking**
  members must be linked through Saved Workspace first.
- **Manage areas…** opens the full editor with the current area selected.
- **Ctrl+F** focuses search; Down moves from search into the tree. **Esc** clears
  a nonempty search, then hides the panel. Closing its window releases its UI subscriptions.

Navigation actions revalidate IDs and memberships when invoked. Removed or
other-desktop areas and stale member rows cannot launch a place. Search and
navigation add no layout history entries or new persisted fields. Search text,
window placement, and expanded rows are kept only for the current panel session.

Jumping frames the area on the primary monitor and keeps the overview open.
Closing the overview uses the existing camera handoff to the real desktop.
Areas remain within the overview's existing zoom limits; very large areas may
not fit entirely at minimum zoom. Saved outlines and names are visible in the
interactive overview, rather than permanently over normal application windows.

Removing an area leaves its windows, saved places, and files in place. Membership
is explicit: overlapping an area does not assign a place. A saved place can belong
to several areas; moving one area moves that shared place once, while other areas'
boundaries stay put. Pinned members remain fixed. Closed-app placeholders move
with their saved places. Minimized and maximized apps keep their state while their
saved normal positions move. Nested areas remain a future milestone.

Layout history keeps the latest 50 moves/resizes per virtual desktop for this
session. Creating/removing areas, membership changes, naming, colors, and ordinary
window dragging are not history commands yet. Undo checks the affected boundary,
member positions, pinning, and membership before applying anything. If those no
longer match, it clears that desktop's layout history and reports the conflict;
it does not restore deleted areas or forgotten places. Renames, colors, and newer
minimize/maximize or z-order choices survive undo. A new layout edit clears redo.
History is not saved across restarts; the resulting layout is saved normally.

## Persistence and boundaries

- Areas belong to the active Windows virtual desktop and save automatically in
  the existing `%APPDATA%\CanvasWindowComposer\workspace.json`.
- `Zones` is an optional additive field of the version-1 workspace document.
  Older documents without it load as an empty area list. An older application
  build can ignore this field and remove it on its next save; use the new build
  after creating areas.
- Area IDs, desktop IDs, names, colors, and world bounds persist without native
  window handles. `ItemIds` is an optional list of saved-place IDs on each zone;
  older zones without it have no members. Existing item identities are unchanged.
- Membership references must be unique, exist, and belong to the same desktop as
  their area. Forgetting a place removes it from all areas. A place transferred to
  another desktop is detached from its previous areas.
- Names have a 120-character limit, and the document allows up to 256 areas.
  Geometry uses the workspace's existing validation limits.
- Failed loads keep the existing write protection. Failed saves retain the
  in-memory edit for a later retry and display the persistence error.

The existing Direct3D grid, DWM thumbnails, input hooks, and native window manager
remain in place. A WPF window provides the Fluent-styled manager. Each overview
monitor owns a transparent, non-activating WPF annotation window above its DWM
thumbnails. Its native click-through style routes input to the existing overview;
saved areas have no fill, so application contents are not tinted. Physical screen
coordinates are converted to WPF DIPs for the current monitor DPI. Annotation
windows hide on overview exit and dispose with their owner. Area membership does
not depend on geometric overlap.

## Implementation boundaries

- `ZoneContracts` exposes detached views and UI commands; `WorkspaceModels` owns
  persisted zone data. `ZoneService` owns membership and persistence notifications.
- `ZoneLayoutPlanner` validates all destination rectangles before any requested
  position changes, including replay conflict checks. `ZoneLayoutHistory` keeps
  bounded desktop timelines using saved IDs and geometry, with no native handles.
  `WorkspaceZoneOperations` coordinates current window identity,
  canvas geometry, placeholder snapshots, persistence, and existing native APIs.
  Its partial class keeps access to workspace-owned bindings private; it adds no
  new dependency-injection container or assembly boundary.
- `OverviewManager.Zones` isolates area input and annotations from the overview's
  native lifecycle. `OverviewZoneInteraction` owns draw/move/resize gesture state and ghost geometry. Preview
  leaves the actual document/canvas alone and snapshots member bounds once per drag.
- `ZoneLabelLayout` and `ZoneResizeLayout` share physical-pixel header/corner geometry
  between drawing and hit testing, including DPI and monitor offsets.
- `ZoneUi` owns WPF lifetime/subscriptions. `ZoneWindow`, `ZoneMembersWindow`, and
  `ZoneTheme` separate the manager, membership editor, and shared styling.
- `IZoneNavigationSource` supplies detached area/membership snapshots.
  `ZoneNavigation` joins these with current saved-place states, searches, and
  revalidates actions. Its narrow `IWorkspaceNavigationCommands` dependency exposes
  snapshots, status, notifications, and the existing open/focus command.
  `ZoneNavigatorWindow` owns tree/search presentation; navigation does not depend
  on the layout command/history layer.

Logical group moves are validated together. Native Windows movement uses the
existing acknowledgement and retry policy, rather than an atomic OS transaction.
Failures while updating collapsed applications' restore positions are surfaced
to the manager; saved layout changes remain available for recovery/retry.

## Verification

Automated tests cover old-document loading, area round trips, failed-save retries,
invalid-file preservation, active-desktop isolation, reverse-direction drawing,
camera fitting with monitor offsets, and manager save/navigation commands. Group
tests cover shared membership, pinned members, app state, placeholders, invalid
destinations, forgotten members, recycled handles, failed saves, filter selection,
desktop changes, and cancelling previews. Resize/history tests cover all corners,
clamping, DPI hit targets, repeated undo/redo, conflicting changes, per-desktop
timelines, bounded history, save/native failures, state retention, and manager
history controls. The manager and membership editor have inspected offscreen renders.
Navigator tests cover area/member/path search, shared and unassigned members,
desktop isolation, current bounds, focus/reopen actions, launch failures, ambiguous
members, stale IDs, subscription cleanup, expansion/selection retention, and
unchanged-tree reuse. The navigator also has an inspected offscreen render.

For an isolated native composition check, run the [zone preview](../Tools/ZonePreview/README.md).
It uses test windows and in-memory areas, with no input hooks or real workspace
file. Check that labels appear above the live thumbnail and drags reach the canvas.
Live mouse routing, DWM layering, and mixed-DPI monitor behavior still require a
manual check; the Windows computer-use native pipe was unavailable during development.

For a focused manual pass in the main app: move and resize an area, undo both,
redo both, cancel a resize with Esc, then switch virtual desktops and repeat.
Include a pinned member and a closed placeholder, and try a corner on each DPI
scale/monitor. Move a member independently before undoing the group to check the
conflict message. Restart to confirm the layout persists and session history resets.
Open the navigator with P, search for an area and a member path, expand a group,
focus an open member, and reopen a closed one. Keep a row selected while renaming
or forgetting its place and switching desktops to confirm refresh behavior.

To use the changes in the main app, exit the current Canvas instance through its
tray, then run `Start-Canvas.cmd --build`. This builds the local launcher’s
`publish-workspace` copy. A normal source build alone does not update that copy.
