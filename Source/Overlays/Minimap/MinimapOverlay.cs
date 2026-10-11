using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

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
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_TOPMOST = 0x8;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private readonly Canvas _canvas;
    private readonly IScreens _screens;
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
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW
                        | WS_EX_TOPMOST | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation
    {
        get { return true; }
    }

    public MinimapOverlay(Canvas canvas, IInputRouter input, DesktopStateCache desktops, IScreens? screens = null,
        bool preferFlip = false)
    {
        _canvas = canvas;
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

        canvas.CameraChanged       += NotifyCanvasChanged;
        canvas.CollapseChanged     += OnWindowChanged;
        canvas.MaximizeChanged     += OnWindowChanged;
        canvas.PinnedChanged       += OnWindowChanged;
        canvas.FrontChanged        += OnFrontChanged;
        input.DragStarted          += BringToFront;
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
        _renderer.UpdateSnapshot(
            _orderedWindows,
            _canvas.GetWorldExtents(),
            _canvas.GetViewport(primary.Width, primary.Height));
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
            _canvas.CameraChanged -= NotifyCanvasChanged;
            _canvas.CollapseChanged -= OnWindowChanged;
            _canvas.MaximizeChanged -= OnWindowChanged;
            _canvas.PinnedChanged -= OnWindowChanged;
            _canvas.FrontChanged -= OnFrontChanged;
            _input.DragStarted -= BringToFront;
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
