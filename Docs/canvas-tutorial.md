# On-canvas tutorial and guided tour

The first startup creates a small neighbourhood of text notes and arrows near a
visible managed window. With no visible windows, a sample canvas card provides the
landmark; the tutorial never launches applications. Placement avoids windows,
personal annotations and project areas without moving them.

The cinematic intro lands at readable camera controls. Choose **Start tour** for
guidance, or **Explore myself** to keep exploring freely. Dismissing or completing
the tour suppresses future automatic invitations on that desktop. Disabling the
startup intro also disables its automatic landing; the tour remains available
from **Tray → Tutorial → Start / resume guided tour**. **Restart guided tour** begins
at the first stop with fresh practice checks. Returning to the notes remains
available independently of the guided tour.

## Guided stops

1. Pan: middle-drag or trackpad two-finger scroll; 64 DIPs of movement checks the exercise.
2. Zoom: pinch, Ctrl/Alt+scroll or +/−; an accumulated 12% logarithmic zoom change checks it.
3. Windows: drag a window or double-click a window/card. Returning to a real window pauses the tour.
4. Notes: create and save a new Text note. Editing an existing note, undo or redo does not count.
5. Make it your workspace: a short overview of project areas, the project navigator and returning to the desktop.

Each stop has **Back**, **Next** (or **I understand — continue**), **Recenter** and
**Skip tour**. Practice never advances the camera automatically, and no exercise
is mandatory. **Finish** leaves the notes available. Instructions stay on the
canvas; progress and navigation buttons stay readable in a screen-space panel.
Standard buttons support Tab, Enter and access keys. The panel hides while drawing
or editing text, and returns afterward.

Transitions take 0.7 seconds. Navigation input stops travel without dismissing
the tour; a keyboard press stopping travel is consumed. Reduced-motion settings
and the Windows animation preference use immediate stationary transitions.
Animation is never counted as practice. Escape first stops travel, then pauses
the tour (after cancelling active drawing/text editing), and a later Escape
returns to the desktop. Closing overview, switching virtual desktops, changing
displays or shutting down pauses the tour and releases its timer/UI resources.
Reopening overview offers Resume; there is no automatic camera flight on resume
until the user chooses it. Skip leaves the notes available for free exploration.

Guided travel and practice pans use only the overview camera, preserve the desktop
camera on close, and do not drive automatic window parking. Moving an actual window
or creating a personal note is a real user edit. Held global pan gestures started
in the tour are drained through release if the tour closes, so they cannot leak
into the desktop camera. Ordinary free exploration keeps the existing camera behaviour.

## Saved data

Tutorial artwork lives in `tutorial.json`, separate from personal drawings and their
undo/redo. Layouts, visibility and tour progress are desktop-local. The startup reveal
flag remains application-wide. Layout identities and origin survive restarts and
DPI changes. Notes are read-only examples; personal practice notes use the ordinary
annotation document and save automatically.

Format version 2 adds the step, checked exercises and paused/completed/dismissed status.
Version 1 files migrate without losing artwork, IDs or visibility. Atomic saves and
backup recovery preserve damaged data and repair the primary. Future versions never
fall back to an older backup. Save errors are shown in the tour panel.

## Verification

Tests cover placement, DPI/text fit/rendering, persistence/recovery/version migration,
tour state transitions, acknowledgement without exercises, camera interruption,
practice versus animation, pause/resume across desktop switches, global pan ownership,
real note creation, and native overview integration. Windows CI builds, tests and
publishes. A manual Windows walkthrough should check mouse/trackpad gestures, keyboard
focus, readable panel placement at 100%/200% DPI, restart/resume, text editing, reduced
motion, and leaving the tour while a pan gesture is held.
