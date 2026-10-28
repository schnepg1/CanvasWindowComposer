using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfButton = System.Windows.Controls.Button;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfCanvas = System.Windows.Controls.Canvas;

namespace CanvasDesktop;

/// <summary>A transparent editor over the actual canvas card, with a separate contextual toolbar.</summary>
internal sealed class AnnotationTextEditor : Window, IDisposable
{
    public WpfTextBox Editor { get; }
    public event Action<bool>? Finished;
    public CanvasAnnotation CurrentItem { get; private set; }
    internal bool ReturnFocusOnFinish { get; private set; } = true;
    internal Window FormattingToolbar => _toolbar;
    private readonly Window _toolbar;
    private readonly WpfCanvas _card;
    private readonly CardArtwork _artwork;
    private readonly ScrollViewer _viewport;
    private readonly WpfComboBox _size, _align, _fill;
    private readonly ToggleButton _bold, _italic;
    private readonly double _minimumHeight;
    private bool _updating, _finished, _disposed;
    private CanvasViewTransform _view = new(0, 0, 1, new(0, 0, 1920, 1080));
    private System.Drawing.Rectangle _workingArea;

    public new double FontSize { get => Editor.FontSize; set => SetFontSize(value); }
    public AnnotationTextAlign TextAlign { get => CurrentItem.TextAlign; set => SetTextAlign(value); }
    public AnnotationTextEmphasis Emphasis { get => CurrentItem.Emphasis; set => SetEmphasis(value); }
    public AnnotationCardFill CardFill { get => CurrentItem.CardFill; set => SetCardFill(value); }

