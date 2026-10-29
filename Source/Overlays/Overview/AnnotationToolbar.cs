using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace CanvasDesktop;

internal sealed record AnnotationToolbarState(
    AnnotationMode Mode, string? Status = null, int SelectedCount = 0, CanvasAnnotation? Selected = null,
    string Color = "#FBBF24", double Stroke = 3, double FontSize = 18,
    AnnotationTextAlign TextAlign = AnnotationTextAlign.Left, AnnotationTextEmphasis Emphasis = AnnotationTextEmphasis.Normal,
    AnnotationCardFill CardFill = AnnotationCardFill.Solid, bool CanUndo = false, bool CanRedo = false,
    bool HasSelectedAnnotations = false, bool AnimateFeedback = true);

internal sealed class AnnotationToolbar : CanvasDockSurface
{
    private readonly ToolStripButton _selectButton, _textButton, _undo, _redo;
    private readonly ToolStripSplitButton _shapesButton;
    private readonly ToolStripDropDownButton _more, _colors, _widths, _sizes, _textStyle, _fills;
    private readonly ToolStripMenuItem _edit, _delete, _save;
    private readonly ToolStripLabel _status = new() { ForeColor = CanvasDockTheme.Muted };
    private readonly CanvasDockSurface _properties = new() { RoundBottom = true, Text = "Annotation properties", AccessibleName = "Annotation properties" };
    private readonly List<ToolStripDropDownItem> _dropDowns = [];
    private readonly List<Image> _images = [];
    private readonly NumericUpDown _radius = new() { Minimum = 1, Maximum = 100000, Increment = 100, Value = 1200, Width = 100, AccessibleName = "Selection radius in canvas units" };
    private readonly Bitmap _image = new(18, 18), _colorImage = new(18, 18);
    private AnnotationMode _lastShape = AnnotationMode.Rectangle;
    private AnnotationToolbarState? _state;
    private bool _hasProperties;
    private bool _layoutDirty;
    private int _availableWidth = int.MaxValue;

    public event Action<double>? NearbyRequested;
    public event Action<AnnotationMode>? ModeRequested;
    public event Action<string>? ColorRequested;
    public event Action<double>? StrokeRequested;
    public event Action<double>? FontSizeRequested;
    public event Action<AnnotationTextAlign>? TextAlignRequested;
    public event Action<AnnotationTextEmphasis>? EmphasisRequested;
    public event Action<AnnotationCardFill>? CardFillRequested;
    public event Action? UndoRequested, RedoRequested, EditRequested, DeleteRequested, RetrySaveRequested, ComposerRequested;
    public event Action? WebResourceRequested;
    internal CanvasDockSurface PropertiesSurface => _properties;

