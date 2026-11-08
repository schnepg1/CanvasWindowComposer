using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace CanvasDesktop;

/// <summary>A detached zoom-policy editor shared by Settings and the canvas popup.</summary>
internal sealed class ZoomSettingsControl : UserControl
{
    private readonly CheckBox _live, _detailed, _simple, _shortcutZoomOut, _peek, _smoothZoom, _zoomAcceleration;
    private readonly Slider _cardEnter, _cardExit, _simpleEnter, _simpleExit, _shortcutAmount, _peekDelay, _maximumZoom;
    private readonly ZoomSpectrumControl _spectrum;
    private readonly ZoomPreviewPlaygroundControl _playground;
    private bool _syncing;

    public ZoomSettingsControl(CanvasPreferences preferences)
    {
        var body = new StackPanel();
        Content = body;
        body.Children.Add(new TextBlock { Text = "Zoom & cards", FontSize = 22,
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 12) });

        _shortcutZoomOut = Option("Zoom out on Alt+Q", preferences.ShortcutZoomOut);
        _shortcutAmount = Threshold("Automatic zoom-out amount", preferences.ShortcutZoomOutAmount, 0, 80);
        _shortcutAmount.IsEnabled = _shortcutZoomOut.IsChecked == true;
        _shortcutZoomOut.Checked += (_, _) => _shortcutAmount.IsEnabled = true;
        _shortcutZoomOut.Unchecked += (_, _) => _shortcutAmount.IsEnabled = false;
        Hint("Reduces the current zoom by this percentage, centered on the main screen. For example, 15% changes 100% zoom to 85%. Turn off to keep the zoom when opening. Applies after saving.");

        _smoothZoom = Option("Smooth canvas zooming", preferences.SmoothZoom);
        Hint("Smoothly glides zoom levels when scrolling the mouse wheel or using + / -. Turn off for immediate discrete steps.");

        _zoomAcceleration = Option("Zoom acceleration", preferences.ZoomAcceleration);
        _zoomAcceleration.IsEnabled = _smoothZoom.IsChecked == true;
        _smoothZoom.Checked += (_, _) => _zoomAcceleration.IsEnabled = true;
        _smoothZoom.Unchecked += (_, _) => _zoomAcceleration.IsEnabled = false;
        Hint("Accelerates zoom velocity during rapid wheel flicking. Turn off for uniform, constant-speed zoom steps.");

        body.Children.Add(new TextBlock
        {
            Text = "Visual representation stages",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = ZoneTheme.Brush("#E2E8F0"),
            Margin = new Thickness(0, 8, 0, 6)
        });
        Hint("Choose which visual styles are active as you zoom. Disabling a stage removes its transitions. Drag the boundary thumbs on the spectrum track to adjust switchover points.");

