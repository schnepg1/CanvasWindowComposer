using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using CanvasDesktop.Dictation;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfButton = System.Windows.Controls.Button;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfCanvas = System.Windows.Controls.Canvas;
using WpfPath = System.Windows.Shapes.Path;
using WpfRectangle = System.Windows.Shapes.Rectangle;

namespace CanvasDesktop;

/// <summary>A transparent editor over the actual canvas card, with a separate contextual toolbar.</summary>
internal sealed class AnnotationTextEditor : Window, IDisposable
{
    private const string DefaultHintText = "Click outside to finish · Esc cancels · Corners scale text";
    private static readonly TimeSpan DictationErrorResetDelay = TimeSpan.FromSeconds(6);
    private static readonly SolidColorBrush RecordingBackgroundBrush = CreateFrozenBrush(Color.FromRgb(0xDC, 0x26, 0x26));
    private static readonly SolidColorBrush RecordingBorderBrush = CreateFrozenBrush(Color.FromRgb(0xF8, 0x71, 0x71));
    private static readonly Geometry MicGlyphGeometry = CreateFrozenGeometry(
        "M7,1.5 A2.2,2.2 0 0 1 9.2,3.7 L9.2,7.3 A2.2,2.2 0 0 1 4.8,7.3 L4.8,3.7 A2.2,2.2 0 0 1 7,1.5 Z M3,7 A4,4 0 0 0 11,7 M7,11 L7,13 M4.8,13 L9.2,13");
    private static readonly Geometry SpinnerArcGeometry = CreateFrozenGeometry("M6,1.2 A4.8,4.8 0 1 1 1.6,7.9");

    public WpfTextBox Editor { get; }
    public event Action<bool>? Finished;
    public CanvasAnnotation CurrentItem { get; private set; }
    internal bool ReturnFocusOnFinish { get; private set; } = true;
    internal Window FormattingToolbar => _toolbar;
    internal WpfButton DictationButton => _mic;
    internal TextBlock HintTextBlock => _hint;
    internal DictationState DictationState => _dictation.State;
    private readonly Window _toolbar;
    private readonly WpfCanvas _card;
    private readonly CardArtwork _artwork;
    private readonly ScrollViewer _viewport;
    private readonly WpfComboBox _size, _align, _fill, _border;
    private readonly ToggleButton _bold, _italic;
    private readonly WpfButton _mic;
    private readonly TextBlock _hint;
    private readonly double _minimumHeight;
    private readonly DictationSessionController _dictation;
    private readonly DispatcherTimer _hintResetTimer;
    private readonly object _snapshotGate = new();
    private DictationEditorSnapshot _editorSnapshot;
    private RotateTransform? _spinnerTransform;
    private bool _updating, _finished, _disposed, _applyingDictation, _showingDictationError, _toolbarControlFocused;
    private CanvasViewTransform _view = new(0, 0, 1, new(0, 0, 1920, 1080));
    private System.Drawing.Rectangle _workingArea;
    private CanvasPoint _projectionOffset;
    private DpiScale _projectionDpi;
    private bool _projectionInitialized;

    public new double FontSize { get => Editor.FontSize; set => SetFontSize(value); }
    public double Stroke { get => CurrentItem.Stroke; set => SetStroke(value); }
    public AnnotationTextAlign TextAlign { get => CurrentItem.TextAlign; set => SetTextAlign(value); }
    public AnnotationTextEmphasis Emphasis { get => CurrentItem.Emphasis; set => SetEmphasis(value); }
    public AnnotationCardFill CardFill { get => CurrentItem.CardFill; set => SetCardFill(value); }

    internal Func<bool>? ClampBelowTaskbar { get; set; }
    private bool ShouldClampBelowTaskbar => ClampBelowTaskbar?.Invoke() ?? true;

