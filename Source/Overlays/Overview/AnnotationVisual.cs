using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace CanvasDesktop;

/// <summary>Card artwork in canvas units, shared by the canvas and the text editor.</summary>
internal static class AnnotationVisual
{
    internal const double FontSize = AnnotationDocument.DefaultFontSize;
    internal const double TextInset = AnnotationGeometry.TextInset;
    private static readonly FontFamily Family = new("Segoe UI");
    internal static readonly Typeface Typeface = new(Family,FontStyles.Normal,FontWeights.Normal,FontStretches.Normal);
    private static readonly Typeface BoldTypeface = new(Family,FontStyles.Normal,FontWeights.Bold,FontStretches.Normal);
    private static readonly Typeface ItalicTypeface = new(Family,FontStyles.Italic,FontWeights.Normal,FontStretches.Normal);
    private static readonly Typeface BoldItalicTypeface = new(Family,FontStyles.Italic,FontWeights.Bold,FontStretches.Normal);
    internal static readonly Brush CardBackground = CreateCardBackground();
    private static Brush CreateCardBackground()
    {
        var brush=new SolidColorBrush(Color.FromArgb(255,28,34,46));
        brush.Freeze(); return brush;
    }

    internal static Typeface TypefaceFor(AnnotationTextEmphasis emphasis) => emphasis switch
    {
        AnnotationTextEmphasis.Bold => BoldTypeface,
        AnnotationTextEmphasis.Italic => ItalicTypeface,
        AnnotationTextEmphasis.BoldItalic => BoldItalicTypeface,
        _ => Typeface
    };

    internal static TextAlignment TextAlignmentFor(AnnotationTextAlign align) => align switch
    {
        AnnotationTextAlign.Center => TextAlignment.Center,
        AnnotationTextAlign.Right => TextAlignment.Right,
        _ => TextAlignment.Left
    };

    private static Brush? CardFillBrush(CanvasAnnotation item,SolidColorBrush color)
    {
        if (item.Kind == AnnotationKind.Arrow || item.CardFill == AnnotationCardFill.None) return null;
        if (item.CardFill == AnnotationCardFill.Tinted)
        {
            var tint=new SolidColorBrush(Color.FromArgb(42,color.Color.R,color.Color.G,color.Color.B));
            tint.Freeze(); return tint;
        }
        return CardBackground;
    }

    internal static Rect TextBounds(CanvasAnnotation item)
    {
        var r = new Rect(0,0,item.Bounds.Width,item.Bounds.Height);
        if (item.Kind == AnnotationKind.Ellipse)
            r = new(r.Width*.15,r.Height*.15,r.Width*.7,r.Height*.7);
        if (item.Kind == AnnotationKind.Arrow)
        {
            var bounds = item.Bounds;
            var (mid, tan) = item.Routing == ConnectorRouting.Curved
                ? AnnotationGeometry.BezierMidpointAndTangent(item.A, AnnotationGeometry.BezierControlPoints(item.A, item.SourcePort, item.B, item.TargetPort).C1, AnnotationGeometry.BezierControlPoints(item.A, item.SourcePort, item.B, item.TargetPort).C2, item.B)
                : (new CanvasPoint((item.A.X + item.B.X) / 2.0, (item.A.Y + item.B.Y) / 2.0), new CanvasPoint(item.B.X - item.A.X, item.B.Y - item.A.Y));
            double approxWidth = Math.Max(40, (item.Text?.Length ?? 0) * item.FontSize * 0.6 + 20);
            double approxHeight = item.FontSize + 12;
            var label = AnnotationGeometry.ArrowLabelBounds(mid, tan, item.Stroke, approxWidth, approxHeight);
            return new Rect(label.X - bounds.X, label.Y - bounds.Y, label.Width, label.Height);
        }
        return new(r.X+TextInset,r.Y+TextInset,Math.Max(1,r.Width-TextInset*2),Math.Max(1,r.Height-TextInset*2));
    }

    /// <summary>Keep wrapping width and the top-left anchor; grow vertically to show every line.</summary>
    internal static CanvasAnnotation FitText(CanvasAnnotation item, double? minimumHeight = null)
    {
        if (item.Kind != AnnotationKind.Text) return item;
        var bounds = item.Bounds;
        var metrics = AnnotationTextLayout.Measure(item, bounds.Width - TextInset * 2);
        // A single glyph or grapheme can be wider than a wrapping line at large
        // font sizes. Grow the width enough to display that unbreakable cluster.
        double width = Math.Max(bounds.Width, Math.Ceiling(metrics.Width + TextInset * 2 + AnnotationGeometry.TextCaretPadding));
        if (width > bounds.Width) metrics = AnnotationTextLayout.Measure(item,width-TextInset*2);
        double height = Math.Max(minimumHeight ?? bounds.Height, Math.Ceiling(metrics.Height + TextInset * 2 + 4));
        return item with { A = new(bounds.X, bounds.Y), B = new(bounds.X + width, bounds.Y + height) };
    }

