using System;
using System.Drawing;
using System.Windows.Forms;

namespace CanvasDesktop;

internal interface ICachedPreviewSurface : IDisposable
{
    IntPtr Handle { get; }
}

/// <summary>
/// A small, nonactivating, off-desktop DWM source for an immutable bitmap.
/// This preserves live/cached thumbnail interleaving in DWM registration order.
/// Bitmap ownership stays with the cache; dispose this surface before the bitmap.
/// </summary>
internal sealed class CachedPreviewWindow : Form, ICachedPreviewSurface
{
    private readonly Bitmap _image;
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x08000080; return cp; }
    }
    public CachedPreviewWindow(Bitmap image)
    {
        _image = image;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        // Beyond the current virtual screen, also on negative-origin desktops.
        var desktop = SystemInformation.VirtualScreen;
        Location = new Point(desktop.Left - image.Width - 32, desktop.Top - image.Height - 32);
        ClientSize = image.Size;
        try { Show(); Update(); }
        catch { Dispose(); throw; }
    }
    protected override void OnPaint(PaintEventArgs e) => e.Graphics.DrawImageUnscaled(_image, 0, 0);
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x21) { m.Result = (IntPtr)3; return; } // MA_NOACTIVATE
        base.WndProc(ref m);
    }
}
