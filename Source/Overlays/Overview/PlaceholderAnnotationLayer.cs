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
            dc.PushClip(VisibleCard(card, context));
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
        Labels = PlaceholderLabelLayout.Get(context.Frame.Placeholders, context.View.CameraX, context.View.CameraY, context.View.Zoom, monitor,
            context.Dpi.DpiScaleX, context.Dpi.DpiScaleY);
        foreach (var label in Labels)
        {
            dc.PushClip(VisibleCard(label.Card, context));
            dc.PushOpacity(label.Card.Opacity * (label.Card.LabelOpacity ?? 1));
            var rect = label.Rect;
            var text = new FormattedText(label.Card.Title, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                label.FontSize, Brushes.WhiteSmoke, context.Dpi.PixelsPerDip)
            {
                MaxTextWidth = rect.Width, MaxTextHeight = rect.Height,
                LineHeight = label.FontSize * 1.2, MaxLineCount = 3,
                Trimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center
            };
            dc.DrawText(text, new Point(rect.X, rect.Y + (rect.Height - text.Height) / 2));
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
