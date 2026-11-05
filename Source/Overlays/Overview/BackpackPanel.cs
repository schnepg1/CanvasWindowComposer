using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Wpf.Ui.Markup;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using ScrollViewer = System.Windows.Controls.ScrollViewer;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;
using UiCanvas = System.Windows.Controls.Canvas;

namespace CanvasDesktop;

/// <summary>The backpack's screen-space controls modernized with WPF-UI. Window ownership and canvas placement stay outside this view.</summary>
internal sealed class BackpackPanel : UserControl
{
    internal static SolidColorBrush Ink(string value) => (SolidColorBrush)new BrushConverter().ConvertFromString(value)!;
    private readonly Border _shell;
    private readonly Grid _expanded = new();
    private readonly Grid _folded = new();
    private readonly WrapPanel _tiles = new() { Margin = new Thickness(10, 8, 8, 8) };
    private readonly Wpf.Ui.Controls.TextBox _search;
    private readonly TextBlock _count = new(), _foldedCount = new(), _hint = new(), _empty = new();
    private readonly Border _hintContainer = new();
    private readonly DynamicScrollViewer _scroll;
    private readonly Border _help;
    private readonly CheckBox _dismissHelp;
    private readonly Wpf.Ui.Controls.Button _info;
    private readonly StackPanel _emptyContainer = new();
    private readonly SymbolIcon _emptyIcon = new() { Symbol = SymbolRegular.Luggage48, FontSize = 44, Margin = new Thickness(0, 0, 0, 12), Foreground = Ink("#8A734C") };
    private const string PlacementHelp = "Drag out to place · double-click for nearby space";
    private const string PersistenceHelp = "KEPT items survive restarts. SESSION items stay temporary.";
    private IReadOnlyList<WorkspaceItemView> _items = Array.Empty<WorkspaceItemView>();
    private Guid? _selected;
    public DrawerRowDrag Drag { get; }
    private readonly Dictionary<Guid, Border> _tileBorders = new();
    public bool IsOpen { get; private set; }
    public bool Dragging => Drag.IsDragging;
    public event Action<bool>? OpenRequested;
    public event Action<bool>? HelpDismissedChanged;
    public event Action<Guid>? PlaceRequested, KeepRequested, OpenItemRequested, ForgetRequested;
    public event Action<Guid, Point>? DropRequested;

