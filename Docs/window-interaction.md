# Interacting with windows on the canvas

Open the overview with **Alt+Q**, right-click a live window thumbnail, and choose
**Interact with window (mouse and keyboard)**. The view stays at its current zoom.
Click links and controls, scroll, drag sliders, select text, and type into the app.
The app keeps real keyboard focus, so typing, clipboard shortcuts, and its own
keyboard shortcuts use the normal Windows input path.

**Esc** returns to canvas navigation without closing the overview. Middle-clicking
or clicking outside the chosen thumbnail also ends interaction; that exit click
is consumed. The next click or drag navigates the canvas normally. While
interacting, ordinary wheel input scrolls the app; **Ctrl+wheel** and **Alt+wheel**
still zoom the canvas. Canvas-wide hotkeys such as Alt+Q and Alt+S remain reserved.
Use **Open window at desktop size** from the thumbnail menu for the native app view.

Interaction is available only for live app thumbnails. Closed-app placeholders,
cached previews, and distant information cards cannot receive app input. Moving
far enough away to replace a live thumbnail with a card ends interaction. Losing
app focus, minimizing or closing the target, changing tools or desktops, or
closing the overview also ends the session and releases forwarded mouse buttons.

## Compatibility

This is an experimental bridge for applications that handle standard Windows
mouse messages. Only the client area receives forwarded clicks; title-bar
dragging and native window resizing remain canvas/native-window operations.
Mouse positions follow the rendered DWM frame, including zoom, frame borders,
and monitor origins. Dragging stays with the original child control even when
the pointer leaves the thumbnail.

Typing uses real foreground focus rather than simulated keyboard messages. Mouse
input is posted to the appropriate native child window. Windows may reject it
for elevated apps. Games, raw-input controls, drag-and-drop between applications,
native popup menus/dialogs, fullscreen transitions, and apps that query the
physical cursor instead of mouse-message coordinates may need the desktop-size
view. A successful message post does not guarantee that an application handles
it. Browser/YouTube compatibility still needs manual validation on the user's
browser; native control and input-routing tests do not establish that support.

## Performance

Rendering still uses the existing live DWM thumbnail. Interaction adds no image
capture, pixel-copy loop, video encoding, or second rendering of the app.
The mouse hook and its dedicated input thread exist only during an interaction
session. Keeping the hook off the canvas UI thread prevents a slow canvas frame
from holding up the system's mouse callback. It queues pointer events to the UI
thread, coalesces pending mouse movement, and maps positions
back to the source frame. Unchanged hover positions are skipped. Keyboard input
goes straight to the real app, without a keyboard hook or keyboard polling loop.

This adds input-processing work while the pointer is in use. The expectation is
small overhead relative to video decoding and composition, but no browser FPS,
CPU, or GPU benchmark has been performed. Foreground focus can make an app stop
background throttling, and interacting can trigger more work inside the app.
Those costs depend on the application. Very small thumbnails also make controls
harder to hit because one screen pixel covers several source pixels.

## Validation

Automated coverage includes scaled coordinates with negative monitor origins,
client/child targeting, captured drags, button cancellation, double clicks,
wheel coordinates, failed input, focus and target loss, and overview escape and
lifecycle behavior. Native smoke tests verify real control clicks, foreground
focus, non-activating overlays, and cursor movement without leaking unscaled
movement to an app that holds mouse capture.

For manual browser validation, play a YouTube video and enter interaction mode
at several zoom levels. Check play/pause, seeking, volume, page scrolling, text
selection, search typing, and clipboard shortcuts. Check Esc, Alt+Q, focus changes,
multiple monitors, and mixed DPI. Compare CPU/GPU use and video frame drops at a
fixed zoom with interaction idle and during seeking; browser video-decoder work
should be measured separately from Canvas's input overhead.
