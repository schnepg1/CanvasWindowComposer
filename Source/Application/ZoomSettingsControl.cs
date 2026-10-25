using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace CanvasDesktop;

/// <summary>A detached zoom-policy editor shared by Settings and the canvas popup.</summary>
internal sealed class ZoomSettingsControl : UserControl
{
    private readonly CheckBox _live, _detailed, _simple;
    private readonly Slider _cardEnter, _cardExit, _simpleEnter, _simpleExit;

    public ZoomSettingsControl(CanvasPreferences preferences)
    {
        var body = new StackPanel();
        Content = body;
        body.Children.Add(new TextBlock { Text = "Zoom & cards", FontSize = 22,
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 12) });
        Hint("Choose how windows appear as you zoom. Thresholds are percentages of the canvas zoom level.");
        _live = Option("Live previews", preferences.LivePreviewsTier);
        _detailed = Option("Detailed placeholder cards", preferences.DetailedCardsTier);
        _simple = Option("Simple cards", preferences.SimpleCardsTier);
        Hint("Cards reduce live preview resources. Simple cards use icons and titles at smaller zoom levels.");
        _cardEnter = Threshold("Detailed cards · zoom out below", preferences.CardEnterZoom, 5, 200);
        _cardExit = Threshold("Live previews · zoom in above", preferences.CardExitZoom, 5, 200);
        _simpleEnter = Threshold("Simple cards · zoom out below", preferences.SimpleCardEnterZoom, 1, 100);
        _simpleExit = Threshold("Detailed cards · zoom in above", preferences.SimpleCardExitZoom, 1, 100);
        Hint("Separate zoom-out and zoom-in thresholds prevent flickering between card styles. Zoom-in must be higher than zoom-out.");
        body.Children.Add(ZoneTheme.Button("Reset zoom defaults", () =>
        {
            _live.IsChecked = _detailed.IsChecked = _simple.IsChecked = true;
            _cardEnter.Value = OverviewZoomDefaults.CardEnter * 100;
            _cardExit.Value = OverviewZoomDefaults.CardExit * 100;
            _simpleEnter.Value = OverviewZoomDefaults.SimpleCardEnter * 100;
            _simpleExit.Value = OverviewZoomDefaults.SimpleCardExit * 100;
        }));

        void Hint(string text) => body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap,
            FontSize = 12, Foreground = ZoneTheme.Brush("#A7B3C9"), Margin = new Thickness(0, 0, 0, 12) });
        CheckBox Option(string label, bool value)
        {
            var box = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 0, 0, 12) };
            AutomationProperties.SetName(box, label);
            body.Children.Add(box);
            return box;
        }
        Slider Threshold(string label, double value, double min, double max)
        {
            var header = new DockPanel();
            var readout = new TextBlock { Width = 48, TextAlignment = TextAlignment.Right, FontWeight = FontWeights.SemiBold };
            DockPanel.SetDock(readout, Dock.Right); header.Children.Add(readout);
            header.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
            body.Children.Add(header);
            var slider = new Slider { Minimum = min, Maximum = max, Value = Math.Round(value * 100),
                TickFrequency = 1, IsSnapToTickEnabled = true, SmallChange = 1, LargeChange = 5,
                AutoToolTipPlacement = System.Windows.Controls.Primitives.AutoToolTipPlacement.TopLeft,
                AutoToolTipPrecision = 0, Margin = new Thickness(0, 4, 0, 12) };
            AutomationProperties.SetName(slider, label + " (percent)");
            readout.Text = $"{slider.Value:0}%";
            slider.ValueChanged += (_, _) => readout.Text = $"{slider.Value:0}%";
            body.Children.Add(slider);
            return slider;
        }
    }

    public bool TryApply(CanvasPreferences current, out CanvasPreferences updated, out string? error)
    {
        updated = current;
        error = null;
        if (_live.IsChecked != true && _detailed.IsChecked != true && _simple.IsChecked != true)
            error = "Enable at least one preview or card style.";
        else if (_cardExit.Value <= _cardEnter.Value || _simpleExit.Value <= _simpleEnter.Value)
            error = "Each zoom-in threshold must be higher than its zoom-out threshold.";
        else if (_simpleEnter.Value >= _cardEnter.Value || _simpleExit.Value >= _cardExit.Value)
            error = "Simple card thresholds must be lower than detailed card thresholds.";
        if (error != null) return false;
        updated = current with { LivePreviewsTier = _live.IsChecked == true, DetailedCardsTier = _detailed.IsChecked == true,
            SimpleCardsTier = _simple.IsChecked == true, CardEnterZoom = _cardEnter.Value / 100,
            CardExitZoom = _cardExit.Value / 100, SimpleCardEnterZoom = _simpleEnter.Value / 100,
            SimpleCardExitZoom = _simpleExit.Value / 100 };
        return true;
    }
}
