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
    public bool ShowScreenFixedWindowsDuringPan { get; set; } = true;
    public bool DisableMouseCurve { get; set; }
    public bool DisableZoomHotkey { get; set; }
    public bool EnableLivePreviewsTier { get; set; } = true;
    public bool EnableDetailedCardsTier { get; set; } = true;
    public bool EnableSimpleCardsTier { get; set; } = true;
    public double OverviewCardEnterZoom { get; set; } = 0.60;
    public double OverviewCardExitZoom { get; set; } = 0.70;
    public double OverviewSimpleCardEnterZoom { get; set; } = 0.20;
    public double OverviewSimpleCardExitZoom { get; set; } = 0.26;
}
