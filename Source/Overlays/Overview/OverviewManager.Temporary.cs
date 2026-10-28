using System;

namespace CanvasDesktop;

internal sealed partial class OverviewManager
{
    private TemporaryAreaNotice? _temporaryNotice;

    private void OnTemporaryWindowsArrived(TemporaryAreaView area)
    {
        if (_disposed) return;
        _temporaryNotice ??= new TemporaryAreaNotice(ShowTemporaryArea);
        _temporaryNotice.ShowArrivals(area.Count, _screens.PrimaryWorkingArea, area.Name);
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
