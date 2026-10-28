using System;

namespace CanvasDesktop;

internal sealed partial class OverviewManager
{
    private TemporaryAreaNotice? _temporaryNotice;

    private void OnTemporaryWindowsArrived(TemporaryAreaView area)
    {
        if (_disposed) return;
        _temporaryNotice ??= new TemporaryAreaNotice(ShowTemporaryArea);
        _temporaryNotice.ShowArrivals(area.Count, _screens.PrimaryWorkingArea);
    }

    public void ShowTemporaryArea()
    {
        if (_placeholderWorkspace?.TemporaryArea is not { } area) return;
        CancelInertia(); StopWindowNavigation();
        if (IsStartupIntroActive) FinishStartupIntro(refresh: false);
        PauseGuidedTutorial();
        JumpToZone(new(Guid.Empty, "Temporary area", "#94A3B8", area.Bounds, area.Count));
        FocusOverview();
    }
}
