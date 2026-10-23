using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace CanvasDesktop;

/// <summary>Card artwork in canvas units, shared by the canvas and the text editor.</summary>
internal static class AnnotationVisual
{
    internal const double FontSize = AnnotationDocument.DefaultFontSize;
    internal const double TextInset = 8;
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
        return new(r.X+TextInset,r.Y+TextInset,Math.Max(1,r.Width-TextInset*2),Math.Max(1,r.Height-TextInset*2));
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
            var a = new Point(item.A.X-bounds.X,item.A.Y-bounds.Y);
            var b = new Point(item.B.X-bounds.X,item.B.Y-bounds.Y);
            dc.DrawLine(pen,a,b);
            var delta = a-b;
            if (delta.Length > 0)
            {
                delta.Normalize();
                var wing = new Vector(-delta.Y,delta.X)*12*.45;
                dc.DrawLine(pen,b,b+delta*12+wing);
                dc.DrawLine(pen,b,b+delta*12-wing);
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
