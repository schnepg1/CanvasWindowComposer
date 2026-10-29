using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;

namespace CanvasDesktop;

/// <summary>Character wrapping and line metrics shared by saved notes and their inline editor.</summary>
internal static class AnnotationTextLayout
{
    internal static double Layout(CanvasAnnotation item, double width, DrawingContext? drawing = null, Point origin = default, Brush? foreground = null)
        => Format(item,width,drawing,origin,foreground).Height;

    internal static (double Height, double Width) Measure(CanvasAnnotation item,double width)
        => Format(item,width,null,default,null);

    private static (double Height, double Width) Format(CanvasAnnotation item,double width,DrawingContext? drawing,Point origin,Brush? foreground)
    {
        using var formatter = TextFormatter.Create();
        var source = new NoteText(item.Text, new RunStyle(item, foreground ?? Brushes.White));
        var paragraph = new ParagraphStyle(source.Style, AnnotationVisual.TextAlignmentFor(item.TextAlign));
        double height = 0, requiredWidth = 0;
        int index = 0;
        // Include the final empty line when the content ends with a newline, just as TextBox does.
        while (index <= item.Text.Length)
        {
            using var line = formatter.FormatLine(source, index, Math.Max(1, width - AnnotationGeometry.TextCaretPadding), paragraph, null);
            if (drawing != null) line.Draw(drawing, new Point(origin.X, origin.Y + height), InvertAxes.None);
            height += line.Height;
            // TextLine reports a negative trailing overhang when ink extends
            // beyond the advance width (notably large italic glyphs).
            requiredWidth = Math.Max(requiredWidth,Math.Max(line.WidthIncludingTrailingWhitespace,line.Width-line.OverhangTrailing));
            if (line.Length == 0) break;
            index += line.Length;
        }
        return (height,requiredWidth);
    }

    private sealed class NoteText(string text, RunStyle style) : TextSource
    {
        internal RunStyle Style => style;
        public override TextRun GetTextRun(int index) => index < text.Length
            ? new TextCharacters(text, index, text.Length - index, style) : new TextEndOfParagraph(1);
        public override TextSpan<CultureSpecificCharacterBufferRange> GetPrecedingText(int limit)
            => new(limit, new(CultureInfo.CurrentUICulture, new CharacterBufferRange(text, 0, limit)));
        public override int GetTextEffectCharacterIndexFromTextSourceCharacterIndex(int index) => index;
    }
    private sealed class RunStyle(CanvasAnnotation item, Brush foreground) : TextRunProperties
    {
        public override Typeface Typeface => AnnotationVisual.TypefaceFor(item.Emphasis);
        public override double FontRenderingEmSize => item.FontSize;
        public override double FontHintingEmSize => item.FontSize;
        public override Brush ForegroundBrush => foreground;
        public override Brush? BackgroundBrush => null;
        public override CultureInfo CultureInfo => CultureInfo.CurrentUICulture;
        public override TextDecorationCollection? TextDecorations => null;
        public override TextEffectCollection? TextEffects => null;
    }
    private sealed class ParagraphStyle(RunStyle style, TextAlignment alignment) : TextParagraphProperties
    {
        public override FlowDirection FlowDirection => FlowDirection.LeftToRight;
        public override TextAlignment TextAlignment => alignment;
        public override double LineHeight => 0;
        public override bool FirstLineInParagraph => true;
        public override TextRunProperties DefaultTextRunProperties => style;
        public override TextWrapping TextWrapping => TextWrapping.Wrap;
        public override TextMarkerProperties? TextMarkerProperties => null;
        public override double Indent => 0;
    }
}
