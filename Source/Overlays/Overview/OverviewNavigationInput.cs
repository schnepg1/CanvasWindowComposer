namespace CanvasDesktop;

/// <summary>Normalizes Windows wheel messages, including precision touchpad scroll and pinch.</summary>
internal static class OverviewNavigationInput
{
    public const int MouseWheelMessage = 0x020A;
    public const int MouseHorizontalWheelMessage = 0x020E;
    private const double WheelDelta = 120.0;

    public static (int ScreenX, int ScreenY, int Delta, bool Horizontal, bool Control) DecodeWheel(
        int message, nint wParam, nint lParam)
    {
        long word = wParam;
        long position = lParam;
        return (unchecked((short)(position & 0xffff)),
            unchecked((short)((position >> 16) & 0xffff)),
            unchecked((short)((word >> 16) & 0xffff)),
            message == MouseHorizontalWheelMessage,
            (word & 0x0008) != 0);
    }

    // Windows sends two-finger scrolling as vertical/horizontal wheel messages.
    // A precision touchpad pinch arrives as Ctrl+wheel. Preserve fractional
    // deltas so smooth scrolling does not stall at low speed.
    public static (double PanX, double PanY, double ZoomNotches) TranslateWheel(
        int delta, bool horizontal, bool zoomModifier)
    {
        if (zoomModifier) return (0, 0, horizontal ? 0 : delta / WheelDelta);
        return horizontal ? (-delta, 0, 0) : (0, delta, 0);
    }
}