    public AnnotationTextEditor(CanvasAnnotation item)
    {
        CurrentItem = item;
        _minimumHeight = item.Bounds.Height;
        Title = "Edit canvas text";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Editor = new WpfTextBox
        {
            Text = item.Text, AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap,
            MaxLength = AnnotationDocument.MaximumTextLength,
            FontFamily = AnnotationVisual.Typeface.FontFamily, FontSize = item.FontSize,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(item.Color)),
            CaretBrush = Brushes.White, SelectionBrush = Brushes.DodgerBlue,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(0), Margin = new Thickness(0), MinWidth = 0, MinHeight = 0,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            FocusVisualStyle = null, FlowDirection = FlowDirection.LeftToRight
        };
        TextOptions.SetTextFormattingMode(Editor, TextFormattingMode.Ideal);
        System.Windows.Automation.AutomationProperties.SetName(Editor, "Canvas text");
        var content = new FrameworkElementFactory(typeof(ScrollViewer));
        content.Name = "PART_ContentHost";
        content.SetValue(ScrollViewer.FocusableProperty, false);
        content.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        content.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        Editor.Template = new ControlTemplate(typeof(WpfTextBox)) { VisualTree = content };
        _card = new WpfCanvas { Background = Brushes.Transparent };
        _artwork = new CardArtwork(() => CurrentItem);
        _card.Children.Add(_artwork);
        _card.Children.Add(Editor);
        _viewport = new ScrollViewer
        {
            Content = _card, Background = Brushes.Transparent,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            CanContentScroll = false, Focusable = false
        };
        Content = _viewport;

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        _size = Combo("Text size", 68, new[] { 12d, 18d, 24d, 32d, 40d, 72d, 120d, 200d, 400d, 800d, 1600d });
        _size.IsEditable = true;
        _size.SelectionChanged += (_, _) => { if (!_updating && _size.SelectedItem is double size) SetFontSize(size); };
        void ApplySize()
        {
            if (double.TryParse(_size.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var size)) SetFontSize(size);
        }
        _size.LostKeyboardFocus += (_, _) => ApplySize();
        _size.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { ApplySize(); Editor.Focus(); e.Handled = true; } };
        _size.DropDownClosed += (_, _) => FocusEditor();
        row.Children.Add(_size);
        _bold = Toggle("B", "Bold · Ctrl+B", () => { ToggleBold(); FocusEditor(); });
        _italic = Toggle("I", "Italic · Ctrl+I", () => { ToggleItalic(); FocusEditor(); });
        row.Children.Add(_bold); row.Children.Add(_italic);
        var color = Combo("Text color", 68, new[] { "Gold", "White", "Coral", "Sky", "Mint", "Lilac" });
        string[] colors = ["#FBBF24", "#FFFFFF", "#FB7185", "#60A5FA", "#34D399", "#C084FC"];
        color.SelectedIndex = Array.IndexOf(colors, item.Color.ToUpperInvariant());
        color.SelectionChanged += (_, _) =>
        {
            if (color.SelectedIndex < 0) return;
            CurrentItem = CurrentItem with { Color = colors[color.SelectedIndex] };
            Editor.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(CurrentItem.Color));
            _artwork.InvalidateVisual();
        };
        color.DropDownClosed += (_, _) => FocusEditor();
        row.Children.Add(color);
        _align = Combo("Text alignment", 78, new[] { "Left", "Center", "Right" });
        _align.SelectionChanged += (_, _) => { if (!_updating && _align.SelectedIndex >= 0) SetTextAlign((AnnotationTextAlign)_align.SelectedIndex); };
        _align.DropDownClosed += (_, _) => FocusEditor();
        row.Children.Add(_align);
        _fill = Combo("Card background", 84, new[] { "Solid", "Tinted", "Outline" });
        _fill.IsEnabled = item.Kind == AnnotationKind.Text;
        _fill.SelectionChanged += (_, _) => { if (!_updating && _fill.SelectedIndex >= 0) SetCardFill((AnnotationCardFill)_fill.SelectedIndex); };
        _fill.DropDownClosed += (_, _) => FocusEditor();
        row.Children.Add(_fill);
        row.Children.Add(ActionButton("Done", "Finish editing · Ctrl+Enter", () => Complete(true)));
        row.Children.Add(ActionButton("×", "Cancel changes · Esc", () => Complete(false)));
        var panel = new StackPanel();
        panel.Children.Add(row);
        panel.Children.Add(new TextBlock
        {
            Text = "Click outside to finish · Esc cancels · Corners scale text",
            Foreground = Brushes.LightSlateGray, FontSize = 11, Margin = new Thickness(3, 7, 0, 0)
        });
        _toolbar = new Window
        {
            Title = "Text formatting", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false,
            Topmost = true, ShowActivated = false, SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(26, 33, 48)),
                BorderBrush = Brushes.SlateGray, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(10), Child = panel
            }
        };
        _toolbar.Deactivated += (_, _) => QueueFinishOnFocusLeave();
        _toolbar.DpiChanged += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Render, (Action)(() =>
        {
            if (!_disposed) PositionToolbar();
        }));
        _toolbar.PreviewKeyDown += HandleKey;
        Deactivated += (_, _) => QueueFinishOnFocusLeave();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Render, (Action)(() =>
        {
            if (!_disposed) UpdateProjection();
        }));
        PreviewKeyDown += HandleKey;
        Editor.TextChanged += (_, _) => UpdateCard();
        Editor.SelectionChanged += (_, _) =>
        {
            if (IsVisible) Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)(() =>
            {
                if (_disposed) return;
                Editor.BringIntoView(Editor.GetRectFromCharacterIndex(Editor.CaretIndex));
            }));
        };
        ContentRendered += (_, _) => { Editor.Focus(); Editor.CaretIndex = Editor.Text.Length; };
        Closing += (_, e) => { if (!_disposed) { e.Cancel = true; Complete(false); } };
        System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(this);
        System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(_toolbar);
        SyncStyle();
        UpdateCard();
    }

    private static WpfComboBox Combo(string name, double width, System.Collections.IEnumerable values)
    {
        var combo = new WpfComboBox { Width = width, Height = 28, Margin = new Thickness(0, 0, 5, 0), ItemsSource = values, ToolTip = name };
        System.Windows.Automation.AutomationProperties.SetName(combo, name);
        return combo;
    }
    private static ToggleButton Toggle(string text, string hint, Action action)
    {
        var button = new ToggleButton { Content = text, ToolTip = hint, Width = 29, Height = 28, Margin = new Thickness(0, 0, 5, 0), Focusable = false };
        button.Click += (_, _) => action();
        System.Windows.Automation.AutomationProperties.SetName(button, hint);
        return button;
    }
    private static WpfButton ActionButton(string text, string hint, Action action)
    {
        var button = new WpfButton { Content = text, ToolTip = hint, MinWidth = 28, Height = 28, Padding = new Thickness(7, 0, 7, 0), Margin = new Thickness(0, 0, 5, 0), Focusable = false };
        button.Click += (_, _) => action();
        System.Windows.Automation.AutomationProperties.SetName(button, hint);
        return button;
    }

    public void SetFontSize(double value)
    {
        value = double.IsFinite(value) ? Math.Clamp(value, AnnotationDocument.MinimumFontSize, AnnotationDocument.MaximumFontSize) : AnnotationDocument.DefaultFontSize;
        CurrentItem = CurrentItem with { FontSize = value };
        SyncStyle(); UpdateCard();
    }
    public void SetTextAlign(AnnotationTextAlign value)
    {
        if (!Enum.IsDefined(value)) throw new ArgumentException("Invalid text alignment.");
        CurrentItem = CurrentItem with { TextAlign = value }; SyncStyle(); UpdateCard();
    }
    public void SetCardFill(AnnotationCardFill value)
    {
        if (!Enum.IsDefined(value)) throw new ArgumentException("Invalid card fill.");
        CurrentItem = CurrentItem with { CardFill = value }; SyncStyle(); UpdateCard();
    }
    public void SetEmphasis(AnnotationTextEmphasis value)
    {
        if (!Enum.IsDefined(value)) throw new ArgumentException("Invalid text emphasis.");
        CurrentItem = CurrentItem with { Emphasis = value }; SyncStyle(); UpdateCard();
    }
    public void ToggleBold() => SetEmphasis((AnnotationTextEmphasis)((int)Emphasis ^ 1));
    public void ToggleItalic() => SetEmphasis((AnnotationTextEmphasis)((int)Emphasis ^ 2));
    private void SyncStyle()
    {
        _updating = true;
        Editor.FontSize = CurrentItem.FontSize;
        Editor.TextAlignment = AnnotationVisual.TextAlignmentFor(TextAlign);
        Editor.FontWeight = Emphasis is AnnotationTextEmphasis.Bold or AnnotationTextEmphasis.BoldItalic ? FontWeights.Bold : FontWeights.Normal;
        Editor.FontStyle = Emphasis is AnnotationTextEmphasis.Italic or AnnotationTextEmphasis.BoldItalic ? FontStyles.Italic : FontStyles.Normal;
        _bold.IsChecked = Editor.FontWeight == FontWeights.Bold;
        _italic.IsChecked = Editor.FontStyle == FontStyles.Italic;
        _size.Text = FontSize.ToString("0.##", CultureInfo.CurrentCulture);
        _align.SelectedIndex = (int)TextAlign; _fill.SelectedIndex = (int)CardFill;
        _updating = false;
    }
    private void UpdateCard()
    {
        CurrentItem = AnnotationVisual.FitText(CurrentItem with { Text = Editor.Text }, _minimumHeight);
        var bounds = CurrentItem.Bounds;
        _card.Width = _artwork.Width = bounds.Width;
        _card.Height = _artwork.Height = bounds.Height;
        var text = AnnotationVisual.TextBounds(CurrentItem);
        Editor.Width = text.Width; Editor.Height = text.Height;
        WpfCanvas.SetLeft(Editor, text.X); WpfCanvas.SetTop(Editor, text.Y);
        _artwork.InvalidateVisual();
        UpdateProjection();
    }

    /// <summary>Project canvas units directly to physical screen pixels, independent of monitor DPI.</summary>
    internal void SetPlacement(CanvasViewTransform view, System.Drawing.Rectangle workingArea)
    {
        _view = view; _workingArea = workingArea; UpdateProjection();
    }
    private void UpdateProjection()
    {
        var dpi = IsVisible ? VisualTreeHelper.GetDpi(this) : new DpiScale(_view.DpiX, _view.DpiY);
        _card.LayoutTransform = new ScaleTransform(_view.Zoom / dpi.DpiScaleX, _view.Zoom / dpi.DpiScaleY);
        var point = _view.WorldToVirtual(new CanvasPoint(CurrentItem.Bounds.X, CurrentItem.Bounds.Y));
        double width = CurrentItem.Bounds.Width * _view.Zoom;
        double height = CurrentItem.Bounds.Height * _view.Zoom;
        if (!_workingArea.IsEmpty) height = Math.Min(height, Math.Max(40, _workingArea.Bottom - point.Y));
        Width = Math.Max(1, width / dpi.DpiScaleX);
        Height = Math.Max(1, height / dpi.DpiScaleY);
        Left = point.X / dpi.DpiScaleX; Top = point.Y / dpi.DpiScaleY;
        if (IsVisible)
        {
            PInvoke.SetWindowPos((HWND)new WindowInteropHelper(this).Handle, (HWND)new IntPtr(-1),
                (int)Math.Round(point.X), (int)Math.Round(point.Y), (int)Math.Ceiling(width), (int)Math.Ceiling(height),
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
            PositionToolbar();
        }
    }
    private void PositionToolbar()
    {
        if (!_toolbar.IsVisible || _workingArea.IsEmpty) return;
        var dpi = VisualTreeHelper.GetDpi(_toolbar);
        var point = _view.WorldToVirtual(new CanvasPoint(CurrentItem.Bounds.X, CurrentItem.Bounds.Y));
        int width = (int)Math.Ceiling(_toolbar.ActualWidth * dpi.DpiScaleX);
        int height = (int)Math.Ceiling(_toolbar.ActualHeight * dpi.DpiScaleY);
        int left = Math.Clamp((int)Math.Round(point.X), _workingArea.Left, Math.Max(_workingArea.Left, _workingArea.Right - width));
        int top = (int)Math.Round(point.Y) - height - 8;
        if (top < _workingArea.Top) top = (int)Math.Ceiling(point.Y + CurrentItem.Bounds.Height * _view.Zoom) + 8;
        top = Math.Clamp(top, _workingArea.Top, Math.Max(_workingArea.Top, _workingArea.Bottom - height));
        PInvoke.SetWindowPos((HWND)new WindowInteropHelper(_toolbar).Handle, (HWND)new IntPtr(-1), left, top, width, height, SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
    }
    public void Show(System.Windows.Forms.Form owner)
    {
        new WindowInteropHelper(this).Owner = owner.Handle;
        Show();
    }
    public new void Show()
    {
        base.Show();
        _toolbar.Owner = this; _toolbar.Show(); _toolbar.UpdateLayout();
        UpdateProjection(); Editor.Focus();
    }
    private void FocusEditor()
    {
        if (_disposed) return;
        Activate(); Editor.Focus();
    }
    private void QueueFinishOnFocusLeave()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)(() =>
        {
            if (!_disposed && !_finished && !IsActive && !_toolbar.IsActive) Complete(true, returnFocus: false);
        }));
    }
    internal void Complete(bool save, bool returnFocus = true)
    {
        if (_finished || _disposed) return;
        ReturnFocusOnFinish = returnFocus; _finished = true; Finished?.Invoke(save);
    }
    private void HandleKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        bool control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (e.Key == Key.Escape || (control && e.Key == Key.Enter))
        { e.Handled = true; Complete(e.Key == Key.Enter); return; }
        if (!control) return;
        switch (e.Key)
        {
            case Key.D0: case Key.NumPad0: SetFontSize(AnnotationDocument.DefaultFontSize); break;
            case Key.B: ToggleBold(); break;
            case Key.I: ToggleItalic(); break;
            case Key.L: SetTextAlign(AnnotationTextAlign.Left); break;
            case Key.E: SetTextAlign(AnnotationTextAlign.Center); break;
            case Key.R: SetTextAlign(AnnotationTextAlign.Right); break;
            case Key.OemPlus: case Key.Add: SetFontSize(FontSize + 1); break;
            case Key.OemMinus: case Key.Subtract: SetFontSize(FontSize - 1); break;
            default: return;
        }
        e.Handled = true;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _toolbar.Close(); Close();
    }
    private sealed class CardArtwork(Func<CanvasAnnotation> item) : FrameworkElement
    {
        protected override void OnRender(DrawingContext dc)
        {
            AnnotationVisual.Draw(dc, item(), VisualTreeHelper.GetDpi(this).PixelsPerDip, drawText: false);
            dc.DrawRoundedRectangle(null, new Pen(Brushes.DeepSkyBlue, 1), new Rect(0, 0, ActualWidth, ActualHeight), 6, 6);
        }
    }
}
