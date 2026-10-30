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

When you notice the bug, open **Save diagnostic history** in the Canvas tray
menu and choose **Last minute**, **Last 5 minutes**, **Last 10 minutes**,
**Last 15 minutes** or **Last 30 minutes**, before restarting Explorer or Canvas.
This preserves only the selected interval in a `window-history-last-<N>m-*.zip` report under
`%LOCALAPPDATA%\CanvasWindowComposer\Logs`. The archive contains `samples.jsonl`
and a manifest with the selected interval and actual time range available.
Saving a short interval does not truncate the rolling 30-minute history. No advance knowledge of
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
  z-order, styles and geometry, including hidden classic choosers, XAML hosts,
  XAML popups, overflow islands and LivePreview windows. These classes are evidence,
  not a claim that every XAML popup is a grouped taskbar chooser.
- Foreground and per-thread focus, mouse capture, menu and move/size state.
- Passive geometry estimates across any visible grouped chooser and classes,
  bounds, process/thread IDs, GUI state and parent/root handles for taskbar children.
- Native capture start/end time and duration, system uptime and session input age.
- A bounded history of power notifications, including suspend/resume, in both
  rolling history and tray recordings. Notifications only record metadata.
- Canvas mode, preview hook status, recent chooser events and scan recoveries.
- Global input-hook health, last window-event time and interaction-hook held buttons.
- Mouse callback counts, time since the last callback, maximum own callback
  duration, and recent left-click routing decisions with geometry age.
- Session bindings and saved-place IDs, without titles, URLs or launch targets.

The recording is read-only, runs native inspection on a worker thread, stops
automatically, and is flushed after each sample
so a partial report survives a later hang. It does not restart Explorer, change
preferences, focus applications or release mouse capture.

The shell mouse observer uses a cached geometry snapshot refreshed on a worker.
Its callback never calls `WindowFromPoint` or sends `WM_NCHITTEST` to Explorer;
native mouse events always pass through. Diagnostics also use geometry instead
of `WindowFromPoint`, with `HitTestMethod: PassiveRootGeometry` recorded explicitly.
These estimates respect native regions and click-through surfaces but do not
claim to reproduce per-pixel transparency or a target's custom hit-test logic.
An absent hook callback cannot be inferred from a still-valid native hook handle
alone, which is why callback counts are now included. Compare callback age with
`LastInputAgeMilliseconds` to distinguish inactivity from a possible lost hook.
The latter comes from the session-specific
[GetLastInputInfo API](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getlastinputinfo).
Power notifications come from
[SystemEvents.PowerModeChanged](https://learn.microsoft.com/en-us/dotnet/api/microsoft.win32.systemevents.powermodechanged).
Their history begins at Canvas startup; it does not establish whether previews
were working before a suspend. A failure noticed after resume is a correlation
until reproduced.

This avoids a known observer risk, not a proven explanation of every taskbar
failure. Microsoft documents that [WindowFromPoint can send WM_NCHITTEST](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-nchittest)
and that [low-level mouse hooks must return promptly](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelmouseproc).

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
