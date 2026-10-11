using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace CanvasDesktop;

/// <summary>
/// Owned, non-activating annotation surface above DWM thumbnails. No input is
/// intercepted: the native transparent style forwards it to the overview HWND.
/// Coordinates arrive in physical pixels and are converted to WPF DIPs per monitor.
/// </summary>
internal sealed class ZoneAnnotationWindow : Window
{
    private readonly ZoneDrawing _drawing = new();
    private System.Drawing.Rectangle _bounds;
    public ZoneAnnotationWindow(IntPtr owner)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        Topmost = true;
        Content = _drawing;
        var interop = new WindowInteropHelper(this) { Owner = owner };
        var hwnd = (HWND)interop.EnsureHandle();
        int style = PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        PInvoke.SetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, style | 0x20 | 0x80 | 0x08000000);
    }

    public void Update(IReadOnlyList<ZoneView> zones, double x, double y, double zoom,
        System.Drawing.Rectangle screen, WorldRect? preview, bool drawing, IReadOnlyList<WorldRect>? ghosts = null)
    {
        _drawing.Update(zones, x, y, zoom, screen, preview, drawing, ghosts);
        bool opening = !IsVisible;
        if (opening) Show();
        if (opening || _bounds != screen)
        {
            _bounds = screen;
            PInvoke.SetWindowPos((HWND)new WindowInteropHelper(this).Handle, (HWND)new IntPtr(-1),
                screen.X, screen.Y, screen.Width, screen.Height,
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER);
        }
    }

    private sealed class ZoneDrawing : FrameworkElement
    {
        private IReadOnlyList<ZoneView> _zones = Array.Empty<ZoneView>();
        private double _x, _y, _zoom;
        private System.Drawing.Rectangle _screen;
        private WorldRect? _preview;
        private bool _drawing;
        private IReadOnlyList<WorldRect> _ghosts = Array.Empty<WorldRect>();

        public void Update(IReadOnlyList<ZoneView> zones, double x, double y, double zoom,
            System.Drawing.Rectangle screen, WorldRect? preview, bool drawing, IReadOnlyList<WorldRect>? ghosts)
        {
            _zones = zones; _x = x; _y = y; _zoom = zoom; _screen = screen;
            _preview = preview; _drawing = drawing;
            _ghosts = ghosts ?? Array.Empty<WorldRect>();
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            var dpi = VisualTreeHelper.GetDpi(this);
            dc.PushClip(new RectangleGeometry(new Rect(RenderSize)));
            foreach (var zone in _zones)
                DrawZone(dc, zone.Bounds, zone.Name + $" · {zone.MemberCount}", (Color)ColorConverter.ConvertFromString(zone.Color), dpi, false);
            foreach (var ghost in _ghosts)
                DrawZone(dc, ghost, "", Color.FromRgb(148, 163, 184), dpi, true);
            if (_preview is { } bounds)
                DrawZone(dc, bounds, "New project area", Color.FromRgb(96, 165, 250), dpi, true);

            // Fixed-size hint remains readable at every zoom level.
            string hint = _drawing ? "Drag to mark a project area  ·  Esc to cancel"
                : "Drag name: move group   Corners: resize   Ctrl+Z/Y: undo/redo   Z: areas   P: projects";
            var text = Text(hint, 13, Brushes.White, dpi);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(240, 23, 29, 41)), null,
                new Rect(20, 20, text.Width + 28, 36), 10, 10);
            dc.DrawText(text, new Point(34, 28));
            dc.Pop();
        }

        private void DrawZone(DrawingContext dc, WorldRect b, string name, Color color, DpiScale dpi, bool preview)
        {
            var rect = new Rect(((b.X - _x) * _zoom - _screen.X) / dpi.DpiScaleX,
                ((b.Y - _y) * _zoom - _screen.Y) / dpi.DpiScaleY,
                b.W * _zoom / dpi.DpiScaleX, b.H * _zoom / dpi.DpiScaleY);
            if (!rect.IntersectsWith(new Rect(RenderSize))) return;
            var brush = new SolidColorBrush(color);
            var pen = new Pen(brush, preview ? 2.5 : 1.5);
            if (preview) pen.DashStyle = DashStyles.Dash;
            // Only the drawing preview has a fill; saved zones don't tint app contents.
            dc.DrawRoundedRectangle(preview ? new SolidColorBrush(Color.FromArgb(22, color.R, color.G, color.B)) : null,
                pen, rect, 8, 8);
            if (name.Length == 0) return;
            var monitor = new ScreenRect(_screen.X, _screen.Y, _screen.Width, _screen.Height);
            if (!preview)
                foreach (var handle in ZoneResizeLayout.Get(b, _x, _y, _zoom, monitor, dpi.DpiScaleX, dpi.DpiScaleY))
                    dc.DrawRoundedRectangle(brush, new Pen(Brushes.White, 1),
                        new Rect(handle.Rect.X / dpi.DpiScaleX, handle.Rect.Y / dpi.DpiScaleY,
                            handle.Rect.Width / dpi.DpiScaleX, handle.Rect.Height / dpi.DpiScaleY), 2, 2);
            if (ZoneLabelLayout.Get(b, _x, _y, _zoom, monitor, dpi.DpiScaleX, dpi.DpiScaleY) is not { } header) return;
            var text = Text(name, 13, Brushes.White, dpi);
            text.MaxTextWidth = Math.Max(1, header.Width / dpi.DpiScaleX - 20);
            text.MaxTextHeight = 20;
            text.Trimming = TextTrimming.CharacterEllipsis;
            double labelX = header.X / dpi.DpiScaleX, labelY = header.Y / dpi.DpiScaleY;
            var label = new Rect(labelX, labelY, header.Width / dpi.DpiScaleX, header.Height / dpi.DpiScaleY);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(23, 29, 41)), pen, label, 6, 6);
            dc.DrawText(text, new Point(labelX + 10, labelY + 5));
        }

        private static FormattedText Text(string value, double size, Brush brush, DpiScale dpi)
            => new(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), size, brush, dpi.PixelsPerDip);
    }
}
