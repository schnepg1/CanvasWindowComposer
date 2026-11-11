# Retained canvas rendering plan

This is a follow-up project for smooth panning, zooming, and window flights in a populated workspace. Keep a recognizable representation of every visible window throughout movement. A cached screenshot can stand in for live content while the camera moves. Empty rectangles remain an explicit low-performance option, off by default.

## Starting point

- The user reports much smoother motion in their latest testing, including camera flights. The current motion changes and full-quality defaults give the next round a usable baseline. See [canvas performance results](canvas-performance.md).
- The first full-workspace trace on the RTX 4070 at 60 Hz found long UI gaps overlapping individual DWM thumbnail updates and registrations. WPF placeholder drawing was 0.51–1.33 ms at p95 in that workspace. The grid swap chain also showed long display gaps. Its timing covers one surface of the current multi-layer canvas.
- The current renderer already has a D3D11 grid and separate annotation and thumbnail layers. It can host a bounded experiment without migrating the WinForms shell, WPF editors, or native window management. The existing annotation-retention prototype covers saved artwork. Revisit it with an annotation-heavy fixture before expanding that work.
- The forest and vector-editor demos inspired retaining visible objects and controlling detail. They provide no comparable measurements for capturing, updating, and compositing foreign Windows applications. Map rendering is a closer design analogy for moving a camera across cached raster content and vector overlays. [Google Maps rendering types](https://developers.google.com/maps/documentation/javascript/map-rendering-type)

## Proposed milestones

All milestones below are open. Each should have a side-by-side comparison with the existing renderer before expanding scope.

### 1. Prove a retained preview during motion

- [ ] Draw a small cohort of windows as textured quads on an opt-in D3D11 path. Prepare snapshots asynchronously, upload a bounded number at a time, and keep accepted textures resident across camera frames. A camera frame should transform the prepared scene without capturing windows, reading pixels back, or uploading unchanged images.
- [ ] Use last-good images where available. Keep the existing live preview when the cohort or source is unready. Where live content is also unavailable, show the existing readable app icon and title. Define freshness and invalidation for resize, minimize, source replacement, desktop switch, and capture loss.
- [ ] Test partially overlapping and clipped windows early. A monitor or overlap group may need to switch as one unit because independent surfaces cannot arbitrarily interleave. Keep previews visible until the receiving surface presents the correct camera and stacking order. Use the existing path on failed or interrupted transitions.
- [ ] Compare repeated, identical runs for separate and stacked layouts, including cached and minimized sources. Measure presented gaps, UI gaps, native thumbnail operations, time to first usable image, time to settle, upload cost and resident memory. Require repeatable pacing improvement and visual parity before pursuing a broader renderer.

### 2. Coordinate the motion scene

- [ ] If the pilot passes, give the grid and window previews one presentation boundary per monitor. Retain world-space geometry, window identity and stacking order, and pass the current camera and visible bounds into rendering. Keep hit testing and native placement on their current owners. Preserve existing annotation and editing semantics during the transition.
- [ ] Cull offscreen work. Measure the cost of scanning the visible model before introducing a spatial index. Choose thumbnail detail by projected size, focus and visibility. Add hysteresis so tiny camera changes do not reload textures or change representation repeatedly. Retain title and icon at small sizes and keep movement visually stable.
- [ ] Size snapshot and upload budgets from the measured workload. Measure binding and draw overhead before considering atlases, texture arrays or instancing. Respect clipping and draw order when grouping draws. Track cache hit rate, eviction, and upload pressure under warm and cold runs.
- [ ] Evaluate moving static annotation drawing and other overlays into the coordinated scene only when traces show cost or visible timing skew. Preserve edit handles, dynamic annotations and selection state.

### 3. Add live textures where they help

- [ ] Prototype a focused or large-window live budget using Windows Graphics Capture. DWM thumbnail handles manage live compositor relationships. Windows Graphics Capture supplies Direct3D surfaces for a texture-based renderer. [DWM thumbnail overview](https://learn.microsoft.com/en-us/windows/win32/dwm/thumbnail-ovw) · [Windows screen capture](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture)
- [ ] Separate capture frame ownership from render texture lifetime. Verify support, capture indicators, protected or unavailable content, DPI and size changes, minimize/restore, and device loss. Verify buffer and adapter compatibility before assuming GPU-only transfer. Keep the most recent usable frame when live capture cannot keep up.
- [ ] Compare snapshots alone with a small live subset. Expand live updates only where the added freshness is visible and the refresh budget and memory cap still hold.

## Acceptance and sequencing

Use the real workspace with the same monitor, refresh, DPI, renderer settings and background workload. Record repeated pan, zoom and flight runs with at least 30 seconds of active motion per scenario. Report p50/p95/p99/max gaps and misses of one and two refresh intervals per observed surface. At 60 Hz the frame budget is 16.67 ms. Use the actual refresh rate on faster displays. Include cold and warm entry, overlap and clipping, mixed DPI, cancelled and interrupted motion, focus, native input recovery, and the transition back to live content. Treat build-time stress scenes as scaling tests and report them separately from the user’s workspace.

Keep the existing renderer available during rollout and exercise fallback on cache miss, capture loss and device reset. Native desktop handoff has a separate safety gate around in-flight window placement and input recovery. It can advance independently of the owned-window rendering pilot. Carry the paired full-workspace trace and remaining visual checks into the baseline for renderer comparisons.
