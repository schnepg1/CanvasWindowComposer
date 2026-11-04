using System;
using System.Linq;
using System.Windows.Forms;

namespace CanvasDesktop;

internal sealed partial class OverviewManager
{
    private BackgroundService? _backgrounds;
    private BackgroundCanvasTool? _backgroundTool;
    private BackgroundAnnotationLayer? _backgroundLayer;
    private BackgroundEditLayer? _backgroundEditLayer;
    private bool _backgroundsEnabled=true;
    private BackgroundWindow? _backgroundEditor;
    public event Action? BackgroundsRequested;
    internal bool BackgroundEditorVisible { get; private set; }

    internal void AttachBackgroundEditor(BackgroundWindow? editor)
    {
        _backgroundEditor=editor;
        if(_disposed)return;
        UpdateCanvasPanelOwners();
        _backgroundEditor?.RaiseWithoutActivation();
    }

    internal void SetBackgroundEditorVisible(bool visible)
    {
        BackgroundEditorVisible=visible;
        if(visible)_input.DisableEscHotkey();
        else if(CurrentMode==OverviewMode.Zooming && !_disposed)_input.EnableEscHotkey();
    }

    internal void SetBackgroundsEnabled(bool enabled)
    {
        _backgroundsEnabled=enabled;
        if(!enabled)EndBackgroundEditing();
        RefreshBackgrounds();
    }

    internal WorldRect BackgroundDefaultBounds()
    {
        var camera=CurrentMode==OverviewMode.Hidden?_mainCanvas.Camera:ViewCamera;
        var screen=_screens.PrimaryWorkingArea;
        return new() { X=camera.X+screen.X/camera.Zoom,Y=camera.Y+screen.Y/camera.Zoom,
            W=screen.Width/camera.Zoom*2,H=screen.Height/camera.Zoom*2 };
    }

    internal void AttachBackgrounds(BackgroundService service,BackgroundVisual visual,Func<bool> lowQuality)
    {
        _backgrounds=service;
        _backgroundTool=new(service);
        _backgroundLayer=new(service,visual,lowQuality,_backgroundTool,()=>_backgroundsEnabled);
        _backgroundEditLayer=new(service,_backgroundTool);
        _backgroundTool.Changed+=OnBackgroundToolChanged;
        _backgroundTool.Completed+=OnBackgroundEditCompleted;
        service.Changed+=OnBackgroundsChanged;
        foreach(var pass in _passes) pass.SetBackgroundLayer(_backgroundLayer);
    }

    internal void RefreshBackgrounds()
    { if(!_disposed && CurrentMode!=OverviewMode.Hidden) _visualFrames.Request(); }
    private void OnBackgroundToolChanged() { if(!_disposed && CurrentMode!=OverviewMode.Hidden) RefreshToolPreview(); }
    private void OnBackgroundEditCompleted(BackgroundSurface item)
    {
        if(_backgrounds?.Update(item)==false) _annotationError=_backgrounds.Status??"Background edit could not be saved.";
    }
    private void OnBackgroundsChanged()
    {
        if(_backgroundTool?.SelectedId is {} id && !_backgrounds!.Items.Any(b=>b.Id==id && !b.Locked && b.ZoneId==null))
            EndBackgroundEditing();
        RefreshBackgrounds();
    }

    internal void EditBackground(Guid id)
    {
        if(_backgrounds==null || _backgroundTool==null)return;
        var item=_backgrounds.Items.FirstOrDefault(b=>b.Id==id);
        if(item==null || item.ZoneId!=null)return;
        TransitionTo(OverviewMode.Zooming);
        if(item.Locked) _backgrounds.Update(item with { Locked=false });
        SetCanvasTool(_backgroundTool,[_backgroundEditLayer!]);
        _backgroundTool.Select(id);
        RefreshToolPreview();FocusOverview();
    }

    private bool EndBackgroundEditing()
    {
        if(_backgroundTool?.SelectedId==null)return false;
        _backgroundTool.Deselect();SetCanvasTool(null);return true;
    }

    internal void JumpToBackground(BackgroundSurface item)
        => JumpToZone(new(item.Id,item.Name,"#60A5FA",item.Bounds));

    private bool HandleBackgroundKey(KeyEventArgs e)
    {
        if(_backgroundTool?.SelectedId==null || _annotationEditor!=null || _shapeEditor!=null || _composerWindow!=null)return false;
        if(e.KeyCode==Keys.Escape && !_backgroundTool.HasPendingInteraction)
        { EndBackgroundEditing();e.Handled=true;e.SuppressKeyPress=true;return true; }
        if(e.Control && !e.Alt && e.KeyCode is Keys.Z or Keys.Y)
        {
            CancelInteraction();
            if(e.KeyCode==Keys.Y||e.Shift)_backgrounds!.Redo();else _backgrounds!.Undo();
            e.Handled=true;e.SuppressKeyPress=true;return true;
        }
        return false;
    }

    private WorldRect[] BackgroundOccluders(CanvasCamera camera)
        => _windows.Windows.Select(entry=>
        {
            if(!entry.ScreenFixed)return entry.World;
            var (x,y,w,h)=_win32.GetWindowRect(entry.HWnd);
            return new WorldRect { X=camera.X+x/camera.Zoom,Y=camera.Y+y/camera.Zoom,W=w/camera.Zoom,H=h/camera.Zoom };
        }).ToArray();

    internal void DetachBackgrounds(BackgroundService service)
    { if(ReferenceEquals(_backgrounds,service))DisposeBackgrounds(); }

    private void DisposeBackgrounds()
    {
        if(!_disposed)EndBackgroundEditing();
        if(_backgrounds!=null)_backgrounds.Changed-=OnBackgroundsChanged;
        if(_backgroundTool!=null)
        { _backgroundTool.Changed-=OnBackgroundToolChanged;_backgroundTool.Completed-=OnBackgroundEditCompleted; }
        _backgrounds=null;_backgroundTool=null;_backgroundLayer=null;_backgroundEditLayer=null;
        if(!_disposed)foreach(var pass in _passes)pass.SetBackgroundLayer(null);
    }
}
