using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CanvasDesktop;

internal sealed class ZoneWindow : Window
{
    private readonly IZoneCommands _zones;
    private readonly Action _draw;
    private readonly Action<ZoneView> _jump;
    private readonly Func<string?> _status;
    private readonly ListBox _list = new();
    private readonly TextBox _name = new() { Name = "AreaName", MaxLength = 120 };
    private readonly TextBox _color = new() { Name = "AreaColor", MaxLength = 7, Text = "#60A5FA" };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _editor = new();
    private readonly IZoneLayoutCommands? _history;
    private readonly Button _undo;
    private readonly Button _redo;
    private bool _refreshing;
    private static SolidColorBrush Brush(string hex) => ZoneTheme.Brush(hex);

    public ZoneWindow(IZoneCommands zones, Action draw, Action<ZoneView> jump, Func<string?> status,
        IZoneLayoutCommands? history = null, Action<bool>? replay = null)
    {
        _zones = zones; _draw = draw; _jump = jump; _status = status;
        _history = history;
        _undo = Button("Undo", () => replay?.Invoke(false));
        _redo = Button("Redo", () => replay?.Invoke(true));
        System.Windows.Automation.AutomationProperties.SetName(_name, "Area name");
        System.Windows.Automation.AutomationProperties.SetName(_color, "Area color in hexadecimal");
        System.Windows.Automation.AutomationProperties.SetName(_list, "Project areas on this desktop");
        Title = "Project areas · Canvas";
        Width = 440; Height = 750; MinWidth = 360; MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ZoneTheme.Apply(this);
        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = "Project areas", FontSize = 28, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = "Give each project a place on your canvas.",
            Foreground = Brush("#A7B3C9"), Margin = new Thickness(0, 6, 0, 16), TextWrapping = TextWrapping.Wrap });
        var drawButton = Button("+  Draw an area", () => { Hide(); _draw(); });
        drawButton.Background = Brush("#2563EB");
        if (history != null)
        {
            var historyButtons = new Grid();
            historyButtons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            historyButtons.ColumnDefinitions.Add(new ColumnDefinition());
            historyButtons.ColumnDefinitions.Add(new ColumnDefinition());
            drawButton.Margin = new Thickness(0, 4, 4, 0);
            _undo.Margin = new Thickness(4, 4, 4, 0); _redo.Margin = new Thickness(4, 4, 0, 0);
            _undo.ToolTip = "Undo the last area move or resize on this desktop";
            _redo.ToolTip = "Redo an undone area move or resize";
            System.Windows.Automation.AutomationProperties.SetName(_undo, "Undo area layout");
            System.Windows.Automation.AutomationProperties.SetName(_redo, "Redo area layout");
            Grid.SetColumn(_undo, 1); Grid.SetColumn(_redo, 2);
            historyButtons.Children.Add(drawButton);
            historyButtons.Children.Add(_undo); historyButtons.Children.Add(_redo);
            header.Children.Add(historyButtons);
            history.Changed += RefreshHistory;
        }
        else header.Children.Add(drawButton);
        header.Children.Add(new TextBlock { Text = "Or press Alt+Q, then Shift + drag. Areas belong to this virtual desktop.",
            FontSize = 12, Foreground = Brush("#A7B3C9"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 16) });
        root.Children.Add(header);

        _list.Background = Brushes.Transparent;
        _list.BorderThickness = new Thickness(0);
        _list.SelectionChanged += (_, _) => LoadSelection();
        _list.MouseDoubleClick += (_, _) => Jump();
        _list.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { Jump(); e.Handled = true; } };
        Grid.SetRow(_list, 1); root.Children.Add(_list);

        var footer = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        Grid.SetRow(footer, 2); root.Children.Add(footer);
        footer.Children.Add(_editor);
        _editor.Children.Add(new TextBlock { Text = "Area name", Margin = new Thickness(0, 0, 0, 6) });
        _editor.Children.Add(_name);
        _editor.Children.Add(new TextBlock { Text = "Color", Margin = new Thickness(0, 12, 0, 6) });
        var palette = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var color in new[] { "#60A5FA", "#A78BFA", "#34D399", "#FBBF24", "#FB7185", "#22D3EE" })
        {
            var swatch = new System.Windows.Controls.Button { Background = Brush(color), Width = 34, Height = 30,
                Margin = new Thickness(0, 0, 8, 8), ToolTip = color };
            System.Windows.Automation.AutomationProperties.SetName(swatch, "Use color " + color);
            swatch.Click += (_, _) => _color.Text = color;
            palette.Children.Add(swatch);
        }
        _editor.Children.Add(palette);
        _editor.Children.Add(_color);
        var actions = new System.Windows.Controls.Primitives.UniformGrid
            { Columns = 2, Margin = new Thickness(0, 8, 0, 4) };
        actions.Children.Add(Button("Save changes", Save));
        actions.Children.Add(Button("Jump to area", Jump));
        ((FrameworkElement)actions.Children[0]).Margin = new Thickness(0, 4, 4, 0);
        ((FrameworkElement)actions.Children[1]).Margin = new Thickness(4, 4, 0, 0);
        _editor.Children.Add(actions);
        _editor.Children.Add(Button("Choose members…", ChooseMembers));
        _editor.Children.Add(Button("Remove area", Remove));
        footer.Children.Add(_message);
        _message.FontSize = 12; _message.Foreground = Brush("#A7B3C9"); _message.Margin = new Thickness(0, 12, 0, 0);
        _zones.Changed += Refresh;
        Closed += (_, _) => { _zones.Changed -= Refresh; if (_history != null) _history.Changed -= RefreshHistory; };
        RefreshHistory();
        Refresh();
    }

    private void RefreshHistory()
    {
        _undo.IsEnabled = _history?.CanUndoZoneLayout == true;
        _redo.IsEnabled = _history?.CanRedoZoneLayout == true;
    }

    private static Button Button(string text, Action action) => ZoneTheme.Button(text, action);

    private ZoneView? Selected => (_list.SelectedItem as ListBoxItem)?.Tag as ZoneView;
    public void Select(Guid id)
    {
        _list.SelectedItem = _list.Items.Cast<ListBoxItem>().FirstOrDefault(i => ((ZoneView)i.Tag).Id == id);
        _name.Focus(); _name.SelectAll();
    }

    private void Refresh()
    {
        RefreshHistory();
        Guid? selected = Selected?.Id;
        _refreshing = true;
        _list.Items.Clear();
        foreach (var zone in _zones.Items)
        {
            var text = new StackPanel { Margin = new Thickness(8) };
            text.Children.Add(new TextBlock { Text = zone.Name, FontWeight = FontWeights.SemiBold,
                Foreground = Brush(zone.Color), TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = $"{zone.MemberCount} members · {zone.Bounds.W:N0} × {zone.Bounds.H:N0} units",
                FontSize = 12, Foreground = Brush("#A7B3C9"), Margin = new Thickness(0, 4, 0, 0) });
            _list.Items.Add(new ListBoxItem { Content = text, Tag = zone,
                HorizontalContentAlignment = HorizontalAlignment.Stretch });
        }
        _list.SelectedItem = _list.Items.Cast<ListBoxItem>().FirstOrDefault(i => ((ZoneView)i.Tag).Id == selected);
        if (_list.SelectedItem == null && _list.Items.Count > 0) _list.SelectedIndex = 0;
        _refreshing = false;
        LoadSelection();
        Report(_list.Items.Count == 0 ? "No areas yet. Draw an area to get started." : "Areas save automatically with your workspace.");
    }

    private void LoadSelection()
    {
        if (_refreshing) return;
        _editor.IsEnabled = Selected != null;
        _name.Text = Selected?.Name ?? ""; _color.Text = Selected?.Color ?? "#60A5FA";
    }

    private void Save()
    {
        if (Selected is not { } zone) return;
        if (!ZoneService.ValidName(_name.Text) || !ZoneService.ValidColor(_color.Text))
        { Report("Enter an area name and a color such as #60A5FA."); return; }
        bool saved = _zones.Update(zone.Id, _name.Text, _color.Text);
        Report(saved ? "Changes saved." : _status() ?? "Changes are in memory; saving failed. Try again.");
    }

    private void Jump()
    { if (Selected is { } zone) { Hide(); _jump(zone); } }

    private void ChooseMembers()
    {
        if (Selected is not { } zone) return;
        var window = new ZoneMembersWindow(_zones, zone, _status) { Owner = this };
        window.ShowDialog();
    }

    private void Remove()
    {
        if (Selected is not { } zone) return;
        if (System.Windows.MessageBox.Show(this, $"Remove the area ‘{zone.Name}’? Its windows and files will stay in place.",
            "Remove area", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        bool saved = _zones.Remove(zone.Id);
        if (!saved) Report(_status() ?? "Removal is in memory; saving failed.");
    }

    public void Report(string message) => _message.Text = message;

}
