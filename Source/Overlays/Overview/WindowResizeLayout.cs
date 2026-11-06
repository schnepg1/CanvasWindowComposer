using System;
using System.Windows.Forms;
using CanvasDesktop.CanvasView;

namespace CanvasDesktop;

[Flags]
internal enum WindowResizeEdge
{
    None = 0,
    Left = 1,
    Right = 2,
    Top = 4,
    Bottom = 8,
    TopLeft = Top | Left,
    TopRight = Top | Right,
    BottomLeft = Bottom | Left,
    BottomRight = Bottom | Right
}

/// <summary>
/// Hit-testing, edge calculation and cursor resolution for resizing windows on the canvas.
/// Allows resizing windows arbitrarily, including exceeding physical screen dimensions.
/// </summary>
internal static class WindowResizeLayout
{
    public const double MinimumWidth = 200;
    public const double MinimumHeight = 100;
    public const double BorderMarginPx = 8.0;
    public const double CornerGripPx = 16.0;

    public static WindowResizeEdge HitTest(CanvasRect localRect, double x, double y)
    {
        double lx = localRect.X, ly = localRect.Y, lw = localRect.Width, lh = localRect.Height;
        if (lw < 20 || lh < 20) return WindowResizeEdge.None;

        bool inOuter = x >= lx - BorderMarginPx && x <= lx + lw + BorderMarginPx &&
                       y >= ly - BorderMarginPx && y <= ly + lh + BorderMarginPx;
        if (!inOuter) return WindowResizeEdge.None;

        bool inInterior = x > lx + BorderMarginPx && x < lx + lw - BorderMarginPx &&
                          y > ly + BorderMarginPx && y < ly + lh - BorderMarginPx;
        if (inInterior) return WindowResizeEdge.None;

        bool cornerLeft = Math.Abs(x - lx) <= CornerGripPx;
        bool cornerRight = Math.Abs(x - (lx + lw)) <= CornerGripPx;
        bool cornerTop = Math.Abs(y - ly) <= CornerGripPx;
        bool cornerBottom = Math.Abs(y - (ly + lh)) <= CornerGripPx;

        if (cornerTop && cornerLeft) return WindowResizeEdge.TopLeft;
        if (cornerTop && cornerRight) return WindowResizeEdge.TopRight;
        if (cornerBottom && cornerLeft) return WindowResizeEdge.BottomLeft;
        if (cornerBottom && cornerRight) return WindowResizeEdge.BottomRight;

        bool isLeft = Math.Abs(x - lx) <= BorderMarginPx || (x < lx && x >= lx - BorderMarginPx);
        bool isRight = Math.Abs(x - (lx + lw)) <= BorderMarginPx || (x > lx + lw && x <= lx + lw + BorderMarginPx);
        bool isTop = Math.Abs(y - ly) <= BorderMarginPx || (y < ly && y >= ly - BorderMarginPx);
        bool isBottom = Math.Abs(y - (ly + lh)) <= BorderMarginPx || (y > ly + lh && y <= ly + lh + BorderMarginPx);

        if (isTop) return WindowResizeEdge.Top;
        if (isBottom) return WindowResizeEdge.Bottom;
        if (isLeft) return WindowResizeEdge.Left;
        if (isRight) return WindowResizeEdge.Right;

        return WindowResizeEdge.None;
    }

    public static bool TryResize(WorldRect original, WindowResizeEdge edge, double dx, double dy, out WorldRect bounds)
    {
        bounds = original;
        if (!double.IsFinite(dx) || !double.IsFinite(dy) || edge == WindowResizeEdge.None) return false;

        bool left = (edge & WindowResizeEdge.Left) != 0;
        bool right = (edge & WindowResizeEdge.Right) != 0;
        bool top = (edge & WindowResizeEdge.Top) != 0;
        bool bottom = (edge & WindowResizeEdge.Bottom) != 0;

        if (left)
        {
            double newW = Math.Max(MinimumWidth, original.W - dx);
            bounds.X = original.X + original.W - newW;
            bounds.W = newW;
        }
        else if (right)
        {
            bounds.W = Math.Max(MinimumWidth, original.W + dx);
        }

        if (top)
        {
            double newH = Math.Max(MinimumHeight, original.H - dy);
            bounds.Y = original.Y + original.H - newH;
            bounds.H = newH;
        }
        else if (bottom)
        {
            bounds.H = Math.Max(MinimumHeight, original.H + dy);
        }

        return true;
    }

    public static Cursor CursorForEdge(WindowResizeEdge edge) => edge switch
    {
        WindowResizeEdge.TopLeft or WindowResizeEdge.BottomRight => Cursors.SizeNWSE,
        WindowResizeEdge.TopRight or WindowResizeEdge.BottomLeft => Cursors.SizeNESW,
        WindowResizeEdge.Top or WindowResizeEdge.Bottom => Cursors.SizeNS,
        WindowResizeEdge.Left or WindowResizeEdge.Right => Cursors.SizeWE,
        _ => Cursors.Default
    };
}
