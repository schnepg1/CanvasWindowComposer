# .NET 10 and privilege-boundary modernization

Implemented 11 October 2026, following the architecture review.

## Toolchain

- Production and tests now target `net10.0-windows`.
- Official Microsoft .NET SDK 10.0.401 and Desktop Runtime 10.0.12 installed
  side by side; existing .NET 6/8 installations were left intact for other apps.
- `global.json` pins SDK 10.0.401, allowing newer patches in the same feature band.
- CsWin32 updated 0.3.106 -> 0.3.360. Its updated raw-input signatures require
  the pointer-sized buffer-length argument instead of the old `ref` overload.
- Test SDK 18.10.1, xUnit 2.9.3, VS adapter 4.0.1, coverlet 10.1.0.
- Lock files capture direct/transitive dependencies; CI uses locked restore.
  NuGet audits all dependencies and reported vulnerability warnings fail restore.
- A Windows GitHub Actions workflow builds/tests and collects coverage. Action
  versions are pinned to verified release commits with read-only repository
  permission. No workflow has been run remotely or code pushed automatically.

## Privilege model

The application manifest is now `asInvoker`. An elevated host refuses to start
the interactive workspace. This also prevents the old highest-privilege task
from silently leaving the updated app elevated. No broad process DACL override
or arbitrary token manipulation remains. The separate workspace-launch boundary
checks elevation again as defence in depth.

Sign-in startup uses an HKCU Run entry, not Task Scheduler. Migration inspects
only the two known legacy Canvas task names, requiring a matching current user,
the exact executable path and a single action without arguments. It preserves
enabled startup, removes the matching legacy task, and exits before loading any
workspace. Other users' or other installations' tasks are not migrated.

The new NSIS definition is per-user and no longer requests administrator rights
or creates an elevated task. Its uninstaller requests graceful shutdown and
aborts if cleanup fails, rather than force-killing the application.

## Saved launches

`WorkspaceLauncher` is a small injectable boundary separate from workspace UI and
persistence orchestration. It rejects elevated hosts before any process start.
Launches use explicit executable paths and `UseShellExecute=false`; there is no
`runas` fallback, shell association, or command string composed from saved targets.
Executables must be existing absolute `.exe` files. Targets must be existing
absolute files/directories, with exact shell-folder GUIDs accepted only for Explorer.
Options, URLs, relative paths and script/shortcut files as the executable are rejected.

This is not an application sandbox: a chosen executable can interpret its file
argument, and a same-user attacker who can change local configuration may also
change other user-level startup data. The boundary being enforced is that Canvas
does not give saved commands administrator privileges. Applications requiring
elevation must be started manually. Windows can block Canvas from moving their
windows; a narrowly scoped native helper remains potential future work.

## Validation and remaining work

192 tests pass on .NET 10, including launch guards, argument/target validation,
legacy task ownership validation and the existing save/exit/restart coverage.
Release build and framework-dependent publish completed with zero warnings/errors;
coverage collection and locked restore also succeeded. Dependency audits reported
no known vulnerable packages in either production or tests. The installed app
loads .NET 10.0.12, reports `Elevated: false` and `StartupEnabled: true`, and matches
the tested build's hash. The owned legacy elevated task was removed successfully.
A live CLI clean-exit/restart kept all 19 saved entries, changed none of their saved
bounds, and preserved the camera. The local install is framework-dependent so
future installed .NET 10 servicing patches can apply.

Rollback files are retained in `publish-before-net10-20261011`: the previous
application, workspace JSON, and legacy startup-task definition. Three obsolete
bundled DLLs (System.IO.Pipelines, System.Text.Encodings.Web and System.Text.Json)
were removed from the active publish only after their rollback hashes matched.
The active build uses the installed .NET 10 runtime instead.

The CI file is prepared but requires a maintained GitHub fork to run remotely.
NSIS is not installed on this machine, so the revised installer has not been built
or exercised. No real reboot, UAC/elevated-window hardware matrix, or full security
audit was performed. Next: split the remaining workspace matching/storage/UI
responsibilities, structured error reporting, and broader native integration tests.

Follow-up: the matching/storage/UI separation is now implemented; see
[workspace boundaries and validation](workspace-architecture.md). The 192-test
count above records the modernization checkpoint; the refactored suite has 220.

References: [Microsoft .NET Windows installation](https://learn.microsoft.com/en-us/dotnet/core/install/windows),
[Microsoft least-privilege guidance](https://learn.microsoft.com/en-us/windows/win32/secbp/running-with-administrator-privileges).
