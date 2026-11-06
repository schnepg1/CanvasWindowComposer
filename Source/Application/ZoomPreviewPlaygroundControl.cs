using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace CanvasDesktop;

/// <summary>
/// An interactive preview playground allowing users to scrub simulated canvas zoom levels
/// and see real-time transformations between Simple cards, Detailed cards, and Live previews.
/// </summary>
internal sealed class ZoomPreviewPlaygroundControl : UserControl
{
    public enum ZoomTier { Simple, Detailed, Live }

    private readonly Slider _zoomScrubber;
    private readonly TextBlock _zoomReadout;
    private readonly Border _previewStage;
    private readonly Border _simpleCardView;
    private readonly Border _detailedCardView;
    private readonly Border _livePreviewView;
    private readonly Border _tierBadge;
    private readonly TextBlock _tierBadgeText;
    private readonly TextBlock _tierExplanation;

    private bool _enableLive = true;
    private bool _enableDetailed = true;
    private bool _enableSimple = true;
    private double _cardEnterZoom = OverviewZoomDefaults.CardEnter;
    private double _simpleCardEnterZoom = OverviewZoomDefaults.SimpleCardEnter;

    public double CurrentZoomPercent => _zoomScrubber.Value;

    public ZoomPreviewPlaygroundControl()
    {
        var container = new Border
        {
            Background = ZoneTheme.Brush("#131B27"),
            BorderBrush = ZoneTheme.Brush("#2D3B4E"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 12, 12, 12),
            Margin = new Thickness(0, 4, 0, 10)
        };
        Content = container;

        var root = new StackPanel();
        container.Child = root;

        // Header
        var header = new TextBlock
        {
            Text = "Preview Playground",
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Foreground = ZoneTheme.Brush("#E2E8F0"),
            Margin = new Thickness(0, 0, 0, 2)
        };
        root.Children.Add(header);

        var subtitle = new TextBlock
        {
            Text = "Scrub the zoom slider below to preview how windows transform at different distances.",
            FontSize = 11,
            Foreground = ZoneTheme.Brush("#94A3B8"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };
        root.Children.Add(subtitle);

        // Quick Preset Buttons Row
        var presetRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var presetLabel = new TextBlock
        {
            Text = "Quick test:",
            FontSize = 11,
            Foreground = ZoneTheme.Brush("#94A3B8"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        presetRow.Children.Add(presetLabel);

        var presetButtons = new StackPanel { Orientation = Orientation.Horizontal };
        presetButtons.Children.Add(CreatePresetButton("8% (Simple)", 8));
        presetButtons.Children.Add(CreatePresetButton("30% (Detailed)", 30));
        presetButtons.Children.Add(CreatePresetButton("80% (Live)", 80));
        presetRow.Children.Add(presetButtons);
        root.Children.Add(presetRow);

        // Zoom Scrubber Row
        var scrubberHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
        _zoomReadout = new TextBlock
        {
            Width = 56,
            TextAlignment = TextAlignment.Right,
            FontWeight = FontWeights.Bold,
            Foreground = ZoneTheme.Brush("#93C5FD"),
            FontSize = 13
        };
        DockPanel.SetDock(_zoomReadout, Dock.Right);
        scrubberHeader.Children.Add(_zoomReadout);

        var scrubberLabel = new TextBlock
        {
            Text = "Simulated canvas zoom:",
            FontSize = 12,
            Foreground = ZoneTheme.Brush("#F1F5F9"),
            VerticalAlignment = VerticalAlignment.Center
        };
        scrubberHeader.Children.Add(scrubberLabel);
        root.Children.Add(scrubberHeader);

        _zoomScrubber = new Slider
        {
            Minimum = 5,
            Maximum = 100,
            Value = 35,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
            SmallChange = 1,
            LargeChange = 5,
            AutoToolTipPlacement = System.Windows.Controls.Primitives.AutoToolTipPlacement.TopLeft,
            AutoToolTipPrecision = 0,
            Margin = new Thickness(0, 2, 0, 10)
        };
        AutomationProperties.SetName(_zoomScrubber, "Preview playground zoom level (percent)");
        _zoomReadout.Text = $"{_zoomScrubber.Value:0}%";
        _zoomScrubber.ValueChanged += (_, _) =>
        {
            _zoomReadout.Text = $"{_zoomScrubber.Value:0}%";
            RefreshPreview();
        };
        root.Children.Add(_zoomScrubber);

        // Interactive Stage
        _previewStage = new Border
        {
            Height = 136,
            Background = ZoneTheme.Brush("#0B0F19"),
            BorderBrush = ZoneTheme.Brush("#1E293B"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true
        };
        root.Children.Add(_previewStage);

        var stageGrid = new Grid();
        _previewStage.Child = stageGrid;

        // Top-right Tier Status Badge
        _tierBadge = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 3, 6, 3),
            Margin = new Thickness(8)
        };
        _tierBadgeText = new TextBlock
        {
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White
        };
        _tierBadge.Child = _tierBadgeText;
        stageGrid.Children.Add(_tierBadge);

        // The 3 Mock Window Views (centered)
        _simpleCardView = BuildSimpleCardView();
        _detailedCardView = BuildDetailedCardView();
        _livePreviewView = BuildLivePreviewView();

        stageGrid.Children.Add(_simpleCardView);
        stageGrid.Children.Add(_detailedCardView);
        stageGrid.Children.Add(_livePreviewView);

        // Bottom explanation text
        _tierExplanation = new TextBlock
        {
            FontSize = 11,
            Foreground = ZoneTheme.Brush("#94A3B8"),
            Margin = new Thickness(2, 6, 2, 0),
            TextWrapping = TextWrapping.Wrap
        };
        root.Children.Add(_tierExplanation);

        RefreshPreview();
    }

    private Button CreatePresetButton(string text, double targetPercent)
    {
        var btn = new Button
        {
            Content = text,
            FontSize = 11,
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(0, 0, 6, 0),
            Background = ZoneTheme.Brush("#1E293B"),
            Foreground = ZoneTheme.Brush("#E2E8F0"),
            BorderBrush = ZoneTheme.Brush("#334155"),
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        btn.Click += (_, _) => _zoomScrubber.Value = targetPercent;
        return btn;
    }

    private static Border BuildSimpleCardView()
    {
        var card = new Border
        {
            Width = 140,
            Height = 76,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = ZoneTheme.Brush("#1E293B"),
            BorderBrush = ZoneTheme.Brush("#475569"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8)
        };
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        card.Child = panel;

        var icon = new Border
        {
            Width = 26,
            Height = 26,
            Background = ZoneTheme.Brush("#334155"),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 4),
            Child = new TextBlock
            {
                Text = "📁",
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        panel.Children.Add(icon);

        panel.Children.Add(new TextBlock
        {
            Text = "Canvas Desktop",
            FontWeight = FontWeights.SemiBold,
            FontSize = 11,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Workspace",
            FontSize = 9,
            Foreground = ZoneTheme.Brush("#94A3B8"),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        return card;
    }

    private static Border BuildDetailedCardView()
    {
        var card = new Border
        {
            Width = 200,
            Height = 98,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = ZoneTheme.Brush("#182333"),
            BorderBrush = ZoneTheme.Brush("#3B82F6"),
            BorderThickness = new Thickness(1.2),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true
        };
        var grid = new Grid();
        card.Child = grid;

        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Border
        {
            Background = ZoneTheme.Brush("#1E2D42"),
            Padding = new Thickness(6, 2, 6, 2)
        };
        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        titleRow.Children.Add(new TextBlock { Text = "📋", FontSize = 10, Margin = new Thickness(0, 0, 4, 0) });
        titleRow.Children.Add(new TextBlock
        {
            Text = "Workspace.cs — Visual Studio",
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White
        });
        header.Child = titleRow;

        var body = new StackPanel { Margin = new Thickness(8, 6, 8, 4) };
        Grid.SetRow(body, 1);
        grid.Children.Add(body);

        body.Children.Add(new TextBlock
        {
            Text = "C:\\Projects\\Canvas\\Source\\Workspace.cs",
            FontSize = 9,
            Foreground = ZoneTheme.Brush("#64748B"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 0, 6)
        });

        // Mock snapshot thumbnail bars
        var previewLines = new StackPanel();
        body.Children.Add(previewLines);
        previewLines.Children.Add(new Rectangle { Height = 4, Width = 140, Fill = ZoneTheme.Brush("#2D3D54"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 4) });
        previewLines.Children.Add(new Rectangle { Height = 4, Width = 110, Fill = ZoneTheme.Brush("#2D3D54"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 4) });
        previewLines.Children.Add(new Rectangle { Height = 4, Width = 80, Fill = ZoneTheme.Brush("#253245"), HorizontalAlignment = HorizontalAlignment.Left });

        return card;
    }

    private static Border BuildLivePreviewView()
    {
        var card = new Border
        {
            Width = 236,
            Height = 112,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = ZoneTheme.Brush("#0F172A"),
            BorderBrush = ZoneTheme.Brush("#2563EB"),
            BorderThickness = new Thickness(1.8),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Effect = new DropShadowEffect
            {
                Color = Color.FromRgb(37, 99, 235),
                BlurRadius = 14,
                ShadowDepth = 0,
                Opacity = 0.4
            }
        };
        var grid = new Grid();
        card.Child = grid;

        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var titleBar = new Border
        {
            Background = ZoneTheme.Brush("#1E293B"),
            Padding = new Thickness(6, 2, 6, 2)
        };
        Grid.SetRow(titleBar, 0);
        grid.Children.Add(titleBar);

        var titleDock = new DockPanel();
        titleBar.Child = titleDock;

        var windowControls = new TextBlock
        {
            Text = "—  □  ✕",
            FontSize = 9,
            Foreground = ZoneTheme.Brush("#94A3B8"),
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(windowControls, Dock.Right);
        titleDock.Children.Add(windowControls);

        var titleLeft = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        titleLeft.Children.Add(new TextBlock { Text = "⚡", FontSize = 10, Margin = new Thickness(0, 0, 4, 0), Foreground = ZoneTheme.Brush("#38BDF8") });
        titleLeft.Children.Add(new TextBlock
        {
            Text = "Visual Studio — Live Workspace",
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White
        });
        titleDock.Children.Add(titleLeft);

        var liveBody = new Grid { Margin = new Thickness(8, 6, 8, 6) };
        Grid.SetRow(liveBody, 1);
        grid.Children.Add(liveBody);

        var mockEditor = new StackPanel();
        liveBody.Children.Add(mockEditor);

        mockEditor.Children.Add(new Rectangle { Height = 4, Width = 150, Fill = ZoneTheme.Brush("#38BDF8"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 4) });
        mockEditor.Children.Add(new Rectangle { Height = 4, Width = 120, Fill = ZoneTheme.Brush("#A78BFA"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(12, 0, 0, 4) });
        mockEditor.Children.Add(new Rectangle { Height = 4, Width = 90, Fill = ZoneTheme.Brush("#4ADE80"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(12, 0, 0, 4) });
        mockEditor.Children.Add(new Rectangle { Height = 4, Width = 70, Fill = ZoneTheme.Brush("#F472B6"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 0) });

        var liveBadge = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = ZoneTheme.Brush("#14532D"),
            BorderBrush = ZoneTheme.Brush("#22C55E"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4, 1, 4, 1)
        };
        liveBadge.Child = new TextBlock
        {
            Text = "● LIVE DWM",
            FontSize = 8,
            FontWeight = FontWeights.Bold,
            Foreground = ZoneTheme.Brush("#4ADE80")
        };
        liveBody.Children.Add(liveBadge);

        return card;
    }

    public void UpdateConfiguration(bool live, bool detailed, bool simple, double cardEnter, double simpleEnter)
    {
        _enableLive = live;
        _enableDetailed = detailed;
        _enableSimple = simple;
        _cardEnterZoom = cardEnter;
        _simpleCardEnterZoom = simpleEnter;
        RefreshPreview();
    }

    public void SetZoom(double zoomPercent)
    {
        _zoomScrubber.Value = Math.Clamp(zoomPercent, 5, 100);
    }

    private void RefreshPreview()
    {
        double currentRatio = _zoomScrubber.Value / 100.0;
        var tier = DetermineTier(currentRatio, _enableLive, _enableDetailed, _enableSimple, _cardEnterZoom, _simpleCardEnterZoom);

        _simpleCardView.Visibility = tier == ZoomTier.Simple ? Visibility.Visible : Visibility.Collapsed;
        _detailedCardView.Visibility = tier == ZoomTier.Detailed ? Visibility.Visible : Visibility.Collapsed;
        _livePreviewView.Visibility = tier == ZoomTier.Live ? Visibility.Visible : Visibility.Collapsed;

        switch (tier)
        {
            case ZoomTier.Simple:
                _tierBadge.Background = ZoneTheme.Brush("#334155");
                _tierBadge.BorderBrush = ZoneTheme.Brush("#64748B");
                _tierBadgeText.Text = "🏷️ SIMPLE CARD";
                _tierExplanation.Text = "At this distance, Canvas renders lightweight icon tiles with floating titles, eliminating GPU thumbnail and process memory usage.";
                break;
            case ZoomTier.Detailed:
                _tierBadge.Background = ZoneTheme.Brush("#1E3A5F");
                _tierBadge.BorderBrush = ZoneTheme.Brush("#3B82F6");
                _tierBadgeText.Text = "📋 DETAILED CARD";
                _tierExplanation.Text = "At medium zoom, Canvas displays crisp placeholder cards with window title, path, and snapshot bitmaps.";
                break;
            case ZoomTier.Live:
                _tierBadge.Background = ZoneTheme.Brush("#1E3A8A");
                _tierBadge.BorderBrush = ZoneTheme.Brush("#60A5FA");
                _tierBadgeText.Text = "⚡ LIVE PREVIEW";
                _tierExplanation.Text = "Close up, Canvas renders active live DWM thumbnails with full mouse and keyboard interaction support.";
                break;
        }
    }

    public static ZoomTier DetermineTier(double zoom, bool live, bool detailed, bool simple, double cardEnter, double simpleEnter)
    {
        int activeCount = (live ? 1 : 0) + (detailed ? 1 : 0) + (simple ? 1 : 0);
        if (activeCount == 0 || (live && !detailed && !simple)) return ZoomTier.Live;
        if (detailed && !live && !simple) return ZoomTier.Detailed;
        if (simple && !live && !detailed) return ZoomTier.Simple;

        if (live && detailed && simple)
        {
            if (zoom < simpleEnter) return ZoomTier.Simple;
            if (zoom < cardEnter) return ZoomTier.Detailed;
            return ZoomTier.Live;
        }
        if (live && simple && !detailed)
        {
            return zoom < simpleEnter ? ZoomTier.Simple : ZoomTier.Live;
        }
        if (live && detailed && !simple)
        {
            return zoom < cardEnter ? ZoomTier.Detailed : ZoomTier.Live;
        }
        if (!live && detailed && simple)
        {
            return zoom < simpleEnter ? ZoomTier.Simple : ZoomTier.Detailed;
        }
        return ZoomTier.Live;
    }
}
