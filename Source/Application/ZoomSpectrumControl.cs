using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using WpfCanvas = System.Windows.Controls.Canvas;

namespace CanvasDesktop;

/// <summary>
/// An interactive multi-segment spectrum slider displaying representation stages across canvas zoom levels.
/// Dynamically adjusts its segments and boundary thumbs when stages are enabled or disabled.
/// </summary>
internal sealed class ZoomSpectrumControl : UserControl
{
    private readonly Border _trackBorder;
    private readonly Grid _trackGrid;
    private readonly ColumnDefinition _colSimple, _colDetailed, _colLive;
    private readonly Border _segSimple, _segDetailed, _segLive;
    private readonly TextBlock _labelSimple, _labelDetailed, _labelLive;
    private readonly WpfCanvas _thumbsCanvas;
    private readonly Thumb _thumbSimple, _thumbDetailed;
    private readonly TextBlock _readout;
    private readonly Border _container;

    private bool _updating;
    public bool EnableLive { get; private set; } = true;
    public bool EnableDetailed { get; private set; } = true;
    public bool EnableSimple { get; private set; } = true;

    public double CardEnterZoom { get; private set; } = OverviewZoomDefaults.CardEnter;
    public double CardExitZoom { get; private set; } = OverviewZoomDefaults.CardExit;
    public double SimpleCardEnterZoom { get; private set; } = OverviewZoomDefaults.SimpleCardEnter;
    public double SimpleCardExitZoom { get; private set; } = OverviewZoomDefaults.SimpleCardExit;

    public event Action<double, double, double, double>? ThresholdsChanged;

    public ZoomSpectrumControl()
    {
        _container = new Border
        {
            Background = ZoneTheme.Brush("#151D2A"),
            BorderBrush = ZoneTheme.Brush("#2D3B4E"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 12, 12, 10),
            Margin = new Thickness(0, 4, 0, 10)
        };
        Content = _container;

        var root = new StackPanel();
        _container.Child = root;

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var title = new TextBlock
        {
            Text = "Zoom Spectrum & Card Boundaries",
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Foreground = ZoneTheme.Brush("#E2E8F0")
        };
        header.Children.Add(title);
        root.Children.Add(header);

        // Track layers: track grid inside clip border + overlay canvas for draggable thumbs
        var trackLayer = new Grid { Height = 40, Margin = new Thickness(0, 0, 0, 6) };
        root.Children.Add(trackLayer);

        _trackBorder = new Border
        {
            Height = 36,
            CornerRadius = new CornerRadius(8),
            BorderBrush = ZoneTheme.Brush("#475569"),
            BorderThickness = new Thickness(1),
            ClipToBounds = true
        };
        trackLayer.Children.Add(_trackBorder);

        _trackGrid = new Grid();
        _trackBorder.Child = _trackGrid;

        _colSimple = new ColumnDefinition();
        _colDetailed = new ColumnDefinition();
        _colLive = new ColumnDefinition();
        _trackGrid.ColumnDefinitions.Add(_colSimple);
        _trackGrid.ColumnDefinitions.Add(_colDetailed);
        _trackGrid.ColumnDefinitions.Add(_colLive);

        _segSimple = CreateSegment("#1E293B", "#334155", "Simple Cards", out _labelSimple);
        Grid.SetColumn(_segSimple, 0);
        _trackGrid.Children.Add(_segSimple);

        _segDetailed = CreateSegment("#2A374A", "#475569", "Detailed Cards", out _labelDetailed);
        Grid.SetColumn(_segDetailed, 1);
        _trackGrid.Children.Add(_segDetailed);

        _segLive = CreateSegment("#1D4ED8", "#3B82F6", "Live Previews", out _labelLive);
        Grid.SetColumn(_segLive, 2);
        _trackGrid.Children.Add(_segLive);

        _thumbsCanvas = new WpfCanvas { IsHitTestVisible = true };
        trackLayer.Children.Add(_thumbsCanvas);

        _thumbSimple = CreateThumb("Simple cards boundary");
        _thumbDetailed = CreateThumb("Detailed cards boundary");

        _thumbSimple.DragDelta += OnSimpleThumbDragDelta;
        _thumbDetailed.DragDelta += OnDetailedThumbDragDelta;

        _thumbsCanvas.Children.Add(_thumbSimple);
        _thumbsCanvas.Children.Add(_thumbDetailed);

        _readout = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = ZoneTheme.Brush("#93C5FD"),
            Margin = new Thickness(2, 4, 2, 0)
        };
        root.Children.Add(_readout);

