using System;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace CanvasDesktop;

/// <summary>
/// Settings control for camera flight path animation kinematics, duration, altitude and profile.
/// </summary>
internal sealed class FlightSettingsControl : UserControl
{
    public const double StandardDuration = 3.0;
    public const double MinimumDuration = 0.5;
    public const double MaximumDuration = 10.0;

    public const double StandardCruisingScale = 1.0;
    public const double MinimumCruisingScale = 0.4;
    public const double MaximumCruisingScale = 2.5;

    private readonly Slider _durationSlider;
    private readonly Slider _altitudeSlider;
    private readonly Slider _fullDurationDistanceSlider;
    private readonly Slider _fullClimbDistanceSlider;
    private readonly Slider _previewDistanceSlider;
    private readonly TextBlock _durationReadout;
    private readonly TextBlock _altitudeReadout;
    private readonly TextBlock _durationRate;
    private readonly TextBlock _climbRate;
    private readonly TextBlock _previewReadout;
    private readonly FlightCurvePreviewControl _previewGraph;
    private readonly Border[] _profileCards;
    private readonly string[] _profileKeys = ["Helicopter", "Brisk", "Cinematic", "Planar"];
    private string _currentProfile;

    public double SelectedDuration => _durationSlider.Value;
    public double SelectedCruisingScale => _altitudeSlider.Value;
    public string SelectedProfile => _currentProfile;

