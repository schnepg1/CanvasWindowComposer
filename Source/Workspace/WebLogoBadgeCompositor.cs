using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Media.Imaging;

namespace CanvasDesktop;

/// <summary>
/// Composites high-resolution web destination logos with a compact host-browser badge
/// in the corner, producing both GDI bitmaps and frozen WPF overview textures.
/// </summary>
internal static class WebLogoBadgeCompositor
{
    public const int DefaultCanvasSize = 256;

    /// <summary>
    /// Creates a composite symbol featuring the web application logo prominently
    /// with the host browser icon badged in the bottom-right corner.
    /// </summary>
    public static SharedArtwork CreateBadgedArtwork(Bitmap webLogo, Bitmap? browserIcon, int canvasSize = DefaultCanvasSize)
    {
        var symbol = new Bitmap(canvasSize, canvasSize, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(symbol))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            // 1. Draw central web app logo
            // Leave margin for aesthetics and to leave room for the badge
            float heroTargetSize = canvasSize * 0.72f;
            float scale = Math.Min(heroTargetSize / webLogo.Width, heroTargetSize / webLogo.Height);
            float heroW = webLogo.Width * scale;
            float heroH = webLogo.Height * scale;
            float heroX = (canvasSize - heroW) / 2f;
            float heroY = (canvasSize - heroH) / 2f;

            g.DrawImage(webLogo, new RectangleF(heroX, heroY, heroW, heroH));

            // 2. Draw host browser badge in bottom-right corner if available
            if (browserIcon != null)
            {
                float badgeSize = canvasSize * 0.32f;
                float badgeX = canvasSize - badgeSize - (canvasSize * 0.04f);
                float badgeY = canvasSize - badgeSize - (canvasSize * 0.04f);

                // Dark backing circle for contrast against any canvas or card background
                using var bgBrush = new SolidBrush(Color.FromArgb(240, 20, 26, 38));
                using var borderPen = new Pen(Color.FromArgb(100, 255, 255, 255), Math.Max(1.5f, canvasSize * 0.008f));

                var badgeRect = new RectangleF(badgeX, badgeY, badgeSize, badgeSize);
                g.FillEllipse(bgBrush, badgeRect);
                g.DrawEllipse(borderPen, badgeRect);

                // Browser icon inside the badge circle
                float iconPadding = badgeSize * 0.16f;
                float iconSize = badgeSize - (iconPadding * 2);
                float bScale = Math.Min(iconSize / browserIcon.Width, iconSize / browserIcon.Height);
                float bW = browserIcon.Width * bScale;
                float bH = browserIcon.Height * bScale;
                float bX = badgeX + (badgeSize - bW) / 2f;
                float bY = badgeY + (badgeSize - bH) / 2f;

                g.DrawImage(browserIcon, new RectangleF(bX, bY, bW, bH));
            }
        }

        BitmapSource overviewLogo = PlaceholderArtwork.CreateOverviewLogo(symbol);
        long bytes = ((long)symbol.Width * symbol.Height * 4) +
                     ((long)overviewLogo.PixelWidth * overviewLogo.PixelHeight * 4);

        return new SharedArtwork(symbol, overviewLogo, bytes);
    }
}
