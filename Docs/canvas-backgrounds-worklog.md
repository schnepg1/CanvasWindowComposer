# Canvas backgrounds delivery

Recovered on 2026-11-04 from the original 11-commit implementation at `c0de89f`.
The recovery bundle's SHA-256 was verified before import. Implementation was
reconciled in an isolated worktree based on maintained fork main `a5dd1c5`.
Unrelated local zoom and active-window-card commits were preserved separately.

## Delivered scope

Reusable managed or externally linked images; four offline starter scenes;
multiple large canvas-coordinate placements; free or project-bound geometry;
locked default and deliberate frame editing; Fill/Fit/Tile, crop, opacity, dimming,
stacking order, replacement, detach, deletion, and desktop-scoped undo/redo.
Project geometry includes live overview previews and lossless detachment when
an area is removed. Desktop and overview share a bounded asynchronous cache
and painter. Desktop surfaces are passive children of Explorer's wallpaper host.

## Recovery fixes and review

- Removed URI-cache invalidation from stream decoding, which failed in actual
  Windows WPF with a null cache key despite the earlier cross-build succeeding.
- Added a compact cache tier so pressure converges while retaining visible imagery.
- Marshalled display-change cleanup to the UI context.
- Kept global canvas shortcuts out of the picker and preserved drafts on Escape.
- Released native capture when cancelling edits through Escape or undo.
- Protected unfinished background drafts and failed saves during update preparation.
- Independent review identified stale project geometry in background history,
  modal-picker desktop changes targeting the wrong placement, and capture left
  active by undo. Each was corrected; regression checks cover history and capture.

## Verification

Release solution build passed with zero warnings or errors. Focused background
model, persistence, asset, cache, editor, rendering and native interaction checks
are run through the serialized test runner. The initial full run passed 1,439
non-native checks and 228 of 229 native checks. The existing cursor-input filter
check also fails on a separately built, unchanged `a5dd1c5` baseline. The final
focused run passed all 61 background checks, including the review regressions.

A separate Windows validation app attached a visible passive surface to the real
Explorer `WorkerW` host. Native checks confirmed foreground focus preservation,
hit-test pass-through, per-monitor-aware child/parent DPI, and HWND removal on
shutdown. The live picker displayed all four images, created a second placement,
and exposed the geometry, crop, appearance, ordering and lock controls. Independent
fix review found no remaining actionable findings. Remote CI and integration
evidence are recorded in the delivery chat and local handoff.

## Limits

Overview clips backgrounds around native thumbnail rectangles, so transparent
app borders cannot reveal the images inside that rectangle. Explorer attachment
depends on the shell wallpaper-host convention and falls back to overview if
unavailable. No portable workspace export exists. Mixed-DPI monitor changes,
Explorer restart and older integrated-GPU performance require further hardware
coverage; they are not established by offscreen rendering tests.
