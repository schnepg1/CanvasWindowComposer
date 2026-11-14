using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Wpf.Ui.Markup;
using Button = System.Windows.Controls.Button;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using TextBlock = System.Windows.Controls.TextBlock;

namespace CanvasDesktop;

/// <summary>
/// Unified Fluid WPF toolbelt and arrange/interact dock control built with WPF-UI.
/// Consolidates canvas mode switching, annotation creation, styling shelf, and controls cheat-sheet.
/// </summary>
internal sealed class UnifiedDockControl : UserControl
{
    private static SolidColorBrush Brush(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;

    // Shell & layout containers
    private readonly Border _shell;
    private readonly StackPanel _dockStack = new() { Orientation = Orientation.Vertical };
    private readonly StackPanel _mainRow = new() { Orientation = Orientation.Horizontal };
    private readonly Border _propertiesShelf;
    private readonly StackPanel _propertiesRow = new() { Orientation = Orientation.Horizontal };

    // Mode Toggle Elements
    private readonly Wpf.Ui.Controls.Button _arrangeModeButton;
    private readonly Wpf.Ui.Controls.Button _interactModeButton;

    // Creation Tools Group (collapses fully in Interact mode)
    private readonly StackPanel _creationToolsPanel = new() { Orientation = Orientation.Horizontal };
    private readonly Wpf.Ui.Controls.Button _selectButton;
    private readonly Wpf.Ui.Controls.Button _shapesButton;
    private readonly Wpf.Ui.Controls.Button _textButton;
    private readonly Wpf.Ui.Controls.Button _undoButton;
    private readonly Wpf.Ui.Controls.Button _redoButton;
    private readonly Wpf.Ui.Controls.Button _moreButton;
    private readonly TextBlock _statusLabel;
    private readonly Border _statusBadge;

    // Interact Mode Live HUD Group (visible only in Interact mode)
    private readonly StackPanel _interactHudPanel = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
    private readonly TextBlock _interactStatusLabel;

    // Controls Cheatsheet Button
    private readonly Wpf.Ui.Controls.Button _controlsButton;

    // Contextual Properties Controls
    private readonly StackPanel _colorSwatchesPanel = new() { Orientation = Orientation.Horizontal };
    private Wpf.Ui.Controls.Button _strokeButton = null!;
    private Wpf.Ui.Controls.Button _sizeButton = null!;
    private Wpf.Ui.Controls.Button _styleButton = null!;
    private Wpf.Ui.Controls.Button _fillButton = null!;

    // State tracking
    private bool _interactMode;
    private AnnotationMode _currentMode = AnnotationMode.Select;
    private AnnotationMode _lastShape = AnnotationMode.Rectangle;
    private AnnotationToolbarState? _state;
    private double _selectionRadius = 1200;
    private ContextMenu? _openMenu;

    // Events matching AnnotationToolbar & CanvasControlsButton
    public event Action<bool>? InteractionModeRequested;
    public event Action? ControlsClicked;
    public event Action<AnnotationMode>? ModeRequested;
    public event Action? UndoRequested, RedoRequested, EditRequested, DeleteRequested, RetrySaveRequested, ComposerRequested;
    public event Action? AiTeamsToggled;
    public event Action<double>? NearbyRequested;
    public event Action? WebResourceRequested, BackgroundsRequested, ProjectNavigatorRequested, ProjectAreasRequested;
    public event Action? ExportAllWorkflowsRequested, ImportWorkflowRequested;
    public event Action<string>? ColorRequested;
    public event Action<double>? StrokeRequested;
    public event Action<double>? FontSizeRequested;
    public event Action<AnnotationTextAlign>? TextAlignRequested;
    public event Action<AnnotationTextEmphasis>? EmphasisRequested;
    public event Action<AnnotationCardFill>? CardFillRequested;

    public bool HasOpenDropDown => _openMenu?.IsOpen == true;
    public bool IsInteractMode => _interactMode;
    public AnnotationMode CurrentAnnotationMode => _currentMode;
    public bool IsAiTeamsActive { get; private set; }
    public void SetAiTeamsActive(bool active) => IsAiTeamsActive = active;
    internal Wpf.Ui.Controls.Button ControlsButton => _controlsButton;
    internal Wpf.Ui.Controls.Button ArrangeModeButton => _arrangeModeButton;
    internal Wpf.Ui.Controls.Button InteractModeButton => _interactModeButton;
    internal Wpf.Ui.Controls.Button MoreButton => _moreButton;
    internal ContextMenu? OpenMoreMenu() { ShowMoreMenu(); return _openMenu; }
    internal StackPanel CreationToolsPanel => _creationToolsPanel;
    internal StackPanel InteractHudPanel => _interactHudPanel;
    internal Border PropertiesShelf => _propertiesShelf;

    public UnifiedDockControl()
    {
        if (Application.Current == null)
        {
            try { _ = new Application(); } catch { }
        }

        Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = ApplicationTheme.Dark });
        void AddResource(string path) => Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Wpf.Ui;component/{path}", UriKind.Absolute)
        });
        AddResource("Resources/Fonts.xaml");
        AddResource("Resources/Typography.xaml");
        AddResource("Resources/StaticColors.xaml");
        AddResource("Resources/Accent.xaml");
        AddResource("Resources/Palette.xaml");
        AddResource("Resources/Variables.xaml");
        AddResource("Controls/Button/Button.xaml");
        AddResource("Controls/Card/Card.xaml");
        AddResource("Controls/ContextMenu/ContextMenu.xaml");
        AddResource("Controls/Menu/MenuItem.xaml");

        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Foreground = Brush("#F1F5F9");

        // Outer translucent acrylic shell
        _shell = new Border
        {
            CornerRadius = new CornerRadius(18),
            BorderThickness = new Thickness(1),
            BorderBrush = Brush("#334155"),
            Background = new LinearGradientBrush(Color.FromArgb(235, 18, 24, 34), Color.FromArgb(235, 12, 17, 24), 90),
            Padding = new Thickness(6, 4, 6, 4),
            Effect = new DropShadowEffect
            {
                BlurRadius = 24,
                ShadowDepth = 5,
                Direction = 270,
                Color = Color.FromArgb(160, 0, 0, 0),
                Opacity = 0.55
            }
        };

        // Contextual Properties Shelf (top tier)
        _propertiesShelf = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            BorderBrush = Brush("#2D3748"),
            Background = Brush("#1A202C"),
            Padding = new Thickness(6, 3, 6, 3),
            Margin = new Thickness(0, 0, 0, 6),
            Visibility = Visibility.Collapsed
        };
        _propertiesShelf.Child = _propertiesRow;

        BuildPropertiesShelf();

        // 1. Mode Switcher (Segmented Capsule)
        var modeSegment = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = Brush("#0D131C"),
            BorderBrush = Brush("#1E293B"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var modeStack = new StackPanel { Orientation = Orientation.Horizontal };
        _arrangeModeButton = CreateModePill("Arrange", SymbolRegular.SlideLayout20, true, () => SetInteractMode(false, triggerEvent: true));
        _interactModeButton = CreateModePill("Interact", SymbolRegular.Flash20, false, () => SetInteractMode(true, triggerEvent: true));
        modeStack.Children.Add(_arrangeModeButton);
        modeStack.Children.Add(_interactModeButton);
        modeSegment.Child = modeStack;
        _mainRow.Children.Add(modeSegment);

        // 2. Creation Tools Group (collapses in Interact mode)
        _creationToolsPanel.VerticalAlignment = VerticalAlignment.Center;
        _creationToolsPanel.Children.Add(CreateDivider());

        _selectButton = CreateToolButton("Select", SymbolRegular.Cursor20, "Select and arrange items (V)", () => RequestMode(AnnotationMode.Select));
        _shapesButton = CreateToolButton("Shapes", SymbolRegular.Shapes20, "Draw shapes and arrows", ShowShapesMenu);
        _textButton = CreateToolButton("Note", SymbolRegular.Note20, "Add text note (T)", () => RequestMode(AnnotationMode.Text));
        _creationToolsPanel.Children.Add(_selectButton);
        _creationToolsPanel.Children.Add(_shapesButton);
        _creationToolsPanel.Children.Add(_textButton);

        _creationToolsPanel.Children.Add(CreateDivider());

        _undoButton = CreateIconButton(SymbolRegular.ArrowUndo20, "Undo (Ctrl+Z)", () => UndoRequested?.Invoke());
        _redoButton = CreateIconButton(SymbolRegular.ArrowRedo20, "Redo (Ctrl+Y)", () => RedoRequested?.Invoke());
        _creationToolsPanel.Children.Add(_undoButton);
        _creationToolsPanel.Children.Add(_redoButton);

        _creationToolsPanel.Children.Add(CreateDivider());

        _moreButton = CreateIconButton(SymbolRegular.MoreHorizontal20, "More actions…", ShowMoreMenu);
        _creationToolsPanel.Children.Add(_moreButton);

        _statusLabel = new TextBlock
        {
            Foreground = Brush("#94A3B8"),
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0)
        };
        _statusBadge = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = Brush("#1E293B"),
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(4, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = _statusLabel,
            Visibility = Visibility.Collapsed
        };
        _statusBadge.MouseLeftButtonUp += (_, _) => { if (_state?.Status != null) RetrySaveRequested?.Invoke(); };
        _creationToolsPanel.Children.Add(_statusBadge);

        _mainRow.Children.Add(_creationToolsPanel);

        // 3. Interact Live App HUD (visible when in Interact mode)
        _interactHudPanel.VerticalAlignment = VerticalAlignment.Center;
        _interactHudPanel.Margin = new Thickness(4, 0, 8, 0);

        var liveDot = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = Brush("#34D399"),
            Margin = new Thickness(4, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        _interactStatusLabel = new TextBlock
        {
            Text = "Click live app to interact  •  ↕ Drag top bar: move  •  Esc: Arrange",
            Foreground = Brush("#D1FAE5"),
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center
        };
        _interactHudPanel.Children.Add(liveDot);
        _interactHudPanel.Children.Add(_interactStatusLabel);
        _mainRow.Children.Add(_interactHudPanel);

        // 4. Controls Cheatsheet Button (Always available on right edge)
        _mainRow.Children.Add(CreateDivider());
        _controlsButton = new Wpf.Ui.Controls.Button
        {
            Icon = new SymbolIcon { Symbol = SymbolRegular.Keyboard20, FontSize = 16, Foreground = Brush("#CBD5E1") },
            Content = "Controls",
            Appearance = ControlAppearance.Transparent,
            Background = Brushes.Transparent,
            Foreground = Brush("#CBD5E1"),
            FontSize = 12,
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(2, 0, 0, 0),
            Cursor = Cursors.Hand,
            ToolTip = "Keyboard & Mouse Controls Cheatsheet"
        };
        _controlsButton.Click += (_, _) => ControlsClicked?.Invoke();
        _mainRow.Children.Add(_controlsButton);

        _dockStack.Children.Add(_propertiesShelf);
        _dockStack.Children.Add(_mainRow);
        _shell.Child = _dockStack;
        Content = _shell;

        // Default state
        UpdateModePillVisuals();
        UpdateToolSelectionVisuals();
    }

    private void BuildPropertiesShelf()
    {
        // Color swatches
        var colors = new[]
        {
            ("Gold", "#FBBF24"),
            ("White", "#FFFFFF"),
            ("Coral", "#FB7185"),
            ("Sky", "#60A5FA"),
            ("Mint", "#34D399"),
            ("Lilac", "#C084FC")
        };
        foreach (var (name, hex) in colors)
        {
            var swatch = new Border
            {
                Width = 16,
                Height = 16,
                CornerRadius = new CornerRadius(8),
                Background = Brush(hex),
                BorderBrush = Brush("#334155"),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(3, 0, 3, 0),
                Cursor = Cursors.Hand,
                ToolTip = name
            };
            swatch.MouseLeftButtonUp += (_, _) => ColorRequested?.Invoke(hex);
            _colorSwatchesPanel.Children.Add(swatch);
        }
        _propertiesRow.Children.Add(_colorSwatchesPanel);
        _propertiesRow.Children.Add(CreateDivider());

        // Stroke dropdown
        _strokeButton = CreatePropertyMenuButton("Stroke", ShowStrokeMenu);
        _propertiesRow.Children.Add(_strokeButton);

        // Size dropdown
        _sizeButton = CreatePropertyMenuButton("Size", ShowSizeMenu);
        _propertiesRow.Children.Add(_sizeButton);

        // Style dropdown
        _styleButton = CreatePropertyMenuButton("Style", ShowStyleMenu);
        _propertiesRow.Children.Add(_styleButton);

        // Fill dropdown
        _fillButton = CreatePropertyMenuButton("Fill", ShowFillMenu);
        _propertiesRow.Children.Add(_fillButton);
    }

    private Wpf.Ui.Controls.Button CreatePropertyMenuButton(string text, Action showMenu)
    {
        var btn = new Wpf.Ui.Controls.Button
        {
            Content = text,
            Appearance = ControlAppearance.Transparent,
            Background = Brushes.Transparent,
            Foreground = Brush("#E2E8F0"),
            FontSize = 11,
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(2, 0, 2, 0),
            Cursor = Cursors.Hand
        };
        btn.Click += (_, _) => showMenu();
        return btn;
    }

    private Wpf.Ui.Controls.Button CreateModePill(string label, SymbolRegular icon, bool active, Action onClick)
    {
        var btn = new Wpf.Ui.Controls.Button
        {
            Content = label,
            Icon = new SymbolIcon { Symbol = icon, FontSize = 14 },
            Appearance = ControlAppearance.Transparent,
            Padding = new Thickness(10, 4, 10, 4),
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            Cursor = Cursors.Hand
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private Wpf.Ui.Controls.Button CreateToolButton(string label, SymbolRegular icon, string hint, Action onClick)
    {
        var btn = new Wpf.Ui.Controls.Button
        {
            Content = label,
            Icon = new SymbolIcon { Symbol = icon, FontSize = 15, Foreground = Brush("#94A3B8") },
            Appearance = ControlAppearance.Transparent,
            Background = Brushes.Transparent,
            Foreground = Brush("#E2E8F0"),
            FontSize = 12,
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(2, 0, 2, 0),
            Cursor = Cursors.Hand,
            ToolTip = hint
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private Wpf.Ui.Controls.Button CreateIconButton(SymbolRegular icon, string hint, Action onClick)
    {
        var btn = new Wpf.Ui.Controls.Button
        {
            Icon = new SymbolIcon { Symbol = icon, FontSize = 16, Foreground = Brush("#CBD5E1") },
            Appearance = ControlAppearance.Transparent,
            Background = Brushes.Transparent,
            Foreground = Brush("#CBD5E1"),
            Width = 32,
            Height = 30,
            Padding = new Thickness(0),
            Margin = new Thickness(2, 0, 2, 0),
            Cursor = Cursors.Hand,
            ToolTip = hint
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private static Border CreateDivider() => new()
    {
        Width = 1,
        Height = 18,
        Background = Brush("#334155"),
        Margin = new Thickness(6, 0, 6, 0),
        VerticalAlignment = VerticalAlignment.Center
    };

    public void SetInteractMode(bool enabled, bool triggerEvent = false)
    {
        _interactMode = enabled;
        UpdateModePillVisuals();

        // Concept 1: Creation tools FULLY COLLAPSE in Interact mode
        if (_interactMode)
        {
            _creationToolsPanel.Visibility = Visibility.Collapsed;
            _interactHudPanel.Visibility = Visibility.Visible;
            _propertiesShelf.Visibility = Visibility.Collapsed;
        }
        else
        {
            _creationToolsPanel.Visibility = Visibility.Visible;
            _interactHudPanel.Visibility = Visibility.Collapsed;
            UpdatePropertiesVisibility();
        }

        if (triggerEvent)
        {
            InteractionModeRequested?.Invoke(enabled);
        }
    }

    private void UpdateModePillVisuals()
    {
        if (_interactMode)
        {
            // Interact active: Emerald green theme
            _interactModeButton.Background = Brush("#065F46");
            _interactModeButton.BorderBrush = Brush("#10B981");
            _interactModeButton.BorderThickness = new Thickness(1);
            _interactModeButton.Foreground = Brush("#A7F3D0");
            if (_interactModeButton.Icon is SymbolIcon interactIcon) interactIcon.Foreground = Brush("#34D399");

            // Arrange inactive
            _arrangeModeButton.Background = Brushes.Transparent;
            _arrangeModeButton.BorderThickness = new Thickness(0);
            _arrangeModeButton.Foreground = Brush("#94A3B8");
            if (_arrangeModeButton.Icon is SymbolIcon arrangeIcon) arrangeIcon.Foreground = Brush("#64748B");
        }
        else
        {
            // Arrange active: Fluent Blue theme
            _arrangeModeButton.Background = Brush("#1E3A8A");
            _arrangeModeButton.BorderBrush = Brush("#3B82F6");
            _arrangeModeButton.BorderThickness = new Thickness(1);
            _arrangeModeButton.Foreground = Brush("#DBEAFE");
            if (_arrangeModeButton.Icon is SymbolIcon arrangeIcon) arrangeIcon.Foreground = Brush("#60A5FA");

            // Interact inactive
            _interactModeButton.Background = Brushes.Transparent;
            _interactModeButton.BorderThickness = new Thickness(0);
            _interactModeButton.Foreground = Brush("#94A3B8");
            if (_interactModeButton.Icon is SymbolIcon interactIcon) interactIcon.Foreground = Brush("#64748B");
        }
    }

    private void RequestMode(AnnotationMode mode)
    {
        _currentMode = mode;
        UpdateToolSelectionVisuals();
        ModeRequested?.Invoke(mode);
    }

    private void UpdateToolSelectionVisuals()
    {
        bool isSelect = _currentMode == AnnotationMode.Select || _currentMode == AnnotationMode.Navigate;
        bool isText = _currentMode == AnnotationMode.Text;
        bool isShape = _currentMode is AnnotationMode.Rectangle or AnnotationMode.Ellipse or AnnotationMode.Arrow;

        HighlightButton(_selectButton, isSelect);
        HighlightButton(_textButton, isText);
        HighlightButton(_shapesButton, isShape);

        // Update Shapes icon & label
        if (isShape) _lastShape = _currentMode;
        _shapesButton.Content = _lastShape switch
        {
            AnnotationMode.Arrow => "Arrow",
            AnnotationMode.Ellipse => "Ellipse",
            _ => "Rectangle"
        };
        if (_shapesButton.Icon is SymbolIcon shapeIcon)
        {
            shapeIcon.Symbol = _lastShape switch
            {
                AnnotationMode.Arrow => SymbolRegular.ArrowUpRight20,
                AnnotationMode.Ellipse => SymbolRegular.Circle20,
                _ => SymbolRegular.Shapes20
            };
        }

        UpdatePropertiesVisibility();
    }

    private static void HighlightButton(Wpf.Ui.Controls.Button btn, bool active)
    {
        if (active)
        {
            btn.Background = Brush("#23334D");
            btn.BorderBrush = Brush("#3B82F6");
            btn.BorderThickness = new Thickness(1);
            btn.Foreground = Brush("#93C5FD");
            if (btn.Icon is SymbolIcon icon) icon.Foreground = Brush("#60A5FA");
        }
        else
        {
            btn.Background = Brushes.Transparent;
            btn.BorderThickness = new Thickness(0);
            btn.Foreground = Brush("#E2E8F0");
            if (btn.Icon is SymbolIcon icon) icon.Foreground = Brush("#94A3B8");
        }
    }

    public void UpdateState(AnnotationToolbarState state)
    {
        _state = state;
        _currentMode = state.Mode == AnnotationMode.Navigate ? AnnotationMode.Select : state.Mode;
        UpdateToolSelectionVisuals();

        _undoButton.IsEnabled = state.CanUndo;
        _undoButton.Opacity = state.CanUndo ? 1.0 : 0.4;
        _redoButton.IsEnabled = state.CanRedo;
        _redoButton.Opacity = state.CanRedo ? 1.0 : 0.4;

        // Status badge
        if (state.Status != null)
        {
            _statusLabel.Text = state.Status;
            _statusLabel.Foreground = Brush("#FBBF24");
            _statusBadge.Visibility = Visibility.Visible;
        }
        else if (state.SelectedCount > 0)
        {
            _statusLabel.Text = $"{state.SelectedCount} selected";
            _statusLabel.Foreground = Brush("#94A3B8");
            _statusBadge.Visibility = Visibility.Visible;
        }
        else
        {
            _statusBadge.Visibility = Visibility.Collapsed;
        }

        // Update Properties Shelf values
        _strokeButton.Content = $"{state.Stroke:0.#} px";
        _sizeButton.Content = $"Size {state.FontSize:0}";

        UpdatePropertiesVisibility();
    }

    private void UpdatePropertiesVisibility()
    {
        if (_interactMode)
        {
            _propertiesShelf.Visibility = Visibility.Collapsed;
            return;
        }

        bool shape = _currentMode is AnnotationMode.Rectangle or AnnotationMode.Ellipse or AnnotationMode.Arrow;
        bool text = _currentMode == AnnotationMode.Text || (_currentMode == AnnotationMode.Select && _state?.Selected?.Kind == AnnotationKind.Text);
        bool hasSelection = _state?.Selected != null;

        bool showProperties = shape || text || hasSelection;
        _propertiesShelf.Visibility = showProperties ? Visibility.Visible : Visibility.Collapsed;

        _strokeButton.Visibility = showProperties && !text ? Visibility.Visible : Visibility.Collapsed;
        _sizeButton.Visibility = text || (_currentMode == AnnotationMode.Arrow) ? Visibility.Visible : Visibility.Collapsed;
        _styleButton.Visibility = text || (_currentMode == AnnotationMode.Arrow) ? Visibility.Visible : Visibility.Collapsed;
        _fillButton.Visibility = text || (_state?.Selected?.Kind is AnnotationKind.Rectangle or AnnotationKind.Ellipse) ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetSelectionRadius(double radius) => _selectionRadius = Math.Clamp(radius, 1, 100000);

    public void CloseDropDowns()
    {
        if (_openMenu != null)
        {
            _openMenu.IsOpen = false;
            _openMenu = null;
        }
    }

    // --- Menus ---

    private void ShowShapesMenu()
    {
        CloseDropDowns();
        var menu = new ContextMenu();
        AddMenuItem(menu, "Rectangle", SymbolRegular.Square20, () => RequestMode(AnnotationMode.Rectangle));
        AddMenuItem(menu, "Ellipse", SymbolRegular.Circle20, () => RequestMode(AnnotationMode.Ellipse));
        AddMenuItem(menu, "Arrow", SymbolRegular.ArrowUpRight20, () => RequestMode(AnnotationMode.Arrow));
        OpenMenu(menu, _shapesButton);
    }

    private void ShowMoreMenu()
    {
        CloseDropDowns();
        var menu = new ContextMenu();
        var edit = AddMenuItem(menu, "Edit annotation", SymbolRegular.Edit20, () => EditRequested?.Invoke(), "Enter");
        edit.IsEnabled = _state?.Selected?.Kind is AnnotationKind.Text or AnnotationKind.Rectangle or AnnotationKind.Ellipse or AnnotationKind.Arrow;

        var delete = AddMenuItem(menu, "Delete annotation", SymbolRegular.Delete20, () => DeleteRequested?.Invoke(), "Delete");
        delete.IsEnabled = _state?.HasSelectedAnnotations == true || _state?.Selected != null;

        AddMenuItem(menu, _state?.Status == null ? "Save now" : "Retry saving", SymbolRegular.Save20, () => RetrySaveRequested?.Invoke());
        menu.Items.Add(new Separator());

        AddMenuItem(menu, "Select around camera", SymbolRegular.Target20, () => NearbyRequested?.Invoke(_selectionRadius));
        AddMenuItem(menu, "AI Compose…", SymbolRegular.Sparkle20, () => ComposerRequested?.Invoke());
        var aiTeams = AddMenuItem(menu, "AI Teams Mission Control", SymbolRegular.Bot20, () => AiTeamsToggled?.Invoke());
        aiTeams.IsCheckable = true;
        aiTeams.IsChecked = IsAiTeamsActive;
        AddMenuItem(menu, "Add web resource…", SymbolRegular.Globe20, () => WebResourceRequested?.Invoke());
        AddMenuItem(menu, "Backgrounds…", SymbolRegular.Image20, () => BackgroundsRequested?.Invoke());

        var nav = AddMenuItem(menu, "Project Navigator…", SymbolRegular.Navigation20, () => ProjectNavigatorRequested?.Invoke(), "P");
        var areas = AddMenuItem(menu, "Project areas…", SymbolRegular.Grid20, () => ProjectAreasRequested?.Invoke(), "Z");

        menu.Items.Add(new Separator());
        AddMenuItem(menu, "Export all workflows (JSON)", SymbolRegular.Share20, () => ExportAllWorkflowsRequested?.Invoke());
        AddMenuItem(menu, "Import workflow (JSON)…", SymbolRegular.FolderOpen20, () => ImportWorkflowRequested?.Invoke());

        OpenMenu(menu, _moreButton);
    }

    private void ShowStrokeMenu()
    {
        CloseDropDowns();
        var menu = new ContextMenu();
        foreach (double width in new[] { 1d, 2d, 3d, 4d, 6d, 8d, 12d, 16d, 24d, 32d })
        {
            double w = width;
            AddMenuItem(menu, $"{w:0} px", () => StrokeRequested?.Invoke(w));
        }
        OpenMenu(menu, _strokeButton);
    }

    private void ShowSizeMenu()
    {
        CloseDropDowns();
        var menu = new ContextMenu();
        foreach (double size in new[] { 12d, 18d, 24d, 32d, 40d, 56d, 72d, 96d, 128d, 200d })
        {
            double s = size;
            AddMenuItem(menu, $"Size {s:0}", () => FontSizeRequested?.Invoke(s));
        }
        OpenMenu(menu, _sizeButton);
    }

    private void ShowStyleMenu()
    {
        CloseDropDowns();
        var menu = new ContextMenu();
        AddMenuItem(menu, "Plain", () => EmphasisRequested?.Invoke(AnnotationTextEmphasis.Normal));
        AddMenuItem(menu, "Bold", () => EmphasisRequested?.Invoke(AnnotationTextEmphasis.Bold));
        AddMenuItem(menu, "Italic", () => EmphasisRequested?.Invoke(AnnotationTextEmphasis.Italic));
        AddMenuItem(menu, "Bold italic", () => EmphasisRequested?.Invoke(AnnotationTextEmphasis.BoldItalic));
        menu.Items.Add(new Separator());
        AddMenuItem(menu, "Align left", () => TextAlignRequested?.Invoke(AnnotationTextAlign.Left));
        AddMenuItem(menu, "Align center", () => TextAlignRequested?.Invoke(AnnotationTextAlign.Center));
        AddMenuItem(menu, "Align right", () => TextAlignRequested?.Invoke(AnnotationTextAlign.Right));
        OpenMenu(menu, _styleButton);
    }

    private void ShowFillMenu()
    {
        CloseDropDowns();
        var menu = new ContextMenu();
        AddMenuItem(menu, "Solid", () => CardFillRequested?.Invoke(AnnotationCardFill.Solid));
        AddMenuItem(menu, "Tinted", () => CardFillRequested?.Invoke(AnnotationCardFill.Tinted));
        AddMenuItem(menu, "Outline", () => CardFillRequested?.Invoke(AnnotationCardFill.None));
        OpenMenu(menu, _fillButton);
    }

    private MenuItem AddMenuItem(ContextMenu menu, string text, Action onClick)
    {
        var item = new MenuItem { Header = text, Cursor = Cursors.Hand };
        item.Click += (_, _) => onClick();
        menu.Items.Add(item);
        return item;
    }

    private MenuItem AddMenuItem(ContextMenu menu, string text, SymbolRegular icon, Action onClick, string? shortcut = null)
    {
        var item = new MenuItem
        {
            Header = text,
            Icon = new SymbolIcon { Symbol = icon, FontSize = 16 },
            InputGestureText = shortcut,
            Cursor = Cursors.Hand
        };
        item.Click += (_, _) => onClick();
        menu.Items.Add(item);
        return item;
    }

    private void OpenMenu(ContextMenu menu, FrameworkElement target)
    {
        _openMenu = menu;
        menu.PlacementTarget = target;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.Closed += (_, _) => { if (_openMenu == menu) _openMenu = null; };
        menu.IsOpen = true;
    }
}
