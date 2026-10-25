using System;
using System.Collections.Generic;
using System.Windows;

namespace CanvasDesktop;

internal sealed record PlaceholderAnnotation(IntPtr Handle, string Title, WorldRect Bounds, bool Priority = false,
    System.Windows.Media.ImageSource? Image = null, IReadOnlyList<WorldRect>? Occluders = null,
    double Opacity = 1, double? ImageOpacity = null, double? LabelOpacity = null,
    System.Windows.Media.ImageSource? Logo = null, string? LaunchStatus = null);
internal sealed record PlaceholderLabel(PlaceholderAnnotation Card, Rect Rect, double FontSize);

/// <summary>Card-local titles: bounds, padding and type all scale with the camera.</summary>
internal static class PlaceholderLabelLayout
{
    public static IReadOnlyList<PlaceholderLabel> Get(IReadOnlyList<PlaceholderAnnotation> cards,
        double camX, double camY, double zoom, ScreenRect monitor, double dpiX, double dpiY)
    {
        var result = new List<PlaceholderLabel>();
        if (!double.IsFinite(zoom) || zoom <= 0 || dpiX <= 0 || dpiY <= 0) return result;
        double viewWidth = monitor.Width / dpiX, viewHeight = monitor.Height / dpiY;
        var view = new CanvasViewTransform(camX, camY, zoom,
            new(monitor.X, monitor.Y, monitor.Width, monitor.Height), dpiX, dpiY);
        foreach (var card in cards)
        {
            if (card.Logo != null
                || (card.LabelOpacity ?? (zoom < WindowManager.OverviewLivePreviewZoom ? 1 : 0)) <= 0
                || card.Opacity <= 0) continue;
            var b = card.Bounds;
            if (!WorkspaceGeometry.ValidBounds(b)) continue;
            var projected = view.WorldToDip(new CanvasRect(b.X, b.Y, b.W, b.H));
            double x = projected.X, y = projected.Y, w = projected.Width, h = projected.Height;
            if (x >= viewWidth || y >= viewHeight || x + w <= 0 || y + h <= 0 || w < 1 || h < 1) continue;
            double paddingX = Math.Min(24, b.W * .08) * zoom / dpiX;
            double paddingY = Math.Min(24, b.H * .08) * zoom / dpiY;
            var rect = new Rect(x + paddingX, y + paddingY, w - 2 * paddingX, h - 2 * paddingY);
            double fontSize = Math.Min(84 * zoom / dpiY, rect.Height / 3.6);
            // Do not clamp to the monitor or arbitrate collisions: each title
            // belongs to its card, including when that card is partly offscreen.
            result.Add(new(card, rect, fontSize));
        }
        return result;
    }
}
