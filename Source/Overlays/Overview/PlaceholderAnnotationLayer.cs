using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace CanvasDesktop;

/// <summary>Placeholder cards and labels preserve thumbnail occlusion and fade semantics.</summary>
internal sealed class PlaceholderAnnotationLayer : IOverviewAnnotationLayer
{
    private static readonly Brush CardFill = FrozenBrush(Color.FromRgb(28, 34, 46));
    private static readonly Brush ActiveCardFill = FrozenLinearGradient(
        Color.FromRgb(22, 60, 98), Color.FromRgb(14, 42, 72), 90);
    private static readonly Brush LabelFill = FrozenBrush(Color.FromArgb(218, 10, 16, 25));
    private static readonly Brush ActiveLabelFill = FrozenBrush(Color.FromArgb(222, 10, 24, 42));
    private static readonly Pen Outline = OutlinePen(Color.FromRgb(91, 125, 166), 1);
    private static readonly Typeface LabelTypeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    private static readonly Pen[] OuterGlowPens = Enumerable.Range(0, 32).Select(i =>
    {
        double t = i / 31.0;
        byte a = (byte)(26 + 38 * t);
        var p = new Pen(FrozenBrush(Color.FromArgb(a, 14, 165, 233)), 4.0 + 3.0 * t);
        p.Freeze();
        return p;
    }).ToArray();

    private static readonly Pen[] InnerGlowPens = Enumerable.Range(0, 32).Select(i =>
    {
        double t = i / 31.0;
        byte a = (byte)(55 + 65 * t);
        var p = new Pen(FrozenBrush(Color.FromArgb(a, 56, 189, 248)), 1.5 + 1.5 * t);
        p.Freeze();
        return p;
    }).ToArray();

    private static readonly Pen[] ActiveOutlinePens = Enumerable.Range(0, 32).Select(i =>
    {
        double t = i / 31.0;
        byte a = (byte)(190 + 65 * t);
        var p = new Pen(FrozenBrush(Color.FromArgb(a, 125, 211, 252)), 1.5);
        p.Freeze();
        return p;
    }).ToArray();

