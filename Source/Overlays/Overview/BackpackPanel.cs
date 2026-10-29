using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using UiCanvas = System.Windows.Controls.Canvas;

namespace CanvasDesktop;

/// <summary>The backpack's screen-space controls. Window ownership and canvas placement stay outside this view.</summary>
internal sealed class BackpackPanel : UserControl
{
    internal static SolidColorBrush Ink(string value) => (SolidColorBrush)new BrushConverter().ConvertFromString(value)!;
    private readonly Border _shell;
    private readonly StackPanel _expanded = new();
    private readonly Grid _folded = new();
    private readonly WrapPanel _tiles = new() { Margin = new Thickness(12, 8, 8, 8) };
    private readonly TextBox _search;
    private readonly TextBlock _count = new(), _foldedCount = new(), _hint = new(), _empty = new();
    private readonly ScrollViewer _scroll;
    private IReadOnlyList<WorkspaceItemView> _items = Array.Empty<WorkspaceItemView>();
    private Guid? _selected;
    public DrawerRowDrag Drag { get; }
    private readonly Dictionary<Guid, Border> _tileBorders = new();
    public bool IsOpen { get; private set; }
    public bool Dragging => Drag.IsDragging;
    public event Action<bool>? OpenRequested;
    public event Action<Guid>? PlaceRequested, KeepRequested, OpenItemRequested, ForgetRequested;
    public event Action<Guid, Point>? DropRequested;

