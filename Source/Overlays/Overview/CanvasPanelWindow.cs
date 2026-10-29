using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace CanvasDesktop;

/// <summary>Interactive screen-space UI, owned by the canvas while it is visible.</summary>
internal class CanvasPanelWindow : Window
{
    protected CanvasPanelWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = (HWND)new WindowInteropHelper(this).Handle;
        int style = PInvoke.GetWindowLong(handle, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        // ShowInTaskbar alone does not exclude borderless WPF windows from Alt-Tab.
        // Keep activation and mouse input available for search, buttons and capture.
        PInvoke.SetWindowLong(handle, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (style | 0x80) & ~0x40000);
    }

    public void SetCanvasOwner(IntPtr owner)
    {
        var interop = new WindowInteropHelper(this);
        if (interop.Owner != owner) interop.Owner = owner;
    }
}
