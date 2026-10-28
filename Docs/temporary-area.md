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

An arrival notice appears near the primary screen's right edge for eight seconds.
It groups a discovery batch, does not take focus or move the camera, and offers
**Show temporary area**. **Go to temporary area** remains available in the tray
after the notice expires. This action also creates an empty area before the
first arrival, so you can choose its location in advance.

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
menu actions, canvas rendering and non-activating native notification behavior.
Area tests also cover moving live/minimized contents without promoting them,
header/corner gestures, group movement, undo/redo, empty-area persistence,
name/color editing, restart and subsequent arrivals at the chosen location.