        _trackBorder.SizeChanged += (_, _) => UpdateLayoutPositions();
        Loaded += (_, _) => UpdateLayoutPositions();

        UpdateLayoutPositions();
    }

    private static Border CreateSegment(string bgHex, string borderHex, string text, out TextBlock label)
    {
        var border = new Border
        {
            Background = ZoneTheme.Brush(bgHex),
            BorderBrush = ZoneTheme.Brush(borderHex),
            BorderThickness = new Thickness(0, 0, 1, 0)
        };
        label = new TextBlock
        {
            Text = text,
            Foreground = Brushes.White,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(4, 0, 4, 0)
        };
        border.Child = label;
        return border;
    }

    private static Thumb CreateThumb(string accessibleName)
    {
        var thumb = new Thumb
        {
            Width = 16,
            Height = 38,
            Cursor = Cursors.SizeWE
        };
        AutomationProperties.SetName(thumb, accessibleName);

        var template = new ControlTemplate(typeof(Thumb));
        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.SetValue(Border.BackgroundProperty, ZoneTheme.Brush("#F8FAFC"));
        borderFactory.SetValue(Border.BorderBrushProperty, ZoneTheme.Brush("#2563EB"));
        borderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));

        var gripFactory = new FrameworkElementFactory(typeof(Rectangle));
        gripFactory.SetValue(FrameworkElement.WidthProperty, 2.0);
        gripFactory.SetValue(FrameworkElement.HeightProperty, 14.0);
        gripFactory.SetValue(Shape.FillProperty, ZoneTheme.Brush("#64748B"));
        gripFactory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        gripFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

        borderFactory.AppendChild(gripFactory);
        template.VisualTree = borderFactory;
        thumb.Template = template;
        return thumb;
    }

    public void SetTiers(bool live, bool detailed, bool simple)
    {
        EnableLive = live;
        EnableDetailed = detailed;
        EnableSimple = simple;
        UpdateLayoutPositions();
    }

    public void SetValues(double cardEnter, double cardExit, double simpleEnter, double simpleExit)
    {
        if (_updating) return;
        CardEnterZoom = Math.Clamp(cardEnter, 0.05, 2.0);
        CardExitZoom = Math.Clamp(cardExit, CardEnterZoom + 0.01, 2.0);
        SimpleCardEnterZoom = Math.Clamp(simpleEnter, 0.01, CardEnterZoom - 0.01);
        SimpleCardExitZoom = Math.Clamp(simpleExit, SimpleCardEnterZoom + 0.01, 1.0);
        UpdateLayoutPositions();
    }

    private void OnSimpleThumbDragDelta(object sender, DragDeltaEventArgs e)
    {
        double width = _trackBorder.ActualWidth;
        if (width <= 0) return;

        double currentX = WpfCanvas.GetLeft(_thumbSimple) + (_thumbSimple.Width / 2);
        double newX = Math.Clamp(currentX + e.HorizontalChange, 0, width);
        double ratio = newX / width;

        // Thumb simple represents transition into Simple cards
        if (EnableDetailed)
        {
            // Must stay lower than detailed threshold
            double maxSimple = Math.Max(0.01, CardEnterZoom - 0.02);
            SimpleCardEnterZoom = Math.Clamp(Math.Round(ratio, 2), 0.01, maxSimple);
            // Automatic hysteresis exit buffer
            SimpleCardExitZoom = Math.Min(1.0, Math.Max(SimpleCardEnterZoom + 0.02, Math.Round(SimpleCardEnterZoom * 1.30, 2)));
        }
        else
        {
            // Transition directly between Simple and Live
            SimpleCardEnterZoom = Math.Clamp(Math.Round(ratio, 2), 0.01, 0.95);
            SimpleCardExitZoom = Math.Min(1.0, Math.Max(SimpleCardEnterZoom + 0.02, Math.Round(SimpleCardEnterZoom * 1.30, 2)));
            CardEnterZoom = SimpleCardEnterZoom;
            CardExitZoom = SimpleCardExitZoom;
        }

        UpdateLayoutPositions();
        NotifyChanged();
    }

    private void OnDetailedThumbDragDelta(object sender, DragDeltaEventArgs e)
    {
        double width = _trackBorder.ActualWidth;
        if (width <= 0) return;

        double currentX = WpfCanvas.GetLeft(_thumbDetailed) + (_thumbDetailed.Width / 2);
        double newX = Math.Clamp(currentX + e.HorizontalChange, 0, width);
        double ratio = newX / width;

        if (EnableSimple)
        {
            // Must stay higher than simple threshold
            double minCard = Math.Min(0.98, SimpleCardEnterZoom + 0.02);
            CardEnterZoom = Math.Clamp(Math.Round(ratio, 2), minCard, 1.50);
        }
        else
        {
            CardEnterZoom = Math.Clamp(Math.Round(ratio, 2), 0.02, 1.50);
        }

        // Automatic hysteresis exit buffer
        CardExitZoom = Math.Min(2.0, Math.Max(CardEnterZoom + 0.03, Math.Round(CardEnterZoom * 1.15, 2)));

        UpdateLayoutPositions();
        NotifyChanged();
    }

    private void NotifyChanged()
    {
        _updating = true;
        try
        {
            ThresholdsChanged?.Invoke(CardEnterZoom, CardExitZoom, SimpleCardEnterZoom, SimpleCardExitZoom);
        }
        finally
        {
            _updating = false;
        }
    }

    private void UpdateLayoutPositions()
    {
        double width = _trackBorder.ActualWidth > 0 ? _trackBorder.ActualWidth : 360;

        int activeCount = (EnableLive ? 1 : 0) + (EnableDetailed ? 1 : 0) + (EnableSimple ? 1 : 0);

        if (activeCount <= 1)
        {
            // Single stage active: span entire width, hide both thumbs
            _thumbSimple.Visibility = Visibility.Collapsed;
            _thumbDetailed.Visibility = Visibility.Collapsed;

            _colSimple.Width = EnableSimple ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            _colDetailed.Width = EnableDetailed ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            _colLive.Width = EnableLive ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

            _segSimple.Visibility = EnableSimple ? Visibility.Visible : Visibility.Collapsed;
            _segDetailed.Visibility = EnableDetailed ? Visibility.Visible : Visibility.Collapsed;
            _segLive.Visibility = EnableLive ? Visibility.Visible : Visibility.Collapsed;

            string stageName = EnableLive ? "Live Previews" : (EnableDetailed ? "Detailed Cards" : "Simple Cards");
            _readout.Text = $"{stageName} active at all zoom levels (no card transitions).";
            return;
        }

        if (EnableLive && EnableDetailed && EnableSimple)
        {
            // All 3 stages active
            _segSimple.Visibility = Visibility.Visible;
            _segDetailed.Visibility = Visibility.Visible;
            _segLive.Visibility = Visibility.Visible;

            _thumbSimple.Visibility = Visibility.Visible;
            _thumbDetailed.Visibility = Visibility.Visible;

            double pSimple = Math.Clamp(SimpleCardEnterZoom, 0.01, 0.50);
            double pDetailed = Math.Clamp(CardEnterZoom - pSimple, 0.02, 0.80);
            double pLive = Math.Max(0.05, 1.0 - (pSimple + pDetailed));

            _colSimple.Width = new GridLength(pSimple, GridUnitType.Star);
            _colDetailed.Width = new GridLength(pDetailed, GridUnitType.Star);
            _colLive.Width = new GridLength(pLive, GridUnitType.Star);

            double xSimple = width * (pSimple / (pSimple + pDetailed + pLive));
            double xDetailed = width * ((pSimple + pDetailed) / (pSimple + pDetailed + pLive));

            WpfCanvas.SetLeft(_thumbSimple, Math.Round(xSimple - (_thumbSimple.Width / 2)));
            WpfCanvas.SetTop(_thumbSimple, 1);

            WpfCanvas.SetLeft(_thumbDetailed, Math.Round(xDetailed - (_thumbDetailed.Width / 2)));
            WpfCanvas.SetTop(_thumbDetailed, 1);

            _labelSimple.Text = $"Simple (<{SimpleCardEnterZoom:0%})";
            _labelDetailed.Text = $"Detailed ({SimpleCardExitZoom:0%}–{CardEnterZoom:0%})";
            _labelLive.Text = $"Live (>{CardExitZoom:0%})";

            _readout.Text = $"Detailed cards below {CardEnterZoom:0%} (live returns at {CardExitZoom:0%}) · Simple cards below {SimpleCardEnterZoom:0%} (detailed returns at {SimpleCardExitZoom:0%})";
        }
        else if (EnableLive && EnableSimple && !EnableDetailed)
        {
            // Live and Simple (skipping Detailed)
            _segSimple.Visibility = Visibility.Visible;
            _segDetailed.Visibility = Visibility.Collapsed;
            _segLive.Visibility = Visibility.Visible;

            _thumbSimple.Visibility = Visibility.Visible;
            _thumbDetailed.Visibility = Visibility.Collapsed;

            double pSimple = Math.Clamp(SimpleCardEnterZoom, 0.01, 0.90);
            double pLive = Math.Max(0.05, 1.0 - pSimple);

            _colSimple.Width = new GridLength(pSimple, GridUnitType.Star);
            _colDetailed.Width = new GridLength(0);
            _colLive.Width = new GridLength(pLive, GridUnitType.Star);

            double xSimple = width * (pSimple / (pSimple + pLive));
            WpfCanvas.SetLeft(_thumbSimple, Math.Round(xSimple - (_thumbSimple.Width / 2)));
            WpfCanvas.SetTop(_thumbSimple, 1);

            _labelSimple.Text = $"Simple (<{SimpleCardEnterZoom:0%})";
            _labelLive.Text = $"Live (>{SimpleCardExitZoom:0%})";

            _readout.Text = $"Simple cards below {SimpleCardEnterZoom:0%} (live previews return at {SimpleCardExitZoom:0%}) · Detailed cards skipped";
        }
        else if (EnableLive && EnableDetailed && !EnableSimple)
        {
            // Live and Detailed (no Simple)
            _segSimple.Visibility = Visibility.Collapsed;
            _segDetailed.Visibility = Visibility.Visible;
            _segLive.Visibility = Visibility.Visible;

            _thumbSimple.Visibility = Visibility.Collapsed;
            _thumbDetailed.Visibility = Visibility.Visible;

            double pDetailed = Math.Clamp(CardEnterZoom, 0.05, 0.90);
            double pLive = Math.Max(0.05, 1.0 - pDetailed);

            _colSimple.Width = new GridLength(0);
            _colDetailed.Width = new GridLength(pDetailed, GridUnitType.Star);
            _colLive.Width = new GridLength(pLive, GridUnitType.Star);

            double xDetailed = width * (pDetailed / (pDetailed + pLive));
            WpfCanvas.SetLeft(_thumbDetailed, Math.Round(xDetailed - (_thumbDetailed.Width / 2)));
            WpfCanvas.SetTop(_thumbDetailed, 1);

            _labelDetailed.Text = $"Detailed (<{CardEnterZoom:0%})";
            _labelLive.Text = $"Live (>{CardExitZoom:0%})";

            _readout.Text = $"Detailed cards below {CardEnterZoom:0%} (live previews return at {CardExitZoom:0%}) · Simple cards disabled";
        }
        else if (!EnableLive && EnableDetailed && EnableSimple)
        {
            // Cards only (Detailed and Simple, no Live)
            _segSimple.Visibility = Visibility.Visible;
            _segDetailed.Visibility = Visibility.Visible;
            _segLive.Visibility = Visibility.Collapsed;

            _thumbSimple.Visibility = Visibility.Visible;
            _thumbDetailed.Visibility = Visibility.Collapsed;

            double pSimple = Math.Clamp(SimpleCardEnterZoom, 0.01, 0.50);
            double pDetailed = Math.Max(0.05, 1.0 - pSimple);

            _colSimple.Width = new GridLength(pSimple, GridUnitType.Star);
            _colDetailed.Width = new GridLength(pDetailed, GridUnitType.Star);
            _colLive.Width = new GridLength(0);

            double xSimple = width * (pSimple / (pSimple + pDetailed));
            WpfCanvas.SetLeft(_thumbSimple, Math.Round(xSimple - (_thumbSimple.Width / 2)));
            WpfCanvas.SetTop(_thumbSimple, 1);

            _labelSimple.Text = $"Simple (<{SimpleCardEnterZoom:0%})";
            _labelDetailed.Text = $"Detailed (>{SimpleCardExitZoom:0%})";

            _readout.Text = $"Simple cards below {SimpleCardEnterZoom:0%} (detailed cards return at {SimpleCardExitZoom:0%}) · Live previews disabled";
        }
    }
}
