using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using FormsKeys = System.Windows.Forms.Keys;

namespace CanvasDesktop;

/// <summary>
/// A topmost, per-monitor DPI aware screen-space window hosting the Unified Fluid WPF Dock.
/// Floats at the bottom center of the active canvas display.
/// </summary>
internal sealed class UnifiedDockWindow : CanvasPanelWindow, IDisposable
{
    public UnifiedDockControl Panel { get; } = new();
    private ScreenRect _work, _lastPosition;
    private bool _disposing;
    private bool _canvasVisible;
    private bool _isClosed;

    public bool IsAlive => !_isClosed && !_disposing;

    /// <summary>Last laid-out window rect in physical pixels.</summary>
    internal ScreenRect Bounds => _lastPosition;

    /// <summary>
    /// Raised when a key is pressed while the dock owns keyboard focus, so canvas shortcuts keep
    /// working after the user clicks a dock button. Returns true when the canvas consumed the key.
    /// </summary>
    public event Func<FormsKeys, bool>? KeyPressed;

    public UnifiedDockWindow(ScreenRect work)
    {
        _work = work;
        Title = "Canvas Unified Dock";
        Content = Panel;
        SizeToContent = SizeToContent.WidthAndHeight;

        Closing += (_, e) =>
        {
            if (_disposing) return;
            e.Cancel = true;
            Hide();
        };
        Closed += (_, _) => _isClosed = true;

        Panel.LayoutUpdated += (_, _) =>
        {
            if (IsVisible && !_disposing && !_isClosed)
            {
                Position();
            }
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = (HWND)new WindowInteropHelper(this).Handle;
        int style = PInvoke.GetWindowLong(handle, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        // Ensure WS_EX_NOACTIVATE (0x08000000) is cleared so buttons and menus can receive mouse clicks,
        // but Alt-Tab remains suppressed via (style | 0x80) in CanvasPanelWindow base.
        PInvoke.SetWindowLong(handle, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, style & ~0x08000000);
    }

    public bool IsDockVisible => _canvasVisible && Visibility == Visibility.Visible;

    public void Present(bool canvasVisible, ScreenRect work = default, bool showTaskbar = true)
    {
        if (_disposing) return;
        if (work != default) _work = work;
        _canvasVisible = canvasVisible;

        if (_canvasVisible)
        {
            if (Visibility != Visibility.Visible)
            {
                Show();
            }
            Position(showTaskbar);
            RaiseWithoutActivation();
        }
        else
        {
            Panel.CloseDropDowns();
            if (Visibility == Visibility.Visible)
            {
                Hide();
            }
        }
    }

    public void RaiseWithoutActivation()
    {
        if (!IsVisible || _disposing) return;
        var handle = new WindowInteropHelper(this).EnsureHandle();
        if (handle == IntPtr.Zero) return;
        PInvoke.SetWindowPos((HWND)handle, (HWND)new IntPtr(-1), 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
    }

    public void Position(bool showTaskbar = true)
    {
        var handle = new WindowInteropHelper(this).EnsureHandle();
        if (handle == IntPtr.Zero || _disposing) return;

        Panel.Measure(new Size(Math.Max(100, _work.Width), Math.Max(100, _work.Height)));
        var desired = Panel.DesiredSize;

        var dpi = VisualTreeHelper.GetDpi(this);
        int width = (int)Math.Ceiling((desired.Width > 0 ? desired.Width : 600) * dpi.DpiScaleX);
        int height = (int)Math.Ceiling((desired.Height > 0 ? desired.Height : 50) * dpi.DpiScaleY);

        int left = Math.Max(_work.Left, _work.Left + (_work.Width - width) / 2);
        // Float 10 physical pixels above bottom of work area for modern floating island aesthetic
        int bottomMargin = (int)Math.Round(10 * dpi.DpiScaleY);
        int top = Math.Max(_work.Top, _work.Bottom - height - bottomMargin);

        var newPosition = new ScreenRect(left, top, width, height);
        if (_lastPosition == newPosition && !Panel.HasOpenDropDown) return;
        _lastPosition = newPosition;

        PInvoke.SetWindowPos((HWND)handle, (HWND)new IntPtr(-1), left, top, width, height,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled || KeyPressed == null) return;
        var keys = ToFormsKeys(e);
        if (keys == FormsKeys.None) return;
        e.Handled = KeyPressed.Invoke(keys);
    }

    /// <summary>
    /// Translates a WPF key event into the WinForms <see cref="FormsKeys"/> shape the canvas
    /// keyboard router already understands, so there is exactly one shortcut implementation.
    /// </summary>
    private static FormsKeys ToFormsKeys(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        int virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0) return FormsKeys.None;

        var keys = (FormsKeys)virtualKey;
        var modifiers = Keyboard.Modifiers;
        if ((modifiers & ModifierKeys.Control) != 0) keys |= FormsKeys.Control;
        if ((modifiers & ModifierKeys.Shift) != 0) keys |= FormsKeys.Shift;
        if ((modifiers & ModifierKeys.Alt) != 0) keys |= FormsKeys.Alt;
        return keys;
    }

    public void Dispose()
    {
        if (_disposing) return;
        _disposing = true;
        _isClosed = true;
        Panel.CloseDropDowns();
        try { Close(); } catch { }
    }
}
