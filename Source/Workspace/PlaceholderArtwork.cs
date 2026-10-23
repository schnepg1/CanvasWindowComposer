using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace CanvasDesktop;

/// <summary>Card artwork, independent of workspace actions and native placement.</summary>
internal sealed class PlaceholderArtwork : Control
{
    private Bitmap? _symbol;
    private string? _symbolKey;
    private bool _folder;

    public PlaceholderArtwork()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
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
            if (!item.IsFolder)
            {
                try
                {
                    if (File.Exists(item.ExecutablePath))
                    {
                        // Request the large resource instead of magnifying the
                        // tiny taskbar-sized associated icon into a watermark.
                        using var icon = Icon.ExtractIcon(item.ExecutablePath, 0, 256)
                            ?? Icon.ExtractAssociatedIcon(item.ExecutablePath);
                        _symbol = icon?.ToBitmap();
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or
                    UnauthorizedAccessException or System.ComponentModel.Win32Exception or
                    System.Runtime.InteropServices.ExternalException)
                { /* Missing/unreadable icons must never prevent a saved place opening. */ }
                _symbol ??= SystemIcons.Application.ToBitmap();
            }
        }
        Invalidate();
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        float dpi = DeviceDpi / 96f;
        float size = Math.Min(Width * .8f, Height * .9f);
        var symbolRect = new RectangleF(Width - size - 8 * dpi, (Height - size) / 2, size, size);
        if (_folder) DrawFolder(g, symbolRect);
        else if (_symbol != null && size > 0)
        {
            using var attributes = new ImageAttributes();
            attributes.SetColorMatrix(new ColorMatrix { Matrix33 = .16f });
            g.DrawImage(_symbol, Rectangle.Round(symbolRect), 0, 0, _symbol.Width, _symbol.Height,
                GraphicsUnit.Pixel, attributes);
        }
        using var captionFont = new Font("Segoe UI", 13 * dpi, FontStyle.Bold, GraphicsUnit.Pixel);
        using var captionBrush = new SolidBrush(Color.FromArgb(160, 181, 208));
        using var captionFormat = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near };
        var captionRect = new RectangleF(0, 4 * dpi, Width, 20 * dpi);
        g.DrawString(_folder ? "SAVED FOLDER" : "SAVED APPLICATION", captionFont, captionBrush, captionRect, captionFormat);
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
        g.DrawString(Text, titleFont, titleBrush, titleRect, format);
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
        // Vector folder stays sharp at any card size, including unavailable folders.
        using var back = new SolidBrush(Color.FromArgb(38, 255, 191, 62));
        using var front = new SolidBrush(Color.FromArgb(50, 255, 207, 86));
        using var path = new GraphicsPath();
        path.AddPolygon(new[] { new PointF(r.X, r.Y + r.Height * .24f),
            new PointF(r.X + r.Width * .38f, r.Y + r.Height * .24f),
            new PointF(r.X + r.Width * .48f, r.Y + r.Height * .35f),
            new PointF(r.Right, r.Y + r.Height * .35f), new PointF(r.Right, r.Y + r.Height * .82f),
            new PointF(r.X, r.Y + r.Height * .82f) });
        g.FillPath(back, path);
        g.FillRectangle(front, r.X, r.Y + r.Height * .42f, r.Width, r.Height * .4f);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _symbol?.Dispose(); _symbol = null; }
        base.Dispose(disposing);
    }
}