        // Stage activation cards grid
        var stagesGrid = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 8) };
        body.Children.Add(stagesGrid);

        _simple = Option("Simple cards", preferences.SimpleCardsTier, addToBody: false);
        _detailed = Option("Detailed placeholder cards", preferences.DetailedCardsTier, addToBody: false);
        _live = Option("Live previews", preferences.LivePreviewsTier, addToBody: false);

        stagesGrid.Children.Add(CreateStageCard("Simple cards", "Icons & titles", _simple));
        stagesGrid.Children.Add(CreateStageCard("Detailed cards", "Titles & snapshots", _detailed));
        stagesGrid.Children.Add(CreateStageCard("Live previews", "Interactive windows", _live));

        // Spectrum Track Control
        _spectrum = new ZoomSpectrumControl();
        _spectrum.SetTiers(preferences.LivePreviewsTier, preferences.DetailedCardsTier, preferences.SimpleCardsTier);
        _spectrum.SetValues(preferences.CardEnterZoom, preferences.CardExitZoom, preferences.SimpleCardEnterZoom, preferences.SimpleCardExitZoom);
        body.Children.Add(_spectrum);

        // Threshold Sliders (kept in an expandable advanced container for precision fine-tuning)
        var fineTuneExpander = new Expander
        {
            Header = new TextBlock
            {
                Text = "Fine-tune transition thresholds",
                FontWeight = FontWeights.Medium,
                Foreground = ZoneTheme.Brush("#93C5FD"),
                FontSize = 12
            },
            Margin = new Thickness(0, 0, 0, 12),
            IsExpanded = false
        };
        var fineTuneBody = new StackPanel { Margin = new Thickness(4, 8, 4, 0) };
        fineTuneExpander.Content = fineTuneBody;
        body.Children.Add(fineTuneExpander);

        _cardEnter = Threshold("Detailed cards · zoom out below", preferences.CardEnterZoom, 5, 200, fineTuneBody);
        _cardExit = Threshold("Live previews · zoom in above", preferences.CardExitZoom, 5, 200, fineTuneBody);
        _simpleEnter = Threshold("Simple cards · zoom out below", preferences.SimpleCardEnterZoom, 1, 100, fineTuneBody);
        _simpleExit = Threshold("Detailed cards · zoom in above", preferences.SimpleCardExitZoom, 1, 100, fineTuneBody);

        fineTuneBody.Children.Add(new TextBlock
        {
            Text = "Separate zoom-out and zoom-in thresholds prevent flickering between card styles. Zoom-in must be higher than zoom-out.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = ZoneTheme.Brush("#A7B3C9"),
            Margin = new Thickness(0, 0, 0, 8)
        });

        // Preview Playground
        _playground = new ZoomPreviewPlaygroundControl();
        _playground.UpdateConfiguration(
            preferences.LivePreviewsTier,
            preferences.DetailedCardsTier,
            preferences.SimpleCardsTier,
            preferences.CardEnterZoom,
            preferences.SimpleCardEnterZoom);
        body.Children.Add(_playground);

        // Link stage toggles to spectrum track and playground
        void OnTierToggled()
        {
            if (_live.IsChecked != true && _detailed.IsChecked != true && _simple.IsChecked != true)
            {
                // Enforce at least one active stage
                _detailed.IsChecked = true;
            }
            bool l = _live.IsChecked == true, d = _detailed.IsChecked == true, s = _simple.IsChecked == true;
            _spectrum.SetTiers(l, d, s);
            _playground.UpdateConfiguration(l, d, s, _spectrum.CardEnterZoom, _spectrum.SimpleCardEnterZoom);
        }
        _live.Checked += (_, _) => OnTierToggled();
        _live.Unchecked += (_, _) => OnTierToggled();
        _detailed.Checked += (_, _) => OnTierToggled();
        _detailed.Unchecked += (_, _) => OnTierToggled();
        _simple.Checked += (_, _) => OnTierToggled();
        _simple.Unchecked += (_, _) => OnTierToggled();

        // Spectrum thumb movement updates threshold sliders and playground
        _spectrum.ThresholdsChanged += (cEnter, cExit, sEnter, sExit) =>
        {
            _syncing = true;
            try
            {
                _cardEnter.Value = Math.Round(cEnter * 100);
                _cardExit.Value = Math.Round(cExit * 100);
                _simpleEnter.Value = Math.Round(sEnter * 100);
                _simpleExit.Value = Math.Round(sExit * 100);
                _playground.UpdateConfiguration(
                    _live.IsChecked == true,
                    _detailed.IsChecked == true,
                    _simple.IsChecked == true,
                    cEnter,
                    sEnter);
            }
            finally
            {
                _syncing = false;
            }
        };

        // Fine-tune sliders update spectrum track and playground
        void OnSliderValueChanged()
        {
            if (_syncing) return;
            double cEnter = _cardEnter.Value / 100.0;
            double cExit = _cardExit.Value / 100.0;
            double sEnter = _simpleEnter.Value / 100.0;
            double sExit = _simpleExit.Value / 100.0;
            _spectrum.SetValues(cEnter, cExit, sEnter, sExit);
            _playground.UpdateConfiguration(
                _live.IsChecked == true,
                _detailed.IsChecked == true,
                _simple.IsChecked == true,
                cEnter,
                sEnter);
        }
        _cardEnter.ValueChanged += (_, _) => OnSliderValueChanged();
        _cardExit.ValueChanged += (_, _) => OnSliderValueChanged();
        _simpleEnter.ValueChanged += (_, _) => OnSliderValueChanged();
        _simpleExit.ValueChanged += (_, _) => OnSliderValueChanged();

        // Canvas Peek
        _peek = Option("Hover to preview windows (Canvas Peek)", preferences.EnableCanvasPeek);
        var delayHeader = new DockPanel();
        var delayReadout = new TextBlock { Width = 72, TextAlignment = TextAlignment.Right, FontWeight = FontWeights.SemiBold };
        DockPanel.SetDock(delayReadout, Dock.Right);
        delayHeader.Children.Add(delayReadout);
        delayHeader.Children.Add(new TextBlock { Text = "Preview hover delay", TextWrapping = TextWrapping.Wrap });
        body.Children.Add(delayHeader);
        _peekDelay = new Slider
        {
            Minimum = AppConfig.MinimumCanvasPeekDelayMs, Maximum = AppConfig.MaximumCanvasPeekDelayMs,
            Value = preferences.CanvasPeekDelayMs, TickFrequency = 100, IsSnapToTickEnabled = true,
            SmallChange = 100, LargeChange = 500, Margin = new Thickness(0, 4, 0, 12),
            AutoToolTipPlacement = System.Windows.Controls.Primitives.AutoToolTipPlacement.TopLeft,
            AutoToolTipPrecision = 0, IsEnabled = _peek.IsChecked == true
        };
        AutomationProperties.SetName(_peekDelay, "Preview hover delay (milliseconds)");
        delayReadout.Text = $"{_peekDelay.Value:0} ms";
        _peekDelay.ValueChanged += (_, _) => delayReadout.Text = $"{_peekDelay.Value:0} ms";
        _peek.Checked += (_, _) => _peekDelay.IsEnabled = true;
        _peek.Unchecked += (_, _) => _peekDelay.IsEnabled = false;
        body.Children.Add(_peekDelay);
        Hint("Peek works independently of card styles, including small live windows. Work here keeps the preview open for input; Return leaves your camera unchanged.");

        _maximumZoom = Threshold("Maximum zoom (magnifier)", preferences.MaximumZoom, 100, 500);
        Hint("Allows zooming in beyond 100% (up to 500%) for a magnifying glass view of windows and text. Default is 100%.");

        body.Children.Add(ZoneTheme.Button("Reset zoom defaults", () =>
        {
            _shortcutZoomOut.IsChecked = true;
            _shortcutAmount.Value = OverviewZoomDefaults.ShortcutZoomOutAmount * 100;
            _smoothZoom.IsChecked = true;
            _zoomAcceleration.IsChecked = true;
            _live.IsChecked = _detailed.IsChecked = _simple.IsChecked = true;
            _cardEnter.Value = OverviewZoomDefaults.CardEnter * 100;
            _cardExit.Value = OverviewZoomDefaults.CardExit * 100;
            _simpleEnter.Value = OverviewZoomDefaults.SimpleCardEnter * 100;
            _simpleExit.Value = OverviewZoomDefaults.SimpleCardExit * 100;
            _spectrum.SetTiers(true, true, true);
            _spectrum.SetValues(OverviewZoomDefaults.CardEnter, OverviewZoomDefaults.CardExit, OverviewZoomDefaults.SimpleCardEnter, OverviewZoomDefaults.SimpleCardExit);
            _playground.UpdateConfiguration(true, true, true, OverviewZoomDefaults.CardEnter, OverviewZoomDefaults.SimpleCardEnter);
            _peek.IsChecked = true;
            _peekDelay.Value = AppConfig.DefaultCanvasPeekDelayMs;
            _maximumZoom.Value = OverviewZoomDefaults.MaximumZoom * 100;
        }));

        void Hint(string text) => body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap,
            FontSize = 12, Foreground = ZoneTheme.Brush("#A7B3C9"), Margin = new Thickness(0, 0, 0, 12) });

        CheckBox Option(string label, bool value, bool addToBody = true)
        {
            var box = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 0, 0, addToBody ? 12 : 6) };
            AutomationProperties.SetName(box, label);
            if (addToBody) body.Children.Add(box);
            return box;
        }

        Border CreateStageCard(string title, string subtitle, CheckBox checkBox)
        {
            var card = new Border
            {
                Background = ZoneTheme.Brush("#172233"),
                BorderBrush = ZoneTheme.Brush("#2D3D54"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 8, 8, 6),
                Margin = new Thickness(3)
            };
            var panel = new StackPanel();
            card.Child = panel;
            panel.Children.Add(new TextBlock
            {
                Text = title,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                Foreground = ZoneTheme.Brush("#F1F5F9"),
                Margin = new Thickness(0, 0, 0, 2)
            });
            panel.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 10,
                Foreground = ZoneTheme.Brush("#94A3B8"),
                Margin = new Thickness(0, 0, 0, 6)
            });
            panel.Children.Add(checkBox);
            return card;
        }

        Slider Threshold(string label, double value, double min, double max, Panel? targetPanel = null)
        {
            var panel = targetPanel ?? body;
            var header = new DockPanel();
            var readout = new TextBlock { Width = 48, TextAlignment = TextAlignment.Right, FontWeight = FontWeights.SemiBold };
            DockPanel.SetDock(readout, Dock.Right); header.Children.Add(readout);
            header.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(header);
            var slider = new Slider { Minimum = min, Maximum = max, Value = Math.Round(value * 100),
                TickFrequency = 1, IsSnapToTickEnabled = true, SmallChange = 1, LargeChange = 5,
                AutoToolTipPlacement = System.Windows.Controls.Primitives.AutoToolTipPlacement.TopLeft,
                AutoToolTipPrecision = 0, Margin = new Thickness(0, 4, 0, 12) };
            AutomationProperties.SetName(slider, label + " (percent)");
            readout.Text = $"{slider.Value:0}%";
            slider.ValueChanged += (_, _) => readout.Text = $"{slider.Value:0}%";
            panel.Children.Add(slider);
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
            ShortcutZoomOut = _shortcutZoomOut.IsChecked == true, ShortcutZoomOutAmount = _shortcutAmount.Value / 100,
            SimpleCardsTier = _simple.IsChecked == true, CardEnterZoom = _cardEnter.Value / 100,
            CardExitZoom = _cardExit.Value / 100, SimpleCardEnterZoom = _simpleEnter.Value / 100,
            SimpleCardExitZoom = _simpleExit.Value / 100,
            EnableCanvasPeek = _peek.IsChecked == true, CanvasPeekDelayMs = (int)Math.Round(_peekDelay.Value),
            SmoothZoom = _smoothZoom.IsChecked == true,
            ZoomAcceleration = _zoomAcceleration.IsChecked == true,
            MaximumZoom = _maximumZoom.Value / 100 };
        return true;
    }
}
