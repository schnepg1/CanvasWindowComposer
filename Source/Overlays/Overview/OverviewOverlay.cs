using System;
using System.Drawing;
using System.Windows.Forms;

namespace CanvasDesktop;

/// <summary>
/// One per monitor: a borderless form + D3D11 swap chain. Thumbnail state
/// for the pass lives in the shared <see cref="OverviewThumbnails"/>.
/// </summary>
internal sealed class OverviewOverlay : Form
{
    private const float StandardDpi = 96f;

    public Screen Screen { get; }
    public int OriginX { get { return Screen.Bounds.X; } }
    public int OriginY { get { return Screen.Bounds.Y; } }

    public GridRenderer? Grid { get; private set; }
    private OverviewAnnotationWindow? _annotations;
    private bool _showTaskbar;
    private readonly OverviewTaskbarRegion _taskbarRegion = new();

    public void SetTaskbarVisible(bool visible)
    {
        _showTaskbar = visible;
        _taskbarRegion.Apply(Handle, Bounds, Screen.WorkingArea, visible);
        _annotations?.SetTaskbarVisible(Screen.WorkingArea, visible);
    }
    private CanvasZoomControls? _zoomControls;
    internal CanvasZoomControls? ZoomControls => _zoomControls;
    public Action<OverviewOverlay, bool>? OnSettingsRequested;
    private System.Collections.Generic.IReadOnlyList<IOverviewAnnotationLayer> _drawingLayers = Array.Empty<IOverviewAnnotationLayer>();

    public void SetDrawingLayers(System.Collections.Generic.IReadOnlyList<IOverviewAnnotationLayer> layers)
    {
        _drawingLayers = layers;
        _annotations?.SetDrawingLayers(layers);
    }

    public void ShowAnnotations(OverviewAnnotationSnapshot frame)
    {
        StageAnnotations(frame);
        PrepareAnnotations();
        FlushAnnotations();
    }

    public void StageAnnotations(OverviewAnnotationSnapshot frame)
    {
        if (_annotations == null)
        {
            _annotations = new OverviewAnnotationWindow(Handle);
            _annotations.SetDrawingLayers(_drawingLayers);
            _annotations.SetTaskbarVisible(Screen.WorkingArea, _showTaskbar);
        }
        _annotations.StageUpdate(frame);
        if (frame.ShowHints && frame.Intro == null)
        {
            if (_zoomControls == null)
            {
                _zoomControls = new CanvasZoomControls();
                _zoomControls.SettingsRequested += zoomOnly => OnSettingsRequested?.Invoke(this, zoomOnly);
            }
            _zoomControls.UpdateZoom(frame.Zoom);
            if (!_zoomControls.Visible) _zoomControls.Show(this);
            double scale = DeviceDpi / 96.0;
            _zoomControls.Location = new Point(OriginX + (int)Math.Round(20 * scale), OriginY + (int)Math.Round(20 * scale));
        }
        else _zoomControls?.Hide();
    }

    public void PrepareAnnotations() => _annotations?.PrepareFrame();
    public void FlushAnnotations() => _annotations?.FlushFrame();

    public void SetIntroOpacity(double opacity)
    {
        if (!IsHandleCreated || IsDisposed) return;
        PInvoke.SetLayeredWindowAttributes((HWND)Handle, new COLORREF(0),
            (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255), LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA);
    }

    public IntPtr HitPlaceholderLabel(int x, int y) => _annotations?.HitPlaceholderLabel(x, y) ?? IntPtr.Zero;

    public void HideZones()
    {
        _zoomControls?.Hide();
        _annotations?.Clear();
        _annotations?.Hide();
    }

