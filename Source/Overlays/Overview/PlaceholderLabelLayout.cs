using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace CanvasDesktop;

internal sealed record PlaceholderAnnotation(IntPtr Handle, string Title, WorldRect Bounds, bool Priority = false,
    System.Windows.Media.ImageSource? Image = null, IReadOnlyList<WorldRect>? Occluders = null,
    double Opacity = 1, double? ImageOpacity = null, double? LabelOpacity = null);
internal sealed record PlaceholderLabel(PlaceholderAnnotation Card, Rect Rect, double FontSize);

/// <summary>Display-sized labels: project positions with the camera, never shrink text below 22 DIPs.</summary>
internal static class PlaceholderLabelLayout
{
    public static IReadOnlyList<PlaceholderLabel> Get(IReadOnlyList<PlaceholderAnnotation> cards,
        double camX, double camY, double zoom, ScreenRect monitor, double dpiX, double dpiY,
        IReadOnlyList<Rect>? reserved = null)
    {
        var result = new List<PlaceholderLabel>();
        if (!double.IsFinite(zoom) || zoom <= 0 || dpiX <= 0 || dpiY <= 0) return result;
        double viewWidth = monitor.Width / dpiX, viewHeight = monitor.Height / dpiY;
        var view = new CanvasViewTransform(camX, camY, zoom,
            new(monitor.X, monitor.Y, monitor.Width, monitor.Height), dpiX, dpiY);
        if (viewWidth < 40 || viewHeight < 40) return result;
        double fontSize = Math.Clamp(44 * zoom, 22, 32);
        foreach (var card in cards.OrderByDescending(c => c.Priority))
        {
            if ((card.LabelOpacity ?? (zoom < WindowManager.OverviewLivePreviewZoom ? 1 : 0)) <= 0
                || card.Opacity <= 0) continue;
            var b = card.Bounds;
            var projected = view.WorldToDip(new CanvasRect(b.X, b.Y, b.W, b.H));
            double x = projected.X, y = projected.Y, w = projected.Width, h = projected.Height;
            if (!WorkspaceGeometry.ValidBounds(b) || x >= viewWidth || y >= viewHeight || x + w <= 0 || y + h <= 0
                || (!card.Priority && (w < 8 || h < 8))) continue;
            double width = Math.Min(viewWidth - 16, Math.Clamp(card.Title.Length * fontSize * .55 + 28, 130, 320));
            double height = Math.Min(viewHeight - 16, fontSize * 2.5 + 16);
            var rect = new Rect(Math.Clamp(x + (w - width) / 2, 8, viewWidth - width - 8),
                Math.Clamp(y + (h - height) / 2, 8, viewHeight - height - 8), width, height);
            // Selected/hovered cards are placed first. Other labels yield rather
            // than growing into an unreadable pile at distant zoom levels.
            if (reserved != null && reserved.Any(r => r.IntersectsWith(rect))) continue;
            var clearance = rect; clearance.Inflate(4, 4);
            if (result.Any(label => label.Rect.IntersectsWith(clearance))) continue;
            result.Add(new(card, rect, fontSize));
        }
        return result;
    }
}
