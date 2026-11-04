using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;

namespace CanvasDesktop;

/// <summary>Modeless background picker and explicit-save editor. Image decoding belongs to the shared cache.</summary>
internal sealed class BackgroundWindow : Window
{
    private readonly BackgroundService _service;
    private readonly BackgroundAssets _assets;
    private readonly BackgroundImageCache _cache;
    private readonly Func<IReadOnlyList<ZoneView>> _zones;
    private readonly Func<WorldRect> _defaultBounds;
    private readonly Action<Guid> _editOnCanvas;
    private readonly Action<BackgroundSurface> _navigate;
    private readonly ListBox _list = new() { Name = "BackgroundList", Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
    private readonly TextBox _name = new() { Name = "BackgroundName", MaxLength = 120 };
    private readonly TextBox _x = Number("BackgroundX"), _y = Number("BackgroundY"), _width = Number("BackgroundWidth"), _height = Number("BackgroundHeight");
    private readonly TextBox _z = Number("BackgroundOrder");
    private readonly ComboBox _project = new() { Name = "BackgroundProject", MinHeight = 36 };
    private readonly ComboBox _fit = new() { Name = "BackgroundFit", ItemsSource = Enum.GetValues<BackgroundFit>(), MinHeight = 36 };
    private readonly Slider _cropX = Range("BackgroundCropX"), _cropY = Range("BackgroundCropY");
    private readonly Slider _opacity = Range("BackgroundOpacity"), _dim = Range("BackgroundDim");
    private readonly CheckBox _locked = new() { Name = "BackgroundLocked", Content = "Lock background during normal canvas use", Margin = new Thickness(0, 12, 0, 0) };
    private readonly CheckBox _external = new() { Name = "LinkExternalImage",
        Content = new TextBlock { Text = "Link to original file instead of copying", TextWrapping = TextWrapping.Wrap },
        Margin = new Thickness(0, 8, 0, 8) };
    private readonly Image _preview = new() { Height = 130, Stretch = Stretch.UniformToFill };
    private readonly TextBlock _source = Muted();
    private readonly TextBlock _message = Muted();
    private readonly StackPanel _editor = new();
    private readonly UniformGrid _actions = new() { Columns = 2, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Dictionary<string, Image> _presetImages = new();
    private readonly Button _undo, _redo, _editCanvas;
    private BackgroundSurface? _draftBase;
    private Guid _desktop;
    private bool _refreshing, _dirty, _closed, _busy;
    private string? _renderingStatus;
    internal bool HasPendingChanges => _dirty || _busy;
    internal bool IsChoosingImage { get; private set; }

    internal void SetCanvasOwner(IntPtr owner)
    {
        var interop=new WindowInteropHelper(this);
        if(interop.Owner!=owner)interop.Owner=owner;
        Topmost=owner!=IntPtr.Zero;
    }

    internal void RaiseWithoutActivation()
    {
        if(!IsVisible || !Topmost)return;
        PInvoke.SetWindowPos((HWND)new WindowInteropHelper(this).Handle,(HWND)new IntPtr(-1),0,0,0,0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE|SET_WINDOW_POS_FLAGS.SWP_NOSIZE|SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
    }
    public void SetRenderingStatus(string? status) { _renderingStatus=status;if(status!=null)Report(status); }

    public BackgroundWindow(BackgroundService service, BackgroundAssets assets, BackgroundImageCache cache,
        Func<IReadOnlyList<ZoneView>> zones, Func<WorldRect> defaultBounds,
        Action<Guid> editOnCanvas, Action<BackgroundSurface> navigate)
    {
        _service = service; _assets = assets; _cache = cache; _zones = zones;
        _defaultBounds = defaultBounds; _editOnCanvas = editOnCanvas; _navigate = navigate;
        _desktop = service.DesktopId;
        Title = "Backgrounds · Canvas";
        Width = 1040; Height = 880; MinWidth = 880; MinHeight = 680;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ZoneTheme.Apply(this);
        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Content = root;

        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = "Make a place for your ideas", FontSize = 28, FontWeight = FontWeights.SemiBold });
        var intro = Muted(); intro.Text = "Add a room, landscape or texture behind your apps. Backgrounds move with the canvas and can span several screens.";
        intro.Margin = new Thickness(0, 6, 0, 14); header.Children.Add(intro);
        var presets = new UniformGrid { Columns = 4 };
        foreach (var preset in new[] { ("study", "Fireplace study"), ("bright", "Bright workspace"), ("nature", "Quiet nature"), ("abstract", "Abstract texture") })
        {
            string assetId = "builtin:" + preset.Item1;
            var image = new Image { Height = 76, Stretch = Stretch.UniformToFill, IsHitTestVisible = false };
            _presetImages.Add(assetId, image);
            var panel = new StackPanel(); panel.Children.Add(image);
            panel.Children.Add(new TextBlock { Text = "+  " + preset.Item2, Margin = new Thickness(0, 8, 0, 0), FontWeight = FontWeights.SemiBold });
            var button = Button("", () => Add(assetId, preset.Item2));
            button.Content = panel; button.Padding = new Thickness(10); button.Margin = new Thickness(0, 0, 8, 0);
            AutomationProperties.SetName(button, "Add " + preset.Item2 + " background");
            presets.Children.Add(button);
        }
        header.Children.Add(presets);
        root.Children.Add(header);

        var body = new Grid { Margin = new Thickness(0, 20, 0, 0) };
        body.ColumnDefinitions.Add(new() { Width = new GridLength(260) });
        body.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(body, 1); root.Children.Add(body);
        var sidebar = new DockPanel { Margin = new Thickness(0, 0, 24, 0) };
        body.Children.Add(sidebar);
        var imports = new StackPanel(); DockPanel.SetDock(imports, Dock.Top); sidebar.Children.Add(imports);
        imports.Children.Add(Button("Import image…", () => _ = ImportAsync(replace: false)));
        imports.Children.Add(_external);
        var help = Muted(); help.FontSize = 11; help.Text = "Copying keeps your background available after the original file moves.";
        help.Margin = new Thickness(0, 0, 0, 12); imports.Children.Add(help);
        var history = new UniformGrid { Columns = 2, Margin = new Thickness(0, 8, 0, 0) };
        _undo = Button("Undo", () => Replay(false)); _redo = Button("Redo", () => Replay(true));
        _undo.Margin = new Thickness(0, 4, 4, 0); _redo.Margin = new Thickness(4, 4, 0, 0);
        history.Children.Add(_undo); history.Children.Add(_redo);
        DockPanel.SetDock(history, Dock.Bottom); sidebar.Children.Add(history);
        AutomationProperties.SetName(_list, "Backgrounds on the current virtual desktop");
        sidebar.Children.Add(_list);
        _list.SelectionChanged += (_, _) => { if (!_refreshing) LoadSelection(); };

        var editorHost = new DockPanel();
        Grid.SetColumn(editorHost, 1); body.Children.Add(editorHost);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 12, 0), Content = _editor };
        editorHost.Children.Add(scroll);
        _editor.Children.Add(new Border { Height = 130, CornerRadius = new CornerRadius(8), ClipToBounds = true,
            Background = ZoneTheme.Brush("#1A2538"), Child = _preview });
        _source.Margin = new Thickness(0, 6, 0, 10); _source.FontSize = 11; _editor.Children.Add(_source);
        Label(_editor, "Name", _name);
        Label(_editor, "Placement", _project);
        var geometry = new UniformGrid { Columns = 4 };
        foreach (var field in new[] { ("X", _x), ("Y", _y), ("Width", _width), ("Height", _height) })
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
            Label(panel, field.Item1, field.Item2); geometry.Children.Add(panel);
        }
        _editor.Children.Add(geometry);
        var areaHelp = Muted(); areaHelp.Text = "A project background follows its area's boundary. Detach it to edit its size independently.";
        areaHelp.FontSize = 11; areaHelp.Margin = new Thickness(0, 6, 0, 0); _editor.Children.Add(areaHelp);
        Label(_editor, "Image sizing", _fit);
        var crop = new UniformGrid { Columns = 2 };
        AddSlider(crop, "Crop · left / right", _cropX); AddSlider(crop, "Crop · top / bottom", _cropY);
        _editor.Children.Add(crop);
        var appearance = new UniformGrid { Columns = 2 };
        AddSlider(appearance, "Opacity", _opacity); AddSlider(appearance, "Darken", _dim);
        _editor.Children.Add(appearance);
        Label(_editor, "Stacking order · larger values appear above other backgrounds", _z);
        _editor.Children.Add(_locked);
        var actions = _actions;
        var save = Button("Save changes", SaveDraft); save.Background = ZoneTheme.Brush("#2563EB");
        save.Name = "SaveBackground";
        _editCanvas = Button("Edit on canvas (unlocks)", EditOnCanvas);
        actions.Children.Add(save); actions.Children.Add(_editCanvas);
        actions.Children.Add(Button("Go to background", () => { if (Selected is { } item) { Hide(); _navigate(item); } }));
        actions.Children.Add(Button("Locate / replace image…", () => _ = ImportAsync(replace: true)));
        actions.Children.Add(Button("Reload saved values", LoadSelection));
        actions.Children.Add(Button("Remove background", Remove));
        foreach (FrameworkElement element in actions.Children) element.Margin = new Thickness(0, 4, 8, 0);
        DockPanel.SetDock(actions, Dock.Bottom); editorHost.Children.Insert(0, actions);

        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        Grid.SetRow(footer, 2); root.Children.Add(footer);
        var retry = Button("Retry save", () => Report(_service.Save() ? "Backgrounds saved." : _service.Status ?? "Saving failed. Try again."));
        retry.Margin = new Thickness(16, 0, 0, 0); DockPanel.SetDock(retry, Dock.Right); footer.Children.Add(retry);
        footer.Children.Add(_message);
        foreach (var text in new[] { _name, _x, _y, _width, _height, _z }) text.TextChanged += (_, _) => MarkDirty();
        foreach (var slider in new[] { _cropX, _cropY, _opacity, _dim }) slider.ValueChanged += (_, _) => MarkDirty();
        _locked.Checked += (_, _) => MarkDirty(); _locked.Unchecked += (_, _) => MarkDirty();
        _fit.SelectionChanged += (_, _) => { UpdateCropEnabled(); MarkDirty(); };
        _project.SelectionChanged += (_, _) => { if (!_refreshing) { ApplyProjectGeometry(); MarkDirty(); } };
        _service.Changed += Refresh;
        _cache.Changed += RefreshImages;
        PreviewKeyDown += (_,e) =>
        {
            if(e.Key==System.Windows.Input.Key.Escape && !_busy) { Hide();e.Handled=true; }
        };
        Closed += (_, _) => { _closed = true; _service.Changed -= Refresh; _cache.Changed -= RefreshImages; };
        Refresh();
    }

    private static TextBox Number(string name) => new() { Name = name, MinWidth = 50 };
    private static Slider Range(string name) => new() { Name = name, Minimum = 0, Maximum = 1, SmallChange = .01, LargeChange = .1,
        Margin = new Thickness(0, 4, 12, 4), IsMoveToPointEnabled = true };
    private static TextBlock Muted() => new() { Foreground = ZoneTheme.Brush("#A7B3C9"), TextWrapping = TextWrapping.Wrap };
    private static Button Button(string caption, Action action) => ZoneTheme.Button(caption, action);
    private static void Label(Panel panel, string caption, FrameworkElement control)
    {
        panel.Children.Add(new TextBlock { Text = caption, Margin = new Thickness(0, 12, 0, 6), TextWrapping = TextWrapping.Wrap });
        AutomationProperties.SetName(control, caption); panel.Children.Add(control);
    }
    private static void AddSlider(Panel panel, string caption, Slider slider)
    { var stack = new StackPanel(); Label(stack, caption, slider); panel.Children.Add(stack); }
    private BackgroundSurface? Selected => (_list.SelectedItem as ListBoxItem)?.Tag as BackgroundSurface;
    private Guid? ProjectId => (_project.SelectedItem as ComboBoxItem)?.Tag is Guid id ? id : null;
    private static string Format(double value) => value.ToString("0.################", CultureInfo.CurrentCulture);
    private void Report(string message) => _message.Text = message;
    private void MarkDirty() { if (!_refreshing) _dirty = true; }

    public void Select(Guid id)
    {
        _list.SelectedItem = _list.Items.Cast<ListBoxItem>().FirstOrDefault(row => ((BackgroundSurface)row.Tag).Id == id);
        LoadSelection();
    }

    private void Refresh()
    {
        if (_closed) return;
        bool desktopChanged = _desktop != _service.DesktopId;
        _desktop = _service.DesktopId;
        Guid? selected = desktopChanged ? null : Selected?.Id;
        bool preserveDraft = !desktopChanged && _dirty;
        _refreshing = true;
        _list.Items.Clear();
        foreach (var item in _service.Items)
        {
            var content = new StackPanel { Margin = new Thickness(6, 9, 6, 9) };
            content.Children.Add(new TextBlock { Text = item.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            var detail = Muted(); detail.FontSize = 11; detail.Margin = new Thickness(0, 4, 0, 0);
            detail.Text = $"{(item.ZoneId == null ? "Free background" : "Project background")} · {item.Bounds.W:N0} × {item.Bounds.H:N0}";
            content.Children.Add(detail);
            _list.Items.Add(new ListBoxItem { Content = content, Tag = item, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        }
        _list.SelectedItem = _list.Items.Cast<ListBoxItem>().FirstOrDefault(row => ((BackgroundSurface)row.Tag).Id == selected);
        if (_list.SelectedItem == null && _list.Items.Count > 0) _list.SelectedIndex = 0;
        _undo.IsEnabled = _service.CanUndo; _redo.IsEnabled = _service.CanRedo;
        _refreshing = false;
        if (!preserveDraft || Selected?.Id != _draftBase?.Id) LoadSelection();
        else if (Selected != _draftBase) Report("This background changed while you were editing. Reload saved values before saving your draft.");
        RefreshImages();
        if (_service.Status != null) Report(_service.Status);
        else if (_renderingStatus != null) Report(_renderingStatus);
        else if (_list.Items.Count == 0) Report("Choose a starter scene or import an image to create your first background.");
    }

    private void LoadSelection()
    {
        if (_refreshing || _closed) return;
        _refreshing = true;
        _draftBase = Selected;
        _dirty = false;
        _editor.IsEnabled = _draftBase != null;
        _actions.IsEnabled = _draftBase != null;
        _name.Text = _draftBase?.Name ?? "";
        var bounds = _draftBase?.Bounds ?? default;
        _x.Text = Format(bounds.X); _y.Text = Format(bounds.Y); _width.Text = Format(bounds.W); _height.Text = Format(bounds.H);
        _project.Items.Clear();
        _project.Items.Add(new ComboBoxItem { Content = "Free background", Tag = null });
        foreach (var zone in _zones().Where(zone => !zone.IsTemporaryArea))
            _project.Items.Add(new ComboBoxItem { Content = zone.Name, Tag = zone.Id });
        _project.SelectedItem = _project.Items.Cast<ComboBoxItem>().FirstOrDefault(row => row.Tag is Guid id && id == _draftBase?.ZoneId)
            ?? _project.Items[0];
        _fit.SelectedItem = _draftBase?.Fit ?? BackgroundFit.Fill;
        _cropX.Value = _draftBase?.CropX ?? .5; _cropY.Value = _draftBase?.CropY ?? .5;
        _opacity.Value = _draftBase?.Opacity ?? 1; _dim.Value = _draftBase?.Dim ?? 0;
        _z.Text = (_draftBase?.ZIndex ?? 0).ToString(CultureInfo.CurrentCulture);
        _locked.IsChecked = _draftBase?.Locked ?? true;
        UpdateCropEnabled(); ApplyProjectGeometry();
        _refreshing = false;
        RefreshImages();
        if (_draftBase != null) Report("Edit the frame and appearance, then save. Your apps stay in place.");
    }

    private void UpdateCropEnabled() => _cropX.IsEnabled = _cropY.IsEnabled = _fit.SelectedItem is BackgroundFit.Fill;

    private void ApplyProjectGeometry()
    {
        bool attached = ProjectId != null;
        foreach (var field in new[] { _x, _y, _width, _height }) field.IsReadOnly = attached;
        _editCanvas.IsEnabled = !attached && _draftBase != null;
        _editCanvas.ToolTip = attached ? "Detach this background to edit its frame, or resize its project area." : "Unlock and show handles for this background in canvas view";
        if (ProjectId is Guid id && _zones().FirstOrDefault(zone => zone.Id == id) is { } zone)
        {
            _x.Text = Format(zone.Bounds.X); _y.Text = Format(zone.Bounds.Y);
            _width.Text = Format(zone.Bounds.W); _height.Text = Format(zone.Bounds.H);
        }
    }

    private void RefreshImages()
    {
        if (_closed) return;
        foreach (var pair in _presetImages) pair.Value.Source = _cache.TryGet(pair.Key, lowQuality: true);
        if (_draftBase == null) { _preview.Source = null; _source.Text = ""; return; }
        _preview.Source = _cache.TryGet(_draftBase.AssetId, lowQuality: true);
        string kind = _draftBase.AssetId.StartsWith("external:", StringComparison.Ordinal) ? "Linked file" :
            _draftBase.AssetId.StartsWith("builtin:", StringComparison.Ordinal) ? "Built-in scene" : "Imported copy";
        _source.Text = _cache.GetError(_draftBase.AssetId) is { } error ? error + " Use Locate / replace image to reconnect it." : kind;
        _source.ToolTip = _assets.Resolve(_draftBase.AssetId);
    }

    private bool TryDraft(out BackgroundSurface? item)
    {
        item = null;
        if (_draftBase == null) return false;
        var current = _service.Items.FirstOrDefault(surface => surface.Id == _draftBase.Id);
        if (current == null || current != _draftBase)
        { Report("This background changed while you were editing. Reload saved values before saving your draft."); return false; }
        bool Number(TextBox field, out double value) => double.TryParse(field.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) && double.IsFinite(value);
        if (!Number(_x, out double x) || !Number(_y, out double y) || !Number(_width, out double width) || !Number(_height, out double height)
            || width < 1 || height < 1 || !int.TryParse(_z.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int z))
        { Report("Enter finite coordinates, width and height of at least 1, and a whole-number stacking order."); return false; }
        item = _draftBase with { Name = _name.Text.Trim(), Bounds = new WorldRect { X = x, Y = y, W = width, H = height },
            ZoneId = ProjectId, Fit = _fit.SelectedItem is BackgroundFit fit ? fit : BackgroundFit.Fill,
            CropX = _cropX.Value, CropY = _cropY.Value, Opacity = _opacity.Value, Dim = _dim.Value, ZIndex = z, Locked = _locked.IsChecked != false };
        if (string.IsNullOrWhiteSpace(item.Name) || !ZoneGeometry.ValidBounds(item.Bounds))
        { Report("Enter a background name and valid frame dimensions."); item = null; return false; }
        return true;
    }

    private void SaveDraft()
    {
        if (!TryDraft(out var item) || item == null) return;
        _dirty = false;
        bool saved = _service.Update(item);
        LoadSelection();
        Report(saved ? "Background saved." : _service.Status ?? "The background could not be saved. Reload its placement and try again.");
    }

    private void Add(string assetId, string name)
    {
        if (_busy) return;
        var items = _service.Items;
        int order = items.Count == 0 ? 0 : items.Max(item => item.ZIndex);
        if (order < int.MaxValue) order++;
        var item = new BackgroundSurface(Guid.NewGuid(), _service.DesktopId, name, assetId, _defaultBounds(), ZIndex: order);
        bool saved = _service.Add(item); Select(item.Id);
        Report(saved ? "Background added. Resize the frame or link it to a project area." : _service.Status ?? "The background could not be added. Check its dimensions or the background limit.");
    }

    private async Task ImportAsync(bool replace)
    {
        if (_busy || (replace && Selected == null)) return;
        if (replace && _dirty) { Report("Save or reload your changes before replacing the image."); return; }
        var selected = replace ? Selected : null;
        Guid desktop = _service.DesktopId;
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = replace ? "Locate or replace background image" : "Import background image",
            Filter = "Background images|*.png;*.jpg;*.jpeg;*.bmp|PNG image|*.png|JPEG image|*.jpg;*.jpeg|Bitmap image|*.bmp", CheckFileExists = true };
        bool? chosen;
        IsChoosingImage=true;
        try { chosen=dialog.ShowDialog(this); }
        finally { IsChoosingImage=false; }
        if (chosen != true) return;
        if (_closed) return;
        if (desktop != _service.DesktopId)
        { Report("The virtual desktop changed while choosing an image. Select the image again on this desktop."); return; }
        string fileName = dialog.FileName;
        bool external = _external.IsChecked == true;
        _busy = true;
        IsEnabled = false; // Keep a replacement from discarding edits made while the import is pending.
        Report("Checking image…");
        try
        {
            var asset = await Task.Run(() => _assets.Import(fileName, external));
            if (_closed) return;
            if (desktop != _service.DesktopId)
            { Report("The virtual desktop changed while importing. Select the image again on this desktop."); return; }
            _cache.Invalidate(asset.Id);
            if (selected == null) AddImported(asset);
            else
            {
                var current = _service.Items.FirstOrDefault(item => item.Id == selected.Id);
                if (current == null) { Report("This background was removed while the image was importing."); return; }
                _dirty = false;
                bool saved = _service.Update(current with { AssetId = asset.Id });
                _cache.Invalidate(selected.AssetId); Select(selected.Id);
                Report(saved ? "Background image replaced." : _service.Status ?? "Image replacement could not be saved.");
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException)
        { if (!_closed) Report("The image could not be imported: " + ex.Message); }
        finally { _busy = false;if(!_closed)IsEnabled=true; }
    }

    private void AddImported(BackgroundAsset asset)
    {
        // Add() normally prevents reentrancy while an import is running.
        _busy = false;
        Add(asset.Id, asset.Name.Length > 120 ? asset.Name[..120] : asset.Name);
    }

    private void EditOnCanvas()
    {
        if (!TryDraft(out var item) || item == null || item.ZoneId != null) return;
        _dirty = false;
        if (!_service.Update(item))
        { Report(_service.Status ?? "The background could not be saved before editing."); return; }
        Hide(); _editOnCanvas(item.Id);
    }

    private void Remove()
    {
        if (Selected is not { } item) return;
        _dirty = false;
        bool saved = _service.Remove(item.Id);
        Report(saved ? "Background removed. Your project area, apps and image file are unchanged." : _service.Status ?? "The removal could not be saved.");
    }

    private void Replay(bool redo)
    {
        _dirty = false;
        bool saved = redo ? _service.Redo() : _service.Undo();
        Report(saved ? (redo ? "Background change redone." : "Background change undone.") : _service.Status ?? "No background change could be restored.");
    }
}
