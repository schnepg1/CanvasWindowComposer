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

| Priority | Topic | Current status |
| --- | --- | --- |
| 1 | Reduce expensive background rendering | Implemented: simple, static-on-idle low quality grid; normal visuals retained |
| 2 | Coordinate camera updates | First pass implemented: one pending UI camera frame across monitor passes; a unified presentation boundary remains future work |
| 3 | Frozen previews or outlines during gestures | Implemented in the second pass: existing snapshots or title cards during low-quality pan/zoom/window drags, with live restoration after settling |
| 4 | Remove repeated process/identity work during camera motion | Implemented in the third pass: shared cached workspace maps, indexed bindings, and no full parked-process scan on steady camera frames |
| 5 | Update only changed preview destinations | Implemented: unchanged integer destinations skip native updates, including stationary neighbours during a drag |
| 6 | Reduce thumbnail registration churn, card allocations and bitmap conversions | Implemented in the fourth pass: hidden-registration reuse, failure-tolerant stacking, shared overlap data and direct snapshot pixels |
| 7 | Compare presentation backends; prototype a unified scene if needed | Not started; existing experimental flip option remains separate |

## Implemented on 19 October 2026

### Optional visual quality

Settings → Rendering → **Low quality mode** adds a simple adaptive grid and origin
axes. A uniform shader branch skips the procedural nebula, decorative parallax
marks and other normal-quality background effects. The normal-quality shader path
is retained. Titles, annotations, canvas geometry and input remain available;
the second pass below adds lightweight previews during low-quality gestures.

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
  instead of rebuilding them a second time in the same reconciliation. The third
  pass below also removes repeated process inspection from steady camera frames.

The background, DWM previews and WPF annotations still have separate presentation
systems. Batching submissions reduces redundant work; it does not guarantee that
all three systems display an atomic frame or eliminate the observed stagger.

## Second pass: lightweight gesture previews, 19 October 2026

**Low quality mode** now also changes the moving window representations:

- Overview pan, wheel/pinch/keyboard zoom and single-window drags use an existing
  cached application snapshot or cached placeholder image when available. Without
  an image, a simple rectangle and readable title preserve position and identity.
  The card continues to move and scale with the latest canvas camera.
- Gesture rendering does not call `PrintWindow`, request capture, generate a new
  placeholder bitmap, or minimize a window. Previously cached images may reflect
  an earlier application state; this is not a fresh screenshot at gesture start.
- Uncached movable DWM sources are hidden while cards represent them. The fourth
  pass below retains already registered sources only while their geometry stays
  in view. Cached sources remain static, and pinned/screen-fixed windows remain live. Visible
  source preparation therefore no longer unclips uncached off-screen windows
  merely to draw their live contents during a low-quality gesture.
- Desktop panning, including inertia, stays lightweight for that Panning session.
  An overview pointer drag stays lightweight while held, even if the pointer
  pauses. Other overview motion restores live previews after **180 ms** without
  further motion. No permanent background timer is added for stationary content.
- Source registration is reconciled before the gesture cards are removed. A
  failed live source keeps a title-card fallback and retries at 250 ms intervals
  for up to one second. Persistent failure retains the card and stops polling;
  later input/window updates retry normally. Native API acceptance still does
  not prove that the target application has painted its first correct frame.
- Turning Low quality mode off restores normal rendering immediately. Closing,
  disposal and reopening discard old gesture state and timers. Startup camera
  flights do not activate the gesture policy.

Normal quality keeps live previews throughout gestures. The timing/source
selection/lifecycle implementation is shared; this optional visual reduction has
no additional setting beyond the existing Low quality mode.

Existing automatic off-screen minimization is independent. Enabling that resource
policy can still minimize eligible distant applications after its own delay. The
gesture controller neither changes that policy nor promises to stop applications'
own background rendering. The fourth pass below avoids re-registration across
unchanged gesture source lifetimes; actual source replacements still re-register.

## Third pass: identity work outside steady camera frames, 19 October 2026

These are architectural changes and apply in both quality modes without a setting:

- Workspace rendering shares one immutable snapshot containing live-window item
  IDs, live-to-placeholder mappings and placeholder-to-source mappings. Overview
  camera frames and card-animation ticks reuse the same dictionaries instead of
  building two dictionaries, opening processes twice and copying the reverse map.
- Snapshot rebuilds validate a binding once for all three maps. Workspace captures,
  card creation/removal, native minimize/restore events, binding changes and virtual
  desktop switches invalidate it. Only the active desktop's items are published.
  Native lifecycle changes during a rebuild discard that incomplete publication.
