using System;
using System.Drawing;
using System.Windows.Forms;

namespace CanvasDesktop;

internal sealed class ModernColorTable : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => Color.FromArgb(20, 24, 33);
    public override Color MenuBorder => Color.FromArgb(50, 60, 80);
    public override Color MenuItemSelected => Color.FromArgb(35, 45, 65);
    public override Color MenuItemBorder => Color.Transparent;
    public override Color MenuItemSelectedGradientBegin => Color.FromArgb(35, 45, 65);
    public override Color MenuItemSelectedGradientEnd => Color.FromArgb(35, 45, 65);
    public override Color MenuItemPressedGradientBegin => Color.FromArgb(40, 52, 75);
    public override Color MenuItemPressedGradientEnd => Color.FromArgb(40, 52, 75);
    public override Color ImageMarginGradientBegin => Color.FromArgb(20, 24, 33);
    public override Color ImageMarginGradientMiddle => Color.FromArgb(20, 24, 33);
    public override Color ImageMarginGradientEnd => Color.FromArgb(20, 24, 33);
    public override Color ButtonSelectedHighlight => Color.FromArgb(35, 45, 65);
    public override Color ButtonSelectedHighlightBorder => Color.FromArgb(60, 75, 105);
    public override Color ButtonPressedHighlight => Color.FromArgb(28, 78, 160);
    public override Color ButtonPressedHighlightBorder => Color.FromArgb(96, 165, 250);
    public override Color ButtonCheckedHighlight => Color.FromArgb(30, 58, 95);
    public override Color ButtonCheckedHighlightBorder => Color.FromArgb(96, 165, 250);
    public override Color SeparatorDark => Color.FromArgb(50, 60, 80);
    public override Color SeparatorLight => Color.FromArgb(30, 36, 48);
}

internal sealed class ModernToolbarRenderer : ToolStripProfessionalRenderer
{
    public ModernToolbarRenderer() : base(new ModernColorTable()) { RoundedEdges = false; }
    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { }
    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var pen = new Pen(Color.FromArgb(50, 60, 80), 1);
        if (e.Vertical)
        {
            int mid = e.Item.Width / 2;
            e.Graphics.DrawLine(pen, mid, 6, mid, e.Item.Height - 7);
        }
        else
        {
            int mid = e.Item.Height / 2;
            e.Graphics.DrawLine(pen, 6, mid, e.Item.Width - 7, mid);
        }
    }
}

internal sealed class AnnotationToolbar : Form
{
    private readonly ToolStripButton _selectButton;
    private readonly ToolStripDropDownButton _shapesButton;
    private readonly ToolStripMenuItem _arrowItem, _rectangleItem, _ellipseItem;
    private readonly ToolStripButton _textButton;
    private readonly ToolStripLabel _status = new();
    private readonly ToolStripDropDownButton _nearby = new("Nearby");
    private readonly System.Collections.Generic.List<ToolStripDropDownItem> _dropDowns = new();
    private readonly NumericUpDown _radius = new() { Minimum = 1, Maximum = 100000, Increment = 100, Value = 1200, Width = 90, AccessibleName = "Selection radius in canvas units" };