    // Input forwarding callbacks (set by OverviewOverlay coordinator)
    public Action<OverviewOverlay, KeyEventArgs>? OnKey;
    public Action<OverviewOverlay, KeyEventArgs>? OnKeyReleased;
    public Action<OverviewOverlay, MouseEventArgs>? OnMouseButtonDown;
    public Action<OverviewOverlay, MouseEventArgs>? OnMouseMoved;
    public Action<OverviewOverlay, MouseEventArgs>? OnMouseButtonUp;
    public Action<OverviewOverlay, int, int, int, bool, bool, bool>? OnWheel;
    public Action<OverviewOverlay, MouseEventArgs>? OnMouseDoubleClicked;
    public Action<OverviewOverlay>? OnCaptureLost;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool WindowInteractionActive { get; set; }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // WS_EX_TOOLWINDOW (0x80): keep out of Alt-Tab / taskbar.
            // No WS_EX_LAYERED — Win10+ accepts WS_EX_TRANSPARENT alone for
            // cross-process click-through, so we skip the layered-window
            // compositing path. Click-through is toggled in SetClickThrough.
            // WS_EX_NOREDIRECTIONBITMAP was tried and broke DWM thumbnail
            // compositing (we're the destination of DwmRegisterThumbnail and
            // DWM needs the redirection surface to draw into).
            cp.ExStyle |= 0x80;
            return cp;
        }
    }

    public OverviewOverlay(Screen screen)
    {
        Text = "Canvas overview";
        Screen = screen;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(15, 15, 15);
        DoubleBuffered = true;
        KeyPreview = true;

        KeyDown += (s, e) => OnKey?.Invoke(this, e);
        KeyUp += (s, e) => OnKeyReleased?.Invoke(this, e);
        MouseDown += (s, e) => OnMouseButtonDown?.Invoke(this, e);
        MouseMove += (s, e) => OnMouseMoved?.Invoke(this, e);
        MouseUp += (s, e) => OnMouseButtonUp?.Invoke(this, e);
        MouseDoubleClick += (s, e) => OnMouseDoubleClicked?.Invoke(this, e);
        MouseCaptureChanged += (_, _) => { if (!Capture) OnCaptureLost?.Invoke(this); };
    }

    /// <summary>Ensure HWND and swap chain are created, sized to the monitor.</summary>
    public void Warmup(bool preferFlip = false)
    {
        var b = Screen.Bounds;
        Location = new Point(b.X, b.Y);
        Size = new Size(b.Width, b.Height);

        _ = Handle; // force HWND creation

        if (Grid == null)
        {
            Grid = new GridRenderer(preferFlip);
            if (!Grid.Initialize(Handle, b.Width, b.Height))
            {
                Grid.Dispose(); Grid = null;
                return;
            }
            using (var g = CreateGraphics())
                Grid.SetDpiScale(g.DpiX / StandardDpi);
            Grid.StartThread();
        }
    }

    public void SetClickThrough(bool enable)
    {
        if (!IsHandleCreated) return;
        HWND h = (HWND)Handle;
        int ex = PInvoke.GetWindowLong(h, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        int flag = (int)WINDOW_EX_STYLE.WS_EX_TRANSPARENT;
        int updated = enable ? (ex | flag) : (ex & ~flag);
        if (updated == ex) return;
        // HTTRANSPARENT only cascades within the same thread (per MSDN), so it
        // can't pass clicks to other apps' windows. WS_EX_TRANSPARENT does, via
        // DWM's input-routing layer.
        PInvoke.SetWindowLong(h, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, updated);
        PInvoke.SetWindowPos(h, HWND.Null, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED);
    }

    /// <summary>
    /// Apply mode-dependent ex-style flags in a single round-trip:
    /// <c>WS_EX_LAYERED</c> (kicks DWM composition — used in Panning to
    /// defeat background-throttle) and <c>WS_EX_NOACTIVATE</c> (prevents
    /// the form from becoming foreground).
    /// </summary>
    public void SetModeStyle(bool layered, bool noActivate)
    {
        if (!IsHandleCreated) return;
        HWND h = (HWND)Handle;
        int ex = PInvoke.GetWindowLong(h, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        int layeredFlag = (int)WINDOW_EX_STYLE.WS_EX_LAYERED;
        int noActivateFlag = (int)WINDOW_EX_STYLE.WS_EX_NOACTIVATE;
        int updated = ex;
        updated = layered    ? (updated | layeredFlag)    : (updated & ~layeredFlag);
        updated = noActivate ? (updated | noActivateFlag) : (updated & ~noActivateFlag);
        if (updated == ex) return;

        bool layeredJustAdded = (ex & layeredFlag) == 0 && layered;
        PInvoke.SetWindowLong(h, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, updated);
        if (layeredJustAdded)
        {
            // Without SetLayeredWindowAttributes after adding WS_EX_LAYERED the
            // window paints as fully transparent until attributes are set.
            PInvoke.SetLayeredWindowAttributes(h, new COLORREF(0), 255, LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA);
        }
        PInvoke.SetWindowPos(h, HWND.Null, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Grid?.RequestFrame();
    }

    protected override void WndProc(ref Message m)
    {
        if (WindowInteractionActive && m.Msg == 0x21) // WM_MOUSEACTIVATE
        {
            m.Result = (IntPtr)3; // MA_NOACTIVATE: leave real typing/shortcuts with the source app.
            return;
        }
        if (m.Msg is OverviewNavigationInput.MouseWheelMessage or OverviewNavigationInput.MouseHorizontalWheelMessage)
        {
            var wheel = OverviewNavigationInput.DecodeWheel(m.Msg, m.WParam, m.LParam);
            var client = PointToClient(new Point(wheel.ScreenX, wheel.ScreenY));
            OnWheel?.Invoke(this, client.X, client.Y, wheel.Delta, wheel.Horizontal, wheel.Control, wheel.Shift);
            m.Result = IntPtr.Zero;
            return;
        }
        if (m.Msg == (int)PInvoke.WM_DPICHANGED)
        {
            // lParam points to a RECT with the suggested new window rect at the new DPI
            var rect = System.Runtime.InteropServices.Marshal.PtrToStructure<RECT>(m.LParam);
            int w = rect.right - rect.left;
            int h = rect.bottom - rect.top;

            // Extract DPI from wParam (low word = X DPI, high word = Y DPI)
            int dpi = (int)((ulong)m.WParam.ToInt64() & 0xFFFF);

            PInvoke.SetWindowPos((HWND)Handle, HWND.Null, rect.left, rect.top, w, h,
                SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);

            Grid?.Resize(w, h);
            Grid?.SetDpiScale(dpi / StandardDpi);

            m.Result = IntPtr.Zero;
            return;
        }

        base.WndProc(ref m);
        if (m.Msg == 0x1A && IsHandleCreated) // WM_SETTINGCHANGE: taskbar position / auto-hide.
            SetTaskbarVisible(_showTaskbar);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _zoomControls?.Dispose();
            _zoomControls = null;
            _annotations?.Close();
            _annotations = null;
            var grid = Grid;
            Grid = null;
            try { grid?.Dispose(); }
            finally { base.Dispose(disposing); }
            return;
        }
        base.Dispose(disposing);
    }
}
