using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CanvasDesktop;

internal enum DockGlyph { Select, Rectangle, Ellipse, Arrow, Text, Undo, Redo, More, Edit, Delete, Save, Compose, Color }

internal static class CanvasDockTheme
{
    public static Color Surface => SystemInformation.HighContrast ? SystemColors.Control : Color.FromArgb(28, 35, 48);
    public static Color Ink => SystemInformation.HighContrast ? SystemColors.ControlText : Color.FromArgb(237, 242, 250);
    public static Color Muted => SystemInformation.HighContrast ? SystemColors.GrayText : Color.FromArgb(164, 177, 196);
    public static Color Border => SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(53, 65, 85);
    public static Color Active => SystemInformation.HighContrast ? SystemColors.Highlight : Color.FromArgb(157, 192, 255);
    public static Color ActiveInk => SystemInformation.HighContrast ? SystemColors.HighlightText : Color.FromArgb(16, 37, 72);

    public static GraphicsPath Outline(RectangleF bounds, float radius, bool roundBottom = true)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        if (roundBottom)
        {
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        }
        else
        {
            path.AddLine(bounds.Right, bounds.Top + d / 2, bounds.Right, bounds.Bottom);
            path.AddLine(bounds.Right, bounds.Bottom, bounds.Left, bounds.Bottom);
        }
        path.CloseFigure();
        return path;
    }
}

/// <summary>Finite hover feedback. There is no animation timer running while the dock is idle or hidden.</summary>
internal sealed class CanvasDockStrip : ToolStrip
{
    private readonly Dictionary<ToolStripItem, float> _hover = new();
    private readonly Timer _feedback = new() { Interval = 16 };
    private readonly Font _font = new("Segoe UI", 9.5f);
    private ToolStripItem? _hovered;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool AnimateFeedback { get; set; } = true;
    private bool MotionEnabled => AnimateFeedback && !SystemInformation.HighContrast && System.Windows.SystemParameters.ClientAreaAnimation;

    public CanvasDockStrip()
    {
        AllowClickThrough = true;
        GripStyle = ToolStripGripStyle.Hidden;
        Dock = DockStyle.None;
        BackColor = CanvasDockTheme.Surface;
        ForeColor = CanvasDockTheme.Ink;
        Padding = Padding.Empty;
        Font = _font;
        Renderer = new CanvasDockRenderer();
        DefaultDropDownDirection = ToolStripDropDownDirection.AboveRight;
        _feedback.Tick += (_, _) =>
        {
            bool pending = false;
            foreach (ToolStripItem item in Items)
            {
                float target = item.Enabled && item.Selected ? 1 : 0;
                float current = _hover.GetValueOrDefault(item);
                float next = Math.Abs(current - target) < .025f ? target : current + (target - current) * .45f;
                _hover[item] = next;
                pending |= next != target;
            }
            Invalidate();
            if (!pending) _feedback.Stop();
        };
    }

    public float Hover(ToolStripItem item) => MotionEnabled ? _hover.GetValueOrDefault(item) : item.Selected ? 1 : 0;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var item = GetItemAt(e.Location);
        if (item == _hovered) return;
        _hovered = item;
        if (MotionEnabled) _feedback.Start(); else Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = null;
        if (MotionEnabled) _feedback.Start(); else Invalidate();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) { _feedback.Stop(); _hover.Clear(); _hovered = null; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _feedback.Dispose(); _hover.Clear(); }
        base.Dispose(disposing);
        if (disposing) _font.Dispose();
    }
}

internal sealed class CanvasDockColorTable : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => CanvasDockTheme.Surface;
    public override Color ImageMarginGradientBegin => CanvasDockTheme.Surface;
    public override Color ImageMarginGradientMiddle => CanvasDockTheme.Surface;
    public override Color ImageMarginGradientEnd => CanvasDockTheme.Surface;
    public override Color MenuBorder => CanvasDockTheme.Border;
    public override Color MenuItemSelected => SystemInformation.HighContrast ? SystemColors.Highlight : Color.FromArgb(44, 55, 75);
    public override Color MenuItemSelectedGradientBegin => MenuItemSelected;
    public override Color MenuItemSelectedGradientEnd => MenuItemSelected;
    public override Color MenuItemBorder => Color.Transparent;
}

