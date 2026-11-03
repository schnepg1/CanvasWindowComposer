using System;
using System.Drawing;

namespace CanvasDesktop;

/// <summary>Stationary-pointer dwell, independent of the overview's rendering tiers.</summary>
internal sealed class CanvasPeekHover
{
    public IntPtr Candidate { get; private set; }
    public long Since => _since;
    private Point _anchor;
    private long _since;

    public void Observe(IntPtr candidate, Point pointer, long now, int tolerance)
    {
        if (candidate == IntPtr.Zero) { Reset(); return; }
        if (candidate != Candidate || Math.Abs((long)pointer.X - _anchor.X) > tolerance
            || Math.Abs((long)pointer.Y - _anchor.Y) > tolerance)
        {
            Candidate = candidate;
            _anchor = pointer;
            _since = now;
        }
    }

    public bool Ready(long now, int delayMs) => Candidate != IntPtr.Zero && now - _since >= delayMs;
    public void Reset() => Candidate = IntPtr.Zero;
}

internal static class CanvasPeekLayout
{
    public const int DismissDelayMs = 400;
    public const int AnimationMs = 200;
    public const double MaximumHoverZoom = .80;

    public static Rectangle Place(Rectangle source, Size native, Rectangle workArea, int dpi)
    {
        double scale = dpi / 96.0;
        int margin = Math.Max(4, (int)Math.Ceiling(12 * scale));
        var available = Rectangle.Inflate(workArea, -Math.Min(margin, Math.Max(0, workArea.Width / 8)),
            -Math.Min(margin, Math.Max(0, workArea.Height / 8)));
        int header = HeaderHeight(dpi);
        int maxW = Math.Max(1, Math.Min(available.Width, (int)(workArea.Width * .88)));
        int maxH = Math.Max(1, Math.Min(available.Height, (int)(workArea.Height * .88)));
        var fitted = Fit(native, new(0, 0, Math.Max(1, maxW - 2), Math.Max(1, maxH - header - 2)));
        int width = Math.Min(maxW, Math.Max((int)(410 * scale), fitted.Width + 2));
        int height = Math.Min(maxH, fitted.Height + header + 2);
        int x = source.Right + margin;
        if (x + width > available.Right) x = source.Left - width - margin;
        if (x < available.Left) x = source.Left;
        int y = source.Top;
        if (source.Right + margin + width > available.Right && source.Left - width - margin < available.Left)
        {
            if (source.Bottom + margin + height <= available.Bottom) y = source.Bottom + margin;
            else if (source.Top - height - margin >= available.Top) y = source.Top - height - margin;
        }
        return new(Math.Clamp(x, available.Left, Math.Max(available.Left, available.Right - width)),
            Math.Clamp(y, available.Top, Math.Max(available.Top, available.Bottom - height)), width, height);
    }

    // Title line + one chrome row (buttons with the "?" control-info tooltip):
    // inner padding top/bottom.
    public static int HeaderHeight(int dpi) => (int)Math.Ceiling(66 * dpi / 96.0);

    /// <summary>Letterbox without stretching or upscaling the native pixels.</summary>
    public static Rectangle Fit(Size source, Rectangle destination)
    {
        if (source.Width <= 0 || source.Height <= 0 || destination.Width <= 0 || destination.Height <= 0) return Rectangle.Empty;
        double scale = Math.Min(1, Math.Min(destination.Width / (double)source.Width, destination.Height / (double)source.Height));
        int w = Math.Max(1, (int)Math.Floor(source.Width * scale));
        int h = Math.Max(1, (int)Math.Floor(source.Height * scale));
        return new(destination.X + (destination.Width - w) / 2, destination.Y + (destination.Height - h) / 2, w, h);
    }

    public static Rectangle Animate(Rectangle from, Rectangle to, double progress)
    {
        double t = 1 - Math.Pow(1 - Math.Clamp(progress, 0, 1), 3);
        int Lerp(int a, int b) => (int)Math.Round(a + (b - (double)a) * t);
        return new(Lerp(from.X, to.X), Lerp(from.Y, to.Y), Math.Max(1, Lerp(from.Width, to.Width)), Math.Max(1, Lerp(from.Height, to.Height)));
    }
}
