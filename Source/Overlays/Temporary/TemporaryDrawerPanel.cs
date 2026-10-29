using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace CanvasDesktop;

/// <summary>Presentation only: the rail, list rows and arrival motion have no workspace ownership.</summary>
internal sealed class TemporaryDrawerPanel : UserControl
{
    internal const double RailWidth = 36, PanelWidth = 346, FullWidth = RailWidth + PanelWidth;
    private readonly TextBlock _name = new(), _count = new(), _railCount = new(), _hint = new();
    private readonly TextBlock _chevron = new() { FontSize = 28, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Button _rail;
    private readonly StackPanel _rows = new();
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _empty;
    private IReadOnlyList<WorkspaceItemView> _items = Array.Empty<WorkspaceItemView>();
    private readonly HashSet<Guid> _arrivals = new();
    private bool _reduceMotion;
    public DrawerRowDrag Drag { get; }
    public event Action? ToggleRequested, ShowAreaRequested;
    public event Action<Guid>? PlaceRequested;

    private static SolidColorBrush Ink(string color) => (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
    public TemporaryDrawerPanel()
    {
        Width = FullWidth; HorizontalAlignment = HorizontalAlignment.Left;
        FontFamily = new FontFamily("Segoe UI"); Foreground = Ink("#E7EFF6"); Focusable = true;
        var root = new Grid(); root.ColumnDefinitions.Add(new() { Width = new GridLength(RailWidth) }); root.ColumnDefinitions.Add(new()); Content = root;
        var railBody = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        railBody.Children.Add(_chevron);
        var railLabel = new TextBlock { Text = "TEMPORARY", FontSize = 10, FontWeight = FontWeights.SemiBold,
            LayoutTransform = new RotateTransform(-90), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 8) };
        railBody.Children.Add(railLabel);
        _railCount.FontSize = 11; _railCount.HorizontalAlignment = HorizontalAlignment.Center; railBody.Children.Add(_railCount);
        _rail = new Button { Content = railBody, Background = Ink("#203B4C"), Foreground = Ink("#A5EBE0"),
            BorderBrush = Ink("#466577"), BorderThickness = new Thickness(1, 1, 0, 1), Padding = new Thickness(0), Cursor = Cursors.Hand,
            Height = 160, VerticalAlignment = VerticalAlignment.Top,
            ToolTip = "Peek at temporary windows" };
        System.Windows.Automation.AutomationProperties.SetName(_rail, "Toggle temporary windows drawer");
        _rail.Click += (_, _) => ToggleRequested?.Invoke(); root.Children.Add(_rail);
        SetExpanded(false);
        var shell = new Border { Background = new LinearGradientBrush(Color.FromRgb(23, 38, 52), Color.FromRgb(12, 22, 34), 90),
            BorderBrush = Ink("#466577"), BorderThickness = new Thickness(1, 1, 0, 1), Padding = new Thickness(18) };
        Grid.SetColumn(shell, 1); root.Children.Add(shell);
        var layout = new Grid(); shell.Child = layout;
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto }); layout.RowDefinitions.Add(new()); layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 15) }; layout.Children.Add(header);
        header.Children.Add(new TextBlock { Text = "TEMPORARY WINDOWS", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Ink("#80DBCD") });
        _name.FontSize = 20; _name.FontWeight = FontWeights.SemiBold; _name.TextTrimming = TextTrimming.CharacterEllipsis;
        _name.Margin = new Thickness(0, 5, 0, 5); header.Children.Add(_name);
        _count.FontSize = 11; _count.Foreground = Ink("#94ABBF"); header.Children.Add(_count);
        var list = new Grid(); Grid.SetRow(list, 1); layout.Children.Add(list);
        _scroll = new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 5, 0) }; list.Children.Add(_scroll);
        _empty = new TextBlock { Text = "All clear\n\nNew temporary-zone windows appear here.\nTheir places last for this session.",
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Foreground = Ink("#94ABBF"),
            FontSize = 13, LineHeight = 23, VerticalAlignment = VerticalAlignment.Center }; list.Children.Add(_empty);
        var footer = new StackPanel { Margin = new Thickness(0, 13, 0, 0) }; Grid.SetRow(footer, 2); layout.Children.Add(footer);
        _hint.Text = "Drag onto clear canvas to place and keep.\nDouble-click to place near the camera.";
        _hint.TextWrapping = TextWrapping.Wrap; _hint.FontSize = 11; _hint.LineHeight = 17; _hint.Foreground = Ink("#AEC2D1"); footer.Children.Add(_hint);
        var visit = new Button { Content = "Show temporary area  \u2197", Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(10, 7, 10, 7),
            Background = Ink("#233D50"), Foreground = Ink("#A5EBE0"), BorderBrush = Ink("#426275"), Cursor = Cursors.Hand };
        visit.Click += (_, _) => ShowAreaRequested?.Invoke(); footer.Children.Add(visit);
        Drag = new(this);
        Drag.HeldChanged += held => { if (!held) Rebuild(); };
        UpdateItems("Temporary area", Array.Empty<WorkspaceItemView>(), Array.Empty<Guid>(), true);
    }

    public void SetHint(string message) => _hint.Text = message;
    public void SetExpanded(bool expanded)
    {
        _chevron.Text = expanded ? "\u203a" : "\u2039";
        _rail.ToolTip = expanded ? "Fold temporary windows" : "Peek at temporary windows";
    }

    public void UpdateItems(string name, IReadOnlyList<WorkspaceItemView> items, IReadOnlyList<Guid> arrivals, bool reduceMotion)
    {
        _name.Text = name; _count.Text = $"{items.Count} windows \u00b7 newest first"; _railCount.Text = items.Count.ToString();
        _arrivals.UnionWith(arrivals); _reduceMotion = reduceMotion;
        bool changed = !_items.SequenceEqual(items); _items = items;
        if (!Drag.IsHeld && (changed || arrivals.Count > 0 || _rows.Children.Count == 0)) Rebuild();
    }

    private void Rebuild()
    {
        _rows.Children.Clear(); int stagger = 0;
        _empty.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in _items)
        {
            bool added = _arrivals.Contains(item.Id);
            var row = new Border { Background = Ink(added ? "#244E55" : "#1D3042"), BorderBrush = Ink(added ? "#78DCCD" : "#344F63"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 7), Cursor = Cursors.Hand, ToolTip = item.Title + "\n" + item.ExecutablePath };
            System.Windows.Automation.AutomationProperties.SetName(row, item.Title);
            var body = new StackPanel(); row.Child = body;
            body.Children.Add(new TextBlock { Text = item.Title, FontSize = 13, FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis });
            body.Children.Add(new TextBlock { Text = Path.GetFileNameWithoutExtension(item.ExecutablePath) + "  \u00b7  " + item.State,
                FontSize = 10, Foreground = Ink("#A7BFD0"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 5, 0, 0) });
            row.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2) { Drag.Cancel(); PlaceRequested?.Invoke(item.Id); }
                else Drag.Begin(item.Id, e.GetPosition(this));
                e.Handled = true;
            };
            _rows.Children.Add(row);
            if (added && !_reduceMotion)
            {
                var delay = TimeSpan.FromMilliseconds(Math.Min(stagger++, 5) * 55);
                var move = new TranslateTransform(); row.RenderTransform = move;
                var duration = TimeSpan.FromMilliseconds(340);
                move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(30, 0, duration) { BeginTime = delay, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                row.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { BeginTime = delay });
            }
        }
        if (_arrivals.Count > 0) _scroll.ScrollToTop();
        _arrivals.Clear();
    }
}
