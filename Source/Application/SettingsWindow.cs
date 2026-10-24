using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;

namespace CanvasDesktop;

/// <summary>Detached edits; opening or cancelling settings never changes resource policy.</summary>
internal sealed class SettingsWindow : Window
{
    private readonly CheckBox _search, _altPan, _zoom, _curve, _fixed, _clip, _parking, _flip, _intro, _reducedMotion, _lowQuality;
    private readonly CheckBox _liveTier, _detailedTier, _simpleTier;
    private readonly TextBox _cardEnter, _cardExit, _simpleEnter, _simpleExit;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };

    public SettingsWindow(AppConfig config, Action applied)
    {
        Title = "Settings · Canvas"; Width = 580; Height = 820; MinWidth = 440; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ZoneTheme.Apply(this);
        var root = new DockPanel { Margin = new Thickness(24) };
        Content = root;
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(_status);
        var actions = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Margin = new Thickness(0, 12, 0, 0) };
        var body = new StackPanel();
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
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
        TextBox NumberInput(string label, double value)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            panel.Children.Add(new TextBlock { Text = label, Width = 280, VerticalAlignment = VerticalAlignment.Center });
            var box = new TextBox
            {
                Text = value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                Width = 70,
                Padding = new Thickness(4, 2, 4, 2),
                VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetName(box, label);
            panel.Children.Add(box);
            body.Children.Add(panel);
            return box;
        }
        double ParseZoom(TextBox box, double fallback, double min, double max)
            => double.TryParse(box.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
               && double.IsFinite(v) && v >= min && v <= max ? v : fallback;
        var preferences = config.Preferences;
        Heading("Interaction");
        _search = Option("Window search · Alt+S", preferences.Search);
        _zoom = Option("Overview shortcut · Alt+Q", preferences.ZoomHotkey);
        _altPan = Option("Pan over windows with Alt+middle-click", preferences.AltPan);
        _curve = Option("Match Windows pointer acceleration when panning", preferences.MouseCurve);
        _fixed = Option("Show pinned and fullscreen windows while panning", preferences.ScreenFixedWindows);
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
        _lowQuality = Option("Low quality mode", preferences.LowQualityMode);
        Hint("Uses a simple grid and cached previews or title cards while panning, zooming or dragging windows. Live previews return after movement stops. Applies immediately.");
        _flip = Option("Use flip-model presentation · Experimental", preferences.FlipRendering);
        Hint("Requires a restart. Keep off if overview previews or minimap opacity render incorrectly. Canvas uses the compatibility renderer if initialization fails.");
        Heading("Overview & Cards");
        _liveTier = Option("Live Previews tier (continuous DWM thumbnails when zoomed in)", preferences.LivePreviewsTier);
        _detailedTier = Option("Detailed Placeholder Cards tier (main resource saver)", preferences.DetailedCardsTier);
        _simpleTier = Option("Simple Cards tier (title-only vector boxes when zoomed far out)", preferences.SimpleCardsTier);
        Hint("Detailed cards save memory and GPU by unregistering live thumbnails and enabling background window parking. Simple cards save texture cache and fillrate when zoomed far out.");
        _cardEnter = NumberInput("Detailed cards enter zoom (zoom out below):", preferences.CardEnterZoom);
        _cardExit = NumberInput("Detailed cards exit zoom (zoom in above):", preferences.CardExitZoom);
        _simpleEnter = NumberInput("Simple cards enter zoom (zoom out below):", preferences.SimpleCardEnterZoom);
        _simpleExit = NumberInput("Simple cards exit zoom (zoom in above):", preferences.SimpleCardExitZoom);
        Hint("Defaults: Detailed cards 0.60 enter / 0.70 exit, Simple cards 0.20 enter / 0.26 exit.");
        var save = ZoneTheme.Button("Save settings", () =>
        {
            try
            {
                double cardEnter = ParseZoom(_cardEnter, 0.60, 0.05, 2.0);
                double cardExit = ParseZoom(_cardExit, 0.70, 0.05, 2.0);
                double simpleEnter = ParseZoom(_simpleEnter, 0.20, 0.01, 1.0);
                double simpleExit = ParseZoom(_simpleExit, 0.26, 0.01, 1.0);
                config.SavePreferences(new(_search.IsChecked == true, _altPan.IsChecked == true,
                    _zoom.IsChecked == true, _curve.IsChecked == true, _fixed.IsChecked == true,
                    _clip.IsChecked == true, _parking.IsChecked == true, _flip.IsChecked == true,
                    _intro.IsChecked == true, _reducedMotion.IsChecked == true, _lowQuality.IsChecked == true,
                    _liveTier.IsChecked == true, _detailedTier.IsChecked == true, _simpleTier.IsChecked == true,
                    cardEnter, cardExit, simpleEnter, simpleExit));
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