- Steady render reads check the native PID and visibility without opening the
  process or reading its executable/start time. Changed PIDs, hidden windows and
  destroyed handles retire a stale association even if a native event was missed.
  Same-PID process reuse can leave cosmetic metadata until an event or the regular
  two-second workspace capture. Render snapshots never authorize native commands:
  focus, close, linking, placement and parking/restore retain fresh identity checks.
- Saved-item binding lookup uses an index instead of scanning every live window
  for every saved item. Bind, unbind, replacement, removal and clear maintain that
  index and a revision used by the snapshot cache. Removed entries retain their
  former item ID long enough for the existing final-geometry save.
- Camera-only automatic-parking passes update proximity and check native PID/style.
  They no longer reopen every parked process or run `CanAutomaticallyMinimize`
  (which enumerates owned windows) for every frame. Periodic reconciliation still
  detects new dialog/ineligibility conditions. An actual restore, manual-restore
  handling or final transition to native positioning validates identity first.

Regression tests exercise 120 camera frames in each quality mode with shared map
instances and no additional process inspections; another 120-frame parked-window
burst checks both process and parking-eligibility query counts. Safety coverage
includes PID replacement, same-PID process reuse before restoration/actions,
missed hide/destroy events, card replacement, desktop switches, periodic refresh
and index consistency after an old binding is removed.

This removes CPU/native-query work and allocation pressure; Surface GPU/frame-time
savings are still unmeasured. Initial overview entry, zoom crossings that change
card mode, lifecycle changes, native actions and periodic captures can still inspect
processes. Runtime eligibility still makes native/virtual-desktop queries per
visibility pass. This is not a zero-native-call render loop. The fourth pass below
addresses registration churn, annotation allocations and image conversion.

## Fourth pass: reusable thumbnails and cheaper card frames, 19 October 2026

Priority 5 was completed in the first pass: unchanged integer destinations skip
native thumbnail-property updates, including stationary neighbours during a drag.
This fourth pass implements priority 6. These resource/architecture fixes apply in
both quality modes; the existing Low quality mode alone controls gesture visuals.

### Thumbnail lifetimes and drawing order

- Low-quality gestures hide an existing uncached thumbnail with `fVisible=false`
  instead of destroying its registration. While hidden it receives no geometry
  updates and is excluded from live-source preparation and readiness checks.
  Restoration submits current geometry and visibility together. Repeated gestures
  can reuse the same registration when the window/source/order stays compatible.
- Retention is limited to already registered sources whose geometry remains in the
  current monitor view. New uncached gesture sources are not registered; leaving
  the view, removal/replacement, a failed hide, overview close and disposal release
  registrations. A failed hide falls back to unregistering, so lightweight mode
  cannot silently leave that native preview visible.
- Target source handles are resolved once per window per reconciliation and reused
  across monitor passes. Missing cached surfaces remain unavailable; they never
  fall back to the original app.
- Reconciliation waits for a required new registration to succeed before replacing
  the higher registration suffix. A failing or unavailable lower source therefore
  leaves healthy higher windows registered. When it becomes available, the suffix
  is rebuilt once in the correct DWM order. Necessary reorder/source-change
  registrations are retained; DWM does not expose arbitrary thumbnail insertion.
- Bringing an already topmost source forward is a no-op. Taskbars are cycled only
  on monitor passes that appended a source, and registration initializes their
  current visibility instead of repushing visibility to all unchanged passes.

### Card data and pixels

- Each annotation frame uses one detached world-rectangle array. Cards borrow
  read-only prefix views for the higher windows that occlude them. The copied world
  data grows linearly with window count instead of copying every higher rectangle
  into every card. Painting still checks overlap/clip geometry; this is not a
  spatial-index optimization or a claim that all draw work is linear.
- Monitor snapshots detach card/zone/ghost lists once per frame and share that data
  across viewports. A later camera frame allocates separate geometry, preserving
  older submitted frames. Card-transition inputs are value records; its seen and
  removal collections are reused. Card brushes/pens are frozen and shared.
- Placeholder snapshots copy premultiplied 32-bit pixels directly from the rendered
  bitmap into an owned, frozen WPF bitmap. PNG compression, the memory stream and
  decoding have been removed from production snapshot creation. Snapshot resolution,
  card artwork, text, clipping and transition timing are retained.
