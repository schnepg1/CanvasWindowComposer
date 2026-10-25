namespace CanvasDesktop.Tests;

internal sealed class FakeAppConfig : IAppConfig
{
    public bool DisableSearch { get; set; }
    public bool DisableAltPan { get; set; }
    public bool DisableGreedyDraw { get; set; }
    public bool EnableStartupIntro { get; set; } = true;
    public bool ReduceStartupMotion { get; set; }
    public bool EnableAutomaticMinimization { get; set; }
    public bool LowQualityMode { get; set; }
    public bool ShowTaskbarInCanvas { get; set; } = true;
    public bool AnimateWindowNavigation { get; set; } = true;
    public bool ShowScreenFixedWindowsDuringPan { get; set; } = true;
    public bool DisableMouseCurve { get; set; }
    public bool DisableZoomHotkey { get; set; }
    public bool EnableLivePreviewsTier { get; set; } = true;
    public bool EnableDetailedCardsTier { get; set; } = true;
    public bool EnableSimpleCardsTier { get; set; } = true;
    public double OverviewCardEnterZoom { get; set; } = OverviewZoomDefaults.CardEnter;
    public double OverviewCardExitZoom { get; set; } = OverviewZoomDefaults.CardExit;
    public double OverviewSimpleCardEnterZoom { get; set; } = OverviewZoomDefaults.SimpleCardEnter;
    public double OverviewSimpleCardExitZoom { get; set; } = OverviewZoomDefaults.SimpleCardExit;
}
