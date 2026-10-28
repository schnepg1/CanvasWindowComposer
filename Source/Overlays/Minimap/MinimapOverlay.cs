using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CanvasDesktop;

/// <summary>
/// Thin shell around <see cref="MinimapRenderer"/>. Canvas events translate
/// to a single <c>UpdateSnapshot</c> on the UI thread. Opacity has its own UI
/// timer, so fading does not require resubmitting unchanged GPU content.
/// </summary>
internal sealed class MinimapOverlay : Form
{
    private const int MapWidth = 240;
    private const int MapHeight = 160;
    private const int ScreenMargin = 20;
    private const int InnerPadding = 8;
    private const double MinimapOpacity = 0.75;

    // Hold without GPU work, then animate only the layered-window opacity.
    private const long HoldMs = 2000;
    private const long FadeMs = 500;
    private const double OpacityQuantum = 0.02;

    private const int WS_EX_LAYERED = 0x80000;

    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_TOPMOST = 0x8;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private readonly Canvas _canvas;
    private readonly IScreens _screens;
    private readonly OverviewManager? _overview;
    private MinimapProjection _projection;
    private MinimapProjection? _dragProjection;
    private CanvasCamera? _lastOverviewCamera;
    private bool _hovered, _pinned, _desktopGesture;
    private readonly ContextMenuStrip _mapMenu = new();
    private readonly ToolStripMenuItem _pinItem = new("Keep minimap visible");

    public bool Pinned => _pinned;
    public event Action? PinnedChanged;
    private readonly IInputRouter _input;
    private readonly DesktopStateCache _desktops;
    private volatile bool _closing;
    private readonly MinimapRenderer _renderer;
    private readonly Timer _fadeTimer;
    private bool _rendererInitialized;

    private long _touchTicks;
    private double _lastAppliedOpacity = -1;

    // Scratch buffer reused across snapshots, refilled per snapshot by
    // sorting Canvas.Windows by WorldRect.ZOrder descending (topmost first).
    private readonly List<WorldRect> _orderedWindows = new();
    private static readonly Comparison<WorldRect> ZOrderDescending = (a, b) => b.ZOrder.CompareTo(a.ZOrder);

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW
                        | WS_EX_TOPMOST | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation
    {
        get { return true; }
    }

    public MinimapOverlay(Canvas canvas, IInputRouter input, DesktopStateCache desktops, IScreens? screens = null,
        bool preferFlip = false, OverviewManager? overview = null)
    {
        _canvas = canvas;
        _overview = overview;
        _screens = screens ?? WinFormsScreens.Instance;
        _input = input; _desktops = desktops;
        _renderer = new MinimapRenderer(preferFlip);
        _fadeTimer = new Timer();
        _fadeTimer.Tick += (_, _) => ApplyFadeOnUi();

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        Size = new Size(MapWidth + InnerPadding * 2, MapHeight + InnerPadding * 2);
        BackColor = Color.Black;
        Opacity = MinimapOpacity;

        PositionOnScreen();
        Cursor = Cursors.Cross;
        _pinItem.Click += (_, _) => SetPinned(!_pinned);
        _mapMenu.Items.Add(_pinItem);
        ContextMenuStrip = _mapMenu;
        _mapMenu.Closed += (_, _) => { _hovered = ClientRectangle.Contains(PointToClient(Cursor.Position)); RestartFade(); };
        if (_overview != null)
        {
            _overview.ViewCameraChanged += OnViewCameraChanged;
            _overview.AfterModeChanged += OnModeChanged;
        }

        canvas.CameraChanged       += NotifyCanvasChanged;
        canvas.CollapseChanged     += OnWindowChanged;
        canvas.MaximizeChanged     += OnWindowChanged;
        canvas.PinnedChanged       += OnWindowChanged;
        canvas.FrontChanged        += OnFrontChanged;
        input.DragStarted          += RaiseWithoutActivation;
        desktops.AfterStateLoaded  += NotifyCanvasChanged;
    }

