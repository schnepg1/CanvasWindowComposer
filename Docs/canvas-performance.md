# Canvas performance work log

This is the reference for the performance work begun on 19 October 2026 after
testing on a Surface Pro 4 Core m3. Update this file with each performance change.
The initial source analysis used main commit `577030b`; hardware measurements on
that Surface have not yet been collected.

## Policy

- Reductions in optional visual quality belong to **Low quality mode** in Settings.
  The mode is opt-in, reversible and persists in `config.ini` as `LowQualityMode`.
- Architectural improvements apply to both modes without extra switches.
- Distinguish source-level reductions in work from measured performance gains.
  No GPU saving percentage or frame-rate improvement is established yet.

## Roadmap and status

| Priority | Topic | Status in this first change set |
| --- | --- | --- |
| 1 | Reduce expensive background rendering | Implemented: simple, static-on-idle low quality grid; normal visuals retained |
| 2 | Coordinate camera updates | First pass implemented: one pending UI camera frame across monitor passes; a unified presentation boundary remains future work |
| 3 | Frozen previews or outlines during gestures | Not started; live previews remain available in both modes |
| 4 | Remove repeated process/identity work during camera motion | Partial: removed the duplicate identity-map refresh within the same reconciliation; event-maintained identity mappings remain to be designed |
| 5 | Update only changed preview destinations | Implemented: unchanged integer destinations skip native updates, including stationary neighbours during a drag |
| 6 | Reduce thumbnail registration churn, card allocations and bitmap conversions | Not started |
| 7 | Compare presentation backends; prototype a unified scene if needed | Not started; existing experimental flip option remains separate |

## Implemented on 19 October 2026

### Optional visual quality

Settings → Rendering → **Low quality mode** adds a simple adaptive grid and origin
axes. A uniform shader branch skips the procedural nebula, decorative parallax
marks and other normal-quality background effects. The normal-quality shader path
is retained. Preview contents, cards, titles, annotations, canvas geometry and input
remain available.

Saving applies the mode to existing overview renderers immediately. Hidden overview
passes pick it up when reopened. It does not require a restart or a rendering-backend
change; cancellation of Settings does not apply an edit. Existing configurations
default to normal quality.

The continuous-frame policy uses the same low quality flag as the shader. At any
zoom, a stationary low quality background sleeps after its invalidated frame.
Camera changes, paints, resizing and mode changes still request frames. Inertia
continues requesting ticks even in low quality mode. Live application previews,
card transitions and other active content may still consume resources; this is not
a promise that all GPU activity stops while the camera is idle.

### Unconditional architecture changes

- Raw desktop-pan camera synchronization, overview pan/wheel input and overview
  window drags request one coalesced visual frame. Camera/model mathematics still
  consume every delta immediately, so small trackpad deltas and cursor-centred zoom
  are preserved. Grid camera/parallax state and DWM/WPF reconciliation are submitted
  in one scheduled UI callback using the latest camera state.
- The shared UI timer has a nominal 16 ms interval. It stops between requests; it
  is neither an always-running loop nor a claim of vsync or 60 FPS. The existing
  Direct3D frame pump separately coalesces GPU invalidations.
- Pointer-down, pointer-up and double-click flush pending navigation before hit
  testing or completing a gesture. Closing/disposal cancels pending frames, so an
  old callback cannot render into a later session. Invalidations raised during a
  scheduled draw are retained for a following frame.
- Each active DWM preview remembers its last successfully submitted destination.
  A drag still refreshes geometry/culling, but unchanged neighbours no longer
  receive `DwmUpdateThumbnailProperties`. Camera motion which projects to the same
  integer rectangle is also skipped. Failed updates remain retryable, and fresh
  thumbnail registrations still receive their initial properties.
- `RefreshZoneAnnotations` reuses the identity mappings prepared by its caller
  instead of rebuilding them a second time in the same reconciliation. This does
  **not** yet remove process inspection from the entire camera path.

The background, DWM previews and WPF annotations still have separate presentation
systems. Batching submissions reduces redundant work; it does not guarantee that
all three systems display an atomic frame or eliminate the observed stagger.

## Verification

Added regression coverage for:

- Legacy/default config, quality persistence, disabling it again, Settings save
  and cancel, and live propagation to existing/reopened overview passes.
- Low quality idling at zoom 0.05, 0.25 and 0.69 while preserving invalidations,
  normal-quality animation and inertia ticks.
- Input-burst coalescing, final-frame flush, stale-callback cancellation and
  invalidations during drawing without reentrant drawing.
- A two-window drag issuing only one thumbnail-destination update, plus skipped
  updates for unchanged pixel destinations. Existing readiness-retry tests remain.
- Both shader quality paths in the native grid smoke test, with both presentation
  backends.

`Tools/PerformanceHarness` accepts `--low-quality` and reports `LowQualityMode`.
It does not modify the installed user's settings. Its existing scenarios use the
default overview zoom and omit the workspace-card layer, so they do not establish
the zoomed-out nebula saving or full-app gesture latency.

Local validation: `git diff --check`. This editing environment is Linux without
the .NET SDK/Windows Desktop runtime. Windows build and test results are recorded
below once the pull-request workflow finishes; no local Windows test pass is claimed.

## Surface / Windows follow-up

Use the same power mode, applications/documents, window count, DPI, resolution,
monitor arrangement and background activity for paired runs. Compare normal and
low quality at zoom 1.0, 0.7, 0.5 and 0.25. Include stationary overview, sustained
pan, zoom bursts, a single-window drag, overview close/reopen and rapid quality
toggling. Include annotations, saved cards and multiple overlapping windows.

Record Canvas, DWM and target-application CPU and per-engine GPU counters separately;
also record frame-time distributions, input-to-visible latency, restoration latency
and the end-of-pan hitch. Existing `GridFrameIncludingPresent` timings include
submission and vsync waiting and are not GPU execution times. Do not sum GPU engine
percentages into a total. Use ETW/WPR/GPUView or PresentMon for presentation and GPU
attribution where appropriate.

Acceptance checks: geometry and final placement remain correct, cards/titles track
the same latest camera, input remains usable, low quality has no continuous
background animation at rest, normal quality is restored when switched back, and
idle scheduling does not suppress inertia or lose the final interaction frame.

See also [the existing visibility/performance work](upgrade_window_visibility_performance.md)
and [the benchmark instructions](../Tools/PerformanceHarness/README.md).
