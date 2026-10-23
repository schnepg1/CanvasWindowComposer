using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace CanvasDesktop;

/// <summary>Placeholder cards and labels preserve thumbnail occlusion and fade semantics.</summary>
internal sealed class PlaceholderAnnotationLayer : IOverviewAnnotationLayer
{
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
                foreach (var above in card.Occluders)
                {
                    var covered = context.Project(above);
                    if (covered.IntersectsWith(rect))
                        visible = new CombinedGeometry(GeometryCombineMode.Exclude, visible, new RectangleGeometry(covered));
                }
            dc.PushClip(visible);
            dc.PushOpacity(card.Opacity);
            if (imageOpacity < 1)
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(28, 34, 46)),
                    new Pen(new SolidColorBrush(Color.FromRgb(80, 103, 137)), 1), rect, 6, 6);
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
            var border = new SolidColorBrush(label.Card.Priority ? Color.FromRgb(147, 197, 253) : Color.FromRgb(80, 103, 137));
            double radius = Math.Clamp(label.FontSize * 0.35, 2, 10);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(23, 31, 46)),
                new Pen(border, label.Card.Priority ? 2 : 1), rect, radius, radius);
            double padX = Math.Max(4, label.FontSize * 0.5);
            double padY = Math.Max(2, label.FontSize * 0.3);
            var text = new FormattedText(label.Card.Title, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                label.FontSize, Brushes.WhiteSmoke, context.Dpi.PixelsPerDip)
            {
                MaxTextWidth = Math.Max(1, rect.Width - padX * 2), MaxTextHeight = Math.Max(1, rect.Height - padY * 2),
                MaxLineCount = 2, Trimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center
            };
            dc.DrawText(text, new Point(rect.X + padX, rect.Y + Math.Max(padY, (rect.Height - text.Height) / 2)));
            dc.Pop();
        }
    }

}
