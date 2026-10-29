using System;
using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using FormsTimer = System.Windows.Forms.Timer;

namespace CanvasDesktop;

internal static class InventoryFlight
{
    public static (double X, double Y, double Scale) Sample(double t, double fromX, double fromY, double toX, double toY)
    {
        t = Math.Clamp(t, 0, 1);
        double ease = 1 - Math.Pow(1 - t, 3);
        return (fromX + (toX - fromX) * ease, fromY + (toY - fromY) * ease - Math.Sin(t * Math.PI) * 90,
            1 - .9 * ease);
    }
}

/// <summary>A passive pickup effect above the desktop. It never receives mouse or keyboard input.</summary>
internal sealed class InventoryFlightWindow : Window, IDisposable
{
    private readonly FlightSurface _surface = new();
    private readonly FormsTimer _timer = new() { Interval = 16 };
    private long _started;
    private bool _disposed;
    private double _duration;

    public InventoryFlightWindow()
    {
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false;
        ShowActivated = false; Focusable = false; IsHitTestVisible = false; Topmost = true; Content = _surface;
        var hwnd = (HWND)new WindowInteropHelper(this).EnsureHandle();
        int style = PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        PInvoke.SetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, style | 0x20 | 0x80 | 0x08000000);
        _timer.Tick += (_, _) =>
        {
            _surface.Progress = Math.Min(1, (Environment.TickCount64 - _started) / _duration);
            _surface.InvalidateVisual();
            if (_surface.Progress >= 1) { _timer.Stop(); Hide(); }
        };
    }

    public void Fly(string title, WindowRect source, ScreenRect target, ScreenRect screen, bool reduceMotion)
    {
        if (_disposed) return;
        _surface.Title = title; _surface.Source = source; _surface.Target = target; _surface.Screen = screen;
        _surface.Reduced = reduceMotion; _surface.Progress = 0; _started = Environment.TickCount64; _duration = reduceMotion ? 220 : 720;
        if (!IsVisible) Show();
        PInvoke.SetWindowPos((HWND)new WindowInteropHelper(this).Handle, (HWND)new IntPtr(-1), screen.X, screen.Y, screen.Width, screen.Height,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER);
        _surface.InvalidateVisual(); _timer.Start();
    }

    public void Dispose() { if (_disposed) return; _disposed = true; _timer.Dispose(); Close(); }

    private sealed class FlightSurface : FrameworkElement
    {
        public string Title = "";
        public WindowRect Source;
        public ScreenRect Target, Screen;
        public double Progress;
        public bool Reduced;

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            var dpi = VisualTreeHelper.GetDpi(this);
            double sx = Math.Clamp((Source.X + Source.W / 2.0 - Screen.X) / dpi.DpiScaleX, 40, Math.Max(40, ActualWidth - 40));
            double sy = Math.Clamp((Source.Y + Source.H / 2.0 - Screen.Y) / dpi.DpiScaleY, 40, Math.Max(40, ActualHeight - 40));
            double tx = (Target.X + 34 - Screen.X) / dpi.DpiScaleX, ty = (Target.Y + 37 - Screen.Y) / dpi.DpiScaleY;
            var sample = Reduced ? (X: tx, Y: ty, Scale: .12) : InventoryFlight.Sample(Progress, sx, sy, tx, ty);
            var gold = BackpackPanel.Ink("#F3D39B");
            if (!Reduced)
                for (int i = 1; i <= 5; i++)
                {
                    var trail = InventoryFlight.Sample(Math.Max(0, Progress - i * .025), sx, sy, tx, ty);
                    dc.PushOpacity((1 - Progress) * (6 - i) / 12.0);
                    dc.DrawEllipse(gold, null, new(trail.X, trail.Y), 3, 3); dc.Pop();
                }
            dc.PushOpacity(Progress < .85 ? 1 : (1 - Progress) / .15);
            dc.PushTransform(new TranslateTransform(sample.X, sample.Y));
            dc.PushTransform(new RotateTransform(-10 * Math.Sin(Progress * Math.PI)));
            dc.PushTransform(new ScaleTransform(sample.Scale, sample.Scale));
            dc.DrawRoundedRectangle(BackpackPanel.Ink("#213C4C"), new Pen(gold, 2), new Rect(-116, -43, 232, 86), 10, 10);
            dc.DrawLine(new Pen(BackpackPanel.Ink("#70D9C4"), 3), new(-100, -25), new(-68, -25));
            var text = new FormattedText(Title, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI Semibold"), 13, Brushes.White, dpi.PixelsPerDip)
                { MaxTextWidth = 200, MaxTextHeight = 35, Trimming = TextTrimming.CharacterEllipsis };
            dc.DrawText(text, new(-100, -11)); dc.Pop(); dc.Pop(); dc.Pop(); dc.Pop();
            if (Progress > .72)
                for (int i = 0; i < 8; i++)
                {
                    double phase = (Progress - .72) / .28, angle = i * Math.PI / 4, radius = 8 + phase * 30;
                    dc.PushOpacity(1 - phase);
                    dc.DrawEllipse(gold, null, new(tx + Math.Cos(angle) * radius, ty + Math.Sin(angle) * radius), 2, 2); dc.Pop();
                }
        }
    }
}