internal sealed class CanvasDockRenderer() : ToolStripProfessionalRenderer(new CanvasDockColorTable())
{
    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        if (e.ToolStrip is ToolStripDropDown) base.OnRenderToolStripBorder(e);
    }

    private static bool Active(ToolStripItem item) => item is ToolStripButton { Checked: true }
        || item is ToolStripSplitButton { Tag: DockGlyph.Rectangle or DockGlyph.Ellipse or DockGlyph.Arrow, BackColor: var color } && color == CanvasDockTheme.Active;

    private static Color Ink(ToolStripItem item) => !item.Enabled ? CanvasDockTheme.Muted
        : Active(item) ? CanvasDockTheme.ActiveInk : CanvasDockTheme.Ink;

    private static void Background(ToolStripItemRenderEventArgs e)
    {
        var rect = new RectangleF(0, 1, e.Item.Width - 1, e.Item.Height - 2);
        bool active = Active(e.Item);
        float hover = e.ToolStrip is CanvasDockStrip strip ? strip.Hover(e.Item) : e.Item.Selected ? 1 : 0;
        Color color = active ? CanvasDockTheme.Active : e.Item.Pressed ? Color.FromArgb(64, 81, 109)
            : Color.FromArgb((int)(hover * 255), 44, 55, 75);
        if (SystemInformation.HighContrast && !active && (e.Item.Selected || e.Item.Pressed)) color = SystemColors.Highlight;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = CanvasDockTheme.Outline(rect, 9 * (e.Item.Owner?.DeviceDpi ?? 96) / 96f);
        using var brush = new SolidBrush(color);
        e.Graphics.FillPath(brush, path);
        if (e.Item.Selected && e.ToolStrip?.ContainsFocus == true)
        {
            using var pen = new Pen(CanvasDockTheme.Border);
            e.Graphics.DrawPath(pen, path);
        }
    }

    protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e) => Background(e);
    protected override void OnRenderDropDownButtonBackground(ToolStripItemRenderEventArgs e) => Background(e);
    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        if (e.Item != null) e.ArrowColor = Ink(e.Item);
        if (e.Item?.Owner is CanvasDockStrip && e.Direction == ArrowDirection.Down) e.Direction = ArrowDirection.Up;
        base.OnRenderArrow(e);
    }
    protected override void OnRenderSplitButtonBackground(ToolStripItemRenderEventArgs e)
    {
        Background(e);
        var item = (ToolStripSplitButton)e.Item;
        var rect = item.DropDownButtonBounds;
        var center = new Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
        int dpi = e.Item.Owner?.DeviceDpi ?? 96;
        using var pen = new Pen(Ink(item), Math.Max(1, dpi / 96f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        int size = Math.Max(3, 3 * dpi / 96);
        e.Graphics.DrawLines(pen, [new(center.X - size, center.Y + 1), new(center.X, center.Y - 2), new(center.X + size, center.Y + 1)]);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item is ToolStripLabel ? e.Item.ForeColor : Ink(e.Item);
        if (SystemInformation.HighContrast && e.Item.Selected) e.TextColor = SystemColors.HighlightText;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var pen = new Pen(CanvasDockTheme.Border);
        if (e.Vertical) e.Graphics.DrawLine(pen, e.Item.Width / 2, 11, e.Item.Width / 2, e.Item.Height - 11);
        else e.Graphics.DrawLine(pen, 9, e.Item.Height / 2, e.Item.Width - 9, e.Item.Height / 2);
    }

    protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
    {
        if (e.Item.Tag is not DockGlyph glyph) { base.OnRenderItemImage(e); return; }
        var g = e.Graphics;
        var state = g.Save();
        try
        {
            g.TranslateTransform(e.ImageRectangle.Left, e.ImageRectangle.Top + (e.Item.Pressed ? 1 : 0));
            g.ScaleTransform(e.ImageRectangle.Width / 20f, e.ImageRectangle.Height / 20f);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(Ink(e.Item), 1.55f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            switch (glyph)
            {
                case DockGlyph.Select: g.DrawPolygon(pen, [new PointF(4, 2), new(16, 10), new(10, 11), new(7, 17)]); break;
                case DockGlyph.Rectangle: g.DrawRectangle(pen, 3, 3, 14, 14); break;
                case DockGlyph.Ellipse: g.DrawEllipse(pen, 3, 3, 14, 14); break;
                case DockGlyph.Arrow: g.DrawLine(pen, 4, 16, 16, 4); g.DrawLines(pen, [new PointF(7, 4), new(16, 4), new(16, 13)]); break;
                case DockGlyph.Text: g.DrawLine(pen, 3, 4, 17, 4); g.DrawLine(pen, 10, 4, 10, 17); g.DrawLine(pen, 7, 17, 13, 17); break;
                case DockGlyph.Redo:
                case DockGlyph.Undo:
                    if (glyph == DockGlyph.Redo) { g.TranslateTransform(20, 0); g.ScaleTransform(-1, 1); }
                    g.DrawLines(pen, [new PointF(7, 3), new(3, 7), new(7, 11)]);
                    g.DrawBezier(pen, new(3, 7), new(18, 3), new(21, 18), new(9, 17)); break;
                case DockGlyph.More:
                    using (var brush = new SolidBrush(Ink(e.Item))) foreach (int x in new[] { 4, 9, 14 }) g.FillEllipse(brush, x, 9, 2.5f, 2.5f); break;
                case DockGlyph.Edit: g.DrawLines(pen, [new PointF(4, 13), new(13, 4), new(17, 8), new(8, 17), new(3, 18), new(4, 13)]); break;
                case DockGlyph.Delete: g.DrawLine(pen, 3, 5, 17, 5); g.DrawRectangle(pen, 6, 2, 8, 3); g.DrawLines(pen, [new PointF(5, 5), new(6, 17), new(14, 17), new(15, 5)]); break;
                case DockGlyph.Save: g.DrawRectangle(pen, 3, 3, 14, 14); g.DrawRectangle(pen, 6, 3, 8, 5); g.DrawRectangle(pen, 6, 12, 8, 5); break;
                case DockGlyph.Compose: g.DrawPolygon(pen, [new PointF(10, 2), new(12, 8), new(18, 10), new(12, 12), new(10, 18), new(8, 12), new(2, 10), new(8, 8)]); break;
                case DockGlyph.Color:
                    using (var brush = new SolidBrush(e.Item.Image!.Tag is Color color ? color : CanvasDockTheme.Active)) g.FillEllipse(brush, 3, 3, 14, 14);
                    g.DrawEllipse(pen, 3, 3, 14, 14); break;
            }
        }
        finally { g.Restore(state); }
    }
}

/// <summary>Opaque native dock surface, with rounded upper corners and a flush lower edge.</summary>
internal class CanvasDockSurface : Form
{
    public CanvasDockStrip Strip { get; } = new();
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool RoundBottom { get; init; }
    private int _availableWidth = int.MaxValue;
    private bool _fitting;
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x80; return cp; } }

    public CanvasDockSurface()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.Manual;
        BackColor = CanvasDockTheme.Surface;
        ForeColor = CanvasDockTheme.Ink;
        DoubleBuffered = true;
        Strip.AutoSize = false;
        Controls.Add(Strip);
        Strip.Layout += (_, _) => FitToContents();
    }

    public int Pixel(int logical) => (int)Math.Round(logical * DeviceDpi / 96.0);
    public void FitToContents(int? availableWidth = null)
    {
        if (availableWidth is {} width) _availableWidth = Math.Max(Pixel(100), width);
        if (_fitting || IsDisposed) return;
        _fitting = true;
        try
        {
            int pad = Pixel(7);
            var size = Strip.GetPreferredSize(Size.Empty);
            Strip.Size = new(Math.Min(size.Width, _availableWidth - pad * 2), Math.Max(Pixel(42), size.Height));
            Strip.Location = new(pad, pad);
            ClientSize = new(Strip.Width + pad * 2, Strip.Height + pad * 2);
        }
        finally { _fitting = false; }
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Width < 2 || Height < 2) return;
        using var outline = CanvasDockTheme.Outline(new(0, 0, Width, Height), Pixel(16), RoundBottom);
        var previous = Region;
        Region = new Region(outline);
        previous?.Dispose();
        Invalidate();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); FitToContents(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var outline = CanvasDockTheme.Outline(new(.5f, .5f, Width - 1, Height - 1), Pixel(16), RoundBottom);
        using var pen = new Pen(CanvasDockTheme.Border);
        e.Graphics.DrawPath(pen, outline);
    }
}
