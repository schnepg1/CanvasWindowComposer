using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CanvasDesktop;

/// <summary>Compact modeless navigator. Stable IDs preserve selection/expansion across workspace refreshes.</summary>
internal sealed class ZoneNavigatorWindow : Window
{
    private sealed record Selection(Guid AreaId, Guid? MemberId = null);
    private readonly ZoneNavigation _navigation;
    private readonly TextBox _search = new() { Name = "ProjectSearch", MaxLength = 240 };
    private readonly TreeView _tree = new() { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
    private readonly TextBlock _empty = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 16, 8, 0) };
    private readonly TextBlock _status = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _jump, _activate;
    private readonly HashSet<Guid> _expanded = new();
    private IReadOnlyList<ZoneNavigationEntry>? _snapshot;
    private string _lastQuery = "";
    private bool _refreshing;
    private bool _closed;

    public ZoneNavigatorWindow(ZoneNavigation navigation, Action<Guid?> manage)
    {
        _navigation = navigation;
        Title = "Project navigator · Canvas"; Width = 360; Height = 560; MinWidth = 320; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ZoneTheme.Apply(this);
        ScrollViewer.SetHorizontalScrollBarVisibility(_tree, ScrollBarVisibility.Disabled);
        System.Windows.Automation.AutomationProperties.SetName(_search, "Search project areas and members");
        System.Windows.Automation.AutomationProperties.SetName(_tree, "Project areas and saved members");
        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = "Projects", FontSize = 24, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = "On this virtual desktop", FontSize = 12,
            Foreground = ZoneTheme.Brush("#A7B3C9"), Margin = new Thickness(0, 4, 0, 12) });
        header.Children.Add(_search);
        header.Children.Add(new TextBlock { Text = "Search areas, members or paths", FontSize = 12,
            Foreground = ZoneTheme.Brush("#A7B3C9"), Margin = new Thickness(0, 6, 0, 12) });
        root.Children.Add(header);
        var body = new Grid(); Grid.SetRow(body, 1); root.Children.Add(body);
        body.Children.Add(_tree); body.Children.Add(_empty);
        _empty.Foreground = ZoneTheme.Brush("#A7B3C9");
        var footer = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        Grid.SetRow(footer, 2); root.Children.Add(footer);
        var actions = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
        _jump = ZoneTheme.Button("Jump to area", Jump);
        _activate = ZoneTheme.Button("Open member", ActivateMember);
        _jump.Margin = new Thickness(0, 4, 4, 0); _activate.Margin = new Thickness(4, 4, 0, 0);
        actions.Children.Add(_jump); actions.Children.Add(_activate); footer.Children.Add(actions);
        footer.Children.Add(ZoneTheme.Button("Manage areas…", () => manage(Current?.AreaId)));
        footer.Children.Add(_status);
        _tree.SelectedItemChanged += (_, _) => UpdateActions();
        _tree.MouseDoubleClick += (_, e) =>
        {
            // Ignore empty space and expander toggles; activate only a row's text.
            if (e.OriginalSource is TextBlock) { ActivateSelection(); e.Handled = true; }
        };
        _tree.KeyDown += (_, e) => { if (e.Key == Key.Enter) { ActivateSelection(); e.Handled = true; } };
        _search.TextChanged += (_, _) => Refresh();
        _search.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Jump(); e.Handled = true; } };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                if (_search.Text.Length > 0) _search.Clear(); else Hide();
                e.Handled = true;
            }
            else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            { FocusSearch(); e.Handled = true; }
            else if (e.Key == Key.Down && _search.IsKeyboardFocusWithin && _tree.Items.Count > 0)
            { ((TreeViewItem)_tree.Items[0]).Focus(); e.Handled = true; }
        };
        navigation.Changed += Refresh;
        IsVisibleChanged += (_, _) => { if (IsVisible) Refresh(); };
        Closed += (_, _) => { _closed = true; navigation.Changed -= Refresh; };
        Refresh();
    }

    public void FocusSearch() { _search.Focus(); _search.SelectAll(); }
    private Selection? Current => (_tree.SelectedItem as TreeViewItem)?.Tag as Selection;

    private void Refresh()
    {
        if (_closed || IsLoaded && !IsVisible) return;
        var entries = _navigation.Read(_search.Text);
        // Workspace ticks need not reconstruct an unchanged tree or disrupt keyboard focus.
        bool same = _snapshot != null && _lastQuery == _search.Text && _snapshot.Count == entries.Count
            && _snapshot.Zip(entries).All(pair => pair.First.Area == pair.Second.Area
                && pair.First.Members.SequenceEqual(pair.Second.Members));
        _status.Text = _navigation.Status ?? "Enter: jump or open   ·   Esc: clear search / hide";
        if (same) { UpdateActions(); return; }
        var selected = Current;
        bool restoreFocus = _tree.IsKeyboardFocusWithin;
        _refreshing = true;
        _tree.Items.Clear();
        _snapshot = entries; _lastQuery = _search.Text;
        foreach (var entry in entries)
        {
            var area = new TreeViewItem { Tag = new Selection(entry.Area.Id),
                Header = Row(entry.Area.Name, $"{entry.Area.MemberCount} members", entry.Area.Color),
                IsExpanded = _expanded.Contains(entry.Area.Id) || _search.Text.Trim().Length > 0,
                Foreground = Brushes.WhiteSmoke, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            System.Windows.Automation.AutomationProperties.SetName(area, $"{entry.Area.Name}, {entry.Area.MemberCount} members");
            area.Expanded += (_, e) => { if (!_refreshing && e.OriginalSource == area) _expanded.Add(entry.Area.Id); };
            area.Collapsed += (_, e) => { if (!_refreshing && e.OriginalSource == area) _expanded.Remove(entry.Area.Id); };
            _tree.Items.Add(area);
            foreach (var member in entry.Members)
            {
                var row = new TreeViewItem { Tag = new Selection(entry.Area.Id, member.Id), Foreground = Brushes.WhiteSmoke,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Header = Row(member.Title, member.State + (member.Pinned ? " · Pinned" : ""), "#F1F5F9"), ToolTip = member.Details };
                System.Windows.Automation.AutomationProperties.SetName(row, member.Title + ", " + member.State);
                area.Items.Add(row);
                if (Equals(row.Tag, selected)) { row.IsSelected = true; area.IsExpanded = true; if (restoreFocus) row.Focus(); }
            }
            if (Equals(area.Tag, selected)) { area.IsSelected = true; if (restoreFocus) area.Focus(); }
        }
        if (_tree.SelectedItem == null && _tree.Items.Count > 0) ((TreeViewItem)_tree.Items[0]).IsSelected = true;
        _refreshing = false;
        _empty.Text = _search.Text.Trim().Length > 0 ? "No matching areas or members. Try another search."
            : "No project areas on this desktop. Use Manage areas to create one.";
        _empty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateActions();
    }

    private static FrameworkElement Row(string title, string subtitle, string color)
    {
        var row = new StackPanel { Margin = new Thickness(4, 6, 4, 6), MaxWidth = 250 };
        row.Children.Add(new TextBlock { Text = title, Foreground = ZoneTheme.Brush(color),
            FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = title });
        row.Children.Add(new TextBlock { Text = subtitle, FontSize = 12, Foreground = ZoneTheme.Brush("#A7B3C9"),
            Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        return row;
    }

    private void UpdateActions()
    {
        var selected = Current;
        var entry = _snapshot?.FirstOrDefault(a => a.Area.Id == selected?.AreaId);
        var member = entry?.Members.FirstOrDefault(m => m.Id == selected?.MemberId);
        _jump.IsEnabled = entry != null;
        _activate.IsEnabled = member?.CanActivate == true;
        _activate.Content = member?.ActionLabel ?? "Open member";
        _activate.ToolTip = member?.State == "Needs linking" ? "Link this place in Saved Workspace first."
            : member?.Details;
    }
    private void ActivateSelection() { if (Current?.MemberId != null) ActivateMember(); else Jump(); }
    private void Jump()
    {
        if (Current is { } selected && !_navigation.Jump(selected.AreaId)) Refresh();
    }
    private void ActivateMember()
    {
        if (Current is { MemberId: { } id } selected)
        {
            bool accepted = _navigation.ActivateMember(selected.AreaId, id);
            Refresh();
            if (!accepted) _status.Text = "That member is no longer available to open. Select it again after refresh.";
        }
    }
}
