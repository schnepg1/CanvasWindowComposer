# Pan momentum delivery record

Repository: `schnepg1/CanvasWindowComposer`. Base: `7121f6f`.
Task branch: `agent/pan-momentum`. Pull request: #28.

## Behavior and acceptance

- Desktop and canvas dragging can coast after release. Both momentum settings
  default on, persist independently, and apply immediately without changing
  pointer-following or Low quality mode.
- Canvas coasting keeps the current zoom and stays in canvas view.
- Desktop pan moves the visual camera while native window projection and automatic
  parking are suspended. The final native placement commits before the overlay
  hides. Visibility and previews follow the coalesced visual camera.
- Desktop overlays own pointer input during panning. A complete left/right stopping
  click (including release) is consumed. The next click interacts with native
  windows. No input is forwarded to a stale native position or synthetically replayed.
- New gestures, wheel/key navigation, tools/editors/settings, mode changes,
  capture loss, and disposal stop the old momentum. Closing while middle remains
  held discards that drag until release; a fresh drag works immediately.
- Velocity uses motion/release event times before UI coalescing. The recent window
  includes proportional sample intervals so raw and pointer cadence agree. Exact
  exponential integration retains subpixels, accounts for stalled frames, and
  applies the final displacement before stopping.

## Validation record

- Source investigation identified transient native projection every ~200 ms,
  additional visibility scans before the projection gate, UI-drain velocity dates,
  whole-pixel tick rounding, and discarded queued tail movement.
- Independent agent review found and fixed post-stop drag suppression, release
  velocity cadence bias, and held-middle input leakage, with regression coverage.
- Deterministic tests cover delayed input, same-time samples, stationary release,
  frame-rate/stall invariant endpoints, final fractional movement, and restart.
- Native integration tests cover desktop final commit, dense layouts, stopping
  click ownership, canvas zoom/mode retention, independent settings, cancellation,
  and placeholders. Settings tests cover legacy defaults and Save/Cancel.
- The performance harness now has desktop/canvas held-drag → release → coast
  phases, using production timers. Independent disable switches allow paired runs.
- This Linux workspace has no .NET SDK or Windows desktop. Local checks are source
  review and `git diff --check`; build/test/native/presentation checks run in Windows CI.
- Windows CI and final review results: pending. Update before merge.

## Measurement limits

No Surface Pro 4 frame-rate, GPU-time, or real-application compatibility result is
claimed. CI's owned windows validate behavior; the harness does not reproduce the
full saved-workspace/placeholder scene. Compare the same machine, power mode,
window layout and quality/backend before/after, including dense real apps and
placeholder-only workspaces. Check actual click/activation, drag/release/coast,
rapid interruption, pinned windows, multiple monitors/DPI, and final handoff.

## Delivery mechanics

Command-line push has no GitHub credentials in this workspace. The connected
GitHub Git-data API publishes the same local tree to the existing public repository;
its commit SHA differs from local history. Verify the published tree and exact PR
head, and wait for both pull-request and post-merge checks. No releases/deployment.
