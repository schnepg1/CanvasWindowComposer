using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace CanvasDesktop;

/// <summary>A passive child of the shell's wallpaper layer. Never changes the user's Windows wallpaper.</summary>
internal sealed class DesktopBackgroundWindow : Window
{
    private readonly BackgroundDesktopSurface _surface;
    private readonly IntPtr _handle;
    private readonly Func<IntPtr> _findParent;
    private IntPtr _parent;
    private System.Drawing.Rectangle _screen;
    public bool IsAlive => PInvoke.IsWindow((HWND)_handle);
    public string? Status { get; private set; }
    public DesktopBackgroundWindow(BackgroundVisual visual,Func<IntPtr>? findParent=null)
    {
        _findParent=findParent??FindWallpaperParent;
        WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;AllowsTransparency=true;
        Background=Brushes.Transparent;ShowInTaskbar=false;ShowActivated=false;Focusable=false;IsHitTestVisible=false;
        _surface=new(visual);Content=_surface;
        _handle=new WindowInteropHelper(this).EnsureHandle();
        int ex=PInvoke.GetWindowLong((HWND)_handle,WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        PInvoke.SetWindowLong((HWND)_handle,WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE,ex|0x080000A0);
    }

    public void Present(System.Drawing.Rectangle screen,CanvasCamera camera,IReadOnlyList<BackgroundSurface> items,bool lowQuality,bool force=false)
    {
        bool reparent=false;
        if(_parent==IntPtr.Zero || !PInvoke.IsWindow((HWND)_parent))
        {
            reparent=true;
            _parent=_findParent();
            if(_parent==IntPtr.Zero) { Status="Desktop backgrounds are waiting for Windows Explorer's wallpaper host.";Hide();return; }
            int style=PInvoke.GetWindowLong((HWND)_handle,WINDOW_LONG_PTR_INDEX.GWL_STYLE);
            PInvoke.SetWindowLong((HWND)_handle,WINDOW_LONG_PTR_INDEX.GWL_STYLE,(style|0x40000000)&unchecked((int)~0x80000000));
            Marshal.SetLastPInvokeError(0);
            var previous=SetParent(_handle,_parent);
            if(previous==IntPtr.Zero && Marshal.GetLastPInvokeError()!=0)
            { Status="Desktop background could not attach to Explorer: "+new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()).Message;_parent=IntPtr.Zero;Hide();return; }
        }
        Status=null;
        if(reparent || _screen!=screen || !IsVisible)
        {
            var origin=new System.Drawing.Point(screen.X,screen.Y);
            PInvoke.ScreenToClient((HWND)_parent,ref origin);
            if(!IsVisible) Show();
            PInvoke.SetWindowPos((HWND)_handle,(HWND)new IntPtr(1),origin.X,origin.Y,screen.Width,screen.Height,
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE|SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER);
            _screen=screen;
        }
        var dpi=VisualTreeHelper.GetDpi(_surface);
        _surface.Update(items,new(camera.X,camera.Y,camera.Zoom,new(screen.X,screen.Y,screen.Width,screen.Height),dpi.DpiScaleX,dpi.DpiScaleY),lowQuality,force);
    }

    internal static IntPtr FindWallpaperParent()
    {
        var progman=PInvoke.FindWindow("Progman",null);
        if(progman==HWND.Null) return IntPtr.Zero;
        // Explorer's established wallpaper host handshake. A hung shell cannot hang Canvas.
        SendMessageTimeout((IntPtr)progman,0x052C,IntPtr.Zero,IntPtr.Zero,2,200,out _);
        HWND worker=HWND.Null;
        WNDENUMPROC callback=(HWND h,LPARAM _) =>
        {
            if(PInvoke.FindWindowEx(h,HWND.Null,"SHELLDLL_DefView",null)!=HWND.Null)
                worker=PInvoke.FindWindowEx(HWND.Null,h,"WorkerW",null);
            return true;
        };
        PInvoke.EnumWindows(callback,0);GC.KeepAlive(callback);
        // On shells with no separate WorkerW, a bottom child of Progman remains behind its icon view.
        return worker!=HWND.Null?(IntPtr)worker:(IntPtr)progman;
    }
    [DllImport("user32.dll",SetLastError=true)] private static extern IntPtr SetParent(IntPtr child,IntPtr parent);
    [DllImport("user32.dll",EntryPoint="SendMessageTimeoutW",SetLastError=true)]
    private static extern IntPtr SendMessageTimeout(IntPtr window,uint message,IntPtr wParam,IntPtr lParam,uint flags,uint timeout,out IntPtr result);
}

internal sealed class BackgroundDesktopSurface(BackgroundVisual visual) : FrameworkElement
{
    private IReadOnlyList<BackgroundSurface> _items=Array.Empty<BackgroundSurface>();
    private CanvasViewTransform _view;
    private bool _lowQuality;
    public void Update(IReadOnlyList<BackgroundSurface> items,CanvasViewTransform view,bool lowQuality,bool force=false)
    {
        if(!force && _view==view && _lowQuality==lowQuality && System.Linq.Enumerable.SequenceEqual(_items,items))return;
        _items=items;_view=view;_lowQuality=lowQuality;InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    { base.OnRender(dc);if(_view.Zoom>0)visual.Draw(dc,_items,_view,RenderSize,_lowQuality); }
}
