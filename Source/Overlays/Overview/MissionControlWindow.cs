using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Wpf.Ui.Markup;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace CanvasDesktop;

internal sealed class MissionControlWindow : FluentWindow
{
    private readonly StackPanel _body = new() { Margin = new(20, 12, 20, 20) };
    private readonly TextBlock _feedback = new() { TextWrapping = TextWrapping.Wrap };
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HerdrSessions _herdr;
    private TextBox? _initialInput;
    internal event Action? TerminalChanged;
    internal event Action? TerminalOpened;

    public MissionControlWindow(IntPtr owner, HerdrSessions herdr)
    {
        _herdr = herdr;
        if (owner != IntPtr.Zero) new WindowInteropHelper(this).Owner = owner;
        Title = "Mission Control";
        Width = 460;
        Height = 475;
        MinWidth = 355;
        MinHeight = 335;
        ShowInTaskbar = false;
        Topmost = true;
        ExtendsContentIntoTitleBar = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = ApplicationTheme.Dark });
        Resources.MergedDictionaries.Add(new ControlsDictionary());
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Background = new SolidColorBrush(Color.FromRgb(24, 30, 42));
        Foreground = Brushes.White;
        var root = new DockPanel();
        var title = new TitleBar { Title = Title, ShowMaximize = false, ShowMinimize = false };
        DockPanel.SetDock(title, Dock.Top);
        root.Children.Add(title);
        root.Children.Add(new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(this);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Closed += (_, _) => { _lifetime.Cancel(); _lifetime.Dispose(); };
    }

    public void Present()
    {
        Show();
        // Let the dock's context menu finish restoring focus before taking it.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
        {
            if (!IsVisible) return;
            Activate();
            _initialInput?.Focus();
        }));
    }

    public void ShowMissions(IReadOnlyList<ZoneView> missions, Func<string, bool> create, Action<ZoneView> jump,
        Action<ZoneView> inspect)
    {
        Heading("Missions on this desktop");
        foreach (var mission in missions)
        {
            var row = new WrapPanel();
            row.Children.Add(ActionButton(MissionName(mission), () => jump(mission)));
            row.Children.Add(ActionButton("Inspect", () => inspect(mission)));
            _body.Children.Add(row);
        }
        if (missions.Count == 0) Note("Create a mission at the current canvas position.");
        NameEntry("Mission name", "New mission", create, "Could not save the mission at this position.");
        _body.Children.Add(_feedback);
    }

    public void ShowMission(ZoneView mission, IReadOnlyList<MissionAgentCard> cards, Action<MissionAgentCard> inspect,
        Func<bool, bool> setLocked, Func<string, bool> addAgent)
    {
        Heading(MissionName(mission));
        _body.Children.Add(LockButton(mission.MissionPositionLocked, setLocked));
        foreach (var card in cards)
            _body.Children.Add(ActionButton(card.Name, () => inspect(card)));
        if (cards.Count == 0) Note("Add the first agent for this mission.");
        NameEntry("Agent name", "Add agent", addAgent, "Could not add the agent to this mission.");
        _body.Children.Add(_feedback);
    }

    private void NameEntry(string label, string action, Func<string, bool> submit, string error)
    {
        var input = new TextBox { PlaceholderText = label, MaxLength = 120, Margin = new(0, 10, 0, 6) };
        System.Windows.Automation.AutomationProperties.SetName(input, label);
        _initialInput = input;
        _body.Children.Add(input);
        void Submit()
        {
            string name = input.Text.Trim();
            if (name.Length == 0) { _feedback.Text = $"Enter a {label.ToLowerInvariant()}."; return; }
            if (!submit(name)) _feedback.Text = error;
        }
        input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Submit(); } };
        _body.Children.Add(ActionButton(action, Submit));
    }

    public void ShowAgent(ZoneView mission, MissionAgentCard card, AgentStatusReport? status, string? folder,
        Func<bool, bool> setLocked, Func<string, bool> saveFolder, Action showMission)
    {
        _body.Children.Add(ActionButton("‹ " + MissionName(mission), showMission));
        Heading(card.Name);
        Note(status == null ? "No workflow status reported." : "At opening · " + status.State + (status.Detail.Length > 0 ? " · " + status.Detail : ""));
        if (status?.Timestamp is { } timestamp && timestamp != default)
            Note("Last workflow update " + timestamp.ToLocalTime().ToString("g"));
        var directory = new TextBox { Text = folder ?? "", PlaceholderText = "Local working folder", Margin = new(0, 10, 0, 5) };
        System.Windows.Automation.AutomationProperties.SetName(directory, "Local working folder");
        _body.Children.Add(directory);
        _body.Children.Add(ActionButton("Save working folder", () =>
        {
            _feedback.Text = saveFolder(directory.Text.Trim())
                ? "Working folder saved for this mission." : "Choose an existing local working folder.";
        }));
        _body.Children.Add(_feedback);
        var actions = new WrapPanel();
        _body.Children.Add(actions);
        var launch = new Button { Content = "Start Codex", Appearance = ControlAppearance.Primary };
        var watch = new Button { Content = "Watch terminal", Margin = new(5, 0, 0, 0) };
        var control = new Button { Content = "Open terminal", Margin = new(5, 0, 0, 0) };
        var refresh = new Button { Content = "Refresh", Margin = new(5, 0, 0, 0) };
        foreach (var button in new[] { launch, watch, control, refresh }) actions.Children.Add(button);
        _body.Children.Add(LockButton(mission.MissionPositionLocked, setLocked));
        HerdrSessions.Terminal? terminal = null;
        async Task Load(CancellationToken token)
        {
            terminal = await _herdr.Find(card.Id, token);
            _feedback.Text = terminal switch
            {
                { IsLive: true } live => $"{live.Address} · {live.AgentState ?? "No status reported"}",
                { } ended => $"{ended.Address} · Session ended",
                _ => "No Herdr terminal linked."
            };
            if (terminal is { IsLive: true, WorkingDirectory: { } cwd }) directory.Text = cwd;
            TerminalChanged?.Invoke();
        }
        async Task Run(Func<CancellationToken, Task> action)
        {
            var token = _lifetime.Token;
            foreach (var button in new[] { launch, watch, control, refresh }) button.IsEnabled = false;
            _feedback.Text = "Checking Herdr…";
            bool checkedTerminal = false;
            try { await action(token); checkedTerminal = true; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                or System.Text.Json.JsonException or System.IO.IOException or ArgumentException or KeyNotFoundException)
            {
                if (!token.IsCancellationRequested) _feedback.Text = ex.Message;
            }
            finally
            {
                if (!token.IsCancellationRequested)
                {
                    refresh.IsEnabled = true;
                    launch.IsEnabled = checkedTerminal && terminal?.IsLive != true;
                    watch.IsEnabled = control.IsEnabled = checkedTerminal && terminal?.IsLive == true;
                    directory.IsEnabled = terminal?.IsLive != true;
                }
            }
        }
        launch.Click += async (_, _) => await Run(async token =>
        {
            if (await _herdr.Find(card.Id, token) is not { IsLive: true })
                await _herdr.StartCodex(directory.Text.Trim(), card.Id, token);
            await Load(token);
        });
        watch.Click += async (_, _) => await Run(async token =>
        {
            await _herdr.Open(card.Id, terminal!.Address, observe: true, token);
            TerminalOpened?.Invoke();
        });
        control.Click += async (_, _) => await Run(async token =>
        {
            await _herdr.Open(card.Id, terminal!.Address, observe: false, token);
            TerminalOpened?.Invoke();
        });
        refresh.Click += async (_, _) => await Run(Load);
        Loaded += async (_, _) => await Run(Load);
    }

    private Button LockButton(bool locked, Func<bool, bool> change)
    {
        var button = ActionButton(locked ? "Unlock position" : "Lock position", () => { });
        button.Click += (_, _) =>
        {
            if (!change(!locked)) { _feedback.Text = "Could not save mission position lock."; return; }
            locked = !locked;
            button.Content = locked ? "Unlock position" : "Lock position";
        };
        return button;
    }

    internal static string MissionName(ZoneView mission) =>
        mission.Name.StartsWith("AI Mission Control: ", StringComparison.Ordinal) ? mission.Name[20..] : mission.Name;

    private static Button ActionButton(string text, Action action)
    {
        var button = new Button { Content = text, Margin = new(0, 5, 6, 5) };
        button.Click += (_, _) => action();
        return button;
    }

    private void Heading(string title) => _body.Children.Add(new TextBlock
    {
        Text = title, FontSize = 21, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap
    });

    private void Note(string detail) => _body.Children.Add(new TextBlock
    {
        Text = detail, TextWrapping = TextWrapping.Wrap, Margin = new(0, 5, 0, 0)
    });
}
