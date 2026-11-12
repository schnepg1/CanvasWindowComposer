# Desktop handoff native writers

The normal close still performs synchronous placement. A future asynchronous close must keep the UI message pump available while the projection worker waits inside USER32. A synchronous region or position write against another HWND can also block the UI if that window shares an owner thread or input queue with a batch target. [SetWindowRgn](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowrgn) · [Win32 message queues](https://learn.microsoft.com/en-us/windows/win32/winmsg/about-messages-and-message-queues)

`WindowManager` pauses preview-region reconciliation from batch submission until the UI owner releases the lease. `SetLivePreviewWindows`, `UnclipAll` and `ReclipAll` can still update desired preview state during the wait. On release the manager reconciles once against current windows and preview state. Observed destruction removes the window from the reconciliation pass. Pre-submission preparation still runs on the UI thread.

`TryDeferShutdownForDesktopPlacement` covers the entire reservation, including preparation. It retains the first shutdown continuation and prevents a new lease after a deferred shutdown request. On release it clears the reservation and gives the continuation priority over preview-region replay. The shutdown coordinator should post the continuation to the UI dispatcher and abandon an unsubmitted lease when preparation returns and `DesktopPlacementShutdownRequested` is true.

While a reservation is active, the manager coalesces implicit camera and display projection and skips automatic parking passes. Release leaves those passes for the next periodic tick, giving the UI owner time to handle the placement result and any lifecycle transition. A display request consumes a pending camera request and uses the current monitor layout and camera. A camera-only replay respects projection suspension and the normal throttle. Parking recalculates its decisions from the current window identity, proximity, settings and recovery records. Periodic workspace maintenance retains its existing deadline and resumes matching and restoring saved slots after release. Shutdown takes priority over all of this work.

WindowManager's restore, focus and model-state callbacks also wait until a tick after lease release. They retain a window observation lifetime and a semantic event. Replay checks visibility, desktop and process identity again. A window minimized again stays minimized, and parked-window focus can recenter only while that window is still foreground. The latest carried-window focus can open inventory after focus moves to a Canvas-owned cover. Discovery and destruction continue while waiting. Destroyed windows and windows whose identity cannot be verified have no replay.

| Entry point | Native writes and pending work |
| --- | --- |
| Camera projection and display change | The camera callback and manager display reprojection are deferred. The overview display handler also tears down and rebuilds passes. It still needs to coordinate the entire transition. Explicit `Reproject`, `ReprojectSync`, and `Canvas.Commit` need to stay with their initiating action until placement can run. |
| Discovery and WinEvent callbacks | `RegisterWindow` updates the canvas without restoring a saved slot. Periodic workspace maintenance can restore the slot and waits for the lease. WindowManager defers restore, focus and model-state callbacks that can move or reclip windows. ForegroundCoordinator also subscribes to focus and explicit shell selection and owns its own navigation and camera commit. |
| Explicit placement and inventory | `RestorePlacement`, `SetWindowPinnedToScreen`, `TryCarryWindow` and `NavigateInventoryWindow` can change model state, clip, restore or move. Workspace live-window, temporary drawer, inventory, zone and selection operations also call `RestoreNativeState` directly. Defer the semantic action before changing the model and revalidate its window identity on replay. |
| Automatic parking and Peek | `ProcessAutomaticParking` pauses its normal minimize and recovery passes. Explicit `WakeForTransientPreview` and lifecycle `RecoverAutomaticParking` can still restore, clip or move windows. Peek and overview window navigation also call `RestoreForNavigation` directly. Keep the wake, navigation and activation intent together until native writers can resume. |
| Refresh, reset and application exit | `RefreshAllWindows` unclips and redraws visible HWNDs. `Reset` and `RestoreDesktopViewport` clear the worker lane and move or restore windows. Desktop switch, refresh, update restart and session-ending paths need to invalidate the close and choose recovery before any of these operations start. The manager gate handles ordinary continuation ordering. A stalled call and OS session ending still require a separate recovery policy. |

The current reprojection builder mutates regions, runtime visibility and parking before it returns its move batch. The caller must establish the touched cohort, including region-only targets, before running such preparation. The worker rejects a move outside the checked cohort, and the caller owns the rest of the writer and lifecycle fences before enabling asynchronous close.

## Recovery verification

The recovery probe uses an owned child and a WinForms cover. A cover command
abandons the close while the child is blocking native placement. The close
generation is invalidated, the reservation stays held, and a late completion
keeps the cover in place. After the child drains, the fixture can submit a
new close. Production still needs to route the same action back to usable
overview controls and suppress native-mutating commands while the lane is
occupied.

Canvas's exit path on this branch waits for the placement owner to release
the reservation before posting the existing shutdown cleanup. The Windows
subscription now uses committed session ending. In .NET, the
`SessionEnding` event corresponds to the query, which can be cancelled,
while `SessionEnded` is raised for committed ending. A committed Windows
session can end before a deferred callback runs. The app cannot rely on
that callback to persist last-minute changes. [SystemEvents source](https://github.com/dotnet/runtime/blob/main/src/libraries/Microsoft.Win32.SystemEvents/src/Microsoft/Win32/SystemEvents.cs) · [WM_QUERYENDSESSION](https://learn.microsoft.com/en-us/windows/win32/shutdown/wm-queryendsession) · [WM_ENDSESSION](https://learn.microsoft.com/en-us/windows/win32/shutdown/wm-endsession)

The owned fixture sends both session messages to its cover. A cancelled
ending leaves the close alone. Committed ending invalidates the pending
close and registers a manager continuation. The fixture then terminates its
stalled child and checks that the continuation runs once, after the UI has
verified completion and released the lease. It also checks that the
unverified result cannot expose the desktop or start another placement.
The probe does not end the actual Windows session. The remaining native
writer and lifecycle entries in the table are activation requirements.

## Overview close integration

The desktop pan release and stopping-click paths accept a controlled placement
task. The normal launcher supplies no task and follows the synchronous path.
The controlled path keeps Panning and its existing cover while placement is
pending. It suppresses new camera input and visual reconciliation. A verified
placement closes the cover after any held stopping click finishes, with one
Hidden notification and no second synchronous commit.

The recovery action is available from an owned control above the layered
overlay and from the Overview hotkey. It invalidates the pending close and
returns to the interactive Overview after the task drains. Failed verification
also returns to Overview. The control reports the wait while placement remains
reserved. It cannot make native input available if a target never returns.
During a pending handoff, Overview records display changes and rebuilds the
monitor passes after recovery. A deferred exit may close the cover after the
reservation releases even if the completion callback has not reached Overview.

The native Overview suite used a controllable placement task and real
WinForms, WPF and monitor overlays. Tests verified Windows hit testing for the
recovery control, button-up, raw camera input suppression, recovery from failed
verification, the synchronous path when placement is declined, and shutdown
ordering. This fixture does not yet feed a real production placement batch
into Overview.

Production activation still needs a read-only complete cohort plan and
observed window identity/geometry, plus coordination of explicit navigation,
saved-layout and inventory actions, Peek, virtual
desktop transitions and update restart. Input recovery for a target that
never returns remains a separate requirement.
