using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace CanvasDesktop;

/// <summary>Screen-space title treatment; never shrinks or rotates with the workspace camera.</summary>
internal static class StartupIntroTitle
{
    public static void Draw(DrawingContext dc, Size size, DpiScale dpi, StartupIntroFrame frame)
    {
        if (frame.Complete || size.Width < 160 || size.Height < 140) return;
        DrawLandingHint(dc, size, dpi, frame);
        if (frame.TitleOpacity <= 0) return;
        dc.PushOpacity(frame.TitleOpacity);
        double centerX = size.Width / 2, centerY = size.Height * .44;
        var shade = new RadialGradientBrush();
        shade.GradientStops.Add(new GradientStop(Color.FromArgb(190, 11, 17, 29), 0));
        shade.GradientStops.Add(new GradientStop(Color.FromArgb(125, 11, 17, 29), .55));
        shade.GradientStops.Add(new GradientStop(Color.FromArgb(0, 11, 17, 29), 1));
        dc.DrawEllipse(shade, null, new Point(centerX, centerY + 12),
            Math.Min(size.Width * .48, 580), Math.Min(size.Height * .4, 260));

        double fontSize = Math.Clamp(size.Width * .054, 30, 72);
        DrawSpaced(dc, "CANVAS", fontSize, frame.TitleSpacing * 2, centerX, centerY - fontSize * .65,
            Brushes.WhiteSmoke, FontWeights.Light, dpi);
        DrawSpaced(dc, "WINDOW COMPOSER", Math.Clamp(fontSize * .23, 11, 16), frame.TitleSpacing,
            centerX, centerY + fontSize * .55, new SolidColorBrush(Color.FromRgb(182, 203, 228)), FontWeights.Medium, dpi);
        var accent = new SolidColorBrush(Color.FromArgb(180, 147, 197, 253));
        dc.DrawRoundedRectangle(accent, null, new Rect(centerX - 24, centerY + fontSize * 1.05, 48, 1), .5, .5);
        dc.Pop();
    }

    private static void DrawLandingHint(DrawingContext dc, Size size, DpiScale dpi, StartupIntroFrame frame)
    {
        dc.PushOpacity(frame.RevealOpacity);
        var hint = new FormattedText("Press any key, click or scroll to land", CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12,
            new SolidColorBrush(Color.FromRgb(166, 180, 201)), dpi.PixelsPerDip);
        if (hint.Width < size.Width - 32)
        {
            var position = new Point((size.Width - hint.Width) / 2, Math.Max(16, size.Height - 58));
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(210, 11, 17, 29)), null,
                new Rect(position.X - 14, position.Y - 8, hint.Width + 28, hint.Height + 16), 8, 8);
            dc.DrawText(hint, position);
        }
        dc.Pop();
    }

    private static void DrawSpaced(DrawingContext dc, string title, double fontSize, double spacing,
        double centerX, double y, Brush brush, FontWeight weight, DpiScale dpi)
    {
        var letters = new FormattedText[title.Length];
        var face = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal);
        double width = 0;
        for (int i = 0; i < title.Length; i++)
        {
            letters[i] = new FormattedText(title[i].ToString(), CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, face, fontSize, brush, dpi.PixelsPerDip);
            width += letters[i].WidthIncludingTrailingWhitespace;
        }
        width += spacing * (title.Length - 1);
        double x = centerX - width / 2;
        foreach (var letter in letters)
        {
            dc.DrawText(letter, new Point(x, y));
            x += letter.WidthIncludingTrailingWhitespace + spacing;
        }
    }
}