    internal static void Draw(DrawingContext dc, CanvasAnnotation item, double pixelsPerDip, bool drawText = true)
    {
        var bounds = item.Bounds;
        var rect = new Rect(0,0,bounds.Width,bounds.Height);
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(item.Color));
        brush.Freeze();
        var pen = new Pen(brush,item.Stroke);
        if (item.Kind == AnnotationKind.Arrow)
        {
            var a = new Point(item.A.X - bounds.X, item.A.Y - bounds.Y);
            var b = new Point(item.B.X - bounds.X, item.B.Y - bounds.Y);

            double headLen = Math.Max(20.0, item.Stroke * 3.2);
            double headWid = Math.Max(10.0, item.Stroke * 1.8);

            Vector arrivalDir;
            Point shaftEnd;

            if (item.Routing == ConnectorRouting.Curved)
            {
                var (c1w, c2w) = AnnotationGeometry.BezierControlPoints(item.A, item.SourcePort, item.B, item.TargetPort);
                var c1 = new Point(c1w.X - bounds.X, c1w.Y - bounds.Y);
                var c2 = new Point(c2w.X - bounds.X, c2w.Y - bounds.Y);

                var arrVector = b - c2;
                if (arrVector.Length > 0.0001) arrVector.Normalize();
                else { arrVector = b - a; if (arrVector.Length > 0.0001) arrVector.Normalize(); else arrVector = new Vector(1, 0); }
                arrivalDir = arrVector;

                shaftEnd = b - arrivalDir * (headLen - Math.Min(headLen * 0.4, item.Stroke * 0.5));

                var pathGeom = new PathGeometry();
                var fig = new PathFigure { StartPoint = a, IsFilled = false, IsClosed = false };
                fig.Segments.Add(new BezierSegment(c1, c2, shaftEnd, true));
                pathGeom.Figures.Add(fig);
                dc.DrawGeometry(null, pen, pathGeom);
            }
            else
            {
                var delta = b - a;
                if (delta.Length > 0.0001) delta.Normalize();
                else delta = new Vector(1, 0);
                arrivalDir = delta;

                shaftEnd = b - arrivalDir * (headLen - Math.Min(headLen * 0.4, item.Stroke * 0.5));
                dc.DrawLine(pen, a, shaftEnd);
            }

            if (headLen > 0)
            {
                var normal = new Vector(-arrivalDir.Y, arrivalDir.X);
                var basePt = b - arrivalDir * headLen;
                var left = basePt + normal * headWid;
                var right = basePt - normal * headWid;

                var headGeom = new StreamGeometry();
                using (var ctx = headGeom.Open())
                {
                    ctx.BeginFigure(b, true, true);
                    ctx.LineTo(left, true, false);
                    ctx.LineTo(right, true, false);
                }
                headGeom.Freeze();
                dc.DrawGeometry(brush, null, headGeom);
            }

            if (drawText && !string.IsNullOrWhiteSpace(item.Text))
            {
                var (midw, tanw) = item.Routing == ConnectorRouting.Curved
                    ? AnnotationGeometry.BezierMidpointAndTangent(item.A, AnnotationGeometry.BezierControlPoints(item.A, item.SourcePort, item.B, item.TargetPort).C1, AnnotationGeometry.BezierControlPoints(item.A, item.SourcePort, item.B, item.TargetPort).C2, item.B)
                    : (new CanvasPoint((item.A.X + item.B.X) / 2.0, (item.A.Y + item.B.Y) / 2.0), new CanvasPoint(item.B.X - item.A.X, item.B.Y - item.A.Y));

                var formattedText = new FormattedText(
                    item.Text,
                    CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight,
                    TypefaceFor(item.Emphasis),
                    item.FontSize,
                    brush,
                    pixelsPerDip);

                double padX = Math.Max(8.0, item.FontSize * 0.35);
                double padY = Math.Max(4.0, item.FontSize * 0.2);
                double badgeWidth = formattedText.Width + padX * 2.0;
                double badgeHeight = formattedText.Height + padY * 2.0;

                var labelWorld = AnnotationGeometry.ArrowLabelBounds(midw, tanw, item.Stroke, badgeWidth, badgeHeight);
                var badgeRect = new Rect(labelWorld.X - bounds.X, labelWorld.Y - bounds.Y, labelWorld.Width, labelWorld.Height);

                var badgePen = new Pen(brush, Math.Min(2.0, Math.Max(1.0, item.Stroke * 0.15)));
                badgePen.Freeze();
                dc.DrawRoundedRectangle(CardBackground, badgePen, badgeRect, 6, 6);

                dc.PushClip(new RectangleGeometry(badgeRect));
                dc.DrawText(formattedText, new Point(badgeRect.X + padX, badgeRect.Y + padY));
                dc.Pop();
            }

            return;
        }
        var fillBrush = CardFillBrush(item, brush);
        if (item.Kind == AnnotationKind.Rectangle) dc.DrawRectangle(fillBrush, pen, rect);
        else if (item.Kind == AnnotationKind.Ellipse) dc.DrawEllipse(fillBrush, pen, new(rect.Width/2, rect.Height/2), rect.Width/2, rect.Height/2);
        else
        {
            dc.DrawRoundedRectangle(fillBrush, pen, rect, 6, 6);
        }
        if (!drawText || string.IsNullOrEmpty(item.Text) || rect.Width <= TextInset*2 || rect.Height <= TextInset*2) return;
        var textRect = TextBounds(item);
        if (item.Kind == AnnotationKind.Text)
        {
            dc.PushClip(new RectangleGeometry(textRect));
            AnnotationTextLayout.Layout(item, textRect.Width, dc, textRect.TopLeft, brush);
            dc.Pop();
            return;
        }
        var text = new FormattedText(item.Text,CultureInfo.CurrentUICulture,FlowDirection.LeftToRight,TypefaceFor(item.Emphasis),item.FontSize,brush,pixelsPerDip)
        {
            MaxTextWidth=textRect.Width, MaxTextHeight=textRect.Height, Trimming=TextTrimming.CharacterEllipsis,
            TextAlignment=TextAlignmentFor(item.TextAlign)
        };
        dc.PushClip(new RectangleGeometry(textRect));
        dc.DrawText(text,textRect.TopLeft);
        dc.Pop();
    }
}