    public AnnotationToolbar()
    {
        Text = AccessibleName = "Canvas tools";
        Strip.CanOverflow = false;
        _images.AddRange([_image, _colorImage]);
        _selectButton = Button("Select", DockGlyph.Select, "Select and move objects", () => ModeRequested?.Invoke(AnnotationMode.Select), true);
        _shapesButton = new ToolStripSplitButton("Shapes", _image) { Name = "Shapes", AccessibleName = "Shapes", Tag = DockGlyph.Rectangle };
        _shapesButton.ButtonClick += (_, _) => ModeRequested?.Invoke(_lastShape);
        Decorate(_shapesButton);
        foreach (var (title, mode) in new[] { ("Rectangle", AnnotationMode.Rectangle), ("Ellipse", AnnotationMode.Ellipse), ("Arrow", AnnotationMode.Arrow) })
        {
            var item = MenuItem(title, () => { _lastShape = mode; ModeRequested?.Invoke(mode); });
            item.Tag = mode; _shapesButton.DropDownItems.Add(item);
        }
        _textButton = Button("Text", DockGlyph.Text, "Add a note · click or drag on the canvas", () => ModeRequested?.Invoke(AnnotationMode.Text), true);
        _undo = Button("Undo", DockGlyph.Undo, "Undo · Ctrl+Z", () => UndoRequested?.Invoke());
        _redo = Button("Redo", DockGlyph.Redo, "Redo · Ctrl+Y / Ctrl+Shift+Z", () => RedoRequested?.Invoke());
        _more = new("More", _image) { Name = "More", AccessibleName = "More canvas actions", Tag = DockGlyph.More, DisplayStyle = ToolStripItemDisplayStyle.Image, ShowDropDownArrow = false, ToolTipText = "More actions" };
        Decorate(_more);
        _edit = MenuItem("Edit annotation", () => EditRequested?.Invoke(), DockGlyph.Edit); _edit.ShortcutKeyDisplayString = "Enter";
        _delete = MenuItem("Delete annotation", () => DeleteRequested?.Invoke(), DockGlyph.Delete); _delete.ShortcutKeyDisplayString = "Delete";
        _save = MenuItem("Save now", () => RetrySaveRequested?.Invoke(), DockGlyph.Save);
        _more.DropDownItems.AddRange([_edit, _delete, new ToolStripSeparator(), _save]);
        var nearby = new ToolStripMenuItem("Select nearby") { Name = "Nearby", ForeColor = CanvasDockTheme.Ink };
        _radius.BackColor = CanvasDockTheme.Surface; _radius.ForeColor = CanvasDockTheme.Ink; _radius.BorderStyle = BorderStyle.FixedSingle;
        nearby.DropDownItems.Add(new ToolStripLabel("Radius in canvas units") { ForeColor = CanvasDockTheme.Muted });
        nearby.DropDownItems.Add(new ToolStripControlHost(_radius));
        nearby.DropDownItems.Add(MenuItem("Select around camera", () => NearbyRequested?.Invoke((double)_radius.Value)));
        _more.DropDownItems.Add(nearby);
        _more.DropDownItems.Add(new ToolStripSeparator());
        _more.DropDownItems.Add(MenuItem("AI Compose", () => ComposerRequested?.Invoke(), DockGlyph.Compose));
        _more.DropDownItems.Add(MenuItem("Add web resource…", () => WebResourceRequested?.Invoke()));
        _status.Click += (_, _) => { if (_state?.Status != null) RetrySaveRequested?.Invoke(); };
        Strip.Items.AddRange([_selectButton, _shapesButton, _textButton, new ToolStripSeparator(), _undo, _redo, _more, _status]);

        _colors = DropDown("Color", "Annotation color"); _colors.Image = _colorImage; _colors.Tag = DockGlyph.Color;
        foreach (var (title, hex) in new[] { ("Gold", "#FBBF24"), ("White", "#FFFFFF"), ("Coral", "#FB7185"), ("Sky", "#60A5FA"), ("Mint", "#34D399"), ("Lilac", "#C084FC") })
        {
            var swatch = new Bitmap(18, 18);
            using (var g = Graphics.FromImage(swatch))
            using (var brush = new SolidBrush(ColorTranslator.FromHtml(hex))) g.FillEllipse(brush, 2, 2, 14, 14);
            _images.Add(swatch);
            var item = MenuItem(title, () => ColorRequested?.Invoke(hex)); item.Tag = hex; item.Image = swatch;
            _colors.DropDownItems.Add(item);
        }
        _widths = DropDown("Stroke", "Stroke width");
        foreach (double width in new[] { 1d, 3d, 6d }) AddValue(_widths, $"{width:0} px", width, () => StrokeRequested?.Invoke(width));
        _sizes = DropDown("Size", "Text size");
        foreach (double size in new[] { 12d, 18d, 24d, 32d, 40d, 56d, 72d, 96d, 128d, 160d, 200d, 280d, 400d, 800d, 1600d, 3200d })
            AddValue(_sizes, size.ToString("0"), size, () => FontSizeRequested?.Invoke(size));
        _textStyle = DropDown("Style", "Text style and alignment");
        foreach (var (title, value) in new[] { ("Plain", AnnotationTextEmphasis.Normal), ("Bold", AnnotationTextEmphasis.Bold), ("Italic", AnnotationTextEmphasis.Italic), ("Bold italic", AnnotationTextEmphasis.BoldItalic) })
            AddValue(_textStyle, title, value, () => EmphasisRequested?.Invoke(value));
        _textStyle.DropDownItems.Add(new ToolStripSeparator());
        foreach (var (title, value) in new[] { ("Align left", AnnotationTextAlign.Left), ("Align center", AnnotationTextAlign.Center), ("Align right", AnnotationTextAlign.Right) })
            AddValue(_textStyle, title, value, () => TextAlignRequested?.Invoke(value));
        _fills = DropDown("Fill", "Card fill");
        foreach (var (title, value) in new[] { ("Solid", AnnotationCardFill.Solid), ("Tinted", AnnotationCardFill.Tinted), ("Outline", AnnotationCardFill.None) })
            AddValue(_fills, title, value, () => CardFillRequested?.Invoke(value));
        _properties.Strip.Items.AddRange([_colors, _widths, _sizes, _textStyle, _fills]);
        _properties.KeyDown += (_, e) => OnKeyDown(e);
        _properties.SizeChanged += (_, _) => PositionProperties();
        _properties.Deactivate += (_, _) => DismissOutsideMenus();
        foreach (var root in Strip.Items.OfType<ToolStripDropDownItem>().Concat(_properties.Strip.Items.OfType<ToolStripDropDownItem>())) RegisterMenu(root);
        RegisterMenu(_properties.Strip.OverflowButton);
        UpdateState(AnnotationMode.Select, null);
    }