    public AnnotationTextEditor(CanvasAnnotation item, string? dictationServerUrl = null, IDictationClient? dictationClient = null)
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
        _size.LostKeyboardFocus += (_, _) => { if (!_disposed) ApplyPendingFontSize(); };
        _size.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                if (ApplyPendingFontSize()) FocusEditor();
                e.Handled = true;
            }
        };
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
        _border = Combo("Border width", 64, new[] { 1d, 2d, 3d, 4d, 6d, 8d, 12d, 16d, 24d, 32d, 64d });
        _border.SelectionChanged += (_, _) => { if (!_updating && _border.SelectedItem is double stroke) SetStroke(stroke); };
        _border.DropDownClosed += (_, _) => FocusEditor();
        row.Children.Add(_border);
        _align = Combo("Text alignment", 78, new[] { "Left", "Center", "Right" });
        _align.SelectionChanged += (_, _) => { if (!_updating && _align.SelectedIndex >= 0) SetTextAlign((AnnotationTextAlign)_align.SelectedIndex); };
        _align.DropDownClosed += (_, _) => FocusEditor();
        row.Children.Add(_align);
        _fill = Combo("Card background", 84, new[] { "Solid", "Tinted", "Outline" });
        _fill.IsEnabled = item.Kind == AnnotationKind.Text;
        _fill.SelectionChanged += (_, _) => { if (!_updating && _fill.SelectedIndex >= 0) SetCardFill((AnnotationCardFill)_fill.SelectedIndex); };
        _fill.DropDownClosed += (_, _) => FocusEditor();
        row.Children.Add(_fill);
        _mic = new WpfButton
        {
            MinWidth = 30,
            Height = 28,
            Padding = new Thickness(7, 0, 7, 0),
            Margin = new Thickness(0, 0, 5, 0),
            Focusable = false
        };
        _mic.Click += (_, _) => _ = ToggleDictationAsync();
        row.Children.Add(_mic);
        row.Children.Add(ActionButton("Done", "Finish editing · Ctrl+Enter", () => Complete(true)));
        row.Children.Add(ActionButton("×", "Cancel changes · Esc", () => Complete(false)));
        var panel = new StackPanel();
        panel.Children.Add(row);
        _hint = new TextBlock
        {
            Text = DefaultHintText,
            Foreground = Brushes.LightSlateGray,
            FontSize = 11,
            Margin = new Thickness(3, 7, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 560
        };
        panel.Children.Add(_hint);
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
        try { _toolbar.Owner = this; } catch { }
        CanvasZOrder.AttachWpfClampHook(this, () => ShouldClampBelowTaskbar);
        CanvasZOrder.AttachWpfClampHook(_toolbar, () => ShouldClampBelowTaskbar);
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
        var client = dictationClient ?? new LocalTranscriptionClient(dictationServerUrl);
        _editorSnapshot = new DictationEditorSnapshot(Editor.Text, IsFocused: false, Editor.SelectionStart, Editor.SelectionLength);
        _dictation = new DictationSessionController(
            client,
            ReadEditorSnapshot,
            AnnotationDocument.MaximumTextLength,
            ownsClient: dictationClient == null);
        _dictation.StateChanged += OnDictationStateChanged;
        _dictation.TextUpdated += OnDictationTextUpdated;
        _dictation.ErrorOccurred += OnDictationErrorOccurred;
        _hintResetTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = DictationErrorResetDelay
        };
        _hintResetTimer.Tick += (_, _) =>
        {
            _hintResetTimer.Stop();
            if (!_disposed && !_finished && _dictation.State == DictationState.Idle)
                RestoreDefaultHint();
        };
        ApplyDictationState(DictationState.Idle);

        Editor.GotFocus += (_, _) => { _toolbarControlFocused = false; RefreshEditorSnapshot(); };
        Editor.GotKeyboardFocus += (_, _) => { _toolbarControlFocused = false; RefreshEditorSnapshot(); };
        Editor.LostKeyboardFocus += (_, _) => RefreshEditorSnapshot();
        foreach (var control in new UIElement[] { _size, color, _border, _align, _fill })
        {
            control.GotFocus += (_, _) => { _toolbarControlFocused = true; RefreshEditorSnapshot(); };
            control.GotKeyboardFocus += (_, _) => { _toolbarControlFocused = true; RefreshEditorSnapshot(); };
        }
        Editor.TextChanged += (_, e) =>
        {
            RefreshEditorSnapshot();
            if (!_applyingDictation)
            {
                foreach (var change in e.Changes)
                    _dictation.NotifyExternalTextEdit(change.Offset, change.RemovedLength, change.AddedLength, Editor.Text);
            }
            UpdateCard();
        };
        Editor.SelectionChanged += (_, _) =>
        {
            RefreshEditorSnapshot();
            if (IsVisible) Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)(() =>
            {
                if (_disposed) return;
                BringCaretIntoView();
            }));
        };
        Closing += (_, e) =>
        {
            if (_disposed) return;
            // The owner's completion handler disposes this window. Defer it until
            // WPF has returned from Closing, when calling Close again is legal.
            e.Cancel = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)(() =>
            {
                if (!_disposed) Complete(false);
            }));
        };
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
    public void SetStroke(double value)
    {
        if (!double.IsFinite(value) || value < AnnotationDocument.MinimumStroke || value > AnnotationDocument.MaximumStroke)
            throw new ArgumentException("Invalid border width.");
        CurrentItem = CurrentItem with { Stroke = value };
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
        _border.SelectedItem = CurrentItem.Stroke;
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
        double cardWidth = CurrentItem.Bounds.Width * _view.Zoom;
        double cardHeight = CurrentItem.Bounds.Height * _view.Zoom;
        var area = _workingArea.IsEmpty
            ? new Rect(_view.Monitor.X,_view.Monitor.Y,_view.Monitor.Width,_view.Monitor.Height)
            : new Rect(_workingArea.X,_workingArea.Y,_workingArea.Width,_workingArea.Height);
        // Keep the native window on-screen and below HWND dimension limits. The
        // full card lives inside a scrollable viewport, independently of its size.
        static (double Start,double Length) VisibleAxis(double start,double length,double areaStart,double areaLength)
        {
            double end = Math.Min(start+length,areaStart+areaLength);
            double visibleStart = Math.Max(start,areaStart);
            double visibleLength = end-visibleStart;
            if (visibleLength <= 0)
            {
                visibleLength = Math.Min(length,areaLength);
                visibleStart = Math.Clamp(start,areaStart,areaStart+areaLength-visibleLength);
            }
            return (visibleStart,Math.Clamp(visibleLength,1,32767));
        }
        var horizontal = VisibleAxis(point.X,cardWidth,area.Left,area.Width);
        var vertical = VisibleAxis(point.Y,cardHeight,area.Top,area.Height);
        double left = horizontal.Start, top = vertical.Start;
        double width = horizontal.Length, height = vertical.Length;
        if (_toolbar.IsVisible && _toolbar.ActualHeight > 0)
        {
            double toolbarSpace = _toolbar.ActualHeight*VisualTreeHelper.GetDpi(_toolbar).DpiScaleY+8;
            if (top-toolbarSpace < area.Top && top+height+toolbarSpace > area.Bottom)
            {
                // Keep the owned toolbar outside the scrolling text viewport so
                // BringIntoView never considers text hidden behind it visible.
                double newTop = Math.Min(area.Top+toolbarSpace,area.Bottom-1);
                height = Math.Max(1,top+height-newTop);
                top = newTop;
            }
        }
        var offset = new CanvasPoint(Math.Max(0,(left-point.X)/dpi.DpiScaleX),Math.Max(0,(top-point.Y)/dpi.DpiScaleY));
        bool resetOffset = !_projectionInitialized || offset != _projectionOffset || !dpi.Equals(_projectionDpi);
        Width = Math.Max(1, width / dpi.DpiScaleX);
        Height = Math.Max(1, height / dpi.DpiScaleY);
        Left = left / dpi.DpiScaleX; Top = top / dpi.DpiScaleY;
        if (IsVisible)
        {
            CanvasZOrder.PlaceAtCeiling(new WindowInteropHelper(this).Handle, ShouldClampBelowTaskbar,
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE,
                (int)Math.Round(left), (int)Math.Round(top), (int)Math.Ceiling(width), (int)Math.Ceiling(height));
            if (resetOffset)
            {
                _viewport.ScrollToHorizontalOffset(offset.X);
                _viewport.ScrollToVerticalOffset(offset.Y);
            }
            _projectionInitialized = true; _projectionOffset = offset; _projectionDpi = dpi;
            PositionToolbar();
        }
    }
    private void PositionToolbar()
    {
        if (!_toolbar.IsVisible || _workingArea.IsEmpty) return;
        var dpi = VisualTreeHelper.GetDpi(_toolbar);
        var point = new CanvasPoint(Left*VisualTreeHelper.GetDpi(this).DpiScaleX,Top*VisualTreeHelper.GetDpi(this).DpiScaleY);
        int width = (int)Math.Ceiling(_toolbar.ActualWidth * dpi.DpiScaleX);
        int height = (int)Math.Ceiling(_toolbar.ActualHeight * dpi.DpiScaleY);
        int left = Math.Clamp((int)Math.Round(point.X), _workingArea.Left, Math.Max(_workingArea.Left, _workingArea.Right - width));
        int top = (int)Math.Round(point.Y) - height - 8;
        if (top < _workingArea.Top) top = (int)Math.Ceiling(point.Y + ActualHeight*VisualTreeHelper.GetDpi(this).DpiScaleY) + 8;
        top = Math.Clamp(top, _workingArea.Top, Math.Max(_workingArea.Top, _workingArea.Bottom - height));
        CanvasZOrder.PlaceAtCeiling(new WindowInteropHelper(_toolbar).Handle, ShouldClampBelowTaskbar,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE, left, top, width, height);
    }
    public void Show(System.Windows.Forms.Form owner)
    {
        new WindowInteropHelper(this).Owner = owner.Handle;
        Show();
    }
    public new void Show()
    {
        base.Show();
        if (_toolbar.Owner != this)
        {
            try { _toolbar.Owner = this; }
            catch (InvalidOperationException)
            {
                var ownerHwnd = new WindowInteropHelper(this).Handle;
                if (ownerHwnd != IntPtr.Zero)
                    new WindowInteropHelper(_toolbar).Owner = ownerHwnd;
            }
        }
        _toolbar.Show(); _toolbar.UpdateLayout();
        UpdateProjection(); Editor.Focus();
        // Initialize before returning control to the caller. ContentRendered is
        // deferred and can otherwise overwrite a click/Home made before the
        // first frame, scrolling a large title back to its end.
        Editor.CaretIndex = Editor.Text.Length;
        RefreshEditorSnapshot();
    }
    private void FocusEditor()
    {
        if (_disposed) return;
        _toolbarControlFocused = false;
        Activate(); Editor.Focus();
    }
    private void BringCaretIntoView()
    {
        var caret = Editor.GetRectFromCharacterIndex(Editor.CaretIndex);
        double scale = _view.Zoom/VisualTreeHelper.GetDpi(this).DpiScaleY;
        double visibleHeight = _viewport.ViewportHeight/scale;
        // A giant title's line can exceed the entire screen. Keep its caret's
        // top reachable rather than bottom-aligning a line larger than the view.
        if (visibleHeight > 0 && caret.Height > visibleHeight) caret.Height = visibleHeight;
        Editor.BringIntoView(caret);
    }
    private void QueueFinishOnFocusLeave()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)(() =>
        {
            if (!_disposed && !_finished && !IsActive && !_toolbar.IsActive) Complete(true, returnFocus: false);
        }));
    }
    private bool ApplyPendingFontSize()
    {
        // Display rounding must not change a fractional size obtained by dragging a corner.
        if (_size.Text == FontSize.ToString("0.##", CultureInfo.CurrentCulture)) return true;
        if (!double.TryParse(_size.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var size)
            || !double.IsFinite(size) || size < AnnotationDocument.MinimumFontSize || size > AnnotationDocument.MaximumFontSize)
        {
            RejectCompletion($"Text size must be between {AnnotationDocument.MinimumFontSize} and {AnnotationDocument.MaximumFontSize}.");
            return false;
        }
        SetFontSize(size);
        return true;
    }
    internal void RejectCompletion(string message)
    {
        _finished = false;
        _showingDictationError = false;
        _hintResetTimer.Stop();
        _hint.Text = message;
        _hint.Foreground = Brushes.LightCoral;
        QueueRepositionToolbar();
    }
    internal bool Complete(bool save, bool returnFocus = true)
    {
        if (_finished || _disposed) return _finished;
        if (save && !ApplyPendingFontSize()) return false;
        ReturnFocusOnFinish = returnFocus;
        _finished = true;
        _hintResetTimer.Stop();
        _dictation.AbortInBackground();
        Finished?.Invoke(save);
        return _finished;
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
        _disposed = true;
        _hintResetTimer.Stop();
        StopSpinnerAnimation();
        _dictation.StateChanged -= OnDictationStateChanged;
        _dictation.TextUpdated -= OnDictationTextUpdated;
        _dictation.ErrorOccurred -= OnDictationErrorOccurred;
        _dictation.Dispose();
        _toolbar.Close(); Close();
    }

    internal Task ToggleDictationAsync()
    {
        if (_disposed || _finished) return Task.CompletedTask;
        bool editorFocused = IsEditorFocusedForDictation();
        CaptureEditorSnapshot(editorFocused);
        if (_dictation.State == DictationState.Recording && IsVisible)
            FocusEditor();
        return ToggleDictationCoreAsync();
    }

    private async Task ToggleDictationCoreAsync()
    {
        try
        {
            await _dictation.ToggleAsync().ConfigureAwait(false);
        }
        catch
        {
            // Expected protocol and transport errors are surfaced via ErrorOccurred.
        }
    }

    private bool IsEditorFocusedForDictation()
    {
        if (Editor.IsKeyboardFocusWithin)
            return true;
        if (_toolbarControlFocused || HasFocusableToolbarControlFocus())
            return false;
        return ReferenceEquals(FocusManager.GetFocusedElement(this), Editor);
    }

    private bool HasFocusableToolbarControlFocus()
    {
        if (Keyboard.FocusedElement is not DependencyObject focused || ReferenceEquals(focused, _toolbar))
            return false;

        DependencyObject? current = focused;
        while (current != null)
        {
            if (ReferenceEquals(current, _toolbar))
                return true;
            current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
        }
        return false;
    }

    private void RefreshEditorSnapshot()
        => CaptureEditorSnapshot(IsEditorFocusedForDictation());

    private void CaptureEditorSnapshot(bool isFocused)
    {
        var snapshot = new DictationEditorSnapshot(Editor.Text, isFocused, Editor.SelectionStart, Editor.SelectionLength);
        lock (_snapshotGate)
            _editorSnapshot = snapshot;
    }

    private DictationEditorSnapshot ReadEditorSnapshot()
    {
        lock (_snapshotGate)
            return _editorSnapshot;
    }

    private void OnDictationStateChanged(DictationState state)
        => DispatchUi(() =>
        {
            if (_disposed || _finished) return;
            ApplyDictationState(state);
            if ((state is DictationState.Starting or DictationState.Recording && _showingDictationError)
                || (state == DictationState.Idle && _showingDictationError && !_hintResetTimer.IsEnabled))
            {
                RestoreDefaultHint();
            }
            if (state is DictationState.Recording or DictationState.Idle && IsVisible)
                FocusEditor();
            QueueRepositionToolbar();
        });

    private void OnDictationTextUpdated(DictationTextUpdate update)
        => DispatchUi(() =>
        {
            if (_disposed || _finished) return;
            _applyingDictation = true;
            try
            {
                if (!string.Equals(Editor.Text, update.Text, StringComparison.Ordinal))
                    Editor.Text = update.Text;
                int caret = Math.Clamp(update.CaretIndex, 0, Editor.Text.Length);
                Editor.SelectionStart = caret;
                Editor.SelectionLength = 0;
                Editor.CaretIndex = caret;
                RefreshEditorSnapshot();
            }
            finally
            {
                _applyingDictation = false;
            }
            if (IsVisible)
                BringCaretIntoView();
        });

    private void OnDictationErrorOccurred(string message)
        => DispatchUi(() =>
        {
            if (_disposed || _finished || string.IsNullOrWhiteSpace(message)) return;
            _showingDictationError = true;
            _hint.Text = message;
            _hint.Foreground = Brushes.LightCoral;
            _hintResetTimer.Stop();
            _hintResetTimer.Start();
            QueueRepositionToolbar();
        });

    private void RestoreDefaultHint()
    {
        _showingDictationError = false;
        _hintResetTimer.Stop();
        _hint.Text = DefaultHintText;
        _hint.Foreground = Brushes.LightSlateGray;
        QueueRepositionToolbar();
    }

    private void ApplyDictationState(DictationState state)
    {
        StopSpinnerAnimation();

        switch (state)
        {
            case DictationState.Idle:
                _mic.IsEnabled = true;
                _mic.ClearValue(Control.BackgroundProperty);
                _mic.ClearValue(Control.BorderBrushProperty);
                _mic.ClearValue(Control.ForegroundProperty);
                _mic.Content = BuildMicGlyph();
                _mic.ToolTip = "Dictate text (local microphone transcription)";
                AutomationProperties.SetName(_mic, "Start dictation");
                break;

            case DictationState.Starting:
                _mic.IsEnabled = false;
                _mic.ClearValue(Control.BackgroundProperty);
                _mic.ClearValue(Control.BorderBrushProperty);
                _mic.ClearValue(Control.ForegroundProperty);
                _mic.Content = "…";
                _mic.ToolTip = "Starting dictation…";
                AutomationProperties.SetName(_mic, "Starting dictation");
                break;

            case DictationState.Recording:
                _mic.IsEnabled = true;
                _mic.Background = RecordingBackgroundBrush;
                _mic.BorderBrush = RecordingBorderBrush;
                _mic.Foreground = Brushes.White;
                _mic.Content = BuildRecordingContent();
                _mic.ToolTip = "Stop dictation";
                AutomationProperties.SetName(_mic, "Stop dictation");
                break;

            case DictationState.Finalizing:
                _mic.IsEnabled = false;
                _mic.ClearValue(Control.BackgroundProperty);
                _mic.ClearValue(Control.BorderBrushProperty);
                _mic.ClearValue(Control.ForegroundProperty);
                _mic.Content = BuildFinalizingContent();
                _mic.ToolTip = "Finalizing transcription…";
                AutomationProperties.SetName(_mic, "Finalizing transcription");
                break;
        }
    }

    private FrameworkElement BuildMicGlyph()
    {
        var path = new WpfPath
        {
            Data = MicGlyphGeometry,
            StrokeThickness = 1.4,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Width = 14,
            Height = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        path.SetBinding(Shape.StrokeProperty, new Binding(nameof(Control.Foreground)) { Source = _mic });
        return path;
    }

    private static FrameworkElement BuildRecordingContent()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(new WpfRectangle
        {
            Width = 8,
            Height = 8,
            RadiusX = 1.5,
            RadiusY = 1.5,
            Fill = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Stop",
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(5, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        return panel;
    }

    private FrameworkElement BuildFinalizingContent()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        _spinnerTransform = new RotateTransform(0, 6, 6);
        var spinner = new WpfPath
        {
            Data = SpinnerArcGeometry,
            StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 12,
            Height = 12,
            RenderTransform = _spinnerTransform,
            VerticalAlignment = VerticalAlignment.Center
        };
        spinner.SetBinding(Shape.StrokeProperty, new Binding(nameof(Control.Foreground)) { Source = _mic });
        var animation = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromMilliseconds(850)))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        _spinnerTransform.BeginAnimation(RotateTransform.AngleProperty, animation);
        panel.Children.Add(spinner);
        panel.Children.Add(new TextBlock
        {
            Text = "Finalizing…",
            Margin = new Thickness(5, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        return panel;
    }

    private void StopSpinnerAnimation()
    {
        if (_spinnerTransform == null) return;
        _spinnerTransform.BeginAnimation(RotateTransform.AngleProperty, null);
        _spinnerTransform = null;
    }

    private void DispatchUi(Action action)
    {
        if (_disposed) return;
        if (Dispatcher.CheckAccess())
        {
            action();
            return;
        }
        try
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, action);
        }
        catch (TaskCanceledException) { }
        catch (OperationCanceledException) { }
    }

    private void QueueRepositionToolbar()
    {
        if (_disposed || !_toolbar.IsVisible) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, (Action)(() =>
        {
            if (_disposed || !_toolbar.IsVisible) return;
            _toolbar.UpdateLayout();
            PositionToolbar();
        }));
    }

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Geometry CreateFrozenGeometry(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
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
