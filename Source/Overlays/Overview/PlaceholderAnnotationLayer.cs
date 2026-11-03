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
        DrawPeekActivation(dc, context);
    }
    private void DrawPlaceholderCards(DrawingContext dc, OverviewAnnotationContext context)
    {
        foreach (var card in context.Frame.Placeholders)
        {
            double imageOpacity = card.ImageOpacity ?? (context.View.Zoom < WindowManager.OverviewLivePreviewZoom ? 0 : 1);
            // A peek activation border is drawn separately in DrawPeekActivation,
            // so a fully live card without pixels stays out of the fill pass.
            if (card.Opacity <= 0 || (imageOpacity >= 1 && card.Image == null && card.PeekProgress == null)) continue;
            var rect = context.Project(card.Bounds);
            if (!rect.IntersectsWith(new Rect(context.Size))) continue;
            dc.PushClip(VisibleCard(card, context));
            dc.PushOpacity(card.Opacity);
            if (imageOpacity < 1 || card.ImageIsOverlay)
            {
                dc.DrawRoundedRectangle(CardFill, Outline, rect, 6, 6);
                if (card.Logo is { Width: > 0, Height: > 0 } logo)
                {
                    double scale = Math.Min(rect.Width / logo.Width, rect.Height / logo.Height);
                    double width = logo.Width * scale, height = logo.Height * scale;
                    var logoRect = new Rect(rect.X + (rect.Width - width) / 2,
                        rect.Y + (rect.Height - height) / 2, width, height);
                    if (card.LogoBounds is { Width: > 0, Height: > 0 } near)
                    {
                        var compact = context.Project(new WorldRect { X = card.Bounds.X + near.X,
                            Y = card.Bounds.Y + near.Y, W = near.Width, H = near.Height });
                        // Smoothly grow from the close-up layout to a full-card
                        // symbol. This is geometry only, including during gestures.
                        double t = Math.Clamp((.75 - context.View.Zoom) / .45, 0, 1);
                        t = t * t * (3 - 2 * t);
                        logoRect = new Rect(compact.X + (logoRect.X - compact.X) * t,
                            compact.Y + (logoRect.Y - compact.Y) * t,
                            compact.Width + (logoRect.Width - compact.Width) * t,
                            compact.Height + (logoRect.Height - compact.Height) * t);
                    }
                    dc.DrawImage(logo, logoRect);
                }
            }
            if (card.Image != null && imageOpacity > 0)
            {
                dc.PushOpacity(imageOpacity);
                dc.DrawImage(card.Image, rect);
                dc.Pop();
            }
            if (imageOpacity < 1 && card.LaunchStatus is { Length: > 0 } status && rect.Width >= 30 && rect.Height >= 30)
            {
                dc.PushOpacity(1 - imageOpacity);
                double height = Math.Min(24, rect.Height * .25);
                int lineBreak = status.IndexOf('\n');
                var text = context.Text(lineBreak < 0 ? status : status[..lineBreak], height * .6, Brushes.LightSkyBlue);
                text.MaxTextWidth = Math.Max(1, rect.Width * .9);
                text.MaxTextHeight = height;
                text.MaxLineCount = 1;
                text.Trimming = TextTrimming.CharacterEllipsis;
                text.TextAlignment = TextAlignment.Center;
                var point = new Point(rect.X + rect.Width * .05, rect.Bottom - height - rect.Height * .04);
                double width = Math.Min(text.MaxTextWidth, text.Width + 6);
                dc.DrawRoundedRectangle(LabelFill, null,
                    new Rect(point.X + (text.MaxTextWidth - width) / 2, point.Y, width, height), 3, 3);
                dc.DrawText(text, point);
                dc.Pop();
            }
            dc.Pop();
            dc.Pop();
        }
    }

    /// <summary>
    /// Activation hint for a Canvas Peek dwell: a bar sweeps once around the
    /// card border as the preview is being armed. Draws above live DWM
    /// thumbnails and placeholder fills alike; occlusion clipping still applies.
    /// </summary>
    private void DrawPeekActivation(DrawingContext dc, OverviewAnnotationContext context)
    {
        foreach (var card in context.Frame.Placeholders)
        {
            if (card.PeekProgress is not { } progress || card.Opacity <= 0) continue;
            var rect = context.Project(card.Bounds);
            if (!rect.IntersectsWith(new Rect(context.Size))) continue;
            dc.PushClip(VisibleCard(card, context));
            dc.PushOpacity(card.Opacity);
            double width = Math.Clamp(Math.Min(rect.Width, rect.Height) * .07, 2, 7);
            dc.DrawGeometry(null, TrackPen(width * .6),
                new RectangleGeometry(rect) { RadiusX = 6, RadiusY = 6 });
            Geometry? sweep = BorderSweep(rect, progress);
            if (sweep != null) dc.DrawGeometry(null, SweepPen(width), sweep);
            dc.Pop();
            dc.Pop();
        }
    }

    private static Pen TrackPen(double width)
    {
        var pen = new Pen(FrozenBrush(Color.FromArgb(80, 158, 198, 255)), width);
        pen.Freeze(); return pen;
    }

    private static Pen SweepPen(double width)
    {
        var pen = new Pen(FrozenBrush(Color.FromArgb(240, 125, 211, 252)), width);
        pen.Freeze(); return pen;
    }

    /// <summary>Partial clockwise border path starting at the top-left corner.</summary>
    private static Geometry? BorderSweep(Rect rect, double progress)
    {
        double remaining = Math.Clamp(progress, 0, 1) * 2 * (rect.Width + rect.Height);
        if (remaining < .5 || rect.Width < 1 || rect.Height < 1) return null;
        var corners = new[] { rect.TopRight, rect.BottomRight, rect.BottomLeft, rect.TopLeft, rect.TopRight };
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(rect.TopLeft, false, false);
            var current = rect.TopLeft;
            foreach (var corner in corners)
            {
                if (remaining <= 0) break;
                double length = Math.Abs(corner.X - current.X) + Math.Abs(corner.Y - current.Y);
                if (length <= remaining)
                {
                    ctx.LineTo(corner, true, true);
                    remaining -= length;
                    current = corner;
                }
                else
                {
                    double t = remaining / length;
                    current = new Point(current.X + (corner.X - current.X) * t, current.Y + (corner.Y - current.Y) * t);
                    ctx.LineTo(current, true, true);
                    remaining = 0;
                }
            }
        }
        geometry.Freeze();
        return geometry;
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
                LabelTypeface,
                label.FontSize, Brushes.WhiteSmoke, context.Dpi.PixelsPerDip)
            {
                MaxTextWidth = rect.Width, MaxTextHeight = rect.Height,
                LineHeight = label.FontSize * 1.2, MaxLineCount = 3,
                Trimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center
            };
            double textY = rect.Y + (rect.Height - text.Height) / 2;
            double backingWidth = Math.Min(rect.Width, text.Width + label.FontSize * .4);
            dc.DrawRoundedRectangle(LabelFill, null,
                new Rect(rect.X + (rect.Width - backingWidth) / 2, textY, backingWidth, text.Height), 3, 3);
            dc.DrawText(text, new Point(rect.X, textY));
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
