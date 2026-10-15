using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace CanvasDesktop;

/// <summary>Project-area painting is independent of placeholder and HUD painting.</summary>
internal sealed class ZoneAnnotationLayer : IOverviewAnnotationLayer
{
    public void Draw(DrawingContext dc, OverviewAnnotationContext context)
    {
        foreach (var zone in context.Frame.Zones)
            DrawZone(dc, zone.Bounds, zone.Name + $" · {zone.MemberCount}",
                (Color)ColorConverter.ConvertFromString(zone.Color), context, false);
        foreach (var ghost in context.Frame.Ghosts)
            DrawZone(dc, ghost, "", Color.FromRgb(148, 163, 184), context, true);
        if (context.Frame.Preview is { } bounds)
            DrawZone(dc, bounds, "New project area", Color.FromRgb(96, 165, 250), context, true);
    }
    private void DrawZone(DrawingContext dc, WorldRect b, string name, Color color, OverviewAnnotationContext context, bool preview)
    {
        var rect = context.Project(b);
        if (!rect.IntersectsWith(new Rect(context.Size))) return;
        var brush = new SolidColorBrush(color);
        var pen = new Pen(brush, preview ? 2.5 : 1.5);
        if (preview) pen.DashStyle = DashStyles.Dash;
        // Only the drawing preview has a fill; saved zones don't tint app contents.
        dc.DrawRoundedRectangle(preview ? new SolidColorBrush(Color.FromArgb(22, color.R, color.G, color.B)) : null,
            pen, rect, 8, 8);
        if (name.Length == 0) return;
        var monitor = new ScreenRect(context.Frame.Screen.X, context.Frame.Screen.Y, context.Frame.Screen.Width, context.Frame.Screen.Height);
        if (!preview)
            foreach (var handle in ZoneResizeLayout.Get(b, context.View.CameraX, context.View.CameraY, context.View.Zoom, monitor, context.Dpi.DpiScaleX, context.Dpi.DpiScaleY))
                dc.DrawRoundedRectangle(brush, new Pen(Brushes.White, 1),
                    new Rect(handle.Rect.X / context.Dpi.DpiScaleX, handle.Rect.Y / context.Dpi.DpiScaleY,
                        handle.Rect.Width / context.Dpi.DpiScaleX, handle.Rect.Height / context.Dpi.DpiScaleY), 2, 2);
        if (ZoneLabelLayout.Get(b, context.View.CameraX, context.View.CameraY, context.View.Zoom, monitor, context.Dpi.DpiScaleX, context.Dpi.DpiScaleY) is not { } header) return;
        var text = context.Text(name, 13, Brushes.White);
        text.MaxTextWidth = Math.Max(1, header.Width / context.Dpi.DpiScaleX - 20);
        text.MaxTextHeight = 20;
        text.Trimming = TextTrimming.CharacterEllipsis;
        double labelX = header.X / context.Dpi.DpiScaleX, labelY = header.Y / context.Dpi.DpiScaleY;
        var label = new Rect(labelX, labelY, header.Width / context.Dpi.DpiScaleX, header.Height / context.Dpi.DpiScaleY);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(23, 29, 41)), pen, label, 6, 6);
        dc.DrawText(text, new Point(labelX + 10, labelY + 5));
    }

}
