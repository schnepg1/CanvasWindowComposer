using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms.Integration;

namespace CanvasDesktop;

/// <summary>Owns background storage, modeless editor and passive desktop surfaces.</summary>
internal sealed class BackgroundCoordinator : IDisposable
{
    private readonly Canvas _canvas;
    private readonly OverviewManager _overview;
    private readonly IVirtualDesktops _desktops;
    private readonly ZoneService _zones;
    private readonly IAppConfig _config;
    private readonly BackgroundAssets _assets;
    private readonly BackgroundImageCache _images;
    private readonly BackgroundVisual _visual;
    private readonly SynchronizationContext _context;
    private readonly List<DesktopBackgroundWindow> _windows=[];
    private BackgroundWindow? _editor;
    private bool _enabled=true,_disposed;
    private string _screenKey="";
    private CanvasCamera? _lastCamera;
    private bool _lastQuality;
    private string? _renderingStatus;
    internal BackgroundService Service { get; }

    public BackgroundCoordinator(Canvas canvas,OverviewManager overview,IVirtualDesktops desktops,ZoneService zones,IAppConfig config)
    {
        _canvas=canvas;_overview=overview;_desktops=desktops;_zones=zones;_config=config;
        _assets=new(Path.Combine(AppConfig.ConfigDir,"Assets","Backgrounds"));
        _context=SynchronizationContext.Current ?? new System.Windows.Forms.WindowsFormsSynchronizationContext();
        _images=new(_assets,_context);
        _visual=new(_images);
        Service=new(new BackgroundPersistence(Path.Combine(AppConfig.ConfigDir,"backgrounds.json")),()=>desktops.CurrentDesktopId,()=>zones.Items);
        try
        {
        Service.ReconcileZones();
        overview.AttachBackgrounds(Service,_visual,()=>config.LowQualityMode);
        Service.Changed+=Refresh;
        _images.Changed+=OnImagesChanged;
        _zones.Changed+=OnZonesChanged;
        _canvas.CameraChanged+=Refresh;
        _desktops.DesktopChanged+=OnDesktopChanged;
        _overview.BeforeModeChanged+=OnModeChanging;
        _overview.AfterModeChanged+=OnModeChanged;
        _overview.BackgroundsRequested+=Show;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged+=OnDisplayChanged;
        Refresh();
        }
        catch { try { Dispose(); } catch { } throw; }
    }

    public void Show()
    {
        if(_disposed)return;
        if(_editor==null)
        {
            _editor=new(Service,_assets,_images,()=>_zones.Items,_overview.BackgroundDefaultBounds,_overview.EditBackground,_overview.JumpToBackground);
            _editor.IsVisibleChanged+=(_,_)=>_overview.SetBackgroundEditorVisible(_editor?.IsVisible==true);
            _editor.Closed+=(_,_)=>{_editor=null;_overview.AttachBackgroundEditor(null);_overview.SetBackgroundEditorVisible(false);};
            ElementHost.EnableModelessKeyboardInterop(_editor);
        }
        _overview.AttachBackgroundEditor(_editor);
        _editor.SetRenderingStatus(_renderingStatus);
        _editor.Show();_editor.Activate();_editor.RaiseWithoutActivation();
    }
    public void SetEnabled(bool enabled) { _enabled=enabled;_overview.SetBackgroundsEnabled(enabled);Refresh(); }

    internal bool PrepareForUpdate(out string? message)
    {
        message=null;
        if(_editor?.HasPendingChanges==true)
        { message="Save or reload the open background changes before restarting to update.";return false; }
        if(!Service.Save())
        { message=Service.Status??"Backgrounds could not be saved. The update will wait.";return false; }
        return true;
    }
    private void OnImagesChanged() { Refresh();_overview.RefreshBackgrounds(); }
    private void OnZonesChanged() => Service.ReconcileZones();
    private void OnDesktopChanged() { _lastCamera=null;Service.NotifyDesktopChanged();Refresh(); }
    private void OnModeChanging(OverviewMode from,OverviewMode to)
    { if(to!=OverviewMode.Hidden)foreach(var window in _windows)window.Hide(); }
    private void OnModeChanged(OverviewMode from,OverviewMode to)
    { _overview.AttachBackgroundEditor(_editor);_overview.SetBackgroundEditorVisible(_editor?.IsVisible==true);Refresh(); }
    private void OnDisplayChanged(object? sender,EventArgs e)
        => _context.Post(_=>
        {
            if(_disposed)return;
            foreach(var w in _windows)w.Close();_windows.Clear();_screenKey="";Refresh();
        },null);

    // Called by the existing reconciliation timer. No additional animation timer.
    public void Tick()
    {
        if(_disposed)return;
        if(_lastQuality!=_config.LowQualityMode || _lastCamera!=_canvas.Camera)
        { Refresh();_overview.RefreshBackgrounds(); }
        if(_overview.CurrentMode==OverviewMode.Hidden && _enabled && Service.Items.Count>0)
            Present(); // Also rebinds after Explorer restarts; retained artwork changes only on invalidation.
    }
    private void Refresh()
    {
        if(_disposed)return;
        _lastCamera=_canvas.Camera;_lastQuality=_config.LowQualityMode;
        if(!_enabled || _overview.CurrentMode!=OverviewMode.Hidden || Service.Items.Count==0)
        { foreach(var w in _windows)w.Hide();return; }
        Present(force:true);
    }
    private void Present(bool force=false)
    {
        var screens=System.Windows.Forms.Screen.AllScreens;
        string key=string.Join(";",screens.Select(s=>s.Bounds.ToString()));
        if(_screenKey!=key)
        {
            foreach(var w in _windows)w.Close();_windows.Clear();
            _screenKey=key;
            foreach(var _ in screens)_windows.Add(new(_visual));
        }
        var items=Service.Items;
        for(int i=0;i<screens.Length;i++)
        {
            if(!_windows[i].IsAlive) { _windows[i].Close();_windows[i]=new(_visual); }
            _windows[i].Present(screens[i].Bounds,_canvas.Camera,items,_config.LowQualityMode,force);
        }
        var status=_windows.Select(w=>w.Status).FirstOrDefault(s=>s!=null);
        if(status!=_renderingStatus)
        { _renderingStatus=status;_editor?.SetRenderingStatus(status);if(status!=null)TrayApp.Log(status); }
    }
    public void Dispose()
    {
        if(_disposed)return;_disposed=true;
        Service.Changed-=Refresh;_images.Changed-=OnImagesChanged;_zones.Changed-=OnZonesChanged;
        _canvas.CameraChanged-=Refresh;_desktops.DesktopChanged-=OnDesktopChanged;
        _overview.BeforeModeChanged-=OnModeChanging;_overview.AfterModeChanged-=OnModeChanged;_overview.BackgroundsRequested-=Show;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged-=OnDisplayChanged;
        var failures=new List<Exception>();
        void Attempt(Action action){try{action();}catch(Exception ex){failures.Add(ex);}}
        Attempt(()=>_overview.DetachBackgrounds(Service));
        Attempt(()=>_overview.AttachBackgroundEditor(null));
        Attempt(()=>_editor?.Close());foreach(var window in _windows)Attempt(window.Close);_windows.Clear();Attempt(_images.Dispose);
        if(failures.Count>0)throw new AggregateException("Background cleanup failed.",failures);
    }
}