    public BackpackPanel()
    {
        if (Application.Current == null)
            _ = new Application();
        Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = ApplicationTheme.Dark });
        void AddResource(string relativePath) => Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Wpf.Ui;component/{relativePath}", UriKind.Absolute)
        });
        AddResource("Resources/Fonts.xaml");
        AddResource("Resources/Typography.xaml");
        AddResource("Resources/StaticColors.xaml");
        AddResource("Resources/Accent.xaml");
        AddResource("Resources/Palette.xaml");
        AddResource("Resources/Variables.xaml");
        AddResource("Controls/TextBox/TextBox.xaml");
        AddResource("Controls/Button/Button.xaml");
        AddResource("Controls/Card/Card.xaml");
        AddResource("Controls/DynamicScrollViewer/DynamicScrollViewer.xaml");
        AddResource("Controls/ContextMenu/ContextMenu.xaml");
        AddResource("Controls/Menu/MenuItem.xaml");
        AddResource("Controls/CheckBox/CheckBox.xaml");

        Focusable = true;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Foreground = Ink("#EAF0F4");

        _shell = new Border
        {
            CornerRadius = new CornerRadius(18),
            BorderThickness = new Thickness(1.2),
            BorderBrush = Ink("#9D7A45"),
            Background = new LinearGradientBrush(Color.FromRgb(26, 38, 50), Color.FromRgb(12, 20, 30), 90),
            Padding = new Thickness(4),
            Effect = new DropShadowEffect
            {
                BlurRadius = 24,
                ShadowDepth = 6,
                Direction = 270,
                Color = Color.FromArgb(160, 0, 0, 0),
                Opacity = 0.55
            }
        };
        Content = _shell;

        var content = new Grid();
        _shell.Child = content;
        content.Children.Add(_folded);
        content.Children.Add(_expanded);

        for (int row = 0; row < 8; row++)
            _expanded.RowDefinitions.Add(new() { Height = row == 4 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        void AddRow(UIElement element, int row) { Grid.SetRow(element, row); _expanded.Children.Add(element); }

        // --- Folded Badge Mode ---
        _folded.Margin = new Thickness(14, 10, 14, 10);
        _folded.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _folded.ColumnDefinitions.Add(new());
        _folded.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _folded.Cursor = Cursors.Hand;

        var bagBadge = new Border
        {
            CornerRadius = new CornerRadius(9),
            Background = Ink("#2C2518"),
            BorderBrush = Ink("#7C6339"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4),
            Child = BagGlyph(30)
        };
        _folded.Children.Add(bagBadge);

        var foldedText = new StackPanel { Margin = new Thickness(12, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(foldedText, 1);
        _folded.Children.Add(foldedText);

        foldedText.Children.Add(new TextBlock { Text = "BACKPACK", FontSize = 13, FontWeight = FontWeights.Bold, Foreground = Ink("#EAC487") });
        _foldedCount.FontSize = 11;
        _foldedCount.Foreground = Ink("#A4B6C7");
        foldedText.Children.Add(_foldedCount);

        var key = Keycap("I");
        Grid.SetColumn(key, 2);
        _folded.Children.Add(key);

        _folded.MouseEnter += (_, _) => _shell.BorderBrush = Ink("#D4AF37");
        _folded.MouseLeave += (_, _) => _shell.BorderBrush = Ink("#9D7A45");
        _folded.MouseLeftButtonUp += (_, e) => { OpenRequested?.Invoke(true); e.Handled = true; };

        // --- Expanded Mode Header ---
        var header = new Grid { Margin = new Thickness(18, 14, 15, 0) };
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new());
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });

        var headerBag = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = Ink("#2C2518"),
            BorderBrush = Ink("#7C6339"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(5),
            Child = BagGlyph(34)
        };
        header.Children.Add(headerBag);

        var heading = new StackPanel { Margin = new Thickness(13, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(heading, 1);
        header.Children.Add(heading);
        heading.Children.Add(new TextBlock { Text = "BACKPACK", FontSize = 19, FontWeight = FontWeights.Bold, Foreground = Ink("#EAC487") });
        heading.Children.Add(new TextBlock { Text = "Collect. Travel. Unpack.", FontSize = 11, Foreground = Ink("#9DB0C3"), Margin = new Thickness(0, 2, 0, 0) });

        _info = new Wpf.Ui.Controls.Button
        {
            Icon = new SymbolIcon { Symbol = SymbolRegular.Info16, Foreground = Ink("#DAC49D"), FontSize = 16 },
            Appearance = ControlAppearance.Transparent,
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 6, 0),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            ToolTip = new TextBlock
            {
                Text = PlacementHelp + "\n\n" + PersistenceHelp + "\n\nClick to show tips again.",
                MaxWidth = 260,
                TextWrapping = TextWrapping.Wrap
            }
        };
        _info.MouseEnter += (_, _) => _info.Background = Ink("#263B4C");
        _info.MouseLeave += (_, _) => _info.Background = Brushes.Transparent;
        System.Windows.Automation.AutomationProperties.SetName(_info, "Backpack tips");
        _info.Click += (_, _) => { SetHelpDismissed(false); HelpDismissedChanged?.Invoke(false); };
        Grid.SetColumn(_info, 2);
        header.Children.Add(_info);

        var close = new Wpf.Ui.Controls.Button
        {
            Icon = new SymbolIcon { Symbol = SymbolRegular.Dismiss16, Foreground = Ink("#DAC49D"), FontSize = 16 },
            Appearance = ControlAppearance.Transparent,
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ToolTip = "Close backpack · I",
            Cursor = Cursors.Hand
        };
        close.MouseEnter += (_, _) => close.Background = Ink("#263B4C");
        close.MouseLeave += (_, _) => close.Background = Brushes.Transparent;
        System.Windows.Automation.AutomationProperties.SetName(close, "Close backpack");
        close.Click += (_, _) => OpenRequested?.Invoke(false);
        Grid.SetColumn(close, 3);
        header.Children.Add(close);
        AddRow(header, 0);

        var divider = new Border { Height = 1, Background = Ink("#384855"), Margin = new Thickness(18, 14, 18, 12) };
        AddRow(divider, 1);

        // --- Summary Row ---
        var summary = new DockPanel { Margin = new Thickness(18, 0, 18, 10) };
        _count.FontSize = 11;
        _count.FontWeight = FontWeights.SemiBold;
        _count.Foreground = Ink("#71D8C2");
        summary.Children.Add(_count);

        var badge = new Border
        {
            Background = Ink("#1E2D3B"),
            BorderBrush = Ink("#3A4D5E"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6, 2, 6, 2),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        DockPanel.SetDock(badge, Dock.Right);
        var badgeStack = new StackPanel { Orientation = Orientation.Horizontal };
        badgeStack.Children.Add(new SymbolIcon { Symbol = SymbolRegular.Pin16, FontSize = 11, Foreground = Ink("#8297AB"), Margin = new Thickness(0, 0, 4, 0) });
        badgeStack.Children.Add(new TextBlock { Text = "TRAVELS WITH YOU", FontSize = 9, FontWeight = FontWeights.SemiBold, Foreground = Ink("#8297AB"), VerticalAlignment = VerticalAlignment.Center });
        badge.Child = badgeStack;
        summary.Children.Add(badge);
        AddRow(summary, 2);

        // --- Search Box ---
        _search = new Wpf.Ui.Controls.TextBox
        {
            PlaceholderText = "Find a window…",
            Icon = new SymbolIcon { Symbol = SymbolRegular.Search20, Foreground = Ink("#7991A6") },
            ClearButtonEnabled = true,
            FontSize = 12,
            Foreground = Ink("#EAF0F4"),
            Background = Ink("#101923"),
            BorderBrush = Ink("#344858"),
            CaretBrush = Ink("#EAC487"),
            ToolTip = "Find a window by its title or application",
            Margin = new Thickness(18, 0, 18, 8)
        };
        System.Windows.Automation.AutomationProperties.SetName(_search, "Find a backpack item");
        _search.TextChanged += (_, _) => RebuildTiles();
        AddRow(_search, 3);

        // --- Items & Empty State ---
        _scroll = new DynamicScrollViewer
        {
            Content = _tiles,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = true
        };
        var items = new Grid { ClipToBounds = true };
        items.Children.Add(_scroll);

        _emptyContainer.VerticalAlignment = VerticalAlignment.Center;
        _emptyContainer.HorizontalAlignment = HorizontalAlignment.Center;
        _emptyContainer.Margin = new Thickness(24, 20, 24, 20);
        _emptyContainer.Children.Add(_emptyIcon);

        _empty.Text = "Your travelling workspace\n\nDrag a window here to carry it.\nPan anywhere, then drag it back out.";
        _empty.TextAlignment = TextAlignment.Center;
        _empty.FontSize = 13;
        _empty.LineHeight = 22;
        _empty.Foreground = Ink("#8FA6BA");
        _empty.TextWrapping = TextWrapping.Wrap;
        _emptyContainer.Children.Add(_empty);
        items.Children.Add(_emptyContainer);
        AddRow(items, 4);

        // --- Hint Banner ---
        _hintContainer.Background = Ink("#1A2D3C");
        _hintContainer.BorderBrush = Ink("#33566D");
        _hintContainer.BorderThickness = new Thickness(1);
        _hintContainer.CornerRadius = new CornerRadius(7);
        _hintContainer.Padding = new Thickness(10, 6, 10, 6);
        _hintContainer.Margin = new Thickness(15, 6, 15, 6);
        _hintContainer.Visibility = Visibility.Collapsed;

        var hintStack = new StackPanel { Orientation = Orientation.Horizontal };
        hintStack.Children.Add(new SymbolIcon { Symbol = SymbolRegular.Info20, Foreground = Ink("#71D8C2"), Margin = new Thickness(0, 0, 8, 0) });
        _hint.FontSize = 11;
        _hint.Foreground = Ink("#BED0DD");
        _hint.TextWrapping = TextWrapping.Wrap;
        _hint.VerticalAlignment = VerticalAlignment.Center;
        hintStack.Children.Add(_hint);
        _hintContainer.Child = hintStack;
        AddRow(_hintContainer, 5);

        // --- Help Footer ---
        _help = new Border
        {
            Background = Ink("#14222E"),
            BorderBrush = Ink("#304352"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10, 14, 8),
            Margin = new Thickness(14, 4, 14, 6)
        };
        var footerStack = new StackPanel();
        _help.Child = footerStack;
        footerStack.Children.Add(new TextBlock { Text = PlacementHelp, FontSize = 11, Foreground = Ink("#BED0DD"), TextWrapping = TextWrapping.Wrap });
        footerStack.Children.Add(new TextBlock { Text = PersistenceHelp, FontSize = 10, Foreground = Ink("#7E95AA"), Margin = new Thickness(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap });
        _dismissHelp = new CheckBox
        {
            Content = "Got it — hide these tips",
            FontSize = 11,
            Foreground = Ink("#BED0DD"),
            Margin = new Thickness(0, 9, 0, 0),
            Cursor = Cursors.Hand
        };
        _dismissHelp.Click += (_, _) =>
        {
            bool dismissed = _dismissHelp.IsChecked == true;
            SetHelpDismissed(dismissed);
            HelpDismissedChanged?.Invoke(dismissed);
        };
        footerStack.Children.Add(_dismissHelp);
        AddRow(_help, 6);

        // --- Fold Button ---
        var fold = new Wpf.Ui.Controls.Button
        {
            Content = "Fold backpack",
            Icon = new SymbolIcon { Symbol = SymbolRegular.ChevronUp20, Foreground = Ink("#A5EBE0"), FontSize = 16 },
            Appearance = ControlAppearance.Secondary,
            Height = 34,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(14, 6, 14, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = Ink("#203B4C"),
            Foreground = Ink("#A5EBE0"),
            BorderBrush = Ink("#466577"),
            ToolTip = "Fold backpack · I",
            Cursor = Cursors.Hand
        };
        fold.Click += (_, _) => OpenRequested?.Invoke(false);
        System.Windows.Automation.AutomationProperties.SetName(fold, "Fold backpack");
        AddRow(fold, 7);

        SetHelpDismissed(false);
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
            {
                if (Drag.IsHeld) Drag.Cancel();
                else OpenRequested?.Invoke(false);
                e.Handled = true;
            }
        };

        SetOpen(false);
        RebuildTiles();
    }

    public void SetOpen(bool open)
    {
        if (!open) Drag.Cancel();
        IsOpen = open;
        _expanded.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        _folded.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
    }

    public void SetAvailableHeight(double height) => _shell.MaxHeight = Math.Max(100, height);

    public void SetDropTarget(bool active)
    {
        _shell.BorderBrush = Ink(active ? "#72F3D2" : "#9D7A45");
        if (_shell.Effect is DropShadowEffect shadow)
        {
            shadow.Color = active ? Color.FromArgb(180, 114, 243, 210) : Color.FromArgb(160, 0, 0, 0);
            shadow.BlurRadius = active ? 32 : 24;
        }
    }

    public void SetHint(string? message)
    {
        _hint.Text = message ?? "";
        bool visible = !string.IsNullOrWhiteSpace(message);
        _hint.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _hintContainer.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetHelpDismissed(bool dismissed)
    {
        _dismissHelp.IsChecked = dismissed;
        _help.Visibility = dismissed ? Visibility.Collapsed : Visibility.Visible;
        _info.Visibility = dismissed ? Visibility.Visible : Visibility.Collapsed;
    }

    public void UpdateItems(IReadOnlyList<WorkspaceItemView> items)
    {
        string Key(WorkspaceItemView i) => $"{i.Id}/{i.Title}/{i.State}/{i.Temporary}/{i.ExecutablePath}";
        bool changed = !_items.Select(Key).SequenceEqual(items.Select(Key));
        _items = items;
        if (Drag.ItemId is { } held && !items.Any(i => i.Id == held)) Drag.Cancel();
        _foldedCount.Text = items.Count == 1 ? "1 window ready to travel" : $"{items.Count} windows ready to travel";
        _count.Text = $"{items.Count} ITEMS  /  {items.Count(i => !i.Temporary)} KEPT";
        if (changed && !Drag.IsHeld) RebuildTiles();
    }

    public void Highlight(Guid? id)
    {
        _selected = id;
        foreach (var (key, border) in _tileBorders)
        {
            bool isSelected = key == id;
            border.BorderBrush = Ink(isSelected ? "#EAC487" : "#344B5B");
            border.BorderThickness = new Thickness(isSelected ? 1.8 : 1);
            border.Background = Ink(isSelected ? "#223546" : "#1B2C3A");
        }
        if (id is { } target && _tileBorders.TryGetValue(target, out var tile)) tile.BringIntoView();
    }

    private void RebuildTiles()
    {
        if (Drag.IsHeld) return;
        _tiles.Children.Clear();
        _tileBorders.Clear();
        string query = _search.Text.Trim();
        var filtered = _items.Where(i => query.Length == 0 || i.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || i.ExecutablePath.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        _scroll.Visibility = filtered.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _emptyContainer.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _emptyIcon.Symbol = _items.Count == 0 ? SymbolRegular.Luggage48 : SymbolRegular.Search24;
        _empty.Text = _items.Count == 0 ? "Your travelling workspace\n\nDrag a window here to carry it.\nPan anywhere, then drag it back out." : "No matching windows.";

        foreach (var item in filtered)
        {
            bool isSelected = item.Id == _selected;
            var tile = new Border
            {
                Width = 126,
                Height = 140,
                Margin = new Thickness(5),
                Padding = new Thickness(9),
                CornerRadius = new CornerRadius(10),
                BorderBrush = Ink(isSelected ? "#EAC487" : "#344B5B"),
                BorderThickness = new Thickness(isSelected ? 1.8 : 1),
                Background = Ink(isSelected ? "#223546" : "#1B2C3A"),
                ToolTip = item.Title + "\n" + item.ExecutablePath,
                Cursor = Cursors.Hand
            };
            System.Windows.Automation.AutomationProperties.SetName(tile, item.Title);

            var body = new StackPanel();
            tile.Child = body;

            // Header row inside tile: App icon + KEPT/SESSION Badge
            var tileHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 7) };
            body.Children.Add(tileHeader);

            var glyph = new Border
            {
                Width = 32,
                Height = 28,
                CornerRadius = new CornerRadius(6),
                Background = Ink(item.Temporary ? "#22474E" : "#4A3E28"),
                BorderBrush = Ink(item.Temporary ? "#386F78" : "#7C663D"),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = DrawerAppIcon.Create(item, 20, Ink(item.Temporary ? "#78E2CB" : "#F0CB8D"))
            };
            tileHeader.Children.Add(glyph);

            var tagBadge = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 2, 5, 2),
                Background = Ink(item.Temporary ? "#17383E" : "#3B331F"),
                BorderBrush = Ink(item.Temporary ? "#439284" : "#B59458"),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(tagBadge, Dock.Right);
            tagBadge.Child = new TextBlock
            {
                Text = item.Temporary ? "SESSION" : "KEPT",
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                Foreground = Ink(item.Temporary ? "#73D8C3" : "#EAC487")
            };
            tileHeader.Children.Add(tagBadge);

            // Title
            body.Children.Add(new TextBlock
            {
                Text = item.Title,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = Ink("#F0F4F8"),
                Height = 31,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            // State row with status indicator dot
            var statePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 6,
                Height = 6,
                Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill = Ink(item.State == "Open" ? "#48D18C" : item.State == "Placeholder" ? "#E0B364" : "#6FA5C7")
            };
            statePanel.Children.Add(dot);
            statePanel.Children.Add(new TextBlock
            {
                Text = item.State == "Open" ? "Live window" : item.State == "Placeholder" ? "Closed · kept" : item.State,
                FontSize = 10,
                Foreground = Ink("#A0B8CB"),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            body.Children.Add(statePanel);

            // Hover interactions
            tile.MouseEnter += (_, _) =>
            {
                if (item.Id != _selected)
                {
                    tile.BorderBrush = Ink("#51728A");
                    tile.Background = Ink("#213647");
                }
            };
            tile.MouseLeave += (_, _) =>
            {
                if (item.Id != _selected)
                {
                    tile.BorderBrush = Ink("#344B5B");
                    tile.Background = Ink("#1B2C3A");
                }
            };

            tile.MouseLeftButtonDown += (_, e) =>
            {
                Highlight(item.Id);
                if (e.ClickCount == 2) { Drag.Cancel(); PlaceRequested?.Invoke(item.Id); e.Handled = true; return; }
                Drag.Begin(item.Id, e.GetPosition(this));
                e.Handled = true;
            };

            var menu = new ContextMenu
            {
                Background = Ink("#182633"),
                BorderBrush = Ink("#3A5063"),
                BorderThickness = new Thickness(1),
                Foreground = Ink("#EAF0F4")
            };
            void Entry(string title, SymbolRegular symbol, Action action)
            {
                var m = new MenuItem
                {
                    Header = title,
                    Icon = new SymbolIcon { Symbol = symbol, FontSize = 16, Foreground = Ink("#EAC487") },
                    Foreground = Ink("#EAF0F4")
                };
                m.Click += (_, _) => action();
                menu.Items.Add(m);
            }
            Entry("Place near camera", SymbolRegular.Open20, () => PlaceRequested?.Invoke(item.Id));
            Entry("Open app", SymbolRegular.ArrowUpRight20, () => OpenItemRequested?.Invoke(item.Id));
            if (item.Temporary) Entry("Keep in backpack", SymbolRegular.Pin20, () => KeepRequested?.Invoke(item.Id));
            else Entry("Forget saved entry", SymbolRegular.Delete20, () => ForgetRequested?.Invoke(item.Id));
            tile.ContextMenu = menu;

            _tiles.Children.Add(tile);
            _tileBorders[item.Id] = tile;
        }
    }

    internal static Button ActionButton(string label, Action action)
    {
        var button = new Wpf.Ui.Controls.Button
        {
            Content = label,
            Appearance = ControlAppearance.Secondary,
            Background = Ink("#263D4C"),
            Foreground = Ink("#EAC487"),
            BorderBrush = Ink("#53616A"),
            Padding = new Thickness(8, 4, 8, 4),
            FontSize = 14,
            Cursor = Cursors.Hand
        };
        button.Click += (_, _) => action();
        return button;
    }

    private static Border Keycap(string key) => new()
    {
        Background = Ink("#20303D"),
        BorderBrush = Ink("#4E6373"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(5),
        Padding = new Thickness(8, 4, 8, 4),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = key, FontSize = 12, Foreground = Ink("#DAC8AA"), FontWeight = FontWeights.Bold }
    };

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
