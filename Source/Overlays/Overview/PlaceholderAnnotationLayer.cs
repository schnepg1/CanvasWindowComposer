using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace CanvasDesktop;

/// <summary>Placeholder cards and labels preserve thumbnail occlusion and fade semantics.</summary>
internal sealed class PlaceholderAnnotationLayer : IOverviewAnnotationLayer
{
    private static readonly Brush CardFill = FrozenBrush(Color.FromRgb(28, 34, 46));
    private static readonly Brush LabelFill = FrozenBrush(Color.FromRgb(23, 31, 46));
    private static readonly Pen Outline = OutlinePen(Color.FromRgb(80, 103, 137), 1);
    private static readonly Pen PriorityOutline = OutlinePen(Color.FromRgb(147, 197, 253), 2);
    private static readonly Typeface LabelTypeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    private static Brush FrozenBrush(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
    private static Pen OutlinePen(Color color, double width) { var pen = new Pen(FrozenBrush(color), width); pen.Freeze(); return pen; }
    public IReadOnlyList<PlaceholderLabel> Labels { get; private set; } = Array.Empty<PlaceholderLabel>();
    public void Clear() => Labels = Array.Empty<PlaceholderLabel>();
    public void Draw(DrawingContext dc, OverviewAnnotationContext context)
    {
        DrawPlaceholderCards(dc, context);
        DrawPlaceholderLabels(dc, context);
    }
    private void DrawPlaceholderCards(DrawingContext dc, OverviewAnnotationContext context)
    {
        foreach (var card in context.Frame.Placeholders)
        {
            double imageOpacity = card.ImageOpacity ?? (context.View.Zoom < WindowManager.OverviewLivePreviewZoom ? 0 : 1);
            if (card.Opacity <= 0 || (imageOpacity >= 1 && card.Image == null)) continue;
            var rect = context.Project(card.Bounds);
            if (!rect.IntersectsWith(new Rect(context.Size))) continue;
            Geometry visible = new RectangleGeometry(rect);
            // Annotations sit above DWM. Cut out higher windows so card
            // snapshots preserve the overview's native thumbnail z-order.
            if (card.Occluders != null)
                for (int i = 0; i < card.Occluders.Count; i++)
                {
                    var covered = context.Project(card.Occluders[i]);
                    if (covered.IntersectsWith(rect))
                        visible = new CombinedGeometry(GeometryCombineMode.Exclude, visible, new RectangleGeometry(covered));
                }
            dc.PushClip(visible);
            dc.PushOpacity(card.Opacity);
            if (imageOpacity < 1)
                dc.DrawRoundedRectangle(CardFill, Outline, rect, 6, 6);
            if (card.Image != null && imageOpacity > 0)
            {
                dc.PushOpacity(imageOpacity);
                dc.DrawImage(card.Image, rect);
                dc.Pop();
            }
            dc.Pop();
            dc.Pop();
        }
    }

    private void DrawPlaceholderLabels(DrawingContext dc, OverviewAnnotationContext context)
    {
        var monitor = new ScreenRect(context.Frame.Screen.X, context.Frame.Screen.Y, context.Frame.Screen.Width, context.Frame.Screen.Height);
        var reserved = new List<Rect> { new(0, 0, context.Size.Width, 64) };
        foreach (var zone in context.Frame.Zones)
            if (ZoneLabelLayout.Get(zone.Bounds, context.View.CameraX, context.View.CameraY, context.View.Zoom, monitor, context.Dpi.DpiScaleX, context.Dpi.DpiScaleY) is { } header)
                reserved.Add(new Rect(header.X / context.Dpi.DpiScaleX, header.Y / context.Dpi.DpiScaleY,
                    header.Width / context.Dpi.DpiScaleX, header.Height / context.Dpi.DpiScaleY));
        Labels = PlaceholderLabelLayout.Get(context.Frame.Placeholders, context.View.CameraX, context.View.CameraY, context.View.Zoom, monitor,
            context.Dpi.DpiScaleX, context.Dpi.DpiScaleY, reserved);
        foreach (var label in Labels)
        {
            dc.PushOpacity(label.Card.Opacity * (label.Card.LabelOpacity ?? 1));
            var rect = label.Rect;
            dc.DrawRoundedRectangle(LabelFill, label.Card.Priority ? PriorityOutline : Outline, rect, 10, 10);
            var text = new FormattedText(label.Card.Title, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                LabelTypeface,
                label.FontSize, Brushes.WhiteSmoke, context.Dpi.PixelsPerDip)
            {
                MaxTextWidth = rect.Width - 24, MaxTextHeight = rect.Height - 16,
                MaxLineCount = 2, Trimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center
            };
            dc.DrawText(text, new Point(rect.X + 12, rect.Y + Math.Max(8, (rect.Height - text.Height) / 2)));
            dc.Pop();
        }
    }

}