    private void Decorate(ToolStripItem item)
    { item.Padding = new Padding(9, 10, 9, 10); item.Margin = new Padding(2, 0, 2, 0); item.ForeColor = CanvasDockTheme.Ink; }

    private ToolStripButton Button(string name, DockGlyph glyph, string hint, Action action, bool label = false)
    {
        var button = new ToolStripButton(name, _image, (_, _) => action())
        { Name = name, AccessibleName = name, Tag = glyph, ToolTipText = hint, DisplayStyle = label ? ToolStripItemDisplayStyle.ImageAndText : ToolStripItemDisplayStyle.Image };
        Decorate(button); return button;
    }
    private ToolStripMenuItem MenuItem(string title, Action action, DockGlyph? glyph = null)
        => new(title, glyph.HasValue ? _image : null, (_, _) => action())
        { Name = title, ForeColor = CanvasDockTheme.Ink, Tag = glyph, Padding = new Padding(5, 7, 5, 7) };
    private ToolStripDropDownButton DropDown(string name, string hint)
        => new(name) { Name = name, AccessibleName = hint, ToolTipText = hint, ForeColor = CanvasDockTheme.Ink, Padding = new Padding(5, 7, 5, 7), Margin = Padding.Empty };
    private void AddValue<T>(ToolStripDropDownButton parent, string title, T value, Action action)
    { var item = MenuItem(title, action); item.Tag = value; parent.DropDownItems.Add(item); }
    private static void CheckValues<T>(ToolStripDropDownItem parent, T value)
    {
        foreach (var item in parent.DropDownItems.OfType<ToolStripMenuItem>())
            if (item.Tag is T tag) item.Checked = EqualityComparer<T>.Default.Equals(tag, value);
    }
    private void RegisterMenu(ToolStripDropDownItem menu)
    {
        _dropDowns.Add(menu);
        menu.DropDown.Renderer = new CanvasDockRenderer(); menu.DropDown.BackColor = CanvasDockTheme.Surface; menu.DropDown.ForeColor = CanvasDockTheme.Ink;
        menu.DropDownOpening += (_, _) =>
        {
            var surface = menu.Owner?.FindForm() ?? this;
            if (surface.IsHandleCreated) PInvoke.SetForegroundWindow((HWND)surface.Handle);
        };
        foreach (var child in menu.DropDownItems.OfType<ToolStripDropDownItem>().Where(child => child.HasDropDownItems)) RegisterMenu(child);
    }
    public bool HasOpenDropDown => _dropDowns.Any(d => d.HasDropDownItems && d.DropDown.Visible);
    public void CloseDropDowns() { foreach (var menu in _dropDowns) if (menu.DropDown.Visible) menu.DropDown.Close(); }
    public void SetSelectionRadius(double radius) => _radius.Value = (decimal)Math.Clamp(radius, 1, 100000);

