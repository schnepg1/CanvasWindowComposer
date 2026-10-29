using System;
using System.Linq;

namespace CanvasDesktop;

internal sealed partial class OverviewManager
{
    private TemporaryDrawerWindow? _temporaryDrawer;
    internal TemporaryDrawerWindow? TemporaryDrawer => _temporaryDrawer;

    private void OnTemporaryWindowsArrived(TemporaryAreaView area)
        => RefreshTemporaryDrawer();

    private void RefreshTemporaryDrawer()
    {
        if (_disposed || _placeholderWorkspace == null) return;
        if (_temporaryDrawer == null)
        {
            _temporaryDrawer = new(_screens.PrimaryWorkingArea);
            _temporaryDrawer.Panel.ShowAreaRequested += ShowTemporaryArea;
            _temporaryDrawer.Panel.PlaceRequested += id => PlaceTemporaryDrawerItem(id, null);
            _temporaryDrawer.Panel.Drag.Dropped += (id, point) => PlaceTemporaryDrawerItem(id, point);
        }
        _temporaryDrawer.SetCanvasOwner(CanvasPanelOwner);
        _temporaryDrawer.Refresh(_screens.PrimaryWorkingArea, _placeholderWorkspace.TemporaryArea,
            _placeholderWorkspace.TemporaryAreaItems,
            _appConfig.ReduceStartupMotion || !System.Windows.SystemParameters.ClientAreaAnimation);
    }

    private void PlaceTemporaryDrawerItem(Guid id, System.Windows.Point? screen)
    {
        if (_placeholderWorkspace == null) return;
        var item = _placeholderWorkspace.TemporaryAreaItems.FirstOrDefault(i => i.Id == id);
        if (item == null) { RefreshTemporaryDrawer(); return; }
        // Do not turn releases on the taskbar or other Canvas panels into canvas placements.
        if (screen is { } p && (!_screens.AllWorkingAreas.Any(r => p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom)
            || InventoryViewport().Reserved!.Any(r => p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom))) return;
        var camera = CurrentMode == OverviewMode.Hidden ? _mainCanvas.Camera : ViewCamera;
        double? x = screen is { } at ? camera.X + at.X / camera.Zoom - item.Bounds.W / 2 : null;
        double? y = screen is { } to ? camera.Y + to.Y / camera.Zoom - item.Bounds.H / 2 : null;
        bool placed = _placeholderWorkspace.PlaceFromTemporaryArea(id, InventoryViewport(), x, y);
        _temporaryDrawer?.Panel.SetHint(placed ? "Placed and kept on the canvas." : _placeholderWorkspace.Status ?? "This window is no longer available.");
        RefreshPlaceholderTitles();
    }

    public void ShowTemporaryArea()
    {
        if (_placeholderWorkspace?.EnsureTemporaryArea() is not { } area) return;
        CancelInertia(); StopWindowNavigation();
        if (IsStartupIntroActive) FinishStartupIntro(refresh: false);
        PauseGuidedTutorial();
        JumpToZone(new(area.Id, area.Name, area.Color, area.Bounds, area.Count) { IsTemporaryArea = true });
        FocusOverview();
    }
}
