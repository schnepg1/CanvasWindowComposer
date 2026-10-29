# Temporary windows

Canvas distinguishes deliberately placed windows from temporary arrivals. New
installations collect existing windows in a non-overlapping temporary area;
later launches restore saved places first and collect unmatched windows there.
The area is a deliberately placed, desktop-local object. Its position, size,
name and color survive a restart, including when it is empty. Its window slots
are only session state: new arrivals never reflow existing windows.

Placement avoids saved windows (including closed placeholders), project-area
boundaries, current canvas windows, annotations and visible tutorial notes, with
64 canvas units of spacing. A minimized arrival remains minimized and has a
live-session card; its normal restore size reserves space in the area.

A **Temporary** tab stays on the primary screen's right border, including when
the list is empty or Canvas overview is closed. Click it to peek into the
temporary zone. New arrivals slide the drawer out for five seconds and animate
into the top of the list, newest first. Notifications do not take focus or move
the camera. Hovering the drawer or dragging a row holds it open; leaving gives
another five seconds before it folds back. Click the tab again or press Escape
to fold it immediately. Reduced-motion settings skip the slide and row motion.

Drag a row onto clear canvas to place that window at the current camera, or
double-click to find nearby space. Explicit placement keeps its new place;
minimized windows stay minimized. Placement avoids existing content and visible
Canvas panels. If there is no room or Windows rejects the move, the entry stays
in the temporary zone. Closing an app removes its row. The drawer shows only
temporary-zone members on the current virtual desktop; live nearby arrivals and
the backpack have their own existing behavior.

**Show temporary area** in the drawer and **Go to temporary area** in the tray
also create an empty area before the first arrival, so you can choose its
location in advance. The backpack moves beside the drawer while it is open.

The area has a dashed canvas boundary. Drag its label to move it together with
its current temporary windows; drag a corner to resize the boundary. These
edits support the usual area undo/redo. Edit its name and color in **Project
Areas**. The area stays independent of the project hierarchy, so moving it
does not sweep up organized project areas or deliberately kept windows.
Its membership follows temporary arrivals automatically.

Using, focusing, minimizing, or rearranging a window inside the temporary area
keeps it temporary. Move its center outside the boundary, choose **Keep this
place**, assign it to a project area, or pin it to the screen to keep its place.
Native placement uses the completed Windows move/resize gesture as intent;
application-generated location changes and camera projection do not promote it.

Temporary items are excluded from workspace writes, backups and the Saved
Workspace list. Closing one removes its session card without leaving a
placeholder. Canvas shutdown still restores native windows using the existing
desktop recovery path; it does not close applications. Only the area's own
properties are saved. An app still open at the next launch is a new temporary
arrival unless it matches an intentional saved place.

Folder/project lookups may take time. New candidates can wait in the temporary
area while matching completes, then return to a matching saved place. Ambiguous
matches keep saved geometry intact and remain manually linkable.
Once that initial matching decision is complete, temporary windows do not
automatically claim saved places that become available later. Explicit linking
still works. Saving a temporary browser window as a web resource detaches the
saved resource from the temporary area after persistence succeeds.

## Upgrade adoption

Workspace format 1 remains readable. Missing `Placement` means deliberate.
When an existing workspace lacks `PlacementPolicyInitialized`, Canvas takes a
one-time identity snapshot of already-open windows and adopts those positions
as deliberate as they are captured. Delayed project/folder discovery uses that
same snapshot. Windows introduced afterward are temporary. Brand-new workspace
files start with the policy enabled, so first use gets the collection behavior.
Outstanding snapshot entries are checkpointed with window handle, PID and process
start time until adopted, forgotten, closed or replaced. This preserves the same
upgrade boundary across Canvas restarts while other desktops or project lookups
are still pending. These records classify adoption only: native actions continue
to require fresh identity validation. Ordinary temporary windows are not included
in this checkpoint, and no live workspace bindings are persisted.
Anchor-only temporary areas from the initial implementation are converted to
editable areas at the same location.

Tests cover upgrade adoption, first-launch batches, minimized state, spacing and
content protection, stable slots and anchors, temporary cleanup and restart,
explicit placement, delayed identity, ambiguous linking, file serialization,
menu actions, canvas rendering and non-activating native drawer behavior.
Area tests also cover moving live/minimized contents without promoting them,
header/corner gestures, group movement, undo/redo, empty-area persistence,
name/color editing, restart and subsequent arrivals at the chosen location.

## Drawer modules

`Overlays/Temporary/TemporaryDrawerState.cs` owns arrival ordering and the
monotonic peek/hover/drag lifetime without UI dependencies. `TemporaryDrawerPanel`
owns list rendering and reduced-motion arrival animations; `DrawerRowDrag`
handles captured-pointer release and cancellation. `TemporaryDrawerWindow`
owns the screen-edge HWND, clipping, slide and timer lifecycle. Only its visible
width intercepts clicks. `OverviewManager.Temporary.cs` translates screen drops
to the active camera and wires navigation. `WorkspaceService.TemporaryDrawer.cs`
provides detached live list snapshots and identity-validated, acknowledged
placement commands. Capture policy and temporary-zone persistence remain in
their existing modules.
