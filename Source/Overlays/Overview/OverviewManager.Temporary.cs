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
            AttachDrawerDragFeedback(_temporaryDrawer.Panel.Drag, inventory: false);
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
        if (!TryDrawerDropTarget(id, false, screen, out var view, out var x, out var y)) return;
        bool placed = _placeholderWorkspace.PlaceFromTemporaryArea(id, view, x, y);
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
