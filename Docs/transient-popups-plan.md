# Transient pop-ups and durable windows

Design proposal, 28 October 2026. Investigated against `03c1beb` on
`schnepg1/CanvasWindowComposer`. This document proposes behavior and implementation;
it does not change window handling. The reported application and reproduction
steps have not yet been identified, so the specific misclassification is not
claimed as reproduced.

## Recommended behavior

A pop-up needed to complete the current task should appear with the window that
opened it, remain usable immediately, and disappear without a saved placeholder
when the application closes it. Canvas should not send a recognized pop-up to
the temporary area. Canvas should also never close it on a timer: the application
controls its lifetime.

An independently useful pop-up, such as a browser window containing a page, should
offer **Keep this place** in its Canvas window menu. Keeping it saves its current
location without moving it. It then follows the existing durable-window policy:
closing leaves a placeholder and reopening uses the configured application or
resource. Keeping a place does not turn on startup opening.

The product should distinguish these cases:

| Kind | Placement | Saved after closing? | Typical example |
| --- | --- | --- | --- |
| Durable window | Its chosen canvas place | Yes | An organized editor or browser page |
| Ordinary temporary arrival | Existing temporary area | No | An unrelated newly opened app |
| Transient companion | At/over its source window | No, unless explicitly kept and supported | A chooser, sign-in window, or short-lived browser pop-up |
| Application surface | Managed by the application | No separate Canvas item | An in-page modal, tooltip, menu, or dropdown |

The last two are related but need different rendering/input treatment. A modal
drawn inside a web page is already part of the browser window. A separate native
dialog has its own window handle. A dropdown or context menu must not acquire a
saved-window menu merely because Windows calls it a pop-up.

## Findings in the current code

- `WorkspaceService.Capture.cs` gathers observations, runs batch matching against
  deliberate saved items, and passes unmatched windows to `StageTemporary`.
  Some delayed folder/project discovery paths also stage before matching finishes.
- `WorkspaceService.Temporary.cs` creates a temporary item, allocates a slot in
  the temporary area, then calls `RestoreBinding` to move it there. There is no
  transient-companion classification before this move. Temporary items already
  disappear on close and are excluded from workspace persistence and backups.
- `Win32WindowApi.IsManageable` excludes most tool windows and windows for which
  `GetParent` returns a handle. `GetParent` can return an owner for a top-level
  pop-up, so many owned dialogs never enter normal Canvas discovery. Conversely,
  an unowned browser pop-up can enter as an ordinary window and reach the
  temporary-area path. This explains plausible paths, not the exact reported case.
- Discovery is split between `WindowManager` show-event handling and enumeration,
  `WorkspaceService.EnrollUntrackedWindows`, and context-menu recovery. Fixing
  only `StageTemporary` would leave inconsistent behavior across these paths.
- `GetWindowContextItem` can create a temporary item on right-click before the
  next periodic capture. Its current fallback needs a companion-aware branch.
- `OverviewManager.Interaction.cs` ends interaction whenever foreground changes
  to a different HWND. `Win32WindowInteractionApi` resolves child controls of one
  source window, not separate owned top-level dialogs. Pop-up placement alone
  therefore cannot fix zoomed interaction.
- `ForegroundCoordinator` can navigate to focused off-screen tracked windows.
  This needs family-aware handling to avoid camera jumps between a source and
  its companion.
- `CanAutomaticallyMinimize` already protects native owners with visible owned
  windows. That protection needs to extend to inferred companion relationships.
- `WorkspacePersistence.SavedState` currently excludes precisely
  `WorkspacePlacement.Temporary`. Adding another enum value without changing that
  predicate would accidentally save it. A runtime-only role avoids that trap.

Microsoft documents owner lookup separately from parent lookup: use
[`GetWindow(..., GW_OWNER)`](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getparent)
for ownership, and
[`GetAncestor(..., GA_PARENT)`](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getancestor)
for parentage. Native ownership also constrains stacking, minimize behavior and
lifetime, as described in
[Window Features](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features).

## Detection policy

Introduce one read-only evidence adapter and a pure classifier shared by all
discovery paths. Observe possible top-level companions before applying the
independent-window filter; do not broadly admit every currently excluded window.

Evidence should include actual owner, root owner, child status, class, styles,
visibility/cloaking, enabled state, window/process identity, desktop, and the
previously active managed window or overview interaction source. Record whether
that source was already active before the new window appeared. Same-process,
small size, a title containing "dialog", `WS_POPUP`, or a recent foreground change
alone must never establish a source relationship.

Use the following precedence:

