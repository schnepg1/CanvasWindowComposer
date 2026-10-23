using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using System.Windows.Input;
using System.Windows.Media;
using CanvasDesktop.CanvasView;
using Button = System.Windows.Controls.Button;
using Color = System.Drawing.Color;
using FlowDirection = System.Windows.FlowDirection;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Forms.KeyEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;
using TextBox = System.Windows.Controls.TextBox;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;

namespace CanvasDesktop;

public sealed record ShapeEditorResult(bool Save, bool Delete, CanvasAnnotation UpdatedItem, ZOrderAction ZOrder);

/// <summary>
/// A modern, focused shape styling and depth editor providing live visual preview,
/// color palette selection, stroke weight adjustment, shape type toggle, inner labeling,
/// and foreground/background layer depth controls.
/// </summary>
internal sealed class AnnotationShapeEditor : Form
{
    private static readonly (string Name, string Hex)[] Palette =
    [
        ("Gold", "#FBBF24"),
        ("Coral", "#FB7185"),
        ("Sky", "#60A5FA"),
        ("Mint", "#34D399"),
        ("Lilac", "#C084FC"),
        ("Amber", "#F59E0B"),
        ("Emerald", "#10B981"),
        ("Rose", "#F43F5E"),
        ("Indigo", "#818CF8"),
        ("White", "#FFFFFF"),
        ("Slate", "#94A3B8")
    ];

    private static readonly double[] StrokePresets = [1, 2, 3, 6, 8, 12];

    public CanvasAnnotation OriginalItem { get; }
    public CanvasAnnotation CurrentItem { get; private set; }
    public ZOrderAction SelectedZOrder { get; private set; } = ZOrderAction.None;
    public event Action<ShapeEditorResult>? Finished;

    // Exposed controls for testing and automation
    internal Button TypeRectButton { get; }
    internal Button TypeEllipseButton { get; }
    internal List<Button> ColorSwatchButtons { get; } = [];
    internal TextBox HexInput { get; }
    internal List<Button> StrokePresetButtons { get; } = [];
    internal Button BringToFrontButton { get; }
    internal Button SendToBackButton { get; }
    internal Button BringForwardButton { get; }
    internal Button SendBackwardButton { get; }
    internal TextBlock ZOrderStatusText { get; }
    internal TextBox LabelInput { get; }
    internal Button MakeSquareButton { get; }
    internal Button SaveBtn { get; }
    internal Button CancelBtn { get; }
    internal Button DeleteBtn { get; }
    internal FrameworkElement ArtworkPreview => _artworkPreview;

    private readonly ShapeArtworkPreview _artworkPreview;
    private readonly TextBlock _dimensionText;
    private readonly TextBlock _hexDisplayBadge;
    private readonly TextBlock _strokeValueBadge;
    private readonly Border _hexColorDot;
    private readonly ElementHost _host;
    private readonly (int Index, int Total) _initialZOrder;
    private bool _suppressHexEvents;

