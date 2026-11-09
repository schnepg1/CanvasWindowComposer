using System;
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
    private readonly TextBlock _durationReadout;
    private readonly TextBlock _altitudeReadout;
    private readonly TextBlock _altitudeHint;
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
        _profileCards[3] = CreateProfileCard("Planar", "📐 Planar", "Direct 2D glide across canvas with no zoom changes", 3);

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
            Text = "Flight Duration",
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
        AutomationProperties.SetName(_durationSlider, "Flight duration in seconds");
        UpdateDurationReadout();
        _durationSlider.ValueChanged += (_, _) =>
        {
            UpdateDurationReadout();
            UpdatePreview();
        };
        root.Children.Add(_durationSlider);

        var durationHint = new TextBlock
        {
            Text = "Standard duration is 3.0 seconds. Can be adjusted from 0.5s up to 10.0 seconds.",
            FontSize = 11,
            Foreground = ZoneTheme.Brush("#94A3B8"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };
        root.Children.Add(durationHint);

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
            Text = "Cruising Altitude Depth",
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

        _altitudeHint = new TextBlock
        {
            Text = "Relative zoom-out depth during flight. Higher values give a broader view of surrounding windows.",
            FontSize = 11,
            Foreground = ZoneTheme.Brush("#94A3B8"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };
        root.Children.Add(_altitudeHint);

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

        var previewBtn = ZoneTheme.Button("▶ Test Flight Preview", () =>
        {
            _previewGraph.PlayFlightSimulation(_durationSlider.Value);
        });
        previewBtn.Margin = new Thickness(0, 0, 4, 0);
        AutomationProperties.SetName(previewBtn, "Test Flight Preview");
        actionsRow.Children.Add(previewBtn);

        var resetBtn = ZoneTheme.Button("Reset flight defaults", ResetDefaults);
        resetBtn.Margin = new Thickness(4, 0, 0, 0);
        AutomationProperties.SetName(resetBtn, "Reset flight defaults");
        actionsRow.Children.Add(resetBtn);

        HighlightSelectedProfileCard();
        UpdatePreview();
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
        _altitudeHint.Text = isPlanar
            ? "Altitude climb is disabled in Planar profile (pure 2D glide)."
            : "Relative zoom-out depth during flight. Higher values give a broader view of surrounding windows.";
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
        _previewGraph?.UpdateKinematics(_currentProfile, _durationSlider.Value, _altitudeSlider.Value);
    }

    public void ResetDefaults()
    {
        _currentProfile = "Helicopter";
        _durationSlider.Value = StandardDuration;
        _altitudeSlider.Value = StandardCruisingScale;
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
            FlightProfile = profile
        };
        return true;
    }
}

/// <summary>
/// Live visual preview control displaying altitude and horizontal progress curves for camera flight.
/// </summary>
internal sealed class FlightCurvePreviewControl : FrameworkElement
{
    private double _durationSeconds = 3.0;
    private double _cruisingScale = 1.0;
    private string _profile = "Helicopter";

    private DispatcherTimer? _animTimer;
    private DateTime _animStartTime;
    private double _animProgress = -1.0; // -1: idle

    public void UpdateKinematics(string profile, double durationSeconds, double cruisingScale)
    {
        _profile = profile;
        _durationSeconds = durationSeconds;
        _cruisingScale = cruisingScale;
        InvalidateVisual();
    }

    public void PlayFlightSimulation(double durationSeconds)
    {
        _durationSeconds = durationSeconds;
        _animStartTime = DateTime.UtcNow;
        _animProgress = 0.0;

        _animTimer ??= new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _animTimer.Tick -= OnAnimTick;
        _animTimer.Tick += OnAnimTick;
        _animTimer.Start();
        InvalidateVisual();
    }

