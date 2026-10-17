# Startup, installation and updates

## For people using Canvas

Use the self-contained Windows x64 installer from a deliberately published GitHub
Release. It bundles .NET 10; Git, an SDK and a separate Desktop Runtime are not
required. Start Canvas through the Start menu. Installation is per user; do not
run Canvas as administrator. The portable bundle has the same external user-data
locations as an installed build; it is not a portable credential store.

The tray's **Check for updates…** window shows the installed version, channel,
copyable errors, download and restart actions. Installed builds check at launch
and every six hours unless disabled. They notify through the tray and never
download, install or restart without a user action. Source/unmanaged builds make
no update requests. Offline checks fail without interrupting canvas use.

The default channel comes from the installed package. Users can explicitly save
stable/preview in Updates. Stable excludes GitHub prereleases; preview can see
prereleases and still requires a matching preview feed. Downgrades are disabled.
A preview user returning to stable may need to wait for a newer stable version.

Downloads are separate from installation. **Restart to update** asks for
confirmation, refuses unfinished note editing/AI proposals, saves annotations and
the workspace, then runs the ordinary shutdown lifecycle. Every cleanup phase,
including native window recovery and UI disposal, must succeed before Update.exe
is started. If cleanup fails, Canvas exits with recovery attempted and the update
is postponed with copyable details. Start Canvas normally to retry. Automatic
application of previously downloaded packages on startup is disabled.

## For people working from source

`Start-Canvas.cmd` is a development launcher. It always publishes current sources
into fresh output before launching; `git pull` followed by the launcher therefore
uses the new code. Exit an already-running build from that checkout first. The
legacy `--build` option remains accepted. More options:

```
Start-Canvas.cmd --diagnose
Start-Canvas.cmd --build --no-launch
```

The launcher uses built-in Windows PowerShell 5.1 to check SDK resolution through
`global.json` and the matching Windows Desktop Runtime. The currently selected
SDK is 10.0.401 with latestPatch roll-forward in the same feature band. A generic
.NET runtime, another major SDK or an earlier SDK feature band is insufficient.
Failures show selectable/copyable details and persist a timestamped startup log.
The old running output is not launched after a failed build. Concurrent publishers
are excluded with a per-checkout file lock. A new publish also removes stale DLLs.

Startup and application logs are in
`%LOCALAPPDATA%\CanvasWindowComposer\Logs`. Settings, workspaces and annotations
remain in `%APPDATA%\CanvasWindowComposer`; encrypted AI account data remains in
`%LOCALAPPDATA%\CanvasWindowComposer\AI`. No user data lives inside versioned
application payloads. Existing application-folder debug logs are left untouched.

## Packaging and release control

Velopack's library and local `vpk` tool are both pinned to 1.2.161. The application
bootstrap runs before single-instance checks or desktop services. The package ID
is `Schnepg1.CanvasWindowComposer`, keeping its installation separate from existing
Canvas data directories and the old NSIS installation.

```
Install\build-installer.bat 1.13.0-preview.1 preview
# or, from PowerShell:
./Tools/Deployment/Build-Release.ps1 -Version 1.13.0 -Channel stable -RequireSigning
```

Packaging publishes win-x64 self-contained output, checks required application
modules and the bundled runtime, runs a diagnostic-only launch, and builds Setup,
a portable bundle, a full update package, the channel feed and SHA-256 checksums.
The legacy NSIS recipe is retired rather than silently producing an incomplete
installer. Full packages are used initially; delta updates can be added later.

**Prepare Windows release** is a manual workflow restricted to main. It runs
locked restore, build and tests before packaging. By default it uploads build
artifacts only. Selecting **create_draft** requires signing and uploads a draft
GitHub Release; preview drafts are marked prerelease. The maintainer reviews and
publishes the draft manually. Normal pushes, PRs and version tags do not publish
releases. Keep the generated feed and full package attached to that release;
GitHubSource uses the public `schnepg1/CanvasWindowComposer` releases, with no user
GitHub account/token required. Never embed a private repository token in the app.

Signing must be provisioned before a distributable draft is created. The manual
workflow can import an exportable code-signing certificate from the
`WINDOWS_SIGNING_PFX_BASE64` and `WINDOWS_SIGNING_PASSWORD` GitHub secrets into
the temporary runner's current-user certificate store, then removes it afterwards.
Hardware-backed certificates require an appropriate signing service instead.
The workflow also accepts Velopack's `VPK_SIGN_PARAMS`, `VPK_SIGN_TEMPLATE` or
`VPK_AZURE_TRUSTED_SIGN_FILE` as GitHub secrets. Configure the corresponding
certificate/service and executable/metadata on the runner; a path by itself is not
a certificate. For example, use a provisioned current-user certificate with
signtool parameters selecting its thumbprint and a timestamp service, or a cloud
signing command. Public release creation fails if signing is absent or the main
executable/Setup signature is not valid. CI packaging smoke tests are deliberately
unsigned and are not public releases. Signing identity/service provisioning is a
maintainer setup step, not something this repository can invent.

For old NSIS users: exit the old application and install the new release. Saved
data is reused. An exact, owned old NSIS sign-in startup entry is migrated on the
first normal installed launch; unrelated startup entries are preserved. The old
installation can then be uninstalled without deleting the new installation or
saved data. Always exit Canvas through its tray before manually uninstalling.
The new package's uninstall callback removes only its own sign-in startup entry.

## Verification

`Windows build and tests` covers update policy, cancellation, retries, channel
selection, persistence and the save/recovery/disposal gate. `Windows installation
and launcher` additionally exercises missing/wrong SDK errors under Windows
PowerShell 5.1, command-file options, fresh publication, actual Setup installation,
Update.exe upgrade/restart, bundled runtime location, and preservation of settings,
workspace, annotations and AI data through upgrade/uninstall. The latter runs only
under an ephemeral CI account. Diagnostic launches never start canvas services,
install hooks, move windows, or load/save a user's workspace.

A physical clean Windows machine and the real signing identity still need a
release-candidate smoke check. CI proves that the installed app loads its bundled
runtime rather than the runner's .NET installation; it does not uninstall the
runner's SDK.

References: [Velopack integration](https://docs.velopack.io/getting-started/csharp),
[update sources](https://docs.velopack.io/integrating/update-sources),
[signing](https://docs.velopack.io/packaging/signing),
[.NET publishing](https://learn.microsoft.com/en-us/dotnet/core/deploying/).