    private static Brush FrozenBrush(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
    private static Brush FrozenLinearGradient(Color top, Color bottom, double angle)
    {
        var brush = new LinearGradientBrush(top, bottom, angle);
        brush.Freeze();
        return brush;
    }
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

            if (card.IsActiveWindow && card.Opacity >= 0.99 && !context.MotionActive && (imageOpacity < 1 || card.ImageIsOverlay))
            {
                double pulse = 0.5;
                if (!context.ReduceMotion)
                {
                    double phase = ((context.Timestamp % 2200) / 2200.0) * 2 * Math.PI;
                    pulse = 0.5 + 0.5 * Math.Sin(phase);
                }
                int glowIdx = Math.Clamp((int)(pulse * 31.99), 0, 31);
                double spread = Math.Clamp(Math.Min(rect.Width, rect.Height) * 0.03, 3, 8) + 2.0 * pulse;
                var outerGlowRect = Rect.Inflate(rect, spread, spread);
                dc.PushClip(VisibleGlow(card, outerGlowRect, context));
                dc.PushOpacity(card.Opacity);
                dc.DrawRoundedRectangle(null, OuterGlowPens[glowIdx], outerGlowRect, 6 + spread * 0.5, 6 + spread * 0.5);
                double innerSpread = spread * 0.5;
                var innerGlowRect = Rect.Inflate(rect, innerSpread, innerSpread);
                dc.DrawRoundedRectangle(null, InnerGlowPens[glowIdx], innerGlowRect, 6 + innerSpread * 0.5, 6 + innerSpread * 0.5);
                dc.Pop();
                dc.Pop();
            }

            dc.PushClip(VisibleCard(card, context));
            dc.PushOpacity(card.Opacity);
            if (imageOpacity < 1 || card.ImageIsOverlay)
            {
                var fill = card.IsActiveWindow ? ActiveCardFill : CardFill;
                int outlineIdx = 16;
                if (card.IsActiveWindow)
                {
                    double pulse = 0.5;
                    if (!context.ReduceMotion)
                    {
                        double phase = ((context.Timestamp % 2200) / 2200.0) * 2 * Math.PI;
                        pulse = 0.5 + 0.5 * Math.Sin(phase);
                    }
                    outlineIdx = Math.Clamp((int)(pulse * 31.99), 0, 31);
                }
                var outline = card.IsActiveWindow ? ActiveOutlinePens[outlineIdx] : Outline;
                dc.DrawRoundedRectangle(fill, outline, rect, 6, 6);
                if (card.Logo is { Width: > 0, Height: > 0 } logo)
                {
                    double cardScale = Math.Min(rect.Width / logo.Width, rect.Height / logo.Height);
                    double width = logo.Width * cardScale, height = logo.Height * cardScale;
                    var logoRect = new Rect(rect.X + (rect.Width - width) / 2,
                        rect.Y + (rect.Height - height) / 2, width, height);
                    if (card.LogoBounds is { Width: > 0, Height: > 0 } near)
                    {
                        var compact = context.Project(new WorldRect { X = card.Bounds.X + near.X,
                            Y = card.Bounds.Y + near.Y, W = near.Width, H = near.Height });
                        // Keep the logo as small as its initial size relative to the
                        // desktop monitor until the card shrinks to fit it, then scale down.
                        double initialScale = Math.Min((near.Width / context.Dpi.DpiScaleX) / logo.Width,
                            (near.Height / context.Dpi.DpiScaleY) / logo.Height);
                        double compactScale = Math.Min(compact.Width / logo.Width, compact.Height / logo.Height);
                        double maxInitialScale = Math.Max(initialScale, compactScale);
                        double scale = Math.Min(maxInitialScale, cardScale);
                        width = logo.Width * scale;
                        height = logo.Height * scale;
                        double centerX = rect.X + (rect.Width - width) / 2;
                        double centerY = rect.Y + (rect.Height - height) / 2;
                        double t = Math.Clamp((1.0 - context.View.Zoom) / 0.70, 0, 1);
                        t = t * t * (3 - 2 * t);
                        double x = Math.Clamp(compact.X + (centerX - compact.X) * t, rect.X, Math.Max(rect.X, rect.Right - width));
                        double y = Math.Clamp(compact.Y + (centerY - compact.Y) * t, rect.Y, Math.Max(rect.Y, rect.Bottom - height));
                        logoRect = new Rect(x, y, width, height);
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
            var labelFill = label.Card.IsActiveWindow ? ActiveLabelFill : LabelFill;
            dc.DrawRoundedRectangle(labelFill, null,
                new Rect(rect.X + (rect.Width - backingWidth) / 2, textY, backingWidth, text.Height), 3, 3);
            dc.DrawText(text, new Point(rect.X, textY));
            dc.Pop();
            dc.Pop();
        }
    }

    internal static Geometry VisibleCard(PlaceholderAnnotation card, OverviewAnnotationContext context)
    {
        var rect = context.Project(card.Bounds);
        if (card.Occluders == null || card.Occluders.Count == 0)
        {
            var r = new RectangleGeometry(rect);
            r.Freeze();
            return r;
        }
        Geometry visible = new RectangleGeometry(rect);
        foreach (var above in card.Occluders)
        {
            var covered = context.Project(above);
            if (covered.IntersectsWith(rect))
            {
                var coverGeom = new RectangleGeometry(covered);
                coverGeom.Freeze();
                visible = new CombinedGeometry(GeometryCombineMode.Exclude, visible, coverGeom);
            }
        }
        visible.Freeze();
        return visible;
    }

    internal static Geometry VisibleGlow(PlaceholderAnnotation card, Rect glowRect, OverviewAnnotationContext context)
    {
        if (card.Occluders == null || card.Occluders.Count == 0)
        {
            var r = new RectangleGeometry(glowRect);
            r.Freeze();
            return r;
        }
        Geometry visible = new RectangleGeometry(glowRect);
        foreach (var above in card.Occluders)
        {
            var covered = context.Project(above);
            if (covered.IntersectsWith(glowRect))
            {
                var coverGeom = new RectangleGeometry(covered);
                coverGeom.Freeze();
                visible = new CombinedGeometry(GeometryCombineMode.Exclude, visible, coverGeom);
            }
        }
        visible.Freeze();
        return visible;
    }
}