1. Exclude Canvas-owned UI, shell/desktop surfaces, child controls, tooltips,
   menus, IME surfaces and inaccessible/protected system UI from independent
   enrollment. Some still need to be recognized as app interaction surfaces.
2. Preserve an existing validated durable binding or an explicit user promotion.
   Do not silently demote saved windows on upgrade. An explicitly requested saved
   resource may bind only with the existing strong target/profile/launch evidence;
   a generic same-executable launch is insufficient.
3. Recognize an owned top-level dialog/companion by a validated owner chain to a
   managed window. A disabled owner is useful modal evidence, not an absolute
   requirement. Allow brokered/cross-process relationships when native ownership
   establishes them. Detect ownership cycles and cap traversal depth.
4. Recognize an unowned browser/app pop-up automatically only through a tested
   application-specific rule with sufficient independent evidence. Preserve its
   current placement while that evidence settles. Do not infer the opener from
   executable/profile equality alone.
5. Otherwise use existing independent-window matching and temporary-area policy.

There is a real detection limit here. The web standard's `popup` feature requests
minimal browser UI; it does not specify a Win32 ownership relationship. Canvas
cannot assume that a web opener is available as `GW_OWNER`. This is an inference
from the scope of the [HTML opening-windows specification](https://html.spec.whatwg.org/multipage/nav-history-apis.html#opening-and-closing-windows),
and must be checked against the actual browser. Do not promise complete browser
coverage from a native-owner fix alone.

For uncertain windows, add **Use as temporary pop-up…** to the session-window
menu, with an explicit source-window choice when no trustworthy source exists.
This also repairs a pop-up already collected into the temporary area. Attach it
near the chosen source, remove its collection membership, and keep it unsaved.
Do not offer this as an implicit demotion of existing saved places in the first
version. Persistent per-app rules can follow after evidence from real cases.

## Runtime model and integration

Keep persistence intent and runtime role separate. Retain the existing
`WorkspacePlacement.Deliberate`/`Temporary` schema. Add a session-only
`TransientWindowRegistry` and a `WindowRoleClassifier` rather than representing
companions as another persisted placement value.

A registry entry needs a window lifetime token, validated immediate and root
source tokens, desktop, classification reason/confidence, current/last geometry,
attachment offset, visibility state and promotion capability. Use a general
window lifetime token following the existing parking token pattern, rather than
coupling this feature to automatic parking. HWND/PID/process-start identity alone
cannot distinguish reuse of a handle inside the same process after destruction.

Companions may have runtime geometry for projection and overview hit testing, but
must not enter `WorkspaceDocument.Items`, automatic workspace matching, launch
queues, upgrade-adoption checkpoints, placeholder factories, temporary-area
membership/counts, or durable project membership. Present them as part of their
source family in navigation; avoid counting the same family as multiple durable
places. Native handles and source relationships remain session-only.

All discovery paths should consume the same decision before registration,
matching, menu recovery or relocation. Observe a whole discovery batch before
resolving owner relationships so enumeration order cannot change the result.
Revalidate tokens immediately before native operations or promotion.

Use show/foreground events for prompt observation, with enumeration as repair
for missed events and windows already open when Canvas starts. Handle hide and
destroy separately: an app may reuse a hidden dialog, and owner minimization can
hide it. The current stale-removal path treats invisibility as removal; it must
not erase a still-live companion's session identity. Reset tokens on destruction
or replacement. Missed destruction plus same-process handle reuse remains a
native-observation limitation; uncertain relationships must be revalidated rather
than authorizing stale actions.

Keep WinEvent callbacks lightweight and marshal work to the owning UI thread.
Coalesce repeated notifications per lifetime. If classification is pending,
leave the native window visible in place and defer destructive placement actions.
Use an injected-clock settling deadline, initially at most 500 ms, for unstable
metadata; confident owner relationships need no delay. A deadline is a discovery
decision, never permission to close or promote a pop-up. Do not rely solely on
dialog-start/end events: Microsoft says those events are not consistently sent
in its [event reference](https://learn.microsoft.com/en-us/windows/win32/winauto/event-constants).

## Placement, focus and lifetime

- Preserve the application's requested position when usable. Permit overlap with
  the source, its project area and other companions. Do not apply the temporary
  area's collision avoidance or displace existing saved windows.
- If the pop-up is inaccessible, make the smallest correction that exposes its
  controls near the source on an available work area. Respect negative monitor
  coordinates, taskbars, mixed DPI and dialogs larger than the source window.
- Follow an unpinned source when Canvas moves/projects it, using one camera
  snapshot and a defined source-before-companion update. Measure the result and
  avoid double movement if the application also repositions its dialog. A user's
  move of the pop-up updates the attachment offset without automatically saving
  it. Pinned sources use screen geometry; maximized sources use their actual
  displayed bounds. Avoid mixing overview and main-camera coordinate systems.
- Preserve native ownership and z-order. For inferred relationships, raise the
  companion with its family only when that family is active; do not make all
  pop-ups globally topmost. Never use `SetParent` or rewrite ownership to fake
  independence.
- Do not take focus merely because an app creates a background pop-up. Preserve
  normal app-driven activation and do not recenter the camera on incidental
  companion focus. Explicit taskbar/Alt+Tab navigation should reach the visible
  enabled member of the selected family, including a blocking dialog.
- While a visible modal/active companion is present, prevent automatic parking
  from minimizing its source or itself. Resume normal resource policy afterward.
  Respect user minimization and virtual-desktop visibility; do not reveal windows
  from another desktop.
- Closing a companion removes only its runtime state. Hiding it retains a bounded
  session record while the native window exists; neither action creates a card.
  Canvas does not auto-close authentication, payment or save prompts.
- If an inferred source disappears but the pop-up survives, retain it at its
  current location as an unsaved orphan. Offer **Keep this place** when supported.
  Do not suddenly collect it or silently promote it.
- On graceful Canvas exit, restore any native geometry/regions Canvas changed
  using fresh identity checks and family-aware desktop recovery. Do not close
  the application's dialogs. This must integrate with existing recovery ordering.

## Right-click promotion

Use **Keep this place**, consistent with existing temporary-window menus, with
status text such as "Temporary pop-up · closes without a saved place" where a
Canvas header/menu can show it. In Arrange mode right-click uses the Canvas
window menu. In Interact mode right-click inside content continues to go to the
app; the Canvas drag header exposes the management menu. Supply a **Current
pop-up** action in the tray for a native window without a usable Canvas header.
Do not globally capture right-clicks inside browser content.

The command addresses a lifetime token, not a stale HWND or a freshly fabricated
temporary workspace item. On invocation:

1. Validate the current window, desktop and capability. Capture current geometry
   in the correct coordinate system. Recheck after any asynchronous prompt.
2. Create exactly one deliberate item using shared `WorkspaceWindowMetadata`.
   Preserve browser/profile/resource identity, and do not guess an existing slot.
   Offer explicit linking when the user wants an existing saved place.
3. Persist successfully before reporting the item as kept. On save failure,
   retain transient behavior and location, roll back provisional bindings/items,
   and show the existing save error. Do not simply call the current `KeepPlace`
   and assume persistence succeeded.
4. Publish the durable binding and end Canvas-relative attachment. Do not move,
   resize, activate or reload the window as a side effect. Native ownership
   restrictions still apply for as long as the application retains ownership.

Two promotion capabilities are necessary. An ordinary independent top-level
window can keep its place using existing reopening behavior. An owned browser
pop-up can save a reusable page resource when a valid URL/profile is explicitly
available; it may still close with its native owner, leaving the saved resource
as a placeholder. Saving does not sever that ownership. Use the existing web
resource editor when a URL is needed. For an unowned browser window, keeping an
application place alone must not promise to reopen the same page: that also
requires a configured URL. Never infer URLs from captions or log authentication
tokens. A file picker, modal confirmation, menu, or other dialog
without an independent reopening target cannot be made durable reliably; disable
the action with a short explanation. Keep an actionable route to the source.

Pinning or assigning a companion to a durable project area must use the same
capability/transaction, or stay unavailable for dialogs. Mere focus, elapsed
time, typing, resizing, or moving outside the source bounds is not promotion.

## Zoomed overview interaction

Treat the source and its companions as an interaction family. A foreground
transition into a validated companion should release any forwarded button state
and switch the target, rather than terminating the entire workflow. A modal
companion receives input ahead of its disabled source. Closing it can return
interaction to the last valid enabled family member only while that family still
owns foreground; it must not steal focus back from another application.

Render separate supported top-level companions above the source thumbnail, with
their own geometry, hit testing and input target. Child controls remain within
their existing source. DWM requires top-level source windows for
[thumbnail registration](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/nf-dwmapi-dwmregisterthumbnail),
and the current input bridge cannot assume every native menu or chooser supports
forwarded messages.

Implement a reliable native handoff before attempting universal scaled support.
For an unsupported dialog triggered during user interaction, suspend forwarding,
expose the source family at desktop size, then focus its enabled dialog. Preserve
the prior overview camera for return. Never foreground the disabled source over
its modal dialog. Only a validated family reached through user interaction or an
explicit navigation action may cause this handoff; a background notification
must not move the view. Native menus require
verification that handoff itself does not dismiss them. Do not advertise scaled
menu compatibility until tested.

Define Escape ownership explicitly: a dialog receiving native input gets its
normal cancel behavior; Canvas must not send the same Escape twice or consume it
first. Canvas navigation hotkeys and exits must release capture and leave an
open dialog reachable. Pending classification should suspend forwarding safely
without losing the originating source, so event order cannot leak clicks into a
disabled parent.

## Implementation sequence

1. **Capture the reported case.** Add opt-in, bounded diagnostic records for
   show/foreground/classification/placement, including class/style, owner chain,
   tokens, desktop and rejection reason. Avoid page content, captions and URLs by
   default. Reproduce in native and overview modes and turn the observed evidence
   into a regression fixture. This determines whether owner handling alone covers
   it or a browser-specific rule is needed.
2. **Classify and retain companions.** Add the evidence adapter, pure classifier
   and runtime registry; wire every discovery path and protect matching,
   persistence, adoption, parking and recovery. Deliver native parent-relative
   placement with no placeholder or temporary-area notice.
3. **Add management actions.** Implement token-based menus, transactional
   **Keep this place**, capability feedback, explicit attachment for uncertain
   arrivals, and reusable web-resource handling. Keep existing saved places intact.
4. **Complete overview handling.** Add family focus/hit testing and native handoff
   as the initial compatibility path. Extend scaled input to specific dialog types
   only after proving geometry, focus, capture and cancellation behavior.

Likely change boundaries: `IWindowApi`/Windows evidence adapter and fake API;
`WindowManager` discovery/projection/recovery; `WorkspaceService.Capture`,
`Temporary`, `ContextMenu`, `Commands`, `Cards` and upgrade adoption;
`WorkspacePersistence`; overview window list/menu/interaction/navigation;
`ForegroundCoordinator`; input event contracts/router and production composition.
Keep classification independent of the UI and preserve whole-batch matching.

## Validation and acceptance

Use existing fake-window/clock harnesses for classification, state transitions,
batch order, pending discovery, save failures and lifecycle races. Add native
coverage for real ownership, stacking, activation, visibility and input. Run all
tests through `Tools/Run-Tests.ps1` to serialize desktop access across worktrees;
do not close a running user Canvas merely to validate this design document.

| Scenario | Required outcome |
| --- | --- |
| Owned modal and modeless dialog; nested dialog | Usable with source, correct stacking/input, no collection or saved card |
| Ownerless browser pop-up; ordinary new browser window; detached tab | Tested evidence distinguishes supported pop-ups; unrelated windows retain collection behavior |
| In-page modal, dropdown, tooltip, IME and context menu | No independent durable item; usable app/native fallback |
| Pop-up appears before/after owner enrollment or closes during settling | Same classification regardless of event/enumeration order; no stale moves |
| Right-click before periodic capture; pop-up closes while menu open | Correct menu without fabricating a saved item; stale action safely rejected |
| Successful promotion, repeated click, failed save, explicit linking | Exactly one durable item on success, original geometry, no false success or duplicate slot |
| Close/hide/re-show, nested owner close, surviving orphan | No accidental placeholder/promotion/collection; native lifetime respected |
| HWND reused in same/different process | No inherited attachment, promotion capability or queued native action |
| Startup with a dialog already open; legacy saved dialog | New companions stay unsaved; existing deliberate records are not silently deleted |
| Parent pinned/maximized/minimized/parked; moving a whole project area | Correct family geometry; no surprise restore, park or durable promotion |
| Desktop switch, display disconnect, mixed DPI and negative origins | No cross-desktop leak or inaccessible interactive dialog |
| Overview at several zooms; mouse held while opening/closing dialog | Correct target/capture release; no stuck buttons or invisible modal blocker |
| Escape, Alt+Q, Alt+Tab/taskbar selection, switching to unrelated app | Clear input ownership, reachable dialog, no unwanted camera/focus steal |
| Graceful Canvas shutdown while dialog open | Native windows remain open, accessible and restored; transient state never saved |

Manually verify browser sign-in (`window.open` with and without opener), file
upload/download pickers, print dialogs, permission prompts, payment flows and
detached tabs in the actual affected browser; repeat key cases in Chrome, Edge
and Firefox. Avoid real transactions or credentials in the test fixture. Add an
elevated-app case and require a usable native path when forwarding is rejected.

The first release should be considered complete only when the reported pop-up
stays immediately usable with its source, closing leaves no placeholder, an
eligible pop-up can be kept in place through its menu and survives as a saved
resource, ordinary new windows still use the temporary area, and overview users
cannot be stranded behind an invisible modal dialog. Ownerless-browser coverage
must be stated per tested rule rather than assumed universal.
