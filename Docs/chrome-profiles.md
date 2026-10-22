# Chrome profiles in saved windows

Right-click a Chrome card or saved live preview and choose **Chrome profile…**.
Select a profile by its display name and directory (for example, Work / Profile 1).
The same action is available in Saved workspace. For a custom user-data location,
use **Choose profile folder…** and select the folder containing `Preferences`,
as shown by the Profile Path at `chrome://version`.

Reopening uses the saved executable with separate, escaped `--user-data-dir`,
`--profile-directory`, and `--new-window` arguments. It does not run a shell or
accept arbitrary command-line text. A missing profile produces a launch error
and keeps the placeholder, rather than creating an empty profile or silently
opening another account. Choosing **Use Chrome's default selection** removes
the profile preference and disables automatic recapture for that saved place.

Choosing a different profile for an open saved window leaves its browser window
open on the canvas. That window is detached from this saved slot; the card can
then reopen using the new profile. Pending launches must finish before a profile
can be changed. No Chrome settings are written.

## Capture and matching

Chrome windows can share one browser process. Process ID, executable, tab title,
and launch timing therefore cannot prove which profile a window belongs to.
Canvas reads the window's `System.AppUserModel.ID` property and compares Chrome's
browser/profile identifier with known profile paths. New or legacy saved entries
can capture a profile automatically when that evidence is unique. Existing
custom names remain independent of profile identity and native captions.

Only local profile names/directories are read from `Local State`; no history,
cookies, passwords, or browser automation are needed. Discovery also recognizes
existing profile folders if that metadata is missing or being rewritten. The
standard catalog is cached for a minute, with an explicit refresh on opening the
profile picker. Guest and System Profile directories are excluded.

Unknown window identifiers, web-app identifiers, and colliding sanitized profile
identifiers do not auto-attach to a configured profile. Such cards keep the
existing **Link open window…** fallback. Explicit linking can select a window
whose profile cannot be read; positively identified other profiles are excluded.
Several indistinguishable windows in the same profile still require linking.
Custom Chrome builds, installer suffixes outside the standard 26-character base32 form,
policy overrides, very long identifiers shortened by Chrome, or nonstandard
user-data paths may require that fallback. Native HWND/process-start validation
and same-application launch serialization remain in place.

Optional `ChromeProfilePath` and `CaptureChromeProfile` fields preserve workspace
version 1 compatibility. Old documents default to no profile and allow capture;
an explicit default-selection choice remains unset after subsequent captures.

## Validation

Tests cover metadata discovery/recovery, launch escaping and missing profiles,
different profiles with identical captions, unknown identities during launches,
sanitized collisions, profile changes without closing existing windows,
custom-name refresh, explicit default selection, persistence, and legacy files.
Windows UI tests exercise saved-card/live-preview menus and dialog save/cancel.
Physical Chrome installation/profile-picker behavior remains a manual check.

The native identity rules follow Chromium's
[Windows shortcut and taskbar documentation](https://chromium.googlesource.com/chromium/src/+/HEAD/docs/windows_shortcut_and_taskbar_handling.md)
and `GetProfileIdFromPath` / `GetAppUserModelIdForBrowser` in
[shell_integration_win.cc](https://chromium.googlesource.com/chromium/src/+/HEAD/chrome/browser/shell_integration_win.cc).
The Windows property is documented as
[System.AppUserModel.ID](https://learn.microsoft.com/en-us/windows/win32/properties/props-system-appusermodel-id).
