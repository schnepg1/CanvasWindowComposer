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
    private string _caption = "APPLICATION";

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
        _caption = PlaceholderPathDisplay.For(item).Kind;
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
        using var captionFont = new Font("Segoe UI", 11 * dpi, FontStyle.Bold, GraphicsUnit.Pixel);
        using var captionBrush = new SolidBrush(Color.FromArgb(160, 181, 208));
        g.DrawString(_caption, captionFont, captionBrush, 2 * dpi, 4 * dpi);
        var titleRect = new RectangleF(0, 26 * dpi, Math.Max(1, Width - 8 * dpi), Math.Max(1, Height - 30 * dpi));
        using var format = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center,
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