    public event Action<double>? NearbyRequested;
    public event Action<AnnotationMode>? ModeRequested;
    public event Action<string>? ColorRequested;
    public event Action<double>? StrokeRequested;
    public event Action<double>? FontSizeRequested;
    public event Action<AnnotationTextAlign>? TextAlignRequested;
    public event Action<AnnotationTextEmphasis>? EmphasisRequested;
    public event Action<AnnotationCardFill>? CardFillRequested;
    public event Action? UndoRequested, RedoRequested, EditRequested, DeleteRequested, RetrySaveRequested, ComposerRequested;
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x80; return cp; } }

    public AnnotationToolbar()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(20, 24, 33);
        ForeColor = Color.White;
        Padding = new Padding(8, 4, 8, 4);

        var bar = new ToolStrip
        {
            // Tools must respond to the click that activates this floating form.
            AllowClickThrough = true,
            Dock = DockStyle.None,
            GripStyle = ToolStripGripStyle.Hidden,
            BackColor = BackColor,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9.25f, FontStyle.Regular),
            AutoSize = true,
            Renderer = new ModernToolbarRenderer()
        };
        Controls.Add(bar);

        _selectButton = new ToolStripButton("Select") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        _selectButton.Click += (_, _) => ModeRequested?.Invoke(AnnotationMode.Select);
        bar.Items.Add(_selectButton);

        _shapesButton = new ToolStripDropDownButton("Shapes") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        _arrowItem = new ToolStripMenuItem("Arrow", null, (_, _) => ModeRequested?.Invoke(AnnotationMode.Arrow)) { ForeColor = Color.White };
        _rectangleItem = new ToolStripMenuItem("Rectangle", null, (_, _) => ModeRequested?.Invoke(AnnotationMode.Rectangle)) { ForeColor = Color.White };
        _ellipseItem = new ToolStripMenuItem("Ellipse", null, (_, _) => ModeRequested?.Invoke(AnnotationMode.Ellipse)) { ForeColor = Color.White };
        _shapesButton.DropDownItems.AddRange([_arrowItem, _rectangleItem, _ellipseItem]);
        bar.Items.Add(_shapesButton);

        _textButton = new ToolStripButton("Text") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        _textButton.Click += (_, _) => ModeRequested?.Invoke(AnnotationMode.Text);
        bar.Items.Add(_textButton);

        bar.Items.Add(new ToolStripSeparator());

        var colors = new ToolStripDropDownButton("Color");
        foreach (var pair in new[] { ("Gold", "#FBBF24"), ("White", "#FFFFFF"), ("Coral", "#FB7185"), ("Sky", "#60A5FA"), ("Mint", "#34D399"), ("Lilac", "#C084FC") })
        {
            var item = colors.DropDownItems.Add(pair.Item1, null, (_, _) => ColorRequested?.Invoke(pair.Item2));
            item.ForeColor = Color.White;
        }
        bar.Items.Add(colors);

        var widths = new ToolStripDropDownButton("Stroke");
        foreach (var width in new[] { 1d, 3d, 6d })
        {
            var item = widths.DropDownItems.Add(width.ToString(), null, (_, _) => StrokeRequested?.Invoke(width));
            item.ForeColor = Color.White;
        }
        bar.Items.Add(widths);
        var sizes = new ToolStripDropDownButton("Text size");
        foreach (var size in new[] { 12d, 18d, 24d, 32d, 40d, 56d, 72d, 96d, 128d, 160d, 200d, 280d, 400d })
        {
            var item = sizes.DropDownItems.Add(size.ToString("0"), null, (_, _) => FontSizeRequested?.Invoke(size));
            item.ForeColor = Color.White;
        }
        bar.Items.Add(sizes);

        var textStyle = new ToolStripDropDownButton("Text");
        void Style(string title, Action action)
        {
            var item = textStyle.DropDownItems.Add(title, null, (_, _) => action());
            item.ForeColor = Color.White;
        }
        Style("Plain", () => EmphasisRequested?.Invoke(AnnotationTextEmphasis.Normal));
        Style("Bold", () => EmphasisRequested?.Invoke(AnnotationTextEmphasis.Bold));
        Style("Italic", () => EmphasisRequested?.Invoke(AnnotationTextEmphasis.Italic));
        Style("Bold italic", () => EmphasisRequested?.Invoke(AnnotationTextEmphasis.BoldItalic));
        textStyle.DropDownItems.Add(new ToolStripSeparator());
        Style("Align left", () => TextAlignRequested?.Invoke(AnnotationTextAlign.Left));
        Style("Align center", () => TextAlignRequested?.Invoke(AnnotationTextAlign.Center));
        Style("Align right", () => TextAlignRequested?.Invoke(AnnotationTextAlign.Right));
        textStyle.DropDownItems.Add(new ToolStripSeparator());
        Style("Solid card", () => CardFillRequested?.Invoke(AnnotationCardFill.Solid));
        Style("Tinted card", () => CardFillRequested?.Invoke(AnnotationCardFill.Tinted));
        Style("Outline card", () => CardFillRequested?.Invoke(AnnotationCardFill.None));
        bar.Items.Add(textStyle);
        bar.Items.Add(new ToolStripSeparator());

        void Button(string title, Action action)
        {
            var b = bar.Items.Add(title, null, (_, _) => action());
            b.ForeColor = Color.White;
        }

        Button("Undo", () => UndoRequested?.Invoke());
        Button("Redo", () => RedoRequested?.Invoke());
        Button("Edit", () => EditRequested?.Invoke());
        Button("Delete", () => DeleteRequested?.Invoke());
        Button("Save", () => RetrySaveRequested?.Invoke());
        Button("AI Compose", () => ComposerRequested?.Invoke());

        bar.Items.Add(new ToolStripSeparator());

        _radius.BackColor = Color.FromArgb(30, 36, 50);
        _radius.ForeColor = Color.White;
        _radius.BorderStyle = BorderStyle.FixedSingle;
        var radiusHost = new ToolStripControlHost(_radius);
        var radiusLabel = new ToolStripLabel("Radius in canvas units") { ForeColor = Color.FromArgb(148, 163, 184) };
        _nearby.DropDownItems.Add(radiusLabel);
        _nearby.DropDownItems.Add(radiusHost);
        var selectNearbyItem = _nearby.DropDownItems.Add("Select around camera", null, (_, _) => NearbyRequested?.Invoke((double)_radius.Value));
        selectNearbyItem.ForeColor = Color.White;
        bar.Items.Add(_nearby);

        _status.ForeColor = Color.FromArgb(148, 163, 184);
        bar.Items.Add(_status);

        foreach (ToolStripItem item in bar.Items)
        {
            if (item is ToolStripDropDownItem dd)
            {
                _dropDowns.Add(dd);
                dd.DropDownOpening += (_, _) =>
                {
                    if (IsHandleCreated) Windows.Win32.PInvoke.SetForegroundWindow((Windows.Win32.Foundation.HWND)Handle);
                };
            }
        }
    }

    public bool HasOpenDropDown
    {
        get
        {
            foreach (var d in _dropDowns)
                if (d.DropDown.Visible) return true;
            return false;
        }
    }

    public void CloseDropDowns()
    {
        foreach (var d in _dropDowns)
            if (d.DropDown.Visible) d.DropDown.Close();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) CloseDropDowns();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        CloseDropDowns();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && HasOpenDropDown)
        {
            CloseDropDowns();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Color.FromArgb(48, 56, 74), 1);
        e.Graphics.DrawLine(pen, 0, 0, Width, 0);
    }

    public void SetSelectionRadius(double radius) => _radius.Value = (decimal)Math.Clamp(radius, 1, 100000);

    public void UpdateState(AnnotationMode mode, string? status, int selectedCount = 0)
    {
        var activeMode = mode == AnnotationMode.Navigate ? AnnotationMode.Select : mode;
        _selectButton.Checked = activeMode == AnnotationMode.Select;
        bool isShape = activeMode is AnnotationMode.Arrow or AnnotationMode.Rectangle or AnnotationMode.Ellipse;
        _shapesButton.BackColor = isShape ? Color.FromArgb(30, 58, 95) : Color.Transparent;
        _shapesButton.ForeColor = isShape ? Color.FromArgb(96, 165, 250) : Color.White;
        _arrowItem.Checked = activeMode == AnnotationMode.Arrow;
        _rectangleItem.Checked = activeMode == AnnotationMode.Rectangle;
        _ellipseItem.Checked = activeMode == AnnotationMode.Ellipse;
        _textButton.Checked = activeMode == AnnotationMode.Text;

        _status.Text = status is null ? $"{selectedCount} selected" : "Warning";
        _status.ToolTipText = status ?? "";
    }
}
