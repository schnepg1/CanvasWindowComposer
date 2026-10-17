# On-canvas tutorial (stage 1)

The first startup with no previous tutorial presentation creates a small neighbourhood
of text notes and arrows near a visible managed window. With no visible managed windows,
a sample canvas card provides the landmark; no applications are launched. The search
avoids managed windows, personal annotations and project areas without moving them.

The cinematic intro reveals the tutorial artwork and settles on the first note at a
readable zoom. The first note explains mouse/trackpad panning; arrows lead to zoom,
window navigation and annotation notes. Notes reflect disabled Alt+Q/Alt-pan shortcuts
at creation. Reduced-motion mode fades at a stationary reading position. Disabling
the startup intro also disables its automatic tutorial landing; the tutorial remains
available manually from the tray.

**Tray → Tutorial → Return to tutorial notes** reveals and frames the first note.
**Hide tutorial notes** hides this desktop's tutorial artwork. Existing notes keep
their positions and IDs across revisits and restarts. A primary-DPI change scales
the neighbourhood around its existing origin, preserving IDs, visibility and the
real-window connector endpoint. Explicitly returning to the
tutorial on another virtual desktop creates a separate local neighbourhood. The
first-run presentation flag is application-wide, so switching desktops never
forces another intro tutorial. Existing installations get this once when updating.

Tutorials reuse annotation artwork but live in `tutorial.json`, separate from
`annotations.json` and personal undo/redo. They are read-only examples. Progress,
visibility, desktop identity, layout and DPI scale use atomic saves with a backup;
unreadable/future files are preserved and suppress automatic onboarding. A corrupt
primary can recover from its backup, preserving a copy of the damaged file and
repairing the primary atomically. A newer-version file never falls back to an older backup.

Opening and closing the tutorial without navigating preserves the saved desktop
camera. Practising navigation uses the normal camera behaviour. Temporary flight
positions and the stationary tutorial reading view do not drive automatic window parking. Ordinary startup behaviour is
unchanged after the first presentation. Notes are visible in interactive overview,
including the tutorial reveal; they do not overlay the live desktop.

Stage 1 intentionally has no guided checkpoints, Back/Next controls, action detection
or resumable tour steps. The tutorial layout and separate document can support those
in stage 2.

## Verification

Automated coverage checks collision-free placement, distant-window exclusion, empty
canvas, negative monitor origins, DPI scaling, shortcut variants, first-run/visibility
persistence, corrupted data and backup recovery, landing/skip camera endpoints, text
fit and WPF rendering, and native overview integration without personal history edits.

Windows manual smoke check: start with a fresh tutorial file and one window; let the
intro complete, practise mouse and trackpad movement, follow the arrows, hide/revisit,
restart, switch desktops, and test reduced motion. Repeat with no windows and a
crowded canvas, and at 100%/200% DPI. Confirm personal notes and real window layout
are preserved on opening/closing without navigation.