    public BackpackPanel()
    {
        Focusable = true;
        FontFamily = new FontFamily("Segoe UI"); Foreground = Ink("#EAF0F4");
        _shell = new Border { CornerRadius = new CornerRadius(17), BorderThickness = new Thickness(1.2),
            BorderBrush = Ink("#9D7A45"), Background = new LinearGradientBrush(Color.FromRgb(30, 44, 57), Color.FromRgb(12, 21, 31), 90),
            Padding = new Thickness(4) };
        Content = _shell;
        var content = new Grid(); _shell.Child = content; content.Children.Add(_folded); content.Children.Add(_expanded);
        _folded.Margin = new Thickness(14, 10, 14, 10);
        _folded.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _folded.ColumnDefinitions.Add(new()); _folded.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _folded.Children.Add(BagGlyph(34));
        var foldedText = new StackPanel { Margin = new Thickness(12, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(foldedText, 1); _folded.Children.Add(foldedText);
        foldedText.Children.Add(new TextBlock { Text = "BACKPACK", FontSize = 13, FontWeight = FontWeights.Bold, Foreground = Ink("#EAC487") });
        _foldedCount.FontSize = 11; _foldedCount.Foreground = Ink("#A4B6C7"); foldedText.Children.Add(_foldedCount);
        var key = Keycap("I"); Grid.SetColumn(key, 2); _folded.Children.Add(key);
        _folded.MouseLeftButtonUp += (_, e) => { OpenRequested?.Invoke(true); e.Handled = true; };

        var header = new Grid { Margin = new Thickness(18, 15, 15, 0) };
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new()); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(BagGlyph(38));
        var heading = new StackPanel { Margin = new Thickness(13, 0, 0, 0) }; Grid.SetColumn(heading, 1); header.Children.Add(heading);
        heading.Children.Add(new TextBlock { Text = "BACKPACK", FontSize = 19, FontWeight = FontWeights.Bold, Foreground = Ink("#EAC487") });
        heading.Children.Add(new TextBlock { Text = "Collect. Travel. Unpack.", FontSize = 11, Foreground = Ink("#9DB0C3"), Margin = new Thickness(0, 3, 0, 0) });
        var close = ActionButton("×", () => OpenRequested?.Invoke(false)); close.Width = 30; close.Height = 30; close.ToolTip = "Close backpack · I";
        System.Windows.Automation.AutomationProperties.SetName(close, "Close backpack"); Grid.SetColumn(close, 2); header.Children.Add(close);
        _expanded.Children.Add(header);
        var divider = new Border { Height = 1, Background = Ink("#40515D"), Margin = new Thickness(18, 16, 18, 14) }; _expanded.Children.Add(divider);
        var summary = new DockPanel { Margin = new Thickness(18, 0, 18, 11) };
        _count.FontSize = 11; _count.FontWeight = FontWeights.SemiBold; _count.Foreground = Ink("#71D8C2"); summary.Children.Add(_count);
        var badge = new TextBlock { Text = "TRAVELS WITH YOU", FontSize = 9, Foreground = Ink("#8297AB"), HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(badge, Dock.Right); summary.Children.Add(badge); _expanded.Children.Add(summary);
        _search = new TextBox { Padding = new Thickness(10, 7, 10, 7), FontSize = 12,
            Background = Ink("#0D1722"), Foreground = Ink("#EAF0F4"), BorderBrush = Ink("#344858"), CaretBrush = Ink("#EAC487"),
            ToolTip = "Find a window by its title or application" };
        System.Windows.Automation.AutomationProperties.SetName(_search, "Find a backpack item");
        var searchBox = new Grid { Margin = new Thickness(18, 0, 18, 8) };
        searchBox.Children.Add(_search);
        var watermark = new TextBlock { Text = "Find a window…", Foreground = Ink("#7991A6"), FontSize = 12,
            Margin = new Thickness(11, 8, 0, 0), IsHitTestVisible = false };
        searchBox.Children.Add(watermark);
        _search.TextChanged += (_, _) => { watermark.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; RebuildTiles(); };
        _expanded.Children.Add(searchBox);
        _scroll = new ScrollViewer { Content = _tiles, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Height = 340, CanContentScroll = true };
        _expanded.Children.Add(_scroll);
        _empty.Text = "Your travelling workspace\n\nDrag a window here to carry it.\nPan anywhere, then drag it back out.";
        _empty.TextAlignment = TextAlignment.Center; _empty.FontSize = 13; _empty.LineHeight = 22; _empty.Foreground = Ink("#8FA6BA");
        _empty.Margin = new Thickness(20, 26, 20, 26);
        _expanded.Children.Add(_empty);
        var footer = new Border { BorderBrush = Ink("#304352"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(15, 12, 15, 13) };
        var footerStack = new StackPanel(); footer.Child = footerStack;
        _hint.Text = "Drag out to place · double-click for nearby space"; _hint.FontSize = 11; _hint.Foreground = Ink("#BED0DD"); _hint.TextWrapping = TextWrapping.Wrap;
        footerStack.Children.Add(_hint);
        footerStack.Children.Add(new TextBlock { Text = "KEPT items survive restarts. SESSION items stay temporary.", FontSize = 10,
            Foreground = Ink("#7E95AA"), Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap });
        _expanded.Children.Add(footer);
        Drag = new(this);
        Drag.Dropped += (id, screen) => DropRequested?.Invoke(id, screen);
        Drag.HeldChanged += held => { if (!held) RebuildTiles(); };
        PreviewMouseMove += (_, _) =>
        {
            if (!Drag.IsDragging) return;
            SetHint("Release over clear canvas to unpack this window.");
            if (_selected is { } id && _tileBorders.TryGetValue(id, out var tile)) tile.Opacity = .5;
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Handled) return;
            if (e.Key == Key.Escape || e.Key == Key.I && Keyboard.FocusedElement is not TextBox && Keyboard.Modifiers == ModifierKeys.None)
            { if (Drag.IsHeld) Drag.Cancel(); else OpenRequested?.Invoke(false); e.Handled = true; }
        };
        SetOpen(false);
        RebuildTiles();
    }

    public void SetOpen(bool open)
    {
        if (!open) Drag.Cancel();
        IsOpen = open; _expanded.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        _folded.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
    }

    public void SetAvailableHeight(double height) => _scroll.Height = Math.Max(100, height - 245);
    public void SetDropTarget(bool active) => _shell.BorderBrush = Ink(active ? "#72F3D2" : "#9D7A45");
    public void SetHint(string? message) => _hint.Text = string.IsNullOrWhiteSpace(message)
        ? "Drag out to place · double-click for nearby space" : message;

    public void UpdateItems(IReadOnlyList<WorkspaceItemView> items)
    {
        string Key(WorkspaceItemView i) => $"{i.Id}/{i.Title}/{i.State}/{i.Temporary}/{i.ExecutablePath}";
        bool changed = !_items.Select(Key).SequenceEqual(items.Select(Key));
        _items = items;
        _foldedCount.Text = items.Count == 1 ? "1 window ready to travel" : $"{items.Count} windows ready to travel";
        _count.Text = $"{items.Count} ITEMS  /  {items.Count(i => !i.Temporary)} KEPT";
        if (changed && !Drag.IsHeld) RebuildTiles();
    }

    public void Highlight(Guid? id)
    {
        _selected = id;
        foreach (var (key, border) in _tileBorders) border.BorderBrush = Ink(key == id ? "#EAC487" : "#344B5B");
        if (id is { } target && _tileBorders.TryGetValue(target, out var tile)) tile.BringIntoView();
    }

    private void RebuildTiles()
    {
        if (Drag.IsHeld) return;
        _tiles.Children.Clear(); _tileBorders.Clear();
        string query = _search.Text.Trim();
        var filtered = _items.Where(i => query.Length == 0 || i.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || i.ExecutablePath.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        _scroll.Visibility = filtered.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _empty.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _empty.Text = _items.Count == 0 ? "Your travelling workspace\n\nDrag a window here to carry it.\nPan anywhere, then drag it back out." : "No matching windows.";
        foreach (var item in filtered)
        {
            var tile = new Border { Width = 122, Height = 136, Margin = new Thickness(4), Padding = new Thickness(9), CornerRadius = new CornerRadius(9),
                BorderBrush = Ink(item.Id == _selected ? "#EAC487" : "#344B5B"), BorderThickness = new Thickness(1), Background = Ink("#1D3040"),
                ToolTip = item.Title + "\n" + item.ExecutablePath };
            System.Windows.Automation.AutomationProperties.SetName(tile, item.Title);
            var body = new StackPanel(); tile.Child = body;
            var glyph = new Border { Width = 34, Height = 30, CornerRadius = new CornerRadius(5), Background = Ink(item.Temporary ? "#284E54" : "#51452E"),
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 7), Child = new TextBlock
                { Text = "▣", Foreground = Ink(item.Temporary ? "#78E2CB" : "#F0CB8D"), FontSize = 21, HorizontalAlignment = HorizontalAlignment.Center } };
            body.Children.Add(glyph);
            body.Children.Add(new TextBlock { Text = item.Title, FontSize = 11, FontWeight = FontWeights.SemiBold, Height = 31,
                TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis });
            body.Children.Add(new TextBlock { Text = item.State == "Open" ? "Live window" : item.State == "Placeholder" ? "Closed · kept" : item.State,
                FontSize = 10, Foreground = Ink("#A0B8CB"), Margin = new Thickness(0, 3, 0, 3), TextTrimming = TextTrimming.CharacterEllipsis });
            body.Children.Add(new TextBlock { Text = item.Temporary ? "SESSION" : "KEPT", FontSize = 9, FontWeight = FontWeights.Bold,
                Foreground = Ink(item.Temporary ? "#73D8C3" : "#EAC487") });
            tile.MouseLeftButtonDown += (_, e) =>
            {
                Highlight(item.Id);
                if (e.ClickCount == 2) { Drag.Cancel(); PlaceRequested?.Invoke(item.Id); e.Handled = true; return; }
                Drag.Begin(item.Id, e.GetPosition(this)); e.Handled = true;
            };
            var menu = new ContextMenu();
            void Entry(string title, Action action) { var m = new MenuItem { Header = title }; m.Click += (_, _) => action(); menu.Items.Add(m); }
            Entry("Place near camera", () => PlaceRequested?.Invoke(item.Id));
            Entry("Open app", () => OpenItemRequested?.Invoke(item.Id));
            if (item.Temporary) Entry("Keep in backpack", () => KeepRequested?.Invoke(item.Id));
            else Entry("Forget saved entry", () => ForgetRequested?.Invoke(item.Id));
            tile.ContextMenu = menu;
            _tiles.Children.Add(tile); _tileBorders[item.Id] = tile;
        }
    }

    internal static Button ActionButton(string label, Action action)
    {
        var button = new Button { Content = label, Background = Ink("#263D4C"), Foreground = Ink("#EAC487"), BorderBrush = Ink("#53616A"),
            Padding = new Thickness(8, 4, 8, 4), FontSize = 14, Cursor = Cursors.Hand };
        button.Click += (_, _) => action(); return button;
    }

    private static Border Keycap(string key) => new() { BorderBrush = Ink("#65747C"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
        Padding = new Thickness(8, 4, 8, 4), VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = key, FontSize = 12, Foreground = Ink("#DAC8AA"), FontWeight = FontWeights.Bold } };

    internal static FrameworkElement BagGlyph(double size)
    {
        var canvas = new UiCanvas { Width = 36, Height = 38 };
        var handle = new Rectangle { Width = 14, Height = 10, RadiusX = 5, RadiusY = 5, Stroke = Ink("#EAC487"), StrokeThickness = 2, Fill = Brushes.Transparent };
        UiCanvas.SetLeft(handle, 11); UiCanvas.SetTop(handle, 1); canvas.Children.Add(handle);
        var bag = new Rectangle { Width = 30, Height = 30, RadiusX = 7, RadiusY = 7, Fill = Ink("#805E35"), Stroke = Ink("#EAC487"), StrokeThickness = 1.5 };
        UiCanvas.SetLeft(bag, 3); UiCanvas.SetTop(bag, 7); canvas.Children.Add(bag);
        var pocket = new Rectangle { Width = 20, Height = 12, RadiusX = 3, RadiusY = 3, Fill = Ink("#3B3930"), Stroke = Ink("#EAC487"), StrokeThickness = 1 };
        UiCanvas.SetLeft(pocket, 8); UiCanvas.SetTop(pocket, 22); canvas.Children.Add(pocket);
        var clasp = new Rectangle { Width = 4, Height = 8, RadiusX = 1, RadiusY = 1, Fill = Ink("#F7D99D") };
        UiCanvas.SetLeft(clasp, 16); UiCanvas.SetTop(clasp, 14); canvas.Children.Add(clasp);
        return new Viewbox { Width = size, Height = size + 2, Child = canvas };
    }
}
