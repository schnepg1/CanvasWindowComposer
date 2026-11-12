# Desktop handoff native writers

The normal close still performs synchronous placement. A future asynchronous close must keep the UI message pump available while the projection worker waits inside USER32. A synchronous region or position write against another HWND can also block the UI if that window shares an owner thread or input queue with a batch target. [SetWindowRgn](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowrgn) · [Win32 message queues](https://learn.microsoft.com/en-us/windows/win32/winmsg/about-messages-and-message-queues)

`WindowManager` pauses preview-region reconciliation from batch submission until the UI owner releases the lease. `SetLivePreviewWindows`, `UnclipAll` and `ReclipAll` can still update desired preview state during the wait. On release the manager reconciles once against current windows and preview state. Observed destruction removes the window from the reconciliation pass. Pre-submission preparation still runs on the UI thread.

`TryDeferShutdownForDesktopPlacement` covers the entire reservation, including preparation. It retains the first shutdown continuation and prevents a new lease after a deferred shutdown request. On release it clears the reservation and gives the continuation priority over preview-region replay. The shutdown coordinator should post the continuation to the UI dispatcher and abandon an unsubmitted lease when preparation returns and `DesktopPlacementShutdownRequested` is true.

While a reservation is active, the manager coalesces implicit camera and display projection and skips automatic parking passes. Release leaves those passes for the next periodic tick, giving the UI owner time to handle the placement result and any lifecycle transition. A display request consumes a pending camera request and uses the current monitor layout and camera. A camera-only replay respects projection suspension and the normal throttle. Parking recalculates its decisions from the current window identity, proximity, settings and recovery records. Periodic workspace maintenance retains its existing deadline and resumes matching and restoring saved slots after release. Shutdown takes priority over all of this work.

| Entry point | Native writes and pending work |
| --- | --- |
| Camera projection and display change | The camera callback and manager display reprojection are deferred. The overview display handler also tears down and rebuilds passes. It still needs to coordinate the entire transition. Explicit `Reproject`, `ReprojectSync`, and `Canvas.Commit` need to stay with their initiating action until placement can run. |
| Discovery and WinEvent callbacks | `RegisterWindow` updates the canvas without restoring a saved slot. Periodic workspace maintenance can restore the slot and now waits for the lease. Window restoration and model state changes still call `ReprojectWindow`. Focus can reclip a carried window or restore or center a parked window. Those event-driven native writes still need coordination. |
| Explicit placement and inventory | `RestorePlacement`, `SetWindowPinnedToScreen`, `TryCarryWindow` and `NavigateInventoryWindow` can change model state, clip, restore or move. Workspace live-window, temporary drawer, inventory, zone and selection operations also call `RestoreNativeState` directly. Defer the semantic action before changing the model and revalidate its window identity on replay. |
| Automatic parking and Peek | `ProcessAutomaticParking` pauses its normal minimize and recovery passes. Explicit `WakeForTransientPreview`, lifecycle `RecoverAutomaticParking`, and restore events can still restore, clip or move windows. Peek and overview window navigation also call `RestoreForNavigation` directly. Keep the wake, navigation and activation intent together until native writers can resume. |
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