    /// <summary>Called on canvas changes + by <see cref="DesktopStateCache"/> after restore.</summary>
    public void NotifyCanvasChanged()
    {
        if (_closing || IsDisposed) return;
        PositionOnScreen();
        EnsureRendererInitialized();
        RebuildSnapshot();

        _touchTicks = Environment.TickCount64;
        _fadeTimer.Stop();
        _fadeTimer.Interval = (int)HoldMs;
        _fadeTimer.Start();
        Opacity = MinimapOpacity;
        _lastAppliedOpacity = MinimapOpacity;
        if (!Visible)
        {
            Show();
            _renderer.Start();
        }
    }

    /// <summary>
    /// Rebuild the z-order snapshot but leave visibility and fade timer alone.
    /// For events that shouldn't wake a hidden minimap (e.g. FrontChanged).
    /// </summary>
    private void RefreshSnapshotIfVisible()
    {
        if (_closing || IsDisposed || !Visible) return;
        EnsureRendererInitialized();
        RebuildSnapshot();
    }

    private void RebuildSnapshot()
    {
        _orderedWindows.Clear();
        foreach (var kv in _canvas.Windows)
            if (kv.Value.State == CanvasDesktop.WindowState.Normal && !kv.Value.PinnedToScreen)
                _orderedWindows.Add(kv.Value);
        _orderedWindows.Sort(ZOrderDescending);

        var primary = _screens.PrimaryBounds;
        var viewport = _overview?.CurrentMode == OverviewMode.Zooming
            ? OverviewViewport(_overview.ViewCamera, primary)
            : _canvas.GetViewport(primary.Width, primary.Height);
        var extents = _canvas.GetWorldExtents();
        _projection = _dragProjection ?? MinimapProjection.Create(extents, viewport, MapWidth, MapHeight);
        _renderer.UpdateSnapshot(_orderedWindows, extents, viewport, fixedProjection: _projection);
    }

    private void EnsureRendererInitialized()
    {
        if (_rendererInitialized) return;
        _ = Handle; // force HWND
        MinimapRenderer.CompileShaders();
        _rendererInitialized = _renderer.Initialize(Handle, Width, Height,
            mapOriginX: InnerPadding, mapOriginY: InnerPadding, mapW: MapWidth, mapH: MapHeight);
        if (_rendererInitialized)
        {
            _renderer.StartThread();
        }
    }

    private void ApplyFadeOnUi()
    {
        if (_closing || IsDisposed) return;
        _fadeTimer.Interval = 16;

        if (_pinned || _hovered || _dragProjection.HasValue || _mapMenu.Visible) { _fadeTimer.Stop(); return; }
        long elapsed = Environment.TickCount64 - _touchTicks;
        double fadeProgress = Math.Clamp((elapsed - HoldMs) / (double)FadeMs, 0.0, 1.0);
        double target = MinimapOpacity * (1.0 - fadeProgress);

        if (target > 0.0 && Math.Abs(target - _lastAppliedOpacity) < OpacityQuantum) return;

        _lastAppliedOpacity = target;
        if (target <= 0.0)
        {
            if (Visible) Hide();
            _renderer.Stop();
            _fadeTimer.Stop();
        }
        else
        {
            Opacity = target;
        }
    }

    private void PositionOnScreen()
    {
        var screen = _screens.PrimaryWorkingArea;
        Location = new Point(
            screen.Right - Width - ScreenMargin,
            screen.Bottom - Height - ScreenMargin
        );
    }

    internal static (double x, double y, double w, double h) OverviewViewport(CanvasCamera camera, ScreenRect primary)
        => (camera.X + primary.X / camera.Zoom, camera.Y + primary.Y / camera.Zoom,
            primary.Width / camera.Zoom, primary.Height / camera.Zoom);

    public void SetPinned(bool pinned)
    {
        if (_pinned == pinned) return;
        _pinned = pinned;
        _pinItem.Checked = pinned;
        PinnedChanged?.Invoke();
        NotifyCanvasChanged();
    }

    private void RestartFade()
    {
        if (_closing || IsDisposed || !Visible || _pinned || _hovered || _dragProjection.HasValue || _mapMenu.Visible) return;
        _touchTicks = Environment.TickCount64;
        _fadeTimer.Stop(); _fadeTimer.Interval = (int)HoldMs; _fadeTimer.Start();
    }

