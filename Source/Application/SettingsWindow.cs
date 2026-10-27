using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;

namespace CanvasDesktop;

/// <summary>Detached edits; opening or cancelling settings never changes resource policy.</summary>
internal sealed class SettingsWindow : Window
{
    private readonly CheckBox _search, _altPan, _zoom, _curve, _fixed, _clip, _parking, _flip, _intro, _reducedMotion, _lowQuality, _taskbar, _navigation, _desktopMomentum, _canvasMomentum;
    private readonly ZoomSettingsControl _zoomSettings;
    internal bool ZoomOnly { get; }
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };

    public SettingsWindow(AppConfig config, Action applied, bool zoomOnly = false)
    {
        ZoomOnly = zoomOnly;
        Title = "Settings · Canvas"; Width = 580; Height = 820; MinWidth = 440; MinHeight = 420;
        if (zoomOnly) { Title = "Zoom settings · Canvas"; Width = 460; Height = 760; MinWidth = 360; }
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ZoneTheme.Apply(this);
        var root = new DockPanel { Margin = new Thickness(24) };
        Content = root;
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(_status);
        var actions = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Margin = new Thickness(0, 12, 0, 0) };
        var body = new StackPanel();
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 12, 0) });
        void Heading(string text) => body.Children.Add(new TextBlock { Text = text, FontSize = 22,
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 12) });
        void Hint(string text) => body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap,
            FontSize = 12, Foreground = ZoneTheme.Brush("#A7B3C9"), Margin = new Thickness(0, 0, 0, 12) });
        CheckBox Option(string text, bool value)
        {
            var box = new CheckBox { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
                IsChecked = value, Margin = new Thickness(0, 0, 0, 14) };
            AutomationProperties.SetName(box, text);
            body.Children.Add(box); return box;
        }
        var preferences = config.Preferences;
        Heading("Interaction");
        _search = Option("Window search · Alt+S", preferences.Search);
        _zoom = Option("Overview shortcut · Alt+Q", preferences.ZoomHotkey);
        _altPan = Option("Pan over windows with Alt+middle-click", preferences.AltPan);
        _curve = Option("Match Windows pointer acceleration when panning", preferences.MouseCurve);
        _desktopMomentum = Option("Desktop pan momentum", preferences.DesktopPanMomentum);
        _canvasMomentum = Option("Canvas pan momentum", preferences.CanvasPanMomentum);
        Hint("Continue gliding after releasing a pan. Turn either off to stop that view on release. Dragging still follows the pointer. Applies immediately. During desktop panning, click to stop, then click again to interact.");
        _fixed = Option("Show pinned and fullscreen windows while panning", preferences.ScreenFixedWindows);
        _navigation = Option("Animate navigation to apps selected with Alt+Tab or the taskbar", preferences.AnimateWindowNavigation);
        Hint("Stays in Canvas view at the current zoom. Turn off for an immediate jump with less rendering work.");
        Hint("Shortcut changes take effect after restarting Canvas.");
        Heading("Window resources");
        _clip = Option("Clip windows outside the desktop", preferences.ClipOffscreen);
        Hint("Clipping may affect application previews. It does not guarantee that applications stop rendering.");
        _parking = Option("Automatically minimize off-screen windows · Experimental", preferences.AutomaticMinimization);
        Hint("Canvas captures a preview first and restores eligible windows as you approach. Application compatibility testing is still in progress.");
        Heading("Startup");
        _intro = Option("Show the cinematic canvas intro at startup", preferences.StartupIntro);
        _reducedMotion = Option("Reduce startup motion · use a short title fade", preferences.ReducedIntroMotion);
        Hint("Click, scroll, or press any key to skip. Windows animation preferences are respected.");
        Heading("Rendering");
        _taskbar = Option("Show Windows taskbar in canvas view", preferences.ShowTaskbarInCanvas);
        Hint("Keeps the taskbar visible and clickable while panning and zooming. Applies immediately. Turn off auto-hide in Windows taskbar settings to keep it visible permanently.");
        _lowQuality = Option("Low quality mode", preferences.LowQualityMode);
        Hint("Uses a simple grid and cached previews or title cards while panning, zooming or dragging windows. Live previews return after movement stops. Applies immediately.");
        _flip = Option("Use flip-model presentation · Experimental", preferences.FlipRendering);
        Hint("Requires a restart. Keep off if overview previews or minimap opacity render incorrectly. Canvas uses the compatibility renderer if initialization fails.");
        if (zoomOnly) body.Children.Clear();
        _zoomSettings = new ZoomSettingsControl(preferences);
        body.Children.Add(_zoomSettings);
        var save = ZoneTheme.Button("Save settings", () =>
        {
            try
            {
                var draft = config.Preferences;
                if (!zoomOnly) draft = draft with { Search = _search.IsChecked == true, AltPan = _altPan.IsChecked == true,
                    ZoomHotkey = _zoom.IsChecked == true, MouseCurve = _curve.IsChecked == true,
                    ScreenFixedWindows = _fixed.IsChecked == true, ClipOffscreen = _clip.IsChecked == true,
                    AutomaticMinimization = _parking.IsChecked == true, FlipRendering = _flip.IsChecked == true,
                    StartupIntro = _intro.IsChecked == true, ReducedIntroMotion = _reducedMotion.IsChecked == true,
                    LowQualityMode = _lowQuality.IsChecked == true, ShowTaskbarInCanvas = _taskbar.IsChecked == true,
                    AnimateWindowNavigation = _navigation.IsChecked == true,
                    DesktopPanMomentum = _desktopMomentum.IsChecked == true, CanvasPanMomentum = _canvasMomentum.IsChecked == true };
                if (!_zoomSettings.TryApply(draft, out var updated, out var error)) { _status.Text = error; return; }
                config.SavePreferences(updated);
                applied(); Close();
            }
            catch (Exception ex) { _status.Text = "Settings could not be saved. " + ex.Message; }
        });
        save.IsDefault = true;
        var cancel = ZoneTheme.Button("Cancel", Close); cancel.IsCancel = true;
        save.Margin = new Thickness(0, 0, 6, 0); cancel.Margin = new Thickness(6, 0, 0, 0);
        actions.Children.Add(save); actions.Children.Add(cancel); footer.Children.Add(actions);
    }
}
