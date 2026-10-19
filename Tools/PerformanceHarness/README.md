# Window visibility performance harness

This Windows-only tool runs the production placement, projection-worker, DWM
thumbnail and overview-rendering code against windows in an owned child process.
It does not start the tray app or install input hooks, discover other applications,
load saved workspaces, or replace an installed build. The test displays temporary
windows and, by default, the overview overlays. Avoid interacting with them during
a measurement.

From the repository root, with the .NET 10 Windows Desktop SDK:

```powershell
dotnet restore Tools/PerformanceHarness/CanvasDesktop.PerformanceHarness.csproj --locked-mode
dotnet build Tools/PerformanceHarness/CanvasDesktop.PerformanceHarness.csproj -c Release --no-restore
& Tools/PerformanceHarness/bin/Release/net10.0-windows/CanvasDesktop.PerformanceHarness.exe --count=8 --repeats=3 --seconds=5 --output=Tools/PerformanceHarness/results/cooperative.json
& Tools/PerformanceHarness/bin/Release/net10.0-windows/CanvasDesktop.PerformanceHarness.exe --count=8 --repeats=3 --seconds=5 --always-work --skip-overview --output=Tools/PerformanceHarness/results/always-work.json
```

`--count` accepts 1..100 windows, `--repeats` 1..20 and `--seconds` 1..60 seconds
per phase. Defaults are 8, 3 and 5. Results are JSON; the local `results/` directory
is ignored by Git. Ctrl+C ends the measurement, writes the completed/partial phase
samples and closes the owned helper. The child also exits if its parent exits.
Setup or measurement errors print to stderr and return a nonzero exit code.

## Phases

Each phase rebuilds the same layout, waits 300 ms before measurement, and resets
the in-memory diagnostic counters. Preparation diagnostics are retained separately.
Phase order rotates between repeats. Placement coordinates and native style bits
are saved so a report can be checked against the intended minimized/off-screen state.

| Phase | Behavior |
| --- | --- |
| Visible | Normal windows in a grid on the primary monitor; production default `DisableGreedyDraw=true` |
| OffScreenDefault | Same windows shifted 10,000 pixels right, with clipping disabled |
| Clipped | Same off-screen world bounds, with empty regions and compatibility edge parking enabled |
| Minimized | Explicit native minimize and corresponding collapsed canvas state |
| StationaryOverview | Visible window layout, stationary Zooming overview with live previews |
| Panning | Visible layout, Panning overview, a repeatable sinusoidal camera movement |
| Restoration | Alternate minimize/restore, recording the full batch's restore API duration |

`--skip-overview` omits the two overview phases. `--always-work` keeps the target's
timer/CPU simulation running even when minimized. The default workload explicitly
skips that work while minimized. Both invalidate/draw animated GDI content while
normal. These two modes deliberately test different application responses to
minimization; they do not simulate every application's rendering pipeline.

`--low-quality` selects the same simple background used by the Settings option.
The report records `LowQualityMode`; it does not change the user's config. The
stationary/overview gesture scenarios accept `--overview-zoom=0.05..1` (default 1).
Native desktop `Panning` always follows the main canvas at zoom 1. Reports include
the actual start/end camera: inspect those values when comparing configurations.

`--flip-rendering` requests the experimental two-buffer flip-discard backend for
overview. Without it, the compatibility backend remains selected. Reports include
`FlipRenderingRequested` and the actual per-monitor `RenderingBackends`; inspect
preparation diagnostics for failed initialization/fallback. This flag changes only
the owned harness, not the user's config. API acceptance and frame counts do not
prove correct DWM thumbnail composition; visually validate live/cached previews,
Panning/Zooming transitions and layered-window behavior before enabling it in the app.

For large counts the grid extends beyond the primary screen, so the Visible phase
does not promise all windows fit. Inspect `NativeWindows` and monitor geometry,
or choose a window count that fits the display. OffScreenDefault likewise needs
the shifted layout to be outside the recorded monitor topology.

## Presentation comparison

The solution now builds this tool during normal Release builds. For a repeatable
comparison of both presentation backends, both quality modes and zoom 1/.5/.25:

```powershell
./Tools/PerformanceHarness/Compare-Presentation.ps1 -Count 8 -Repeats 3 -Seconds 5
```

The default 12 configurations, 3 repeats and 5 phases take roughly 15 minutes plus
setup. The script creates a timestamped directory in `results/`, with raw reports
and `summary.csv` / `summary.json`. Case order rotates/reverses between repeats to
reduce fixed thermal/backend order bias. Each report uses one repeat and the same
owned child-window layout. No installed application settings are changed.

A short smoke run (also run in Windows CI) is:

```powershell
./Tools/PerformanceHarness/Compare-Presentation.ps1 -Count 3 -Repeats 1 -Seconds 1 -Zooms 0.5 -OutputDirectory TestResults/PresentationSmoke
```

`--presentation-only` selects these phases without the minimize/visibility phases:

