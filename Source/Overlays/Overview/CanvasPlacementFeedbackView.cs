using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace CanvasDesktop;

/// <summary>Two small passive overlays, so neither mouse capture nor foreground focus changes during a drag.</summary>
internal sealed class CanvasPlacementFeedbackView(IScreens screens, bool clickToPlace = false) : ICanvasPlacementFeedbackView
{
    private PassiveDragWindow? _pointer, _landing;
    private readonly CanvasPlacementBadgeSurface _badge = new();
    internal Window? PointerWindow => _pointer;
    internal Window? LandingWindow => _landing;

    public void ShowPointer(Point point, string title, bool outside, bool canDrop)
    {
        _pointer ??= new(_badge);
        var screen = screens.AllBounds.FirstOrDefault(s => point.X >= s.Left && point.X < s.Right && point.Y >= s.Top && point.Y < s.Bottom);
        if (screen.Width <= 0) screen = screens.PrimaryBounds;
        _badge.Update(title, outside, canDrop, clickToPlace);
        // Move to the target monitor first so WPF reports its current DPI before sizing the badge.
        _pointer.MoveTo(point);
        var dpi = VisualTreeHelper.GetDpi(_pointer);
        _pointer.Present(BadgeBounds(point, screen, dpi.DpiScaleX, dpi.DpiScaleY));
    }

    internal static ScreenRect BadgeBounds(Point point, ScreenRect screen, double dpiX, double dpiY)
    {
        int width = (int)Math.Ceiling(180 * dpiX), height = (int)Math.Ceiling(48 * dpiY);
        int dx = (int)Math.Ceiling(18 * dpiX), dy = (int)Math.Ceiling(20 * dpiY);
        int x = (int)point.X + dx, y = (int)point.Y + dy;
        if (x + width > screen.Right) x = (int)point.X - width - dx;
        if (y + height > screen.Bottom) y = (int)point.Y - height - dy;
        return new(Math.Clamp(x, screen.Left, Math.Max(screen.Left, screen.Right - width)),
            Math.Clamp(y, screen.Top, Math.Max(screen.Top, screen.Bottom - height)), width, height);
    }

    public void ShowLanding(WindowRect? landing)
    {
        if (landing is not { } rect) { _landing?.Hide(); return; }
        _landing ??= new(new CanvasLandingSurface());
        _landing.Present(new(rect.X - 2, rect.Y - 2, rect.W + 4, rect.H + 4));
    }

    public void Hide() { _pointer?.Hide(); _landing?.Hide(); }
    public void Dispose() { _pointer?.Close(); _landing?.Close(); }

    private sealed class PassiveDragWindow : Window
    {
        public PassiveDragWindow(FrameworkElement surface)
        {
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false;
            ShowActivated = false; Focusable = false; IsHitTestVisible = false; Topmost = true;
            Width = 1; Height = 1; Content = surface;
            var hwnd = (HWND)new WindowInteropHelper(this).EnsureHandle();
            int style = PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
            PInvoke.SetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (style | 0x20 | 0x80 | 0x08000000) & ~0x40000);
        }

        public void MoveTo(Point point)
            => PInvoke.SetWindowPos((HWND)new WindowInteropHelper(this).Handle, (HWND)new IntPtr(-1), (int)point.X, (int)point.Y, 0, 0,
                SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER);

        public void Present(ScreenRect rect)
        {
            if (!IsVisible) Show();
            PInvoke.SetWindowPos((HWND)new WindowInteropHelper(this).Handle, (HWND)new IntPtr(-1), rect.X, rect.Y, rect.Width, rect.Height,
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER);
        }
    }
}

internal sealed class CanvasPlacementBadgeSurface : FrameworkElement
{
    private string _title = "", _status = "";
    private bool _canDrop;

    public void Update(string title, bool outside, bool canDrop, bool clickToPlace = false)
    {
        string status = !outside ? "Drag onto canvas" : canDrop
            ? (clickToPlace ? "Click to place · Esc: cancel" : "Release to place") : "No clear landing space";
        if (_title == title && _status == status && _canDrop == canDrop) return;
        _title = title; _status = status; _canDrop = canDrop; InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var accent = BackpackPanel.Ink(_canDrop ? "#97EDBD" : "#EAC487");
        dc.DrawRoundedRectangle(BackpackPanel.Ink("#182E3A"), new Pen(accent, 1), new Rect(.5, .5, Math.Max(0, ActualWidth - 1), Math.Max(0, ActualHeight - 1)), 7, 7);
        dc.DrawRoundedRectangle(BackpackPanel.Ink("#294A58"), new Pen(accent, 1.5), new Rect(12, 14, 24, 20), 2, 2);
        dc.DrawLine(new Pen(accent, 1.5), new(12, 20), new(36, 20));
        Text(_title, 12, Brushes.White, 7);
        Text(_status, 10, accent, 26);
        void Text(string value, double size, Brush brush, double y)
        {
            var text = new FormattedText(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
                { MaxTextWidth = 126, MaxTextHeight = 18, Trimming = TextTrimming.CharacterEllipsis };
            dc.DrawText(text, new(46, y));
        }
    }
}

internal sealed class CanvasLandingSurface : FrameworkElement
{
    protected override void OnRender(DrawingContext dc)
        => dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(25, 130, 235, 174)), new Pen(BackpackPanel.Ink("#97EDBD"), 2),
            new Rect(2, 2, Math.Max(0, ActualWidth - 4), Math.Max(0, ActualHeight - 4)), 4, 4);
}
