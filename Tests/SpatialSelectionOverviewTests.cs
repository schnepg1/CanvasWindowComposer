using CanvasDesktop;
using CanvasDesktop.CanvasView;
using System.Windows.Forms;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class SpatialSelectionOverviewTests
{
    [Fact]
    [Trait("Category","NativeRendering")]
    public void MixedSelectionMovesAsOneSavedUndoStepAndDesktopSwitchClearsIt()
        =>WorkspaceViewTests.OnSta(()=>
        {
            var folder=Path.Combine(Path.GetTempPath(),"spatial-selection-"+Guid.NewGuid().ToString("N"));
            try
            {
                var item=new WorkspaceItem { Title="Guardian editor",ExecutablePath=@"C:\Apps\Editor.exe",Bounds=new WorldRect { X=100,Y=200,W=600,H=400 } };
                var tile=new WorkspaceZone { Name="Guardian",Bounds=new WorldRect { W=1800,H=1200 },ItemIds=[item.Id] };
                var config=new FakeAppConfig { EnableStartupIntro=false };
                using var rig=new WorkspaceHarness(new WorkspaceDocument { Items=[item],Zones=[tile] },config:config);rig.Workspace.Start();
                using var overview=new OverviewManager(rig.Canvas,rig.Wm,rig.Api,rig.Input,config);
                overview.AttachZones(rig.Workspace.Zones);overview.AttachPlaceholderWorkspace(rig.Workspace);
                overview.AttachAnnotations(rig.Desktops,Path.Combine(folder,"annotations.json"));overview.TransitionTo(OverviewMode.Zooming);
                var note=new CanvasAnnotation(Guid.NewGuid(),rig.Desktops.CurrentDesktopId,AnnotationKind.Text,new(800,300),new(1100,500),Text:"Next action");
                overview.Annotations!.Put(note);overview.SelectAnnotationMode(AnnotationMode.Select);
                overview.Selection.Pick(new(CanvasItemKind.Project,tile.Id),overview.SelectionItems(),false);Assert.Equal(3,overview.Selection.Ids.Count);
                var selected=overview.Selection.Ids;var group=overview.SelectionItems().Where(i=>selected.Contains(i.Key)).ToArray();
                overview.MoveSelection(new(group,400,-100));Assert.Equal(500,item.Bounds.X);Assert.Equal(400,tile.Bounds.X);
                Assert.Equal(note.Translate(400,-100),Assert.Single(overview.Annotations.Items));
                Assert.True(overview.Annotations.Undo(rig.Desktops.CurrentDesktopId));Assert.Equal(100,item.Bounds.X);Assert.Equal(0,tile.Bounds.X);Assert.Equal(note,Assert.Single(overview.Annotations.Items));
                Assert.True(overview.Annotations.Undo(rig.Desktops.CurrentDesktopId,true));Assert.Equal(500,item.Bounds.X);
                rig.Desktops.SwitchTo(Guid.NewGuid());Assert.Empty(overview.Selection.Ids);Assert.False(overview.SelectionTool!.HasPendingInteraction);
            }
            finally { if(Directory.Exists(folder))Directory.Delete(folder,true); }
        });

    [Fact]
    [Trait("Category","NativeRendering")]
    public void SelectModeUsesMarqueeOnEmptyCanvasAndEscapeCancelsGhostMove()
        =>WorkspaceViewTests.OnSta(()=>
        {
            var folder=Path.Combine(Path.GetTempPath(),"marquee-selection-"+Guid.NewGuid().ToString("N"));
            try
            {
                var canvas=new Canvas();var api=new FakeWindowApi();var input=new FakeInputRouter();var config=new FakeAppConfig { EnableStartupIntro=false };
                using var wm=new WindowManager(canvas,api,config,input,new FakeClock());using var overview=new OverviewManager(canvas,wm,api,input,config);
                var desktops=new FakeVirtualDesktops();overview.AttachAnnotations(desktops,Path.Combine(folder,"annotations.json"));overview.TransitionTo(OverviewMode.Zooming);
                var a=new CanvasAnnotation(Guid.NewGuid(),desktops.CurrentDesktopId,AnnotationKind.Text,new(100,100),new(250,200),Text:"One");
                var b=a with { Id=Guid.NewGuid(),A=new(350,100),B=new(500,200),Text="Two" };overview.Annotations!.ApplyBatch(0,desktops.CurrentDesktopId,[a,b]);
                overview.SelectAnnotationMode(AnnotationMode.Select);
                var tool=overview.SelectionTool!;var view=new CanvasViewTransform(0,0,1,new(0,0,1920,1080));
                CanvasPointer P(double x,double y)=>new(0,new(x,y),CanvasPointerButton.Left,CanvasModifiers.None,view);
                tool.PointerDown(P(50,50));tool.PointerUp(P(550,250));Assert.Equal(2,overview.Selection.Ids.Count);
                tool.PointerDown(P(170,150));tool.PointerMove(P(270,250));Assert.Equal(new CanvasPoint(100,100),tool.Offset);
                input.RaiseEscPressed();Assert.Equal(new[]{a,b},overview.Annotations.Items);Assert.False(tool.HasPendingInteraction);
            }
            finally { if(Directory.Exists(folder))Directory.Delete(folder,true); }
        });
}
