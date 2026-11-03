using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace CanvasDesktop;

/// <summary>
/// One owned, nonactivating DWM destination. Never moves, resizes, reparents or
/// activates the source. Work here delegates to the existing interaction bridge.
/// </summary>
internal sealed class CanvasPeekWindow : Form
{
    private readonly IDwmThumbnailApi _dwm;
    private readonly Button _work = new(), _open = new(), _return = new();
    private IntPtr _thumbnail;
    private Rectangle _sourceCrop;
    private string _title = "";
    private string _status = "Preview only";
    private bool _animating;
    private Button? _pressed;
    public bool Working { get; private set; }
    public bool ShowingOwnedDialog { get; private set; }
    public bool Live => _thumbnail != IntPtr.Zero;
    public Rectangle ContentBounds { get; private set; }
    public event Action? WorkRequested, ReturnRequested, OpenRequested;
    public event Action<MouseEventArgs, bool, bool>? ContentPointer;
    public event Action? PreviewFailed;

    public IEnumerable<IntPtr> InputHandles
    {
        get { yield return Handle; foreach (Control control in Controls) yield return control.Handle; }
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x08000080; return cp; }
    }

    public CanvasPeekWindow(IDwmThumbnailApi dwm)
    {
        _dwm = dwm;
        Text = AccessibleName = "Canvas Peek";
        AccessibleDescription = "Expanded window preview. Work here enables interaction; Return preserves the canvas view.";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(20, 24, 33);
        ForeColor = Color.FromArgb(235, 241, 249);
        Font = new Font("Segoe UI", 9.25f);
        foreach (var button in new[] { _work, _open, _return })
        {
            button.FlatStyle = FlatStyle.Flat;
            button.BackColor = Color.FromArgb(30, 36, 50);
            button.ForeColor = ForeColor;
            button.FlatAppearance.BorderColor = Color.FromArgb(65, 78, 99);
            button.Cursor = Cursors.Hand;
            Controls.Add(button);
        }
        _work.Text = _work.AccessibleName = "Work here";
        _open.Text = _open.AccessibleName = "Open app";
        _return.Text = _return.AccessibleName = "Return";
        _return.AccessibleDescription = "Return to the unchanged canvas view without closing the application.";
        _work.Click += (_, _) => WorkRequested?.Invoke();
        _open.Click += (_, _) => OpenRequested?.Invoke();
        _return.Click += (_, _) => ReturnRequested?.Invoke();
    }

    public bool ShowPreview(OverviewOverlay owner, IntPtr source, Rectangle crop, string title,
        Rectangle bounds, bool animate)
    {
        _sourceCrop = crop;
        _title = title;
        Bounds = bounds;
        _animating = animate;
        Show(owner);
        if (source != IntPtr.Zero)
        {
            if (_dwm.Register((HWND)Handle, (HWND)source, out _thumbnail).Failed)
            { _thumbnail = IntPtr.Zero; return false; }
        }
        else _status = "Preview unavailable. Open app to continue.";
        LayoutPreview();
        return !Live || UpdateThumbnail();
    }

    public void SetFrame(Rectangle bounds, bool animating)
    {
        _animating = animating;
        Bounds = bounds;
        LayoutPreview();
        if (Live && !UpdateThumbnail()) PreviewFailed?.Invoke();
        Invalidate();
    }

    public void SetWorking(bool working, string? error = null)
    {
        Working = working;
        _status = error ?? (working ? (ShowingOwnedDialog ? "Finish or cancel this dialog to return" : "Working here · Return to resume the canvas")
            : Live ? "Preview only · Enter to work here" : "Preview unavailable · Choose Open app");
        LayoutPreview(); Invalidate();
    }

    public void UpdateSource(Rectangle crop)
    {
        if (_sourceCrop == crop) return;
        _sourceCrop = crop;
        LayoutPreview();
        if (Live && !UpdateThumbnail()) PreviewFailed?.Invoke();
    }

    public bool ChangeSource(IntPtr source, Rectangle crop, string title, bool ownedDialog)
    {
        var old = _thumbnail; _thumbnail = IntPtr.Zero;
        if (old != IntPtr.Zero) _dwm.Unregister(old);
        _sourceCrop = crop; _title = title; ShowingOwnedDialog = ownedDialog;
        if (_dwm.Register((HWND)Handle, (HWND)source, out _thumbnail).Failed)
        { _thumbnail = IntPtr.Zero; return false; }
        SetWorking(true);
        return UpdateThumbnail();
    }

    public void RaiseWithoutActivation()
    {
        if (Visible && !IsDisposed) PInvoke.SetWindowPos((HWND)Handle, HWND.Null,
            0, 0, 0, 0, SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
    }

    private void LayoutPreview()
    {
        int dpi = DeviceDpi;
        double scale = dpi / 96.0;
        int pad = Math.Max(4, (int)(8 * scale));
        int header = CanvasPeekLayout.HeaderHeight(dpi);
        int buttonHeight = (int)Math.Ceiling(28 * scale);
        int buttonWidth = Math.Max(1, Math.Min((int)(102 * scale), (ClientSize.Width - pad * 4) / 3));
        _return.SetBounds(ClientSize.Width - pad - buttonWidth, header - buttonHeight - pad,
            buttonWidth, buttonHeight);
        _open.SetBounds(_return.Left - pad - buttonWidth, _return.Top, buttonWidth, buttonHeight);
        _work.SetBounds(_open.Left - pad - buttonWidth, _return.Top, buttonWidth, buttonHeight);
        foreach (var button in new[] { _work, _open, _return }) button.Visible = !_animating;
        _work.Enabled = Live && !Working;
        _return.Enabled = !ShowingOwnedDialog;
        ContentBounds = CanvasPeekLayout.Fit(_sourceCrop.Size,
            new(1, header + 1, Math.Max(0, ClientSize.Width - 2), Math.Max(0, ClientSize.Height - header - 2)));
    }

    private bool UpdateThumbnail()
    {
        if (ContentBounds.IsEmpty) return true;
        var properties = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = PInvoke.DWM_TNP_RECTDESTINATION | PInvoke.DWM_TNP_RECTSOURCE | PInvoke.DWM_TNP_VISIBLE
                | PInvoke.DWM_TNP_OPACITY,
            rcDestination = new RECT(ContentBounds.Left, ContentBounds.Top, ContentBounds.Right, ContentBounds.Bottom),
            rcSource = new RECT(_sourceCrop.Left, _sourceCrop.Top, _sourceCrop.Right, _sourceCrop.Bottom),
            fVisible = true, fSourceClientAreaOnly = false, opacity = 255
        };
        return _dwm.Update(_thumbnail, properties).Succeeded;
    }

    // The interaction hook owns physical clicks while working. Keep the same
    // native, accessible buttons usable without forwarding their clicks to the app.
    public bool RouteChrome(Point screen, MouseButtons button, bool down, bool up)
    {
        var local = PointToClient(screen);
        if (ContentBounds.Contains(local)) return false;
        if (button == MouseButtons.Left)
        {
            if (down) _pressed = Array.Find(new[] { _work, _open, _return }, b => b.Visible && b.Enabled && b.Bounds.Contains(local));
            if (up)
            {
                var pressed = _pressed; _pressed = null;
                if (pressed != null && pressed.Bounds.Contains(local)) pressed.PerformClick();
            }
        }
        return true;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    { base.OnMouseDown(e); if (ContentBounds.Contains(e.Location)) ContentPointer?.Invoke(e, true, false); }
    protected override void OnMouseUp(MouseEventArgs e)
    { base.OnMouseUp(e); if (ContentBounds.Contains(e.Location)) ContentPointer?.Invoke(e, false, true); }
    protected override void OnMouseMove(MouseEventArgs e)
    { base.OnMouseMove(e); if (ContentBounds.Contains(e.Location)) ContentPointer?.Invoke(e, false, false); }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    { base.OnDpiChanged(e); LayoutPreview(); if (Live && !UpdateThumbnail()) PreviewFailed?.Invoke(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var border = new Pen(Working ? Color.MediumSpringGreen : Color.FromArgb(81, 105, 139), Working ? 2 : 1);
        e.Graphics.DrawRectangle(border, 1, 1, Math.Max(0, Width - 3), Math.Max(0, Height - 3));
        if (_animating) return;
        int pad = Math.Max(4, (int)(8 * DeviceDpi / 96.0));
        TextRenderer.DrawText(e.Graphics, _title, Font, new Rectangle(pad, pad, Math.Max(1, Width - 2 * pad), 22 * DeviceDpi / 96),
            ForeColor, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(e.Graphics, _status, Font, new Rectangle(pad, pad + 22 * DeviceDpi / 96,
            Math.Max(1, Width - 2 * pad), 22 * DeviceDpi / 96), ForeColor,
            TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        // Status is in the content's empty letterbox, never on top of DWM pixels.
        if (!Live) TextRenderer.DrawText(e.Graphics, _status, Font,
            new Rectangle(pad, CanvasPeekLayout.HeaderHeight(DeviceDpi) + pad, Math.Max(1, Width - pad * 2), Math.Max(1, Height / 2)),
            ForeColor, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        AccessibleDescription = _status;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x21) { m.Result = (IntPtr)3; return; } // MA_NOACTIVATE, including passive peeks.
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        var thumbnail = _thumbnail; _thumbnail = IntPtr.Zero;
        try { if (thumbnail != IntPtr.Zero) _dwm.Unregister(thumbnail); }
        finally { base.Dispose(disposing); }
    }
}
