namespace CanvasDesktop;

internal static class OverviewZoomDefaults
{
    public const double ShortcutZoomOutAmount = 0.15;
    public const double DefaultShortcutFlightDuration = 1.0;
    public const double MinimumShortcutFlightDuration = 0.2;
    public const double MaximumShortcutFlightDuration = 5.0;
    public const double CardEnter = 0.40;
    public const double CardExit = 0.45;
    public const double SimpleCardEnter = 0.10;
    public const double SimpleCardExit = 0.13;
    public const double MaximumZoom = 1.0;
    public const double MagnifierMin = 1.0;
    public const double MagnifierMax = 5.0;
    public const double DistantLodZoomThreshold = 0.25;
    public const double DistantLodProjectedSizeThreshold = 40.0;

    public const long DefaultCardImageBudgetBytes = 48L * 1024 * 1024; // 48 MB
    public const int DefaultCardImageMaxCount = 64;
}
