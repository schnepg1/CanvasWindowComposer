using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace CanvasDesktop;

/// <summary>Placeholder cards and labels preserve thumbnail occlusion and fade semantics.</summary>
internal sealed class PlaceholderAnnotationLayer : IOverviewAnnotationLayer
{
    private static readonly Brush CardFill = FrozenBrush(Color.FromRgb(35, 53, 76));
    private static readonly Brush LabelFill = FrozenBrush(Color.FromArgb(218, 10, 16, 25));
    private static readonly Pen Outline = OutlinePen(Color.FromRgb(91, 125, 166), 1);
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
            dc.PushClip(VisibleCard(card, context));
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
        Labels = PlaceholderLabelLayout.Get(context.Frame.Placeholders, context.View.CameraX, context.View.CameraY, context.View.Zoom, monitor,
            context.Dpi.DpiScaleX, context.Dpi.DpiScaleY);
        foreach (var label in Labels)
        {
            dc.PushClip(VisibleCard(label.Card, context));
            dc.PushOpacity(label.Card.Opacity * (label.Card.LabelOpacity ?? 1));
            var rect = label.Rect;
            dc.DrawRoundedRectangle(LabelFill, null, rect, 6, 6);
            var text = new FormattedText(label.Card.Title, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                LabelTypeface,
                label.FontSize, Brushes.WhiteSmoke, context.Dpi.PixelsPerDip)
            {
                MaxTextWidth = rect.Width, MaxTextHeight = rect.Height,
                LineHeight = label.FontSize * 1.2, MaxLineCount = 3,
                Trimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center
            };
            dc.DrawText(text, new Point(rect.X, rect.Y + (rect.Height - text.Height) / 2));
            if (label.Card.Logo != null && label.LogoRect is { } logoRect)
                dc.DrawImage(label.Card.Logo, logoRect);
            dc.Pop();
            dc.Pop();
        }
    }

    private static Geometry VisibleCard(PlaceholderAnnotation card, OverviewAnnotationContext context)
    {
        var rect = context.Project(card.Bounds);
        Geometry visible = new RectangleGeometry(rect);
        // Apply the same native z-order clipping to both the card and its title.
        if (card.Occluders != null)
            foreach (var above in card.Occluders)
            {
                var covered = context.Project(above);
                if (covered.IntersectsWith(rect))
                    visible = new CombinedGeometry(GeometryCombineMode.Exclude, visible, new RectangleGeometry(covered));
            }
        return visible;
    }

}