    public FlightSettingsControl(CanvasPreferences preferences)
    {
        _currentProfile = string.IsNullOrWhiteSpace(preferences.FlightProfile) ? "Helicopter" : preferences.FlightProfile;

        var container = new Border
        {
            Background = ZoneTheme.Brush("#131B27"),
            BorderBrush = ZoneTheme.Brush("#2D3B4E"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 4, 0, 14)
        };
        Content = container;

        var root = new StackPanel();
        container.Child = root;

        // Header
        var title = new TextBlock
        {
            Text = "Flight Path Kinematics",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = ZoneTheme.Brush("#E2E8F0"),
            Margin = new Thickness(0, 0, 0, 2)
        };
        root.Children.Add(title);

        var hint = new TextBlock
        {
            Text = "Fine-tune camera trajectory pacing and altitude profile when switching between apps.",
            FontSize = 12,
            Foreground = ZoneTheme.Brush("#A7B3C9"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };
        root.Children.Add(hint);

        // Profile Cards Selector (2x2 Grid)
        var profileGrid = new UniformGrid { Columns = 2, Margin = new Thickness(0, 0, 0, 10) };
        root.Children.Add(profileGrid);

        _profileCards = new Border[4];
        _profileCards[0] = CreateProfileCard("Helicopter", "🚁 Helicopter (Standard)", "Vertical liftoff first, gradual 2D acceleration, soft flared landing", 0);
        _profileCards[1] = CreateProfileCard("Brisk", "⚡ Brisk", "Fast lateral takeoff and swift ascent for rapid switching", 1);
        _profileCards[2] = CreateProfileCard("Cinematic", "🎬 Cinematic", "Grand overview climb, extended gliding cruise, sweeping curves", 2);
        _profileCards[3] = CreateProfileCard("Planar", "📐 Planar", "Direct 2D glide across canvas with no extra climb", 3);

        foreach (var card in _profileCards) profileGrid.Children.Add(card);

        // Duration Slider
        var durationHeader = new DockPanel { Margin = new Thickness(0, 4, 0, 2) };
        _durationReadout = new TextBlock
        {
            Width = 140,
            TextAlignment = TextAlignment.Right,
            FontWeight = FontWeights.SemiBold,
            Foreground = ZoneTheme.Brush("#93C5FD")
        };
        DockPanel.SetDock(_durationReadout, Dock.Right);
        durationHeader.Children.Add(_durationReadout);
        durationHeader.Children.Add(new TextBlock
        {
            Text = "Maximum Flight Duration",
            FontWeight = FontWeights.Medium,
            Foreground = ZoneTheme.Brush("#F1F5F9")
        });
        root.Children.Add(durationHeader);

        _durationSlider = new Slider
        {
            Minimum = MinimumDuration,
            Maximum = MaximumDuration,
            Value = Math.Clamp(preferences.FlightDurationSeconds, MinimumDuration, MaximumDuration),
            TickFrequency = 0.25,
            IsSnapToTickEnabled = true,
            SmallChange = 0.25,
            LargeChange = 1.0,
            AutoToolTipPlacement = AutoToolTipPlacement.TopLeft,
            AutoToolTipPrecision = 2,
            Margin = new Thickness(0, 2, 0, 2)
        };
        AutomationProperties.SetName(_durationSlider, "Maximum flight duration in seconds");
        UpdateDurationReadout();
        _durationSlider.ValueChanged += (_, _) =>
        {
            UpdateDurationReadout();
            UpdatePreview();
        };
        root.Children.Add(_durationSlider);

        _fullDurationDistanceSlider = AddDistanceSlider(root, "Distance for maximum duration", preferences.FlightFullDurationDistance);
        _durationRate = AddHint(root);

        // Cruising Altitude Scale Slider
        var altitudeHeader = new DockPanel { Margin = new Thickness(0, 4, 0, 2) };
        _altitudeReadout = new TextBlock
        {
            Width = 140,
            TextAlignment = TextAlignment.Right,
            FontWeight = FontWeights.SemiBold,
            Foreground = ZoneTheme.Brush("#93C5FD")
        };
        DockPanel.SetDock(_altitudeReadout, Dock.Right);
        altitudeHeader.Children.Add(_altitudeReadout);
        altitudeHeader.Children.Add(new TextBlock
        {
            Text = "Maximum Climb Depth",
            FontWeight = FontWeights.Medium,
            Foreground = ZoneTheme.Brush("#F1F5F9")
        });
        root.Children.Add(altitudeHeader);

        _altitudeSlider = new Slider
        {
            Minimum = MinimumCruisingScale,
            Maximum = MaximumCruisingScale,
            Value = Math.Clamp(preferences.FlightCruisingScale, MinimumCruisingScale, MaximumCruisingScale),
            TickFrequency = 0.1,
            IsSnapToTickEnabled = true,
            SmallChange = 0.1,
            LargeChange = 0.5,
            AutoToolTipPlacement = AutoToolTipPlacement.TopLeft,
            AutoToolTipPrecision = 1,
            Margin = new Thickness(0, 2, 0, 2)
        };
        AutomationProperties.SetName(_altitudeSlider, "Cruising altitude zoom depth multiplier");
        UpdateAltitudeReadout();
        _altitudeSlider.ValueChanged += (_, _) =>
        {
            UpdateAltitudeReadout();
            UpdatePreview();
        };
        root.Children.Add(_altitudeSlider);

        _fullClimbDistanceSlider = AddDistanceSlider(root, "Distance for full climb", preferences.FlightFullClimbDistance);
        _climbRate = AddHint(root);

        _previewDistanceSlider = AddDistanceSlider(root, "Preview flight distance", 500, minimum: 100);
        var distances = new UniformGrid { Columns = 3 };
        AddPreset("Nearby", 500);
        AddPreset("Across canvas", 1500);
        AddPreset("Far", 5000);
        root.Children.Add(distances);

        void AddPreset(string label, double distance)
        {
            var button = ZoneTheme.Button(label, () => _previewDistanceSlider.Value = distance);
            button.Padding = new Thickness(4);
            button.Margin = new Thickness(2);
            AutomationProperties.SetName(button, $"{label} preview, {distance:N0} canvas pixels");
            distances.Children.Add(button);
        }

        _previewReadout = AddHint(root);
        AutomationProperties.SetAutomationId(_previewReadout, "FlightPreviewResult");
        _previewReadout.Margin = new Thickness(0, 4, 0, 0);

        // Interactive Live Curve Graph
        _previewGraph = new FlightCurvePreviewControl
        {
            Height = 110,
            Margin = new Thickness(0, 4, 0, 8)
        };
        root.Children.Add(_previewGraph);

        // Action Buttons Row: Test Preview & Reset Defaults
        var actionsRow = new UniformGrid { Columns = 2, Margin = new Thickness(0, 4, 0, 0) };
        root.Children.Add(actionsRow);

        var previewBtn = ZoneTheme.Button("▶ Test Flight Preview", _previewGraph.PlayFlightSimulation);
        previewBtn.Margin = new Thickness(0, 0, 4, 0);
        AutomationProperties.SetName(previewBtn, "Test Flight Preview");
        actionsRow.Children.Add(previewBtn);

        var resetBtn = ZoneTheme.Button("Reset flight defaults", ResetDefaults);
        resetBtn.Margin = new Thickness(4, 0, 0, 0);
        AutomationProperties.SetName(resetBtn, "Reset flight defaults");
        actionsRow.Children.Add(resetBtn);

        _fullDurationDistanceSlider.ValueChanged += (_, _) => UpdatePreview();
        _fullClimbDistanceSlider.ValueChanged += (_, _) => UpdatePreview();
        _previewDistanceSlider.ValueChanged += (_, _) => UpdatePreview();
        HighlightSelectedProfileCard();
        UpdatePreview();
    }

    private static Slider AddDistanceSlider(Panel parent, string label, double value, double minimum = WindowNavigationFlight.MinimumRampDistance)
    {
        var heading = new DockPanel { Margin = new Thickness(0, 4, 0, 2) };
        var readout = new TextBlock { Foreground = ZoneTheme.Brush("#93C5FD"), Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(readout, Dock.Right);
        heading.Children.Add(readout);
        heading.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        parent.Children.Add(heading);

        var slider = new Slider
        {
            Minimum = minimum,
            Maximum = WindowNavigationFlight.MaximumRampDistance,
            Value = Math.Clamp(value, minimum, WindowNavigationFlight.MaximumRampDistance),
            TickFrequency = 100,
            IsSnapToTickEnabled = true,
            SmallChange = 100,
            LargeChange = 500
        };
        AutomationProperties.SetName(slider, label + " in canvas pixels");
        readout.Text = $"{slider.Value:N0} px";
        slider.ValueChanged += (_, _) => readout.Text = $"{slider.Value:N0} px";
        parent.Children.Add(slider);
        return slider;
    }

    private static TextBlock AddHint(Panel parent)
    {
        var hint = new TextBlock
        {
            FontSize = 11,
            Foreground = ZoneTheme.Brush("#A7B3C9"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };
        parent.Children.Add(hint);
        return hint;
    }

    private Border CreateProfileCard(string key, string title, string subtitle, int index)
    {
        var card = new Border
        {
            Background = ZoneTheme.Brush("#172233"),
            BorderBrush = ZoneTheme.Brush("#2D3D54"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(3),
            Cursor = Cursors.Hand
        };
        AutomationProperties.SetName(card, title);

        var stack = new StackPanel();
        card.Child = stack;

        var headerBlock = new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            Foreground = ZoneTheme.Brush("#F1F5F9"),
            Margin = new Thickness(0, 0, 0, 2)
        };
        stack.Children.Add(headerBlock);

        var descBlock = new TextBlock
        {
            Text = subtitle,
            FontSize = 10,
            Foreground = ZoneTheme.Brush("#94A3B8"),
            TextWrapping = TextWrapping.Wrap
        };
        stack.Children.Add(descBlock);

        card.PreviewMouseDown += (_, _) =>
        {
            _currentProfile = key;
            HighlightSelectedProfileCard();
            UpdatePreview();
        };

        return card;
    }

    private void HighlightSelectedProfileCard()
    {
        for (int i = 0; i < _profileKeys.Length; i++)
        {
            bool isSelected = string.Equals(_currentProfile, _profileKeys[i], StringComparison.OrdinalIgnoreCase);
            _profileCards[i].Background = ZoneTheme.Brush(isSelected ? "#1E2D42" : "#172233");
            _profileCards[i].BorderBrush = ZoneTheme.Brush(isSelected ? "#3B82F6" : "#2D3D54");
            _profileCards[i].BorderThickness = new Thickness(isSelected ? 2 : 1);
        }

        bool isPlanar = string.Equals(_currentProfile, "Planar", StringComparison.OrdinalIgnoreCase);
        _altitudeSlider.IsEnabled = !isPlanar;
        _fullClimbDistanceSlider.IsEnabled = !isPlanar;
    }

    private void UpdateDurationReadout()
    {
        double val = _durationSlider.Value;
        bool isStd = Math.Abs(val - StandardDuration) < 0.05;
        _durationReadout.Text = isStd ? $"{val:0.00} s (Standard)" : $"{val:0.00} s";
    }

    private void UpdateAltitudeReadout()
    {
        double val = _altitudeSlider.Value;
        bool isStd = Math.Abs(val - StandardCruisingScale) < 0.05;
        _altitudeReadout.Text = isStd ? $"{val:0.0}× (Standard)" : $"{val:0.0}×";
    }

    private void UpdatePreview()
    {
        double maxDuration = _durationSlider.Value;
        double durationDistance = _fullDurationDistanceSlider.Value;
        double climbDistance = _fullClimbDistanceSlider.Value;
        double minimumDuration = WindowNavigationFlight.MinimumFlightDuration;
        double noClimbDistance = WindowNavigationFlight.NoClimbDistance;
        double secondsPerChunk = (maxDuration - minimumDuration) * 1000 / durationDistance;
        double climbPerChunk = 100 / (climbDistance - noClimbDistance);
        _durationRate.Text = $"Starts at {minimumDuration:0.00} s and adds {secondsPerChunk:0.00} s per 1,000 canvas px up to the maximum.";
        _climbRate.Text = !string.Equals(_currentProfile, "Planar", StringComparison.OrdinalIgnoreCase)
            ? $"After {noClimbDistance:N0} canvas px, adds {climbPerChunk:0.#%} of full climb depth per 100 px, capped at full climb."
            : "Planar flies without extra climb.";

        var flight = new WindowNavigationFlight(new(0, 0, 1), new(_previewDistanceSlider.Value, 0, 1),
            duration: maxDuration, cruisingScale: _altitudeSlider.Value, profile: _currentProfile,
            fullDurationDistance: durationDistance, fullClimbDistance: climbDistance);
        _previewReadout.Text = $"{flight.Distance:N0} canvas px  ·  {flight.FlightDuration:0.00} s  ·  Lowest zoom {flight.CruisingZoom:0.#%}";
        _previewGraph.UpdateFlight(flight, maxDuration);
    }

    public void ResetDefaults()
    {
        _currentProfile = "Helicopter";
        _durationSlider.Value = StandardDuration;
        _altitudeSlider.Value = StandardCruisingScale;
        _fullDurationDistanceSlider.Value = WindowNavigationFlight.DefaultFullDurationDistance;
        _fullClimbDistanceSlider.Value = WindowNavigationFlight.DefaultFullClimbDistance;
        HighlightSelectedProfileCard();
        UpdateDurationReadout();
        UpdateAltitudeReadout();
        UpdatePreview();
    }

    public bool TryApply(CanvasPreferences current, out CanvasPreferences updated, out string? error)
    {
        updated = current;
        error = null;

        double duration = Math.Clamp(_durationSlider.Value, MinimumDuration, MaximumDuration);
        double cruising = Math.Clamp(_altitudeSlider.Value, MinimumCruisingScale, MaximumCruisingScale);
        string profile = string.IsNullOrWhiteSpace(_currentProfile) ? "Helicopter" : _currentProfile;

        updated = current with
        {
            FlightDurationSeconds = duration,
            FlightCruisingScale = cruising,
            FlightProfile = profile,
            FlightFullDurationDistance = _fullDurationDistanceSlider.Value,
            FlightFullClimbDistance = _fullClimbDistanceSlider.Value
        };
        return true;
    }
}

/// <summary>
/// Live visual preview control displaying altitude and horizontal progress curves for camera flight.
/// </summary>
internal sealed class FlightCurvePreviewControl : FrameworkElement
{
    private readonly Stopwatch _elapsed = new();
    private DispatcherTimer? _timer;
    private WindowNavigationFlight? _flight;
    private double _axisDuration;

    public FlightCurvePreviewControl() => Unloaded += (_, _) => StopSimulation();

    public void UpdateFlight(WindowNavigationFlight flight, double maximumDuration)
    {
        StopSimulation();
        _flight = flight;
        _axisDuration = maximumDuration;
        InvalidateVisual();
    }

    public void PlayFlightSimulation()
    {
        if (_flight == null) return;
        if (_timer == null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            _timer.Tick += (_, _) =>
            {
                if (_flight is not { } flight || _elapsed.Elapsed.TotalSeconds >= flight.FlightDuration) StopSimulation();
                InvalidateVisual();
            };
        }
        _elapsed.Restart();
        _timer.Start();
        InvalidateVisual();
    }

    private void StopSimulation()
    {
        _timer?.Stop();
        _elapsed.Reset();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (_flight is not { } flight || ActualWidth <= 64 || ActualHeight <= 46) return;

        var background = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRoundedRectangle(ZoneTheme.Brush("#0D1522"), new Pen(ZoneTheme.Brush("#2D3B4E"), 1), background, 8, 8);

        const double left = 16;
        const double top = 22;
        double right = ActualWidth - left;
        double baseline = ActualHeight - 20;
        double plotHeight = baseline - top;
        double plotWidth = right - left;
        var axisPen = new Pen(ZoneTheme.Brush("#334155"), 1);
        dc.DrawLine(axisPen, new Point(left, baseline), new Point(right, baseline));
        double landingX = TimeToX(flight.FlightDuration);
        dc.DrawLine(axisPen, new Point(landingX, top), new Point(landingX, baseline));

        var altitudeBrush = ZoneTheme.Brush("#38BDF8");
        var panBrush = ZoneTheme.Brush("#34D399");
        DrawCurve(altitudeBrush, seconds => 1 - flight.Sample(seconds).Zoom);
        DrawCurve(panBrush, seconds => (flight.SampleFocalPoint(seconds).X - flight.FromFocalPoint.X) / flight.Distance);

        void DrawCurve(Brush brush, Func<double, double> sample)
        {
            const int steps = 80;
            var pen = new Pen(brush, 2);
            var previous = new Point(left, baseline - sample(0) * plotHeight);
            for (int i = 1; i <= steps; i++)
            {
                double seconds = flight.FlightDuration * i / steps;
                var next = new Point(TimeToX(seconds), baseline - sample(seconds) * plotHeight);
                dc.DrawLine(pen, previous, next);
                previous = next;
            }

            if (_elapsed.IsRunning)
            {
                double seconds = Math.Min(_elapsed.Elapsed.TotalSeconds, flight.FlightDuration);
                dc.DrawEllipse(brush, new Pen(Brushes.White, 1), new Point(TimeToX(seconds), baseline - sample(seconds) * plotHeight), 4, 4);
            }
        }

        double TimeToX(double seconds) => left + plotWidth * seconds / _axisDuration;

        var axisBrush = ZoneTheme.Brush("#94A3B8");
        DrawLabel("Zoom out", left, 5, altitudeBrush);
        DrawLabel("Panning progress", right, 5, panBrush, alignRight: true);
        DrawLabel("0 s", left, baseline + 4, axisBrush);
        DrawLabel($"{_axisDuration:0.##} s", right, baseline + 4, axisBrush, alignRight: true);

        void DrawLabel(string label, double x, double y, Brush brush, bool alignRight = false)
        {
            var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 10, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point(alignRight ? x - text.Width : x, y));
        }
    }
}
