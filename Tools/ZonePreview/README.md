# Isolated project area preview

```powershell
dotnet run --project Tools/ZonePreview/CanvasDesktop.ZonePreview.csproj -c Release
```

This opens a test canvas, a live DWM thumbnail source, and the actual WPF project
area manager. It does not register global input hooks, manage existing windows,
or read/write the real workspace. Close the test canvas window to end the preview.

Use **Draw an area** and drag inside the test canvas. Rename/recolor the new area,
then save. Use **Choose members…** to change the sample membership, then drag an
area's name on the test canvas. Confirm that the dashed previews follow the drag,
the thumbnail and saved-place card move on release, and Esc cancels the preview.
Drag a corner square to resize just the area boundary, then use the manager's
Undo/Redo buttons or Ctrl+Z / Ctrl+Y on the test canvas to reverse/reapply layouts.
Ctrl+Shift+Z also redoes. The preview uses the production history and replay policy.
The pinned music-player card stays fixed. The preview reuses the production
gesture controller and layout planner, and its data stays in memory.

Press **P** on the test canvas or choose **Navigator** in the manager to open the
compact project panel. Search and expand its sample area. Jump updates the test
canvas title; member focus/reopen actions only simulate a state change in memory
and display a preview message. They never launch or focus real applications.

This preview does not exercise the main application's camera, native group window
placement, or global keyboard shortcuts. Its Jump callback returns to the manager;
camera fitting and workspace coordination are covered separately by automated tests.

Generate a manager layout preview without opening a desktop window:

```powershell
dotnet run --project Tools/ZonePreview/CanvasDesktop.ZonePreview.csproj -c Release -- --render Tools/ZonePreview/zone-manager-preview.png
dotnet run --project Tools/ZonePreview/CanvasDesktop.ZonePreview.csproj -c Release -- --render-members Tools/ZonePreview/zone-members-preview.png

dotnet run --project Tools/ZonePreview/CanvasDesktop.ZonePreview.csproj -c Release -- --render-navigator Tools/ZonePreview/zone-navigator-preview.png
```

![Project area manager](zone-manager-preview.png)

![Membership editor](zone-members-preview.png)

![Project navigator](zone-navigator-preview.png)

Render the production placeholder cards and overview title labels at 25% and 10%
zoom without showing windows or touching the saved workspace:

```powershell
dotnet run --project Tools/ZonePreview/CanvasDesktop.ZonePreview.csproj -c Release -- --render-placeholders Tools/ZonePreview/placeholder-cards-preview.png
```

![Placeholder cards and zoom-aware titles](placeholder-cards-preview.png)
