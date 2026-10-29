# Missing windows and taskbar previews

An open window already linked to a deliberate saved place does not appear in
**Link open window…** for a second place. One live window has one saved-place
binding. Having many Chrome tabs does not change this rule. The dialog explains
when a compatible window is already bound, and **Refresh windows** rereads the
candidate list. It does not reassign an existing saved place.

Canvas exposes Explorer's grouped taskbar chooser through its overview surfaces.
Shell events update the cutouts immediately; a 500 ms scan repairs missed events,
stale rectangles and destroyed chooser handles. This recovery only concerns
Canvas covering a chooser. It cannot make Explorer create a chooser that is
missing on the normal desktop too.

Canvas now keeps a rolling history automatically from startup, including while
Canvas view is closed. It samples window state every five seconds, with native
inspection, compression and disk writes on a worker thread. Relevant window
events are retained between samples, including shell chooser show/hide events,
foreground changes, minimize/restore and destruction. The event ring reports any
entries discarded during unusually heavy activity. Model bindings are copied
without making extra process queries on the UI thread; native evidence in the
same sample can be compared with those cached bindings.

When you notice the bug, choose **Save last 30 minutes of diagnostics** from the
Canvas tray menu before restarting Explorer or Canvas. This preserves the recent
history in a `window-history-*.zip` report under
`%LOCALAPPDATA%\CanvasWindowComposer\Logs`. The archive contains `samples.jsonl`
and a manifest with the actual time range available. No advance knowledge of
the trigger or manual recording is needed. Saving can use completed samples even
if the current native inspection is stuck; the manifest records that condition.
The first 30 minutes after enabling
history naturally contain only the time collected so far.

Rolling samples are individually compressed in `Logs\WindowHistory`, retained
for 30 minutes and capped at 64 MiB and 361 samples. Old samples are removed
automatically, including after a restart; preserved reports are kept separately
until you delete them. Storage limits can shorten the available time range.
If the tray is unresponsive, the compressed files in `WindowHistory` still
contain the most recently completed samples. Titles, URLs, document contents
and keystrokes are not collected. History is never uploaded automatically.

Before restarting Explorer or Canvas, choose **Record window diagnostics
(15 seconds)** if you also want a denser recording while reproducing the failure. A balloon
shows the report path under `%LOCALAPPDATA%\CanvasWindowComposer\Logs`.
The JSONL file samples every 500 ms when the previous inspection has finished,
including:

- Visible native window handles, process names, visibility, minimized/cloaked state,
  z-order, styles and geometry, including hidden shell chooser windows.
- Foreground and per-thread focus, mouse capture, menu and move/size state.
- Native hit-test results across any visible grouped chooser.
- Canvas mode, preview hook status, recent chooser events and scan recoveries.
- Global input-hook health, last window-event time and interaction-hook held buttons.
- Session bindings and saved-place IDs, without titles, URLs or launch targets.

The recording is read-only, runs native inspection on a worker thread, stops
automatically, and is flushed after each sample
so a partial report survives a later hang. It does not restart Explorer, change
preferences, focus applications or release mouse capture.

The source checkout also has an independent recorder that works while an older
Canvas build is running, or when Canvas's UI thread is unresponsive:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\Capture-WindowDiagnostics.ps1 -Seconds 30
```

Use `-Seconds 0` for one snapshot, `-OutputPath <file>` for a chosen JSONL path,
or `-IntervalMilliseconds <100..5000>` to change sampling. `-IncludeTitles` is
optional and may record private document or page titles. The independent tool
captures native state; session bindings and hook history require the tray recorder.

If no chooser HWND appears while clicking a grouped icon, investigate shell
creation/input handling. If it exists but is hidden, inspect its owner, shell GUI
flags and capture state. If it is visible, compare its z-order, bounds and hit-test
results with Canvas surfaces and other overlays. A healthy snapshot after an
Explorer restart alone does not identify what caused the earlier failure.
