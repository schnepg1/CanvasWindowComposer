# Temporary windows

Canvas distinguishes deliberately placed windows from temporary arrivals. New
installations collect existing windows in a non-overlapping temporary area;
later launches restore saved places first and collect unmatched windows there.
The area's anchor is desktop-local and survives a restart. Its slots are only
session state: new arrivals never reflow existing windows.

Placement avoids saved windows (including closed placeholders), project-area
boundaries, current canvas windows, annotations and visible tutorial notes, with
64 canvas units of spacing. A minimized arrival remains minimized and has a
live-session card; its normal restore size reserves space in the area.

An arrival notice appears near the primary screen's right edge for eight seconds.
It groups a discovery batch, does not take focus or move the camera, and offers
**Show temporary area**. **Go to temporary area** remains available in the tray
after the notice expires. The area has a dashed, non-editable canvas boundary.

Using, focusing, minimizing, or rearranging a window inside the temporary area
keeps it temporary. Move its center outside the boundary, choose **Keep this
place**, assign it to a project area, or pin it to the screen to keep its place.
Native placement uses the completed Windows move/resize gesture as intent;
application-generated location changes and camera projection do not promote it.

Temporary items are excluded from workspace writes, backups and the Saved
Workspace list. Closing one removes its session card without leaving a
placeholder. Canvas shutdown still restores native windows using the existing
desktop recovery path; it does not close applications. Only the empty area's
location is saved. An app still open at the next launch is a new temporary
arrival unless it matches an intentional saved place.

Folder/project lookups may take time. New candidates can wait in the temporary
area while matching completes, then return to a matching saved place. Ambiguous
matches keep saved geometry intact and remain manually linkable.

## Upgrade adoption

Workspace format 1 remains readable. Missing `Placement` means deliberate.
When an existing workspace lacks `PlacementPolicyInitialized`, Canvas takes a
one-time identity snapshot of already-open windows and adopts those positions
as deliberate as they are captured. Delayed project/folder discovery uses that
same snapshot. Windows introduced afterward are temporary. Brand-new workspace
files start with the policy enabled, so first use gets the collection behavior.

Tests cover upgrade adoption, first-launch batches, minimized state, spacing and
content protection, stable slots and anchors, temporary cleanup and restart,
explicit placement, delayed identity, ambiguous linking, file serialization,
menu actions, canvas rendering and non-activating native notification behavior.
