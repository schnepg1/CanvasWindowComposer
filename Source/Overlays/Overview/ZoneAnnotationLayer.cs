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
        {
            const string legacyPrefix = "AI Mission Control: ";
            string name = zone.IsMission && zone.Name.StartsWith(legacyPrefix, StringComparison.Ordinal)
                ? zone.Name[legacyPrefix.Length..] : zone.Name;
            if (!zone.IsMission) name += (zone.IsTemporaryArea ? " · temporary windows" : "") + $" · {zone.MemberCount}";
            DrawZone(dc, zone.Bounds, name, (Color)ColorConverter.ConvertFromString(zone.Color), context,
                false, zone.IsTemporaryArea, context.Frame.SelectedZoneId == zone.Id, zone.IsMission, zone.MissionPositionLocked);
        }
        foreach (var ghost in context.Frame.Ghosts)
            DrawZone(dc, ghost, "", Color.FromRgb(148, 163, 184), context, true);
        if (context.Frame.Preview is { } bounds)
            DrawZone(dc, bounds, "New project area", Color.FromRgb(96, 165, 250), context, true);
    }
    private void DrawZone(DrawingContext dc, WorldRect b, string name, Color color, OverviewAnnotationContext context,
        bool preview, bool temporary = false, bool isSelected = false, bool mission = false, bool locked = false)
    {
        var rect = context.Project(b);
        if (!rect.IntersectsWith(new Rect(context.Size))) return;
        var brush = new SolidColorBrush(color);
        var pen = new Pen(brush, preview || mission ? 2.5 : (isSelected ? 2.0 : 1.5));
        if (preview || temporary) pen.DashStyle = DashStyles.Dash;
        // Only the drawing preview has a fill; saved zones don't tint app contents.
        dc.DrawRoundedRectangle(preview ? new SolidColorBrush(Color.FromArgb(22, color.R, color.G, color.B)) : null,
            pen, rect, 8, 8);
        if (name.Length == 0) return;
        var monitor = new ScreenRect(context.Frame.Screen.X, context.Frame.Screen.Y, context.Frame.Screen.Width, context.Frame.Screen.Height);
        if (!preview && isSelected && !mission)
            foreach (var handle in ZoneResizeLayout.Get(b, context.View.CameraX, context.View.CameraY, context.View.Zoom, monitor, context.Dpi.DpiScaleX, context.Dpi.DpiScaleY))
                dc.DrawRoundedRectangle(brush, new Pen(Brushes.White, 1),
                    new Rect(handle.Rect.X / context.Dpi.DpiScaleX, handle.Rect.Y / context.Dpi.DpiScaleY,
                        handle.Rect.Width / context.Dpi.DpiScaleX, handle.Rect.Height / context.Dpi.DpiScaleY), 2, 2);
        if (ZoneLabelLayout.Get(b, context.View.CameraX, context.View.CameraY, context.View.Zoom, monitor, context.Dpi.DpiScaleX, context.Dpi.DpiScaleY) is not { } header) return;
        double labelX = header.X / context.Dpi.DpiScaleX, labelY = header.Y / context.Dpi.DpiScaleY;
        var label = new Rect(labelX, labelY, header.Width / context.Dpi.DpiScaleX, header.Height / context.Dpi.DpiScaleY);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(23, 29, 41)), pen, label, 6, 6);
        double left = 10;
        if (mission)
        {
            if (label.Width < 78) name = locked ? "Locked" : "Mission";
            else if (!locked)
                for (int row = 0; row < 3; row++)
                    for (int column = 0; column < 2; column++)
                        dc.DrawEllipse(brush, null, new(labelX + 9 + column * 4, labelY + 10 + row * 4), 1, 1);
            string badge = locked ? "Mission · Locked" : "Mission";
            var badgeText = context.Text(badge, 11, Brushes.White);
            double badgeWidth = badgeText.Width + 12;
            if (badgeWidth + 35 < label.Width)
            {
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(61, 42, 100)), null,
                    new Rect(labelX + 21, labelY + 4, badgeWidth, 20), 4, 4);
                dc.DrawText(badgeText, new Point(labelX + 27, labelY + 6));
                left = 29 + badgeWidth;
            }
            else if (label.Width >= 78)
            {
                name = badge + " · " + name;
                left = 21;
            }
        }
        var text = context.Text(name, 13, Brushes.White);
        text.MaxTextWidth = Math.Max(1, label.Width - left - 8);
        text.MaxTextHeight = 20;
        text.Trimming = TextTrimming.CharacterEllipsis;
        dc.DrawText(text, new Point(labelX + left, labelY + 5));
    }

}