    private void OnAnimTick(object? sender, EventArgs e)
    {
        double elapsed = (DateTime.UtcNow - _animStartTime).TotalSeconds;
        if (elapsed >= _durationSeconds)
        {
            _animProgress = 1.0;
            _animTimer?.Stop();
        }
        else
        {
            _animProgress = elapsed / _durationSeconds;
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 10 || h <= 10) return;

        // Background
        var bgRect = new Rect(0, 0, w, h);
        dc.DrawRoundedRectangle(ZoneTheme.Brush("#0D1522"), new Pen(ZoneTheme.Brush("#2D3B4E"), 1), bgRect, 8, 8);

        double padX = 24;
        double padTop = 18;
        double padBottom = 22;
        double plotW = Math.Max(10, w - padX * 2);
        double plotH = Math.Max(10, h - padTop - padBottom);

        // Baseline and grid lines
        var gridPen = new Pen(ZoneTheme.Brush("#1E2D42"), 1) { DashStyle = DashStyles.Dash };
        var axisPen = new Pen(ZoneTheme.Brush("#334155"), 1);

        // Horizontal baseline (Y = 0)
        double baselineY = padTop + plotH;
        dc.DrawLine(axisPen, new Point(padX, baselineY), new Point(padX + plotW, baselineY));

        // Mid vertical grid line (u = 0.5)
        dc.DrawLine(gridPen, new Point(padX + plotW * 0.5, padTop), new Point(padX + plotW * 0.5, baselineY));

        // Evaluate curves across 80 segments
        const int steps = 80;
        var altPoints = new Point[steps + 1];
        var panPoints = new Point[steps + 1];

        bool isPlanar = string.Equals(_profile, "Planar", StringComparison.OrdinalIgnoreCase);
        double altScale = isPlanar ? 0.0 : Math.Clamp(_cruisingScale / 2.0, 0.25, 1.0);

        for (int i = 0; i <= steps; i++)
        {
            double u = (double)i / steps;
            double x = padX + u * plotW;

            // Altitude curve Z(u)
            double altNorm = 0.0;
            if (!isPlanar)
            {
                if (u <= 0.42)
                {
                    double t = u / 0.42;
                    altNorm = t * t * t * (t * (t * 6.0 - 15.0) + 10.0);
                }
                else if (u <= 0.50)
                {
                    altNorm = 1.0;
                }
                else
                {
                    double t = (u - 0.50) / 0.50;
                    altNorm = 1.0 - t * t * t * (t * (t * 6.0 - 15.0) + 10.0);
                }
            }
            double altY = baselineY - (altNorm * altScale * plotH * 0.88);
            altPoints[i] = new Point(x, altY);

            // Lateral progress curve P(u)
            double panNorm;
            if (string.Equals(_profile, "Brisk", StringComparison.OrdinalIgnoreCase))
            {
                panNorm = u * u * u * u * (35.0 - 84.0 * u + 70.0 * u * u - 20.0 * u * u * u);
            }
            else if (string.Equals(_profile, "Cinematic", StringComparison.OrdinalIgnoreCase))
            {
                double u2 = u * u; double u3 = u2 * u; double u4 = u2 * u2; double u5 = u4 * u; double u6 = u3 * u3;
                panNorm = u6 * (462.0 - 1980.0 * u + 3465.0 * u2 - 3080.0 * u3 + 1386.0 * u4 - 252.0 * u5);
            }
            else
            {
                panNorm = u * u * u * u * u * (126.0 - 420.0 * u + 540.0 * u * u - 315.0 * u * u * u + 70.0 * u * u * u * u);
            }
            double panY = baselineY - (panNorm * plotH * 0.88);
            panPoints[i] = new Point(x, panY);
        }

        // Draw curves
        var altPen = new Pen(ZoneTheme.Brush("#38BDF8"), 2); // Sky blue
        var panPen = new Pen(ZoneTheme.Brush("#34D399"), 2); // Emerald green

        for (int i = 0; i < steps; i++)
        {
            dc.DrawLine(altPen, altPoints[i], altPoints[i + 1]);
            dc.DrawLine(panPen, panPoints[i], panPoints[i + 1]);
        }

        // Draw animated beads if simulation is running
        if (_animProgress >= 0.0 && _animProgress <= 1.0)
        {
            double curX = padX + _animProgress * plotW;
            var guidePen = new Pen(ZoneTheme.Brush("#F43F5E"), 1.5) { DashStyle = DashStyles.Dash };
            dc.DrawLine(guidePen, new Point(curX, padTop), new Point(curX, baselineY));

            int idx = (int)Math.Clamp(Math.Round(_animProgress * steps), 0, steps);
            // Bead on altitude
            dc.DrawEllipse(ZoneTheme.Brush("#38BDF8"), new Pen(Brushes.White, 1.5), altPoints[idx], 5, 5);
            // Bead on lateral progress
            dc.DrawEllipse(ZoneTheme.Brush("#34D399"), new Pen(Brushes.White, 1.5), panPoints[idx], 5, 5);
        }

        // Text labels: Legend & markers
        var typeFace = new Typeface("Segoe UI");
        void DrawLabel(string text, double lx, double ly, Brush brush, TextAlignment align = TextAlignment.Left)
        {
            var ft = new FormattedText(
                text,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeFace,
                10,
                brush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            if (align == TextAlignment.Right) lx -= ft.Width;
            else if (align == TextAlignment.Center) lx -= ft.Width / 2.0;
            dc.DrawText(ft, new Point(lx, ly));
        }

        DrawLabel("Altitude (Zoom out)", padX + 4, 3, ZoneTheme.Brush("#38BDF8"));
        DrawLabel("Panning Progress", padX + plotW * 0.5, 3, ZoneTheme.Brush("#34D399"), TextAlignment.Center);
        DrawLabel($"{_durationSeconds:0.0}s", padX + plotW, 3, ZoneTheme.Brush("#94A3B8"), TextAlignment.Right);

        DrawLabel("0.0s Liftoff", padX, baselineY + 4, ZoneTheme.Brush("#64748B"));
        DrawLabel("Cruising", padX + plotW * 0.5, baselineY + 4, ZoneTheme.Brush("#64748B"), TextAlignment.Center);
        DrawLabel($"{_durationSeconds:0.0}s Landing", padX + plotW, baselineY + 4, ZoneTheme.Brush("#64748B"), TextAlignment.Right);
    }
}