- Updating saved world coordinates no longer invalidates an unchanged card image
  or repaints its controls. Visible text/icon/state changes invalidate it; native
  size and DPI changes are checked on lookup. Explicit release, distant cards and
  form disposal retain their existing image-release behavior. Gesture lookups still
  borrow cached images without creating a screenshot.

New regressions cover ten gesture cycles with no new application registrations,
no hidden geometry pushes, retained-source readiness, off-screen cleanup, no new
uncached gesture registrations, failed-hide cleanup, failed lower/replacement
sources with stable neighbours, recovery ordering, topmost/reorder behavior, shared
occlusion data with detached older frames, geometry-only snapshot reuse, visual
invalidation, encoded-versus-raw pixel comparison and snapshot ownership after form
disposal. Existing readiness and cached-source lifetime tests also remain in place.

Fourth-pass local validation: locked restore and Release cross-compilation of the
desktop test project succeeded with zero warnings/errors. Full execution of these
new Windows regressions is pending on the PR workflow.
No Surface GPU saving or input-latency percentage has been measured. Full-window
`DrawToBitmap` remains necessary when a detailed card's appearance changes; text
layout, clipping and separate DWM/WPF/Direct3D presentation remain potential costs.

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
- Gesture timing at the settle boundary, wheel bursts, held-drag pauses, quality
  disable, close/reset, cached/pinned source selection and live restoration.
- Controller integration for normal versus low quality, no captures/minimizations
  initiated by a gesture, existing cached-snapshot reuse, drag geometry, closing
  and reopening, and retaining a card across a failed live-registration retry.

`Tools/PerformanceHarness` accepts `--low-quality` and reports `LowQualityMode`.
It does not modify the installed user's settings. Its existing scenarios use the
default overview zoom and omit the workspace-card layer, so they do not establish
the zoomed-out nebula saving or full-app gesture latency.

Local validation: `git diff --check`. This editing environment is Linux; it cannot
execute the Windows Desktop tests. Windows validation runs through the PR workflow;
no local Windows test pass is claimed.

PR #14's first implementation (`f0247d2`) completed automatic code review with no
findings. Its [Windows build/test run](https://github.com/schnepg1/CanvasWindowComposer/actions/runs/39280900660)
completed successfully: Release build with zero warnings/errors, **813 desktop
tests and 81 AI tests passed**, and publish succeeded. Automatic code review has
not rerun on the later passes.

Second-pass local checks with .NET SDK 10.0.401: locked restore and Release
cross-compilation of the desktop test project succeeded with **zero warnings and
errors**. A temporary portable test project linking the repository's actual
`GesturePreviewPolicy`, `OverviewFrameScheduler`, `GridFrameSchedule` and their
test files passed **14 tests** on Linux. This validates the timing and scheduling
logic, not the WinForms/WPF/DWM integration. The second-pass revision `7d09393`
subsequently passed its [Windows build/test workflow](https://github.com/schnepg1/CanvasWindowComposer/actions/runs/39285454646):
Release build with zero warnings/errors, **822 desktop tests and 81 AI tests passed**,
and publish succeeded. Its [installer/launcher workflow](https://github.com/schnepg1/CanvasWindowComposer/actions/runs/39285454645)
also passed. These results predate the third-pass changes.

Third-pass local validation: locked restore and Release cross-compilation of the
desktop test project succeeded with **zero warnings/errors**. The portable suite
now also links the actual binding registry and its new tests: **16 tests passed**
on Linux. The third-pass code revision `60778f1` passed its
[Windows build/test workflow](https://github.com/schnepg1/CanvasWindowComposer/actions/runs/39291037682):
Release build with zero warnings/errors, **836 desktop tests and 81 AI tests passed**,
and publish succeeded. Its [installer/launcher workflow](https://github.com/schnepg1/CanvasWindowComposer/actions/runs/39291037792)
also passed. Surface GPU and interaction-latency measurements remain pending.

## Surface / Windows follow-up

Use the same power mode, applications/documents, window count, DPI, resolution,
monitor arrangement and background activity for paired runs. Compare normal and
low quality at zoom 1.0, 0.7, 0.5 and 0.25. Include stationary overview, sustained
pan, zoom bursts, a single-window drag, overview close/reopen and rapid quality
toggling. Include annotations, saved cards and multiple overlapping windows.
Exercise held-pointer pauses, inertial desktop pans, wheel bursts less than 180 ms
apart, pinned windows, unavailable snapshots and failed live-preview restoration.

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
