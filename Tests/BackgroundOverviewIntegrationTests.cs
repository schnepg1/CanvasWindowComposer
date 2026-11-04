using CanvasDesktop;
using System.Windows.Forms;
using System.Windows.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class BackgroundOverviewIntegrationTests
{
    [Fact]
    [Trait("Category","NativeRendering")]
    public void PickerRemainsClickableAndKeepsItsDraftAcrossCanvasTogglesAndPassRebuilds()
        => WorkspaceViewTests.OnSta(()=>
        {
            var folder=Path.Combine(Path.GetTempPath(),"canvas-background-panel-"+Guid.NewGuid().ToString("N"));
            try
            {
                var config=new FakeAppConfig { EnableStartupIntro=false };
                using var rig=new WorkspaceHarness(new WorkspaceDocument { PlacementPolicyInitialized=true },config:config);
                rig.Workspace.Start();
                var input=rig.Input;var desktops=rig.Desktops;
                using var overview=new OverviewManager(rig.Canvas,rig.Wm,rig.Api,input,config);
                overview.AttachPlaceholderWorkspace(rig.Workspace);
                overview.AttachAnnotations(desktops,Path.Combine(folder,"annotations.json"));
                var service=new BackgroundService(new BackgroundPersistence(Path.Combine(folder,"backgrounds.json")),
                    ()=>desktops.CurrentDesktopId,()=>[]);
                using var cache=new BackgroundImageCache(new BackgroundAssets(folder),
                    new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
                service.Add(new(Guid.NewGuid(),desktops.CurrentDesktopId,"Study","builtin:study",new() { W=800,H=400 }));
                var editor=new BackgroundWindow(service,new(folder),cache,()=>[],overview.BackgroundDefaultBounds,overview.EditBackground,overview.JumpToBackground);
                try
                {
                    overview.AttachBackgroundEditor(editor);
                    editor.IsVisibleChanged+=(_,_)=>overview.SetBackgroundEditorVisible(editor.IsVisible);
                    editor.Show();editor.Activate();
                    var name=Descendants(editor).OfType<System.Windows.Controls.TextBox>().Single(t=>t.Name=="BackgroundName");
                    name.Text="Keep my unsaved draft";
                    for(int i=0;i<3;i++)
                    {
                        input.RaiseOverviewHotkey();Assert.Equal(OverviewMode.Zooming,overview.CurrentMode);
                        var pass=(OverviewOverlay)Control.FromHandle(overview.MonitorHandles[0])!;
                        pass.Activate();Application.DoEvents();
                        Assert.Contains(new WindowInteropHelper(editor).Owner,overview.MonitorHandles);
                        Assert.True(editor.Topmost);AssertPointerHits(editor);
                        input.RaiseOverviewHotkey();Assert.Equal(OverviewMode.Hidden,overview.CurrentMode);
                        Assert.Equal(IntPtr.Zero,new WindowInteropHelper(editor).Owner);
                        Assert.True(editor.IsVisible);Assert.False(editor.Topmost);
                        Assert.Equal("Keep my unsaved draft",name.Text);
                    }
                    input.RaiseOverviewHotkey();
                    typeof(OverviewManager).GetMethod("OnDisplaySettingsChanged",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!
                        .Invoke(overview,[null,EventArgs.Empty]);
                    Application.DoEvents();AssertPointerHits(editor);
                    Assert.Contains(new WindowInteropHelper(editor).Owner,overview.MonitorHandles);
                    Assert.Equal("Keep my unsaved draft",name.Text);
                    overview.TransitionTo(OverviewMode.Hidden);
                    overview.Dispose(); // The app disposes canvas panels before the coordinator closes its picker.
                    overview.AttachBackgroundEditor(null);
                }
                finally { overview.AttachBackgroundEditor(null);editor.Close();overview.SetBackgroundEditorVisible(false); }
            }
            finally { if(Directory.Exists(folder))Directory.Delete(folder,true); }
        });

    private static void AssertPointerHits(System.Windows.Window window)
    {
        window.UpdateLayout();var handle=new WindowInteropHelper(window).Handle;
        Assert.True(PInvoke.GetWindowRect((HWND)handle,out var rect));
        Assert.Equal(handle,(IntPtr)PInvoke.WindowFromPoint(new System.Drawing.Point((rect.left+rect.right)/2,(rect.top+rect.bottom)/2)));
    }

    private static IEnumerable<System.Windows.DependencyObject> Descendants(System.Windows.DependencyObject root)
    {
        yield return root;
        foreach(var child in System.Windows.LogicalTreeHelper.GetChildren(root).OfType<System.Windows.DependencyObject>())
            foreach(var descendant in Descendants(child))yield return descendant;
    }

    [Fact]
    [Trait("Category","NativeRendering")]
    public void GlobalEscapeCancelsTheDragThenLeavesEditingWithoutStealingLaterHistory()
        => WorkspaceViewTests.OnSta(()=>
        {
            var folder=Path.Combine(Path.GetTempPath(),"canvas-background-overview-"+Guid.NewGuid().ToString("N"));
            try
            {
                var canvas=new Canvas();var api=new FakeWindowApi();var input=new FakeInputRouter();
                var config=new FakeAppConfig { EnableStartupIntro=false };
                using var wm=new WindowManager(canvas,api,config,input,new FakeClock());
                using var overview=new OverviewManager(canvas,wm,api,input,config);
                var desktops=new FakeVirtualDesktops { CurrentDesktopId=Guid.NewGuid() };
                overview.AttachAnnotations(desktops,Path.Combine(folder,"annotations.json"));
                var service=new BackgroundService(new BackgroundPersistence(Path.Combine(folder,"backgrounds.json")),
                    ()=>desktops.CurrentDesktopId,()=>[]);
                using var cache=new BackgroundImageCache(new BackgroundAssets(folder),new SynchronizationContext());
                overview.AttachBackgrounds(service,new(cache),()=>false);
                var item=new BackgroundSurface(Guid.NewGuid(),desktops.CurrentDesktopId,"Study","builtin:study",
                    new() { X=100,Y=100,W=800,H=400 });
                service.Add(item);overview.EditBackground(item.Id);
                var pass=(OverviewOverlay)Control.FromHandle(overview.MonitorHandles[0])!;
                pass.OnMouseButtonDown!(pass,new(MouseButtons.Left,1,400,100,0));
                pass.OnMouseMoved!(pass,new(MouseButtons.Left,1,500,160,0));
                Assert.True(pass.Capture);
                pass.OnKey!(pass,new(Keys.Control|Keys.Z));
                Assert.False(pass.Capture);Assert.True(service.Items.Single().Locked);
                Assert.Equal(item.Bounds,service.Items.Single().Bounds);
                service.Redo();overview.EditBackground(item.Id);
                pass.OnMouseButtonDown!(pass,new(MouseButtons.Left,1,400,100,0));
                pass.OnMouseMoved!(pass,new(MouseButtons.Left,1,500,160,0));
                Assert.True(pass.Capture);
                input.RaiseEscPressed();
                Assert.False(pass.Capture);Assert.Equal(item.Bounds,service.Items.Single().Bounds);
                Assert.Equal(OverviewMode.Zooming,overview.CurrentMode);
                input.RaiseEscPressed();
                Assert.Equal(OverviewMode.Zooming,overview.CurrentMode);
                pass.OnKey!(pass,new(Keys.Control|Keys.Z));
                Assert.Single(service.Items); // Background shortcuts no longer own Ctrl+Z.
                overview.SetBackgroundEditorVisible(true);
                Assert.True(overview.CanvasInputSuspended);Assert.False(input.EscHotkeyEnabled);
                input.RaiseOverviewHotkey();Assert.Equal(OverviewMode.Hidden,overview.CurrentMode);
                input.RaiseOverviewHotkey();overview.HandleEscape();
                Assert.Equal(OverviewMode.Zooming,overview.CurrentMode);
                overview.SetBackgroundEditorVisible(false);
                Assert.False(overview.CanvasInputSuspended);Assert.True(input.EscHotkeyEnabled);
                input.RaiseEscPressed();Assert.Equal(OverviewMode.Hidden,overview.CurrentMode);
            }
            finally { if(Directory.Exists(folder))Directory.Delete(folder,true); }
        });
}
