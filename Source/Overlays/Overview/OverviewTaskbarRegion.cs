using System;
using System.Drawing;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace CanvasDesktop;

/// <summary>Expose the native taskbar without changing canvas coordinates or shell preferences.</summary>
internal sealed class OverviewTaskbarRegion
{
    private IntPtr _window;
    private Rectangle? _region;
    private bool? _visible;
    private Rectangle[] _previews = Array.Empty<Rectangle>();

    internal static Rectangle LocalWorkingArea(Rectangle bounds, Rectangle workingArea)
    {
        var area = Rectangle.Intersect(bounds, workingArea);
        return new(area.X - bounds.X, area.Y - bounds.Y, area.Width, area.Height);
    }

    public void Apply(IntPtr window, Rectangle bounds, Rectangle workingArea, bool visible,
        IReadOnlyList<Rectangle>? previews = null)
    {
        Rectangle? clip = visible ? LocalWorkingArea(bounds, workingArea) : null;
        var holes = visible && previews is { Count: > 0 } ? previews.Select(p => Rectangle.Intersect(bounds, p))
            .Where(p => p.Width > 0 && p.Height > 0)
            .Select(p => new Rectangle(p.X - bounds.X, p.Y - bounds.Y, p.Width, p.Height)).ToArray()
            : Array.Empty<Rectangle>();
        if (_window == window && _visible == visible && _region == clip && _previews.SequenceEqual(holes)) return;

        // Microsoft documents this property as preventing fullscreen detection
        // from pushing Explorer's taskbar below a borderless monitor-sized window.
        if (visible) SetProp(window, "NonRudeHWND", new IntPtr(1));
        else RemoveProp(window, "NonRudeHWND");

        HRGN region = clip is { } r ? PInvoke.CreateRectRgn(r.Left, r.Top, r.Right, r.Bottom) : default;
        if (visible && region == default(HRGN)) return;
        foreach (var hole in holes)
        {
            var cutout = PInvoke.CreateRectRgn(hole.Left, hole.Top, hole.Right, hole.Bottom);
            if (cutout == default(HRGN)) { PInvoke.DeleteObject(region); return; }
            int result = CombineRgn(region, region, cutout, 4); // RGN_DIFF
            PInvoke.DeleteObject(cutout);
            if (result == 0) { PInvoke.DeleteObject(region); return; }
        }
        if (PInvoke.SetWindowRgn((HWND)window, region, true) == 0)
        {
            if (region != default(HRGN)) PInvoke.DeleteObject(region);
            return;
        }
        // A successful SetWindowRgn transfers ownership of the region to Windows.
        _window = window;
        _region = clip;
        _visible = visible;
        _previews = holes;
    }

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(HRGN destination, HRGN source, HRGN cutout, int mode);

    [DllImport("user32.dll", EntryPoint = "SetPropW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProp(IntPtr window, string name, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "RemovePropW", CharSet = CharSet.Unicode)]
    private static extern IntPtr RemoveProp(IntPtr window, string name);
}