    private void OnViewCameraChanged()
    {
        if (_overview?.CurrentMode != OverviewMode.Zooming) return;
        var camera = _overview.ViewCamera;
        if (_lastOverviewCamera == camera) return;
        _lastOverviewCamera = camera;
        NotifyCanvasChanged();
    }

    private void OnModeChanged(OverviewMode from, OverviewMode to)
    {
        _lastOverviewCamera = to == OverviewMode.Zooming ? _overview?.ViewCamera : null;
        FinishDrag();
        if (Capture) Capture = false;
        NotifyCanvasChanged();
        RaiseWithoutActivation();
    }

    private void RaiseWithoutActivation()
    {
        if (_closing || IsDisposed || !IsHandleCreated) return;
        PInvoke.SetWindowPos((HWND)Handle, HWND.Null, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x21) { m.Result = (IntPtr)3; return; } // MA_NOACTIVATE
        base.WndProc(ref m);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e); _hovered = true; _fadeTimer.Stop();
        Opacity = MinimapOpacity; _lastAppliedOpacity = MinimapOpacity;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e); _hovered = false; RestartFade();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!_input.Enabled || _overview?.CanvasInputSuspended == true || e.Button != MouseButtons.Left || !new Rectangle(InnerPadding, InnerPadding, MapWidth, MapHeight).Contains(e.Location)
            || !_projection.IsValid) return;
        _dragProjection = _projection; _desktopGesture = _overview?.CurrentMode != OverviewMode.Zooming;
        Capture = true; _fadeTimer.Stop();
        Navigate(e.Location);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragProjection.HasValue && Capture) Navigate(e.Location);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || !_dragProjection.HasValue) return;
        Navigate(e.Location); FinishDrag(); Capture = false;
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture && _dragProjection.HasValue) FinishDrag();
    }

    private void FinishDrag()
    {
        if (!_dragProjection.HasValue) return;
        _dragProjection = null;
        if (_desktopGesture) _canvas.Commit();
        _desktopGesture = false;
        if (!_closing && !IsDisposed) { RebuildSnapshot(); RestartFade(); }
    }

    private void Navigate(Point point)
    {
        if (!_input.Enabled || _overview?.CanvasInputSuspended == true || _dragProjection is not { } projection) return;
        var local = new PointF(Math.Clamp(point.X - InnerPadding, 0, MapWidth), Math.Clamp(point.Y - InnerPadding, 0, MapHeight));
        var (x, y) = projection.WorldFromMap(local);
        if (_overview?.CurrentMode == OverviewMode.Zooming) _overview.CenterViewOnMinimap(x, y);
        else { var screen = _screens.PrimaryBounds; _canvas.CenterOn(x, y, 0, 0, screen.Width, screen.Height); }
    }

    private void OnWindowChanged(IntPtr _) => NotifyCanvasChanged();
    private void OnFrontChanged(IntPtr _) => RefreshSnapshotIfVisible();

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        _renderer?.RequestFrame();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_rendererInitialized) _renderer.Resize(ClientSize.Width, ClientSize.Height);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_closing)
        {
            _closing = true;
            if (_overview != null) { _overview.ViewCameraChanged -= OnViewCameraChanged; _overview.AfterModeChanged -= OnModeChanged; }
            _mapMenu.Dispose();
            _canvas.CameraChanged -= NotifyCanvasChanged;
            _canvas.CollapseChanged -= OnWindowChanged;
            _canvas.MaximizeChanged -= OnWindowChanged;
            _canvas.PinnedChanged -= OnWindowChanged;
            _canvas.FrontChanged -= OnFrontChanged;
            _input.DragStarted -= RaiseWithoutActivation;
            _desktops.AfterStateLoaded -= NotifyCanvasChanged;
            _renderer.OnFrameTick = null;
            _fadeTimer.Dispose();
            try { _renderer.Dispose(); }
            finally { base.Dispose(disposing); }
            return;
        }
        base.Dispose(disposing);
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
}