    public AnnotationShapeEditor(CanvasAnnotation item, AnnotationDocument? document = null, Guid? currentDesktop = null)
    {
        OriginalItem = item;
        CurrentItem = item;
        _initialZOrder = (document != null && currentDesktop.HasValue)
            ? document.GetZOrder(currentDesktop.Value, item.Id)
            : (0, 1);

        Text = "Shape editor";
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.FromArgb(15, 20, 31);
        ForeColor = Color.White;
        MinimumSize = new Size(640, 520);
        Size = new Size(720, 580);

        // WPF Root Container
        var rootGrid = new Grid
        {
            Background = BrushFromHex("#0B0F19"),
            Margin = new Thickness(0)
        };
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Header
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Main Body
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Action Bar

        // 1. Header
        var headerBorder = new Border
        {
            Background = BrushFromHex("#111827"),
            BorderBrush = BrushFromHex("#1F2937"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(18, 12, 18, 12)
        };
        var headerPanel = new StackPanel { Orientation = Orientation.Vertical };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        var titleIcon = new TextBlock
        {
            Text = "◆",
            Foreground = BrushFromHex("#60A5FA"),
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var titleText = new TextBlock
        {
            Text = "Shape Editor",
            Foreground = WpfBrushes.White,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        titleRow.Children.Add(titleIcon);
        titleRow.Children.Add(titleText);

        var subtitleText = new TextBlock
        {
            Text = "Customize shape geometry, color palette, stroke width, layer depth, and label",
            Foreground = BrushFromHex("#94A3B8"),
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0)
        };
        headerPanel.Children.Add(titleRow);
        headerPanel.Children.Add(subtitleText);
        headerBorder.Child = headerPanel;
        Grid.SetRow(headerBorder, 0);
        rootGrid.Children.Add(headerBorder);

        // 2. Main Content Split: Left = Preview, Right = Properties
        var contentGrid = new Grid { Margin = new Thickness(18, 14, 18, 14) };
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(270, GridUnitType.Pixel) });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Left: Preview Card
        var previewCard = new Border
        {
            Background = BrushFromHex("#111827"),
            BorderBrush = BrushFromHex("#1F2937"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 0, 14, 0),
            Padding = new Thickness(12)
        };
        var previewPanel = new Grid();
        previewPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        previewPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        previewPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var previewHeader = new TextBlock
        {
            Text = "LIVE PREVIEW",
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Foreground = BrushFromHex("#64748B"),
            Margin = new Thickness(0, 0, 0, 8)
        };
        Grid.SetRow(previewHeader, 0);
        previewPanel.Children.Add(previewHeader);

        _artworkPreview = new ShapeArtworkPreview(CurrentItem);
        var previewViewbox = new Viewbox
        {
            Stretch = Stretch.Uniform,
            Margin = new Thickness(8),
            Child = _artworkPreview
        };
        var previewViewboxHost = new Border
        {
            Background = BrushFromHex("#0B0F19"),
            BorderBrush = BrushFromHex("#1E293B"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = previewViewbox
        };
        Grid.SetRow(previewViewboxHost, 1);
        previewPanel.Children.Add(previewViewboxHost);

        var previewFooter = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(0, 10, 0, 0)
        };
        _dimensionText = new TextBlock
        {
            Text = $"{Math.Round(CurrentItem.Bounds.Width)} × {Math.Round(CurrentItem.Bounds.Height)} px",
            Foreground = BrushFromHex("#94A3B8"),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 6)
        };
        MakeSquareButton = CreateStyledButton("Make 1:1 (Square/Circle)", BrushFromHex("#1E293B"), BrushFromHex("#334155"), BrushFromHex("#CBD5E1"), 11);
        MakeSquareButton.Height = 28;
        MakeSquareButton.Click += (_, _) => MakeSquare();
        previewFooter.Children.Add(_dimensionText);
        previewFooter.Children.Add(MakeSquareButton);
        Grid.SetRow(previewFooter, 2);
        previewPanel.Children.Add(previewFooter);

        previewCard.Child = previewPanel;
        Grid.SetColumn(previewCard, 0);
        contentGrid.Children.Add(previewCard);