| Phase | Behavior |
| --- | --- |
| StationaryOverview | Stationary view at the requested zoom after a 300 ms setup/settle period |
| OverviewPan | Held middle-button pan through the production overview input callbacks |
| ZoomBursts | Alternating Ctrl-wheel bursts, with pauses that exercise low-quality restoration |
| WindowDrag | Held drag of the first owned window through the production pointer callbacks |
| Panning | Layered desktop-pan mode, following the main canvas at zoom 1 |

The tool invokes the controller's input callbacks; it does not synthesize OS-wide
mouse/keyboard input. Requested zoom is applied via the production wheel scale,
with integer-delta rounding. `CameraStart` / `CameraEnd` are the actual values.
The current workload still omits saved-workspace cards and the minimap; low-quality
gesture cards are exercised, but this is not a complete full-app scene benchmark.

The matrix records actual per-monitor `RenderingBackends` and marks
`BackendMatched=false` on fallback/unavailable backends. Do not group fallback
samples with successful flip samples. It does not choose a backend automatically.

Schema 2 adds `InputPumpMs` nearest-rank p50/p95/p99/max and sample counts for each
overview phase. A sample covers one synthetic-input/WinForms-pump iteration,
including any scheduled frame processed during it, and excludes the 10 ms pacing
sleep. Idle iterations are included. It is a UI wall-time distribution, not a
present-to-screen measurement. Pointer-down/setup and pointer-up/final-release
costs are not represented by that distribution; detailed real-app end-of-gesture
latency still needs tracing. Process samples include ownership checks and harness
measurement overhead. The final release occurs after phase diagnostics are captured.

The new diagnostics separate `GridUploadAndDraw` / `MinimapUploadAndDraw` from
`GridPresentCall` / `MinimapPresentCall`. The former includes snapshot copying,
D3D buffer upload and command submission. The latter is CPU wall time in `Present`,
which may include a vsync/queue wait. Neither is GPU execution time.
`OverviewAnnotationFlush` measures one shared WPF render-priority barrier after all
monitor annotation windows have been staged/prepared. Existing
`*FrameIncludingPresent` totals overlap these measurements; do not add them.

CI smoke results establish execution/API compatibility on the runner. Its display,
virtual GPU and background activity do not rank backends for a Surface Pro 4.
Use the full matrix on the target machine and inspect visual composition before
changing backend defaults.

## Reading a report

- `CanvasHarness` is the host running the production controllers and overlays;
  `Workload` is the separate helper process. DWM is sampled separately in the same
  session. This is not a CPU baseline of the entire tray app: input hooks, workspace
  timers and other services are excluded, and the host includes measurement work.
- `CpuOneCorePercent` comes from process CPU time over phase wall time. 100 means
  one logical core; divide by `LogicalProcessors` to compare with a whole-machine
  percentage. Memory values are end-of-phase working set and private bytes, not
  allocations, GPU memory or guaranteed reclaimable memory.
- GPU data comes from English PDH `GPU Engine` counters over the same interval.
  Each value belongs to a process and engine instance. Do not sum engine percentages
  into an overall GPU percentage. Missing counters have an `UnavailableReason`;
  inaccessible process samples have null values and an `Error`. DWM includes
  unrelated activity on the desktop and may deny CPU/memory access.
- Diagnostic aggregates contain operation counts, outcomes and total/maximum API
  duration. The last 256 operations and last 128 failures are bounded histories,
  not complete traces. A batch duration includes its component API calls: adding
  their durations together would double-count work. Failed finalization retains
  affected HWNDs, and a successful fallback does not erase the original failure.
- `GridFrameIncludingPresent` / `MinimapFrameIncludingPresent` include CPU command
  submission and the wait in `Present`. They are not GPU execution times or end-user
  frame latency. Frame counts aggregate across monitor renderers. The harness opens
  overview; it does not exercise the separate minimap renderer.
- `RestoreBatchApiLatencyMs` measures all restore/placement calls for the batch of
  windows, including unclipping. It is neither a per-window number nor time until
  the first correct image is displayed. `Queued` means an asynchronous move was
  accepted; native success does not prove that the target accepted its final bounds
  or displayed a new frame.
- Thumbnail counts include wallpaper and taskbar registrations as well as program
  previews. Failed unregister calls retain their count until native release is
  confirmed; a gauge is not a precise DWM memory measurement.

## Real-application follow-up

Use the same application versions, documents, video/animation load, window count,
monitor arrangement, DPI, power mode and sampling duration for every comparison.
Record CPU, per-engine GPU and memory for Canvas, DWM and **all** processes belonging
to each target application; browsers and editors often span multiple processes.
Repeat visible, off-screen/default, clipped and user-minimized states, then
stationary overview, panning and restoration. Collect before/after exports with
the new tray action **Export performance diagnostics...** in a build containing
this change. That action reports native/rendering diagnostics; it does not itself
collect process CPU or PDH counters.

Extend validation to actual mixed-DPI monitor changes, elevated apps, dialogs,
multiple windows per process, video/WebGL workloads, and apps that restore or move
themselves. Use ETW/WPR/GPUView or PresentMon when first-present latency, scheduling
or GPU execution needs attribution. The synthetic results alone cannot establish
an automatic-minimization allowlist or safe default.

The initial local measurements and their limitations are recorded in
[the implementation plan](../../Docs/upgrade_window_visibility_performance.md).
