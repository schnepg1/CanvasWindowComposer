using CanvasDesktop;
using System.Windows.Forms;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class BackgroundOverviewIntegrationTests
{
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
                input.RaiseOverviewHotkey();overview.HandleEscape();
                Assert.Equal(OverviewMode.Zooming,overview.CurrentMode);
                overview.SetBackgroundEditorVisible(false);
                Assert.False(overview.CanvasInputSuspended);Assert.True(input.EscHotkeyEnabled);
                input.RaiseEscPressed();Assert.Equal(OverviewMode.Hidden,overview.CurrentMode);
            }
            finally { if(Directory.Exists(folder))Directory.Delete(folder,true); }
        });
}
