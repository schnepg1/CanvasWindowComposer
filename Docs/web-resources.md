# Web resource placeholders

Choose **Add web resource…** from the canvas tools' **More** menu, the tray menu,
or **Saved Workspace**. Enter a full HTTP/HTTPS URL, choose an installed Chrome,
Edge or Firefox browser, and optionally choose one of its detected profiles.
**Choose browser executable…** supports custom installation locations.
**Choose profile folder…** supports profiles outside the standard catalog.

From canvas tools, choose the place first: move the pointer to preview the
placeholder's landing outline, then click to open the creation dialog. The
preview uses the backpack's placement system to find clear space in the current
view. **Esc** or right-click cancels placement. Saving creates the placeholder
at the chosen place; cancelling the dialog leaves the canvas unchanged.
If no clear space fits, scroll down or press **−** to zoom out while keeping the
preview active. **Ctrl+scroll** pans vertically; **Shift+scroll** pans horizontally.

Browser cards without a configured website offer **Set website URL…** in their
right-click menu. Configured resources offer **Web resource settings…**. The
same actions appear in Saved Workspace. Saving
changed launch settings keeps the current window open and detaches it from the
saved place; reopening creates a window with the new settings. Renaming alone
keeps an already-attached web resource window attached. Saving errors leave the
previous configuration and binding intact.

Older browser placeholders saved a window title, application and optionally a
Chrome profile, without saving the page's address. These cards show **No website
URL saved**. A page title, even one containing a URL, is not used as a launch
address. Choose **Set website URL…** and paste the address once; the existing
Chrome profile is preselected. Saving preserves the card's position, startup
choice and automatic minimization preference.

Launch settings follow the application: supported browsers offer website/profile
settings, Explorer offers a folder, and VS Code (including Insiders) offers files
and project folders. VS Code folders reopen in VS Code; Explorer folders reopen
in Explorer. Other apps retain manual file/folder arguments under **Advanced
launch settings**, since support depends on the application. Application types
are derived from executables, not titles, so older cards need no format upgrade.

A web resource saves its URL independently of file/folder launch targets. It
opens in a separate browser window at the saved canvas position. Opening an
already-attached resource focuses that window without opening another copy.
Navigation, login redirects and tab changes do not replace the configured URL.
Reopening a closed resource starts at that URL. This is a starting address, not a
saved inventory of tabs or a browser-session backup.

Profiles and authentication belong to the selected browser. Selecting another
browser resets the profile choice; profiles with the same display name are not
interchangeable across browsers. Resources using the same browser/profile share
its cookies and login state. Sites can still require sign-in or MFA. Canvas does
not read or copy cookies or passwords and does not install a browser extension.

## Browser support

| Browser | Profile discovery | Launch |
| --- | --- | --- |
| Chrome | Local State metadata and existing profile directories | `--user-data-dir`, `--profile-directory`, `--new-window URL` |
| Edge | Edge channel's Local State metadata and existing profile directories | `--user-data-dir`, `--profile-directory`, `--new-window URL` |
| Firefox | `profiles.ini` relative/absolute paths; manual profile-folder selection | `--profile PATH --new-window URL` |

Browser discovery reads Windows App Paths registrations and standard install
locations. Profile discovery is read-only. Missing browsers or selected profiles
produce errors instead of silently opening another browser/account or creating
an empty profile. For a custom folder, choose the directory containing
`Preferences` (Chrome/Edge) or `prefs.js` (Firefox). Firefox profiles absent from
`profiles.ini`, including profiles managed by newer profile-group catalogs, can
be selected by folder.

Only Chrome's existing, documented window profile identification is used for
automatic profile verification. Edge and Firefox launches rely on explicit
launch intent, not profile verification from window captions. Browser policy,
profile locks, startup/profile pickers, session restore and version-specific
behavior still require real-browser compatibility testing.

## Matching and recovery

Canvas serializes launches of the same application and records pre-existing
window handles. A unique new window from an explicit launch can attach to that
resource; Chrome's configured profile additionally requires matching profile
evidence. Positively identified conflicting Chrome profiles are excluded.
Competing new windows are left unresolved rather than choosing by enumeration
order. Launch intent does not prove the page finished loading or authenticated.

Canvas does not read live tab URLs. After Canvas restarts while resource windows
remain open, use **Link open window…** to select each resource's existing window.
Neither a matching title nor a unique browser profile proves a URL identity.
This also avoids confusing two resources sharing the same profile. Opening an
unlinked placeholder launches another window; link first to reuse the old one.
Independent tab activation and automatic URL matching would need a browser
bridge and are outside this feature.

Existing application/file/folder/Chrome-profile placeholders remain compatible.
The optional `WebResource` object stores `Url` and `ProfilePath` inside workspace
version 1; old files load with no web-resource settings. URLs are restricted to
HTTP/HTTPS, without embedded username/password, and passed as individual escaped
arguments to a supported executable without a shell or arbitrary flags.

## Validation

Automated coverage exercises all three browsers' launch argument vectors,
missing profiles, URL validation, legacy persistence, atomic save rollback,
browser/profile selection, rendering, explicit linking, conflicting profiles,
competing windows, fixed saved URLs and repeated-open focus behavior. Native
desktop regressions run through the machine-wide serialized test runner.
Argument-vector tests do not establish physical browser login behavior.

References: [Firefox command line](https://firefox-source-docs.mozilla.org/browser/CommandLineParameters.html),
[Firefox profiles service](https://firefox-source-docs.mozilla.org/toolkit/profile/index.html),
[Chromium switches](https://chromium.googlesource.com/chromium/src/+/HEAD/chrome/common/chrome_switches.h).