    public void SetAvailableWidth(int width)
    {
        if (_availableWidth == width && !_layoutDirty) return;
        _availableWidth = width;
        _layoutDirty = false;
        foreach (var item in new ToolStripItem[] { _selectButton, _shapesButton, _textButton }) item.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
        _status.Available = false;
        if (Strip.GetPreferredSize(Size.Empty).Width + Pixel(14) > width)
            foreach (var item in new ToolStripItem[] { _selectButton, _shapesButton, _textButton }) item.DisplayStyle = ToolStripItemDisplayStyle.Image;
        _status.Available = _state?.Status != null || _state?.SelectedCount > 0 && Strip.GetPreferredSize(Size.Empty).Width + Pixel(90) < width;
        if (_state?.Status != null) _status.Text = Strip.GetPreferredSize(Size.Empty).Width + Pixel(14) > width ? "!" : "Not saved";
        FitToContents(width); _properties.FitToContents(width); PositionProperties();
    }
    public void UpdateState(AnnotationMode mode, string? status, int selectedCount = 0) => UpdateState(new AnnotationToolbarState(mode, status, selectedCount));
    public void UpdateState(AnnotationToolbarState state)
    {
        if (_state == state) return;
        _state = state;
        _layoutDirty = true;
        var mode = state.Mode == AnnotationMode.Navigate ? AnnotationMode.Select : state.Mode;
        bool shape = mode is AnnotationMode.Rectangle or AnnotationMode.Ellipse or AnnotationMode.Arrow;
        if (shape) _lastShape = mode;
        _selectButton.Checked = mode == AnnotationMode.Select; _textButton.Checked = mode == AnnotationMode.Text;
        _shapesButton.BackColor = shape ? CanvasDockTheme.Active : CanvasDockTheme.Surface;
        _shapesButton.Tag = _lastShape switch { AnnotationMode.Arrow => DockGlyph.Arrow, AnnotationMode.Ellipse => DockGlyph.Ellipse, _ => DockGlyph.Rectangle };
        _shapesButton.ToolTipText = $"{_lastShape} · click to draw; use the arrow to choose a shape";
        _shapesButton.AccessibleDescription = shape ? $"{_lastShape} tool, active" : $"Draw {_lastShape.ToString().ToLowerInvariant()}";
        CheckValues(_shapesButton, mode);
        _undo.Enabled = state.CanUndo; _redo.Enabled = state.CanRedo;
        _edit.Enabled = state.Selected?.Kind is AnnotationKind.Text or AnnotationKind.Rectangle or AnnotationKind.Ellipse;
        _delete.Enabled = state.HasSelectedAnnotations || state.Selected != null;
        _save.Text = state.Status == null ? "Save now" : "Retry saving";
        _status.Text = state.Status == null ? $"{state.SelectedCount} selected" : "Not saved";
        _status.ToolTipText = state.Status ?? "Selected canvas objects";
        _status.ForeColor = state.Status == null ? CanvasDockTheme.Muted : Color.FromArgb(251, 191, 36);
        _status.IsLink = state.Status != null;
        _status.LinkColor = _status.ActiveLinkColor = _status.VisitedLinkColor = _status.ForeColor;
        _status.Available = state.Status != null || state.SelectedCount > 0;
        bool text = mode == AnnotationMode.Text || mode == AnnotationMode.Select && state.Selected?.Kind == AnnotationKind.Text;
        bool selectedShape = mode == AnnotationMode.Select && state.Selected?.Kind is AnnotationKind.Rectangle or AnnotationKind.Ellipse;
        _hasProperties = text || shape || state.Selected != null;
        _widths.Available = _hasProperties && !text;
        _sizes.Available = _textStyle.Available = text; _fills.Available = text || selectedShape;
        var selected = state.Selected;
        string color = selected?.Color ?? state.Color;
        _colorImage.Tag = ColorTranslator.FromHtml(color);
        double stroke = selected?.Stroke ?? state.Stroke, size = selected?.FontSize ?? state.FontSize;
        _widths.Text = $"{stroke:0.#} px"; _sizes.Text = $"Size {size:0}";
        CheckValues(_colors, color); CheckValues(_widths, stroke); CheckValues(_sizes, size);
        CheckValues(_textStyle, selected?.TextAlign ?? state.TextAlign); CheckValues(_textStyle, selected?.Emphasis ?? state.Emphasis); CheckValues(_fills, selected?.CardFill ?? state.CardFill);
        Strip.AnimateFeedback = _properties.Strip.AnimateFeedback = state.AnimateFeedback;
        FitToContents(); _properties.FitToContents(_availableWidth);
        Strip.Invalidate(); _properties.Strip.Invalidate();
        if (Visible) ShowProperties();
    }
    private void PositionProperties()
    {
        if (_properties.IsDisposed) return;
        var area = Screen.FromRectangle(Bounds).Bounds;
        _properties.Location = new(Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - _properties.Width)), Math.Max(area.Top, Top - _properties.Height - Pixel(8)));
    }
    private void ShowProperties()
    {
        if (!_hasProperties) { _properties.Hide(); return; }
        PositionProperties(); if (!_properties.Visible) _properties.Show(this);
    }
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (_properties == null) return;
        if (Visible) ShowProperties(); else { CloseDropDowns(); _properties.Hide(); }
    }
    protected override void OnLocationChanged(EventArgs e) { base.OnLocationChanged(e); if (_properties != null) PositionProperties(); }
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); if (_properties != null) PositionProperties(); }
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        int available = _availableWidth; _availableWidth = int.MaxValue; SetAvailableWidth(available);
    }
    protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); DismissOutsideMenus(); }
    private void DismissOutsideMenus()
    {
        if (!IsHandleCreated || IsDisposed) return;
        BeginInvoke((Action)(() =>
        {
            if (IsDisposed || !Visible) return;
            var foreground = PInvoke.GetForegroundWindow();
            if (foreground == (HWND)Handle || _properties.IsHandleCreated && foreground == (HWND)_properties.Handle) return;
            if (_dropDowns.Any(d => d.DropDown.Visible && foreground == (HWND)d.DropDown.Handle)) return;
            CloseDropDowns();
        }));
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && HasOpenDropDown) { CloseDropDowns(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _properties.Dispose(); foreach (var image in _images) image.Dispose(); }
        base.Dispose(disposing);
    }
}
