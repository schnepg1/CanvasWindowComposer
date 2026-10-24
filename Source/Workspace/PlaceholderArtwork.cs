using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using System.Windows.Media.Imaging;

namespace CanvasDesktop;

/// <summary>Card artwork, independent of workspace actions and native placement.</summary>
internal sealed class PlaceholderArtwork : Control
{
    private Bitmap? _symbol;
    private BitmapSource? _overviewLogo;
    private string? _symbolKey;
    private bool _folder;

    public PlaceholderArtwork()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.SupportsTransparentBackColor |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(28, 34, 46);
        ForeColor = Color.WhiteSmoke;
        AccessibleRole = AccessibleRole.StaticText;
    }

    public void UpdateItem(WorkspaceItemView item)
    {
        Text = item.Title;
        AccessibleName = item.Title;
        _folder = item.IsFolder;
        string key = item.IsFolder ? "folder" : item.ExecutablePath;
        if (_symbolKey != key)
        {
            _symbolKey = key;
            _symbol?.Dispose();
            _symbol = null;
            _overviewLogo = null;
            if (item.IsFolder)
            {
                _symbol = new Bitmap(256, 256, PixelFormat.Format32bppPArgb);
                using var graphics = Graphics.FromImage(_symbol);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                DrawFolder(graphics, new RectangleF(0, 0, 256, 256));
            }
            else
            {
                try
                {
                    if (File.Exists(item.ExecutablePath))
                    {
                        // Request the large resource instead of magnifying the
                        // tiny taskbar-sized associated icon into a watermark.
                        using var icon = Icon.ExtractIcon(item.ExecutablePath, 0, 256);
                        _symbol = icon?.ToBitmap();
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or
                    UnauthorizedAccessException or System.ComponentModel.Win32Exception or
                    System.Runtime.InteropServices.ExternalException)
                { /* Missing/unreadable icons must never prevent a saved place opening. */ }
            }
            if (_symbol != null) _overviewLogo = CreateOverviewLogo(_symbol);
        }
        Invalidate();
    }

    /// <summary>One immutable icon reused at every distant-card size; null means extraction failed.</summary>
    internal BitmapSource? OverviewLogo => _overviewLogo;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool DrawSymbolBackground { get; set; } = true;

    private static BitmapSource CreateOverviewLogo(Bitmap source)
    {
        const int size = 128;
        using var thumbnail = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(thumbnail))
        {
            graphics.Clear(Color.Transparent);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            float scale = Math.Min((float)size / source.Width, (float)size / source.Height);
            float width = source.Width * scale, height = source.Height * scale;
            graphics.DrawImage(source, new RectangleF((size - width) / 2, (size - height) / 2, width, height));
        }
        var rect = new Rectangle(0, 0, thumbnail.Width, thumbnail.Height);
        var bits = thumbnail.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var image = BitmapSource.Create(size, size, 96, 96, System.Windows.Media.PixelFormats.Pbgra32,
                null, bits.Scan0, checked(bits.Stride * size), bits.Stride);
            image.Freeze();
            return image;
        }
        finally { thumbnail.UnlockBits(bits); }
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

    internal void DrawIconBackground(Graphics graphics, RectangleF bounds)
    {
        if (_symbol == null || bounds.Width <= 0 || bounds.Height <= 0) return;
        float scale = Math.Min(bounds.Width / _symbol.Width, bounds.Height / _symbol.Height);
        float width = _symbol.Width * scale, height = _symbol.Height * scale;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(_symbol, new RectangleF(bounds.X + (bounds.Width - width) / 2,
            bounds.Y + (bounds.Height - height) / 2, width, height));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        float dpi = DeviceDpi / 96f;
        if (DrawSymbolBackground) DrawIconBackground(g, ClientRectangle);
        using var captionFont = new Font("Segoe UI", 13 * dpi, FontStyle.Bold, GraphicsUnit.Pixel);
        using var captionBrush = new SolidBrush(Color.FromArgb(160, 181, 208));
        using var captionFormat = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near };
        var captionRect = new RectangleF(0, 4 * dpi, Width, 20 * dpi);
        DrawBackedText(g, _folder ? "SAVED FOLDER" : "SAVED APPLICATION", captionFont, captionBrush, captionRect, captionFormat, dpi);
        var titleRect = new RectangleF(4 * dpi, 28 * dpi, Math.Max(1, Width - 8 * dpi), Math.Max(1, Height - 32 * dpi));
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisWord, FormatFlags = StringFormatFlags.LineLimit };
        float fontSize = Math.Min(84 * dpi, titleRect.Height * .65f);
        float minimum = 20 * dpi;
        fontSize = Math.Max(minimum, fontSize);
        while (fontSize > minimum)
        {
            using var candidate = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            var measured = g.MeasureString(Text, candidate, new SizeF(titleRect.Width, 10000), format);
            if (measured.Height <= titleRect.Height) break;
            fontSize = Math.Max(minimum, fontSize - 2 * dpi);
        }
        using var titleFont = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
        using var titleBrush = new SolidBrush(ForeColor);
        DrawBackedText(g, Text, titleFont, titleBrush, titleRect, format, dpi);
    }

    private static void DrawBackedText(Graphics graphics, string text, Font font, Brush brush,
        RectangleF area, StringFormat format, float dpi)
    {
        var measured = graphics.MeasureString(text, font, area.Size, format);
        float width = Math.Min(area.Width, measured.Width + 8 * dpi);
        float height = Math.Min(area.Height, measured.Height + 4 * dpi);
        var backing = new RectangleF(area.X + (area.Width - width) / 2,
            format.LineAlignment == StringAlignment.Center ? area.Y + (area.Height - height) / 2 : area.Y,
            width, height);
        using var background = new SolidBrush(Color.FromArgb(224, 10, 16, 25));
        graphics.FillRectangle(background, backing);
        graphics.DrawString(text, font, brush, area, format);
    }

    internal static float CalculateCardTitleFontSize(string text, float cardWidth, float cardHeight)
    {
        float artworkWidth = Math.Max(40, cardWidth - 36);
        float artworkHeight = Math.Max(40, cardHeight - 184);
        var titleRect = new RectangleF(4, 28, Math.Max(1, artworkWidth - 8), Math.Max(1, artworkHeight - 32));
        float fontSize = Math.Min(84, titleRect.Height * .65f);
        float minimum = 20;
        fontSize = Math.Max(minimum, fontSize);
        while (fontSize > minimum)
        {
            float charsPerLine = Math.Max(1, titleRect.Width / (fontSize * 0.58f));
            int lines = (int)Math.Ceiling(Math.Max(1, text.Length) / charsPerLine);
            float neededHeight = lines * fontSize * 1.25f;
            if (neededHeight <= titleRect.Height) break;
            fontSize = Math.Max(minimum, fontSize - 2);
        }
        return fontSize;
    }

    private static void DrawFolder(Graphics g, RectangleF r)
    {
        // Folder artwork is available even when the saved folder is unavailable.
        using var back = new SolidBrush(Color.FromArgb(255, 191, 62));
        using var front = new SolidBrush(Color.FromArgb(255, 207, 86));
        using var path = new GraphicsPath();
        path.AddPolygon(new[] { new PointF(r.X, r.Y),
            new PointF(r.X + r.Width * .38f, r.Y),
            new PointF(r.X + r.Width * .48f, r.Y + r.Height * .19f),
            new PointF(r.Right, r.Y + r.Height * .19f), new PointF(r.Right, r.Bottom),
            new PointF(r.X, r.Bottom) });
        g.FillPath(back, path);
        g.FillRectangle(front, r.X, r.Y + r.Height * .31f, r.Width, r.Height * .69f);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _symbol?.Dispose(); _symbol = null; _overviewLogo = null; }
        base.Dispose(disposing);
    }
}

/// <summary>The icon covers the whole card behind the transparent title and backed text rows.</summary>
internal sealed class PlaceholderCardPanel(PlaceholderArtwork artwork) : TableLayoutPanel
{
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
        artwork.DrawIconBackground(e.Graphics, ClientRectangle);
    }
}
