using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;

namespace CanvasDesktop;

/// <summary>WPF workspace shell; all actions pass through the existing commands.</summary>
internal sealed class WorkspaceWindow : Window
{
    private readonly IWorkspaceCommands _workspace;
    private readonly TextBox _filter = new();
    private readonly ListBox _items = new() { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock _selection = new() { TextWrapping = TextWrapping.Wrap, FontSize = 18, FontWeight = FontWeights.SemiBold };
    private readonly StackPanel _actions = new();
    private bool _closed, _refreshQueued;

    public WorkspaceWindow(IWorkspaceCommands workspace)
    {
        _workspace = workspace;
        Title = "Saved workspace · Canvas"; Width = 1000; Height = 720; MinWidth = 760; MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ZoneTheme.Apply(this);
        var root = new DockPanel { Margin = new Thickness(24) }; Content = root;
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        header.Children.Add(new TextBlock { Text = "Saved workspace", FontSize = 28, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = "Find a saved place, reopen it or choose what opens at startup.",
            Margin = new Thickness(0, 6, 0, 16), Foreground = ZoneTheme.Brush("#A7B3C9") });
        header.Children.Add(new TextBlock { Text = "Search by name, application or path", FontSize = 12,
            Margin = new Thickness(0, 0, 0, 6), Foreground = ZoneTheme.Brush("#A7B3C9") });
        header.Children.Add(_filter); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        System.Windows.Automation.AutomationProperties.SetName(_filter, "Search saved workspace");
        System.Windows.Automation.AutomationProperties.SetName(_items, "Saved windows");
        _status.Margin = new Thickness(0, 16, 0, 0); _status.Foreground = ZoneTheme.Brush("#A7B3C9");
        DockPanel.SetDock(_status, Dock.Bottom); root.Children.Add(_status);
        var body = new Grid(); root.Children.Add(body);
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        body.Children.Add(_items);
        _items.Margin = new Thickness(0, 0, 20, 0);
        ScrollViewer.SetHorizontalScrollBarVisibility(_items, ScrollBarVisibility.Disabled);
        _items.ItemContainerStyle = (Style)XamlReader.Parse("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="ListBoxItem">
              <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
              <Setter Property="Padding" Value="6"/>
              <Setter Property="Margin" Value="0,0,0,6"/>
            </Style>
            """);
        _items.ItemTemplate = (DataTemplate)XamlReader.Parse("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <Grid Margin="4">
                <Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                <CheckBox IsChecked="{Binding OpenOnStartup, Mode=OneWay}" VerticalAlignment="Top"
                          AutomationProperties.Name="Open this saved window on startup"/>
                <StackPanel Grid.Column="1">
                  <TextBlock Text="{Binding Title}" FontSize="15" FontWeight="SemiBold" TextTrimming="CharacterEllipsis"/>
                  <TextBlock Text="{Binding State}" Foreground="#93C5FD" Margin="0,4,0,4"/>
                  <TextBlock Foreground="#A7B3C9" FontSize="12" TextTrimming="CharacterEllipsis">
                    <TextBlock.Style>
                      <Style TargetType="TextBlock">
                        <Setter Property="Text" Value="{Binding Target}"/>
                        <Style.Triggers>
                          <DataTrigger Binding="{Binding Target}" Value="">
                            <Setter Property="Text" Value="{Binding ExecutablePath}"/>
                          </DataTrigger>
                        </Style.Triggers>
                      </Style>
                    </TextBlock.Style>
                  </TextBlock>
                </StackPanel>
              </Grid>
            </DataTemplate>
            """);
        var detail = new StackPanel();
        var detailScroll = new ScrollViewer { Content = detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(detailScroll, 1); body.Children.Add(detailScroll);
        detail.Children.Add(_selection); detail.Children.Add(_actions);
        AddAction("Go to", workspace.FocusItem); AddAction("Open", workspace.Open);
        AddAction("Link open window…", id => WorkspaceDialogs.LinkWindow(OwnerHandle(), workspace, id));
        AddAction("Name saved place…", id => WorkspaceDialogs.NamePlace(OwnerHandle(), workspace, id));
        AddAction("Close to placeholder", workspace.CloseToPlaceholder);
        AddAction("Choose file…", id => WorkspaceDialogs.ChooseTarget(OwnerHandle(), workspace, id, false));
        AddAction("Choose folder…", id => WorkspaceDialogs.ChooseTarget(OwnerHandle(), workspace, id, true));
        AddAction("Forget saved place", workspace.Forget);
        _items.SelectionChanged += (_, _) => UpdateSelection();
        _items.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is CheckBox box && box.DataContext is WorkspaceItemView item)
                workspace.SetAutoOpen(item.Id, box.IsChecked == true);
        }));
        _items.MouseDoubleClick += (_, e) =>
        {
            if (e.OriginalSource is TextBlock && _items.SelectedItem is WorkspaceItemView item) workspace.FocusItem(item.Id);
        };
        _items.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _items.SelectedItem is WorkspaceItemView item)
            { workspace.FocusItem(item.Id); e.Handled = true; }
        };
        _filter.TextChanged += (_, _) => RefreshItems();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        workspace.Changed += OnChanged;
        Closed += (_, _) => { _closed = true; workspace.Changed -= OnChanged; };
        RefreshItems();
    }

    private System.Windows.Forms.IWin32Window OwnerHandle() => new NativeDialogOwner(new WindowInteropHelper(this).Handle);

    private void AddAction(string text, Action<Guid> command)
        => _actions.Children.Add(ZoneTheme.Button(text, () =>
        { if (_items.SelectedItem is WorkspaceItemView item) command(item.Id); }));

    private void OnChanged()
    {
        if (_closed || _refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.BeginInvoke(() => { _refreshQueued = false; if (!_closed) RefreshItems(); });
    }

    private void RefreshItems()
    {
        Guid? selected = (_items.SelectedItem as WorkspaceItemView)?.Id;
        string query = _filter.Text.Trim();
        var items = _workspace.Items.Where(item => query.Length == 0 ||
            item.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            item.Target.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            item.ExecutablePath.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
        _items.ItemsSource = items;
        _items.SelectedItem = items.FirstOrDefault(item => item.Id == selected);
        _status.Text = _workspace.Status ?? (items.Length == 0 ? "No saved places match your search." :
            $"{items.Length} saved {(items.Length == 1 ? "place" : "places")} · Checked items open when Canvas starts. Double-click a row to go to it.");
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var selected = _items.SelectedItem as WorkspaceItemView;
        _actions.IsEnabled = selected != null;
        _selection.Text = selected == null ? "Select a saved place" :
            $"{selected.Title}\n\n{selected.Bounds.X:0}, {selected.Bounds.Y:0}";
        _selection.Margin = new Thickness(0, 0, 0, 12);
    }
}

internal sealed record NativeDialogOwner(IntPtr Handle) : System.Windows.Forms.IWin32Window;

