using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace CanvasDesktop;

/// <summary>A model snapshot that can be browsed without calling into a window owner.</summary>
internal sealed class DesktopHandoffRecoveryMap : Control
{
    private readonly OverviewCamera _camera;
    private readonly CanvasCamera _initialCamera;
    private readonly Rectangle _screen;
    private readonly IReadOnlyList<(WorldRect Bounds, string Title)> _windows;
    private Point? _drag;

    public DesktopHandoffRecoveryMap(IScreens screens, CanvasCamera camera, double maximumZoom,
        IReadOnlyList<(WorldRect Bounds, string Title)> windows)
    {
        _camera = new(screens) { MaxZoom = Math.Max(camera.Zoom, maximumZoom) };
        _initialCamera = camera;
        var screen = screens.PrimaryBounds;
        _screen = new(screen.X, screen.Y, screen.Width, screen.Height);
        _windows = windows;
        BackColor = Color.FromArgb(27, 31, 39);
        ForeColor = Color.White;
        Cursor = Cursors.Hand;
        DoubleBuffered = true;
        AccessibleName = "Read-only canvas map";
        ResetView();
    }

    public CanvasCamera Camera => new(_camera.X, _camera.Y, _camera.Zoom);
    private double PixelsPerMapPixel =>
        Math.Max(_screen.Width / (double)Math.Max(1, ClientSize.Width), _screen.Height / (double)Math.Max(1, ClientSize.Height));

    public void ResetView()
    {
        _camera.SetTo(_initialCamera.X, _initialCamera.Y, _initialCamera.Zoom);
        Invalidate();
    }

    public void Zoom(double notches)
        => ZoomAt(new(ClientSize.Width / 2, ClientSize.Height / 2), notches);

    private void ZoomAt(Point point, double notches)
    {
        double scale = PixelsPerMapPixel;
        if (_camera.ZoomToCursor(_screen.X + (int)(point.X * scale), _screen.Y + (int)(point.Y * scale), notches))
            Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        double scale = PixelsPerMapPixel;
        using var fill = new SolidBrush(Color.FromArgb(55, 69, 89));
        using var border = new Pen(Color.FromArgb(119, 147, 184));
        foreach (var (world, title) in _windows)
        {
            var card = new RectangleF(
                (float)(((world.X - _camera.X) * _camera.Zoom - _screen.X) / scale),
                (float)(((world.Y - _camera.Y) * _camera.Zoom - _screen.Y) / scale),
                (float)(world.W * _camera.Zoom / scale), (float)(world.H * _camera.Zoom / scale));
            if (!card.IntersectsWith(ClientRectangle) || card.Width < 1 || card.Height < 1) continue;
            e.Graphics.FillRectangle(fill, card);
            e.Graphics.DrawRectangle(border, card.X, card.Y, card.Width, card.Height);
            if (card.Width > 26 && card.Height > Font.Height + 8)
                TextRenderer.DrawText(e.Graphics, title, Font,
                    new Rectangle((int)card.X + 4, (int)card.Y + 4, (int)card.Width - 8, Font.Height),
                    ForeColor, TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _drag = e.Location;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag is not { } start) return;
        double scale = PixelsPerMapPixel;
        _camera.PanByVirtual((e.X - start.X) * scale, (e.Y - start.Y) * scale);
        _drag = e.Location;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left) Capture = false;
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture) _drag = null;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        ZoomAt(e.Location, e.Delta / 120.0);
    }
}