        // Right: Properties ScrollViewer
        var propertiesScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0)
        };
        var propertiesStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(0, 0, 4, 0)
        };

        // --- SECTION: Shape Kind ---
        propertiesStack.Children.Add(CreateSectionLabel("SHAPE TYPE"));
        var shapeTypeGrid = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        shapeTypeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        shapeTypeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        TypeRectButton = CreateStyledButton("▢  Rectangle", BrushFromHex("#1E293B"), BrushFromHex("#334155"), BrushFromHex("#CBD5E1"), 12);
        TypeRectButton.Height = 34;
        TypeRectButton.Margin = new Thickness(0, 0, 4, 0);
        TypeRectButton.Click += (_, _) => SetShapeKind(AnnotationKind.Rectangle);
        Grid.SetColumn(TypeRectButton, 0);
        shapeTypeGrid.Children.Add(TypeRectButton);

        TypeEllipseButton = CreateStyledButton("◯  Ellipse", BrushFromHex("#1E293B"), BrushFromHex("#334155"), BrushFromHex("#CBD5E1"), 12);
        TypeEllipseButton.Height = 34;
        TypeEllipseButton.Margin = new Thickness(4, 0, 0, 0);
        TypeEllipseButton.Click += (_, _) => SetShapeKind(AnnotationKind.Ellipse);
        Grid.SetColumn(TypeEllipseButton, 1);
        shapeTypeGrid.Children.Add(TypeEllipseButton);
        propertiesStack.Children.Add(shapeTypeGrid);

        // --- SECTION: Color Setting ---
        var colorHeader = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        colorHeader.Children.Add(CreateSectionLabelInline("COLOR"));
        _hexDisplayBadge = new TextBlock
        {
            Text = CurrentItem.Color.ToUpperInvariant(),
            Foreground = BrushFromHex("#94A3B8"),
            FontSize = 10,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 4)
        };
        colorHeader.Children.Add(_hexDisplayBadge);
        propertiesStack.Children.Add(colorHeader);

        var swatchesPanel = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        foreach (var (name, hex) in Palette)
        {
            var swatch = new Button
            {
                Width = 28,
                Height = 28,
                Margin = new Thickness(0, 0, 6, 6),
                Background = BrushFromHex(hex),
                BorderBrush = BrushFromHex("#334155"),
                BorderThickness = new Thickness(1),
                ToolTip = $"{name} ({hex})",
                Cursor = System.Windows.Input.Cursors.Hand,
                Focusable = false
            };
            var radiusFactory = new FrameworkElementFactory(typeof(Border));
            radiusFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(14));
            radiusFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            radiusFactory.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
            radiusFactory.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
            swatch.Template = new ControlTemplate(typeof(Button)) { VisualTree = radiusFactory };

            string targetHex = hex;
            swatch.Click += (_, _) => SetColor(targetHex);
            ColorSwatchButtons.Add(swatch);
            swatchesPanel.Children.Add(swatch);
        }
        propertiesStack.Children.Add(swatchesPanel);

        // Custom Hex Input Row
        var hexRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        _hexColorDot = new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(10),
            Background = BrushFromHex(CurrentItem.Color),
            BorderBrush = BrushFromHex("#475569"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        hexRow.Children.Add(_hexColorDot);

        var hexLabel = new TextBlock
        {
            Text = "Hex:",
            Foreground = BrushFromHex("#94A3B8"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        hexRow.Children.Add(hexLabel);

        HexInput = new TextBox
        {
            Text = CurrentItem.Color,
            Width = 90,
            Height = 28,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = BrushFromHex("#1E293B"),
            Foreground = WpfBrushes.White,
            BorderBrush = BrushFromHex("#334155"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 2, 6, 2),
            FontSize = 11,
            FontFamily = new System.Windows.Media.FontFamily("Consolas")
        };
        HexInput.TextChanged += (_, _) =>
        {
            if (_suppressHexEvents) return;
            string text = HexInput.Text.Trim();
            if (AnnotationDocument.ValidColor(text)) SetColor(text, fromHexInput: true);
        };
        hexRow.Children.Add(HexInput);
        propertiesStack.Children.Add(hexRow);

        // --- SECTION: Stroke Width ---
        var strokeHeader = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        strokeHeader.Children.Add(CreateSectionLabelInline("STROKE WIDTH"));
        _strokeValueBadge = new TextBlock
        {
            Text = $"{CurrentItem.Stroke} px",
            Foreground = BrushFromHex("#94A3B8"),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 4)
        };
        strokeHeader.Children.Add(_strokeValueBadge);
        propertiesStack.Children.Add(strokeHeader);

        var strokeGrid = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        for (int i = 0; i < StrokePresets.Length; i++)
            strokeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (int i = 0; i < StrokePresets.Length; i++)
        {
            double w = StrokePresets[i];
            var btn = CreateStyledButton($"{w}px", BrushFromHex("#1E293B"), BrushFromHex("#334155"), BrushFromHex("#CBD5E1"), 11);
            btn.Height = 28;
            btn.Margin = new Thickness(i == 0 ? 0 : 2, 0, i == StrokePresets.Length - 1 ? 0 : 2, 0);
            btn.Click += (_, _) => SetStroke(w);
            StrokePresetButtons.Add(btn);
            Grid.SetColumn(btn, i);
            strokeGrid.Children.Add(btn);
        }
        propertiesStack.Children.Add(strokeGrid);

        // --- SECTION: Position / Layer Depth (Foreground & Background) ---
        var depthHeader = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        depthHeader.Children.Add(CreateSectionLabelInline("LAYER DEPTH"));
        ZOrderStatusText = new TextBlock
        {
            Text = FormatZOrderStatus(),
            Foreground = BrushFromHex("#60A5FA"),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 4)
        };
        depthHeader.Children.Add(ZOrderStatusText);
        propertiesStack.Children.Add(depthHeader);

        var depthPrimaryGrid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        depthPrimaryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        depthPrimaryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        BringToFrontButton = CreateStyledButton("⤒  Bring to Foreground", BrushFromHex("#1E293B"), BrushFromHex("#334155"), BrushFromHex("#CBD5E1"), 11);
        BringToFrontButton.Height = 32;
        BringToFrontButton.ToolTip = "Move shape to the foreground, in front of windows and other shapes";
        BringToFrontButton.Margin = new Thickness(0, 0, 3, 0);
        BringToFrontButton.Click += (_, _) => SetZOrder(ZOrderAction.BringToFront);
        Grid.SetColumn(BringToFrontButton, 0);
        depthPrimaryGrid.Children.Add(BringToFrontButton);

        SendToBackButton = CreateStyledButton("⤓  Send to Background", BrushFromHex("#1E293B"), BrushFromHex("#334155"), BrushFromHex("#CBD5E1"), 11);
        SendToBackButton.Height = 32;
        SendToBackButton.ToolTip = "Move shape to the background, behind windows and other shapes";
        SendToBackButton.Margin = new Thickness(3, 0, 0, 0);
        SendToBackButton.Click += (_, _) => SetZOrder(ZOrderAction.SendToBack);
        Grid.SetColumn(SendToBackButton, 1);
        depthPrimaryGrid.Children.Add(SendToBackButton);
        propertiesStack.Children.Add(depthPrimaryGrid);

        var depthSecondaryGrid = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        depthSecondaryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        depthSecondaryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        BringForwardButton = CreateStyledButton("↑  Step Forward", BrushFromHex("#161E2E"), BrushFromHex("#26334D"), BrushFromHex("#94A3B8"), 10);
        BringForwardButton.Height = 26;
        BringForwardButton.ToolTip = "Move one layer up towards foreground";
        BringForwardButton.Margin = new Thickness(0, 0, 3, 0);
        BringForwardButton.Click += (_, _) => SetZOrder(ZOrderAction.BringForward);
        Grid.SetColumn(BringForwardButton, 0);
        depthSecondaryGrid.Children.Add(BringForwardButton);

        SendBackwardButton = CreateStyledButton("↓  Step Backward", BrushFromHex("#161E2E"), BrushFromHex("#26334D"), BrushFromHex("#94A3B8"), 10);
        SendBackwardButton.Height = 26;
        SendBackwardButton.ToolTip = "Move one layer down towards background";
        SendBackwardButton.Margin = new Thickness(3, 0, 0, 0);
        SendBackwardButton.Click += (_, _) => SetZOrder(ZOrderAction.SendBackward);
        Grid.SetColumn(SendBackwardButton, 1);
        depthSecondaryGrid.Children.Add(SendBackwardButton);
        propertiesStack.Children.Add(depthSecondaryGrid);

        // --- SECTION: Inner Label ---
        propertiesStack.Children.Add(CreateSectionLabel("INNER LABEL (OPTIONAL)"));
        LabelInput = new TextBox
        {
            Text = CurrentItem.Text,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MaxLength = AnnotationDocument.MaximumTextLength,
            Height = 60,
            Background = BrushFromHex("#1E293B"),
            Foreground = WpfBrushes.White,
            BorderBrush = BrushFromHex("#334155"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8),
            FontSize = 12,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 0, 0, 8)
        };
        LabelInput.TextChanged += (_, _) =>
        {
            CurrentItem = CurrentItem with { Text = LabelInput.Text };
            _artworkPreview.Update(CurrentItem);
        };
        propertiesStack.Children.Add(LabelInput);

        propertiesScroll.Content = propertiesStack;
        Grid.SetColumn(propertiesScroll, 1);
        contentGrid.Children.Add(propertiesScroll);
        Grid.SetRow(contentGrid, 1);
        rootGrid.Children.Add(contentGrid);

        // 3. Action Bar (Bottom)
        var actionBorder = new Border
        {
            Background = BrushFromHex("#111827"),
            BorderBrush = BrushFromHex("#1F2937"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(18, 12, 18, 12)
        };
        var actionGrid = new Grid();
        actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        DeleteBtn = CreateStyledButton("🗑  Delete", BrushFromHex("#2A1215"), BrushFromHex("#7F1D1D"), BrushFromHex("#FCA5A5"), 11);
        DeleteBtn.Height = 34;
        DeleteBtn.Padding = new Thickness(12, 0, 12, 0);
        DeleteBtn.Click += (_, _) => Finish(save: false, delete: true);
        Grid.SetColumn(DeleteBtn, 0);
        actionGrid.Children.Add(DeleteBtn);

        var rightActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var shortcutHint = new TextBlock
        {
            Text = "Ctrl+Enter to save · Esc to cancel",
            Foreground = BrushFromHex("#64748B"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0)
        };
        rightActions.Children.Add(shortcutHint);

        CancelBtn = CreateStyledButton("Cancel", BrushFromHex("#1E293B"), BrushFromHex("#334155"), BrushFromHex("#CBD5E1"), 12);
        CancelBtn.Height = 34;
        CancelBtn.Padding = new Thickness(16, 0, 16, 0);
        CancelBtn.Margin = new Thickness(0, 0, 8, 0);
        CancelBtn.Click += (_, _) => Finish(save: false, delete: false);
        rightActions.Children.Add(CancelBtn);

        SaveBtn = CreateStyledButton("Save Shape", BrushFromHex("#2563EB"), BrushFromHex("#3B82F6"), WpfBrushes.White, 12, bold: true);
        SaveBtn.Height = 34;
        SaveBtn.Padding = new Thickness(20, 0, 20, 0);
        SaveBtn.Click += (_, _) => Finish(save: true, delete: false);
        rightActions.Children.Add(SaveBtn);

        Grid.SetColumn(rightActions, 1);
        actionGrid.Children.Add(rightActions);
        actionBorder.Child = actionGrid;
        Grid.SetRow(actionBorder, 2);
        rootGrid.Children.Add(actionBorder);

        // Host in WinForms ElementHost
        _host = new ElementHost
        {
            Dock = DockStyle.Fill,
            BackColor = BackColor,
            Child = rootGrid
        };
        Controls.Add(_host);

        // Refresh UI state
        RefreshActiveState();

        // Keyboard handling
        rootGrid.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Finish(save: false, delete: false);
            }
            else if (e.Key == Key.Enter && (Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
            {
                e.Handled = true;
                Finish(save: true, delete: false);
            }
        };

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                Finish(save: false, delete: false);
            }
            else if (e.Control && e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                Finish(save: true, delete: false);
            }
        };

        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Finish(save: false, delete: false);
            }
        };

        Shown += (_, _) =>
        {
            SaveBtn.Focus();
        };
    }

    private void SetShapeKind(AnnotationKind kind)
    {
        CurrentItem = CurrentItem with { Kind = kind };
        _artworkPreview.Update(CurrentItem);
        RefreshActiveState();
    }

    private void SetColor(string hex, bool fromHexInput = false)
    {
        if (!AnnotationDocument.ValidColor(hex)) return;
        CurrentItem = CurrentItem with { Color = hex };
        _hexColorDot.Background = BrushFromHex(hex);
        _hexDisplayBadge.Text = hex.ToUpperInvariant();
        if (!fromHexInput)
        {
            _suppressHexEvents = true;
            HexInput.Text = hex;
            _suppressHexEvents = false;
        }
        _artworkPreview.Update(CurrentItem);
        RefreshActiveState();
    }

    private void SetStroke(double stroke)
    {
        CurrentItem = CurrentItem with { Stroke = stroke };
        _strokeValueBadge.Text = $"{stroke} px";
        _artworkPreview.Update(CurrentItem);
        RefreshActiveState();
    }

    private void SetZOrder(ZOrderAction action)
    {
        SelectedZOrder = action;
        if (action == ZOrderAction.SendToBack)
            CurrentItem = CurrentItem with { InBackground = true };
        else if (action is ZOrderAction.BringToFront or ZOrderAction.BringForward)
            CurrentItem = CurrentItem with { InBackground = false };
        ZOrderStatusText.Text = FormatZOrderStatus();
        RefreshActiveState();
    }

    private void MakeSquare()
    {
        var bounds = CurrentItem.Bounds;
        double side = Math.Max(bounds.Width, bounds.Height);
        double signX = CurrentItem.B.X < CurrentItem.A.X ? -1 : 1;
        double signY = CurrentItem.B.Y < CurrentItem.A.Y ? -1 : 1;
        CurrentItem = CurrentItem with { B = new CanvasPoint(CurrentItem.A.X + signX * side, CurrentItem.A.Y + signY * side) };
        _dimensionText.Text = $"{Math.Round(side)} × {Math.Round(side)} px";
        _artworkPreview.Update(CurrentItem);
    }

    private string FormatZOrderStatus()
    {
        return SelectedZOrder switch
        {
            ZOrderAction.BringToFront => "Will move to Foreground (In front of windows & shapes)",
            ZOrderAction.SendToBack => "Will move to Background (Behind windows & shapes)",
            ZOrderAction.BringForward => "Will step Forward (+1 layer)",
            ZOrderAction.SendBackward => "Will step Backward (-1 layer)",
            _ => (CurrentItem.InBackground || (_initialZOrder.Index == 0 && _initialZOrder.Total > 0 && CurrentItem.InBackground))
                ? "Currently: Background (Behind windows & shapes)"
                : _initialZOrder.Total <= 1
                    ? "Single layer"
                    : _initialZOrder.Index == _initialZOrder.Total - 1
                        ? "Currently: Foreground (In front of windows)"
                        : $"Current layer: {_initialZOrder.Index + 1} of {_initialZOrder.Total}"
        };
    }

    private void RefreshActiveState()
    {
        // Shape Kind buttons
        bool isRect = CurrentItem.Kind == AnnotationKind.Rectangle;
        HighlightButton(TypeRectButton, isRect);
        HighlightButton(TypeEllipseButton, !isRect);

        // Color swatches
        for (int i = 0; i < Palette.Length && i < ColorSwatchButtons.Count; i++)
        {
            bool match = string.Equals(Palette[i].Hex, CurrentItem.Color, StringComparison.OrdinalIgnoreCase);
            ColorSwatchButtons[i].BorderBrush = match ? WpfBrushes.White : BrushFromHex("#334155");
            ColorSwatchButtons[i].BorderThickness = new Thickness(match ? 2.5 : 1);
        }

        // Stroke buttons
        for (int i = 0; i < StrokePresets.Length && i < StrokePresetButtons.Count; i++)
        {
            bool match = Math.Abs(StrokePresets[i] - CurrentItem.Stroke) < 0.01;
            HighlightButton(StrokePresetButtons[i], match);
        }

        // Z-Order buttons
        HighlightButton(BringToFrontButton, SelectedZOrder == ZOrderAction.BringToFront);
        HighlightButton(SendToBackButton, SelectedZOrder == ZOrderAction.SendToBack);
        HighlightButton(BringForwardButton, SelectedZOrder == ZOrderAction.BringForward);
        HighlightButton(SendBackwardButton, SelectedZOrder == ZOrderAction.SendBackward);
    }

    private static void HighlightButton(Button btn, bool active)
    {
        if (active)
        {
            btn.Background = BrushFromHex("#1E3A8A");
            btn.BorderBrush = BrushFromHex("#3B82F6");
            btn.Foreground = BrushFromHex("#93C5FD");
            btn.BorderThickness = new Thickness(1.5);
        }
        else
        {
            btn.Background = BrushFromHex("#1E293B");
            btn.BorderBrush = BrushFromHex("#334155");
            btn.Foreground = BrushFromHex("#CBD5E1");
            btn.BorderThickness = new Thickness(1);
        }
    }

    private void Finish(bool save, bool delete)
    {
        Finished?.Invoke(new ShapeEditorResult(save, delete, CurrentItem, SelectedZOrder));
    }

    private static TextBlock CreateSectionLabel(string text) => new()
    {
        Text = text,
        Foreground = BrushFromHex("#64748B"),
        FontSize = 10,
        FontWeight = FontWeights.Bold,
        Margin = new Thickness(0, 0, 0, 6)
    };

    private static TextBlock CreateSectionLabelInline(string text) => new()
    {
        Text = text,
        Foreground = BrushFromHex("#64748B"),
        FontSize = 10,
        FontWeight = FontWeights.Bold,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static Button CreateStyledButton(string text, WpfBrush bg, WpfBrush border, WpfBrush fg, double fontSize, bool bold = false)
    {
        var btn = new Button
        {
            Content = text,
            Background = bg,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            Foreground = fg,
            FontSize = fontSize,
            FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
            Cursor = System.Windows.Input.Cursors.Hand,
            Focusable = true
        };
        var factory = new FrameworkElementFactory(typeof(Border));
        factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        factory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
        factory.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
        factory.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        factory.AppendChild(content);
        btn.Template = new ControlTemplate(typeof(Button)) { VisualTree = factory };
        return btn;
    }

    private static SolidColorBrush BrushFromHex(string hex)
    {
        var brush = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private sealed class ShapeArtworkPreview : FrameworkElement
    {
        private CanvasAnnotation _item;

        public ShapeArtworkPreview(CanvasAnnotation item)
        {
            _item = item;
            Width = Math.Max(1, item.Bounds.Width);
            Height = Math.Max(1, item.Bounds.Height);
        }

        public void Update(CanvasAnnotation item)
        {
            _item = item;
            Width = Math.Max(1, item.Bounds.Width);
            Height = Math.Max(1, item.Bounds.Height);
            InvalidateMeasure();
            InvalidateVisual();
        }

        protected override System.Windows.Size MeasureOverride(System.Windows.Size availableSize) =>
            new(Math.Max(1, _item.Bounds.Width), Math.Max(1, _item.Bounds.Height));

        protected override void OnRender(DrawingContext dc)
        {
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            AnnotationVisual.Draw(dc, _item, dpi, drawText: true);
        }
    }
}
