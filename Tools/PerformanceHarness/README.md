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
