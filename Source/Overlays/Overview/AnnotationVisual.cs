using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace CanvasDesktop;

/// <summary>Card artwork in canvas units, shared by the canvas and the text editor.</summary>
internal static class AnnotationVisual
{
    internal const double FontSize = 18;
    internal const double TextInset = 8;
    internal static readonly Typeface Typeface = new("Segoe UI");
    internal static readonly Brush CardBackground = CreateCardBackground();
    private static Brush CreateCardBackground()
    {
        var brush=new SolidColorBrush(Color.FromArgb(245,28,34,46));
        brush.Freeze(); return brush;
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
        if (item.Kind == AnnotationKind.Rectangle) dc.DrawRectangle(null,pen,rect);
        else if (item.Kind == AnnotationKind.Ellipse) dc.DrawEllipse(null,pen,new(rect.Width/2,rect.Height/2),rect.Width/2,rect.Height/2);
        else dc.DrawRoundedRectangle(CardBackground,pen,rect,6,6);
        if (!drawText || string.IsNullOrEmpty(item.Text) || rect.Width <= TextInset*2 || rect.Height <= TextInset*2) return;
        var textRect = TextBounds(item);
        var text = new FormattedText(item.Text,CultureInfo.CurrentUICulture,FlowDirection.LeftToRight,Typeface,FontSize,brush,pixelsPerDip)
        {
            MaxTextWidth=textRect.Width, MaxTextHeight=textRect.Height, Trimming=TextTrimming.CharacterEllipsis
        };
        dc.PushClip(new RectangleGeometry(textRect));
        dc.DrawText(text,textRect.TopLeft);
        dc.Pop();
    }
}
