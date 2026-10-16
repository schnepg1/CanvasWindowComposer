using CanvasDesktop;
using CanvasDesktop.CanvasView;
using System.Windows.Forms;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class SpatialSelectionOverviewTests
{
    [Fact]
    [Trait("Category","NativeRendering")]
    public void KeyboardHistoryOrdersProjectAnnotationAndGroupEditsAndClearsBothRedoBranches()
        =>WorkspaceViewTests.OnSta(()=>
        {
            var folder=Path.Combine(Path.GetTempPath(),"ordered-selection-"+Guid.NewGuid().ToString("N"));
            try
            {
                var item=new WorkspaceItem { Title="Editor",ExecutablePath=@"C:\Apps\Editor.exe",Bounds=new WorldRect { X=100,Y=200,W=600,H=400 } };
                var tile=new WorkspaceZone { Bounds=new WorldRect { W=1800,H=1200 },ItemIds=[item.Id] };var config=new FakeAppConfig { EnableStartupIntro=false };
                using var rig=new WorkspaceHarness(new WorkspaceDocument { Items=[item],Zones=[tile] },config:config);rig.Workspace.Start();
                using var overview=new OverviewManager(rig.Canvas,rig.Wm,rig.Api,rig.Input,config);
                overview.AttachZones(rig.Workspace.Zones);overview.AttachPlaceholderWorkspace(rig.Workspace);overview.AttachAnnotations(rig.Desktops,Path.Combine(folder,"annotations.json"));overview.TransitionTo(OverviewMode.Zooming);
                var note=new CanvasAnnotation(Guid.NewGuid(),rig.Desktops.CurrentDesktopId,AnnotationKind.Text,new(900,300),new(1100,500),Text:"Original");
                overview.Annotations!.Put(note);Assert.True(rig.Workspace.MoveZone(tile.Id,100,0));
                overview.Selection.Pick(new(CanvasItemKind.Project,tile.Id),overview.SelectionItems(),false);
                var selected=overview.Selection.Ids;overview.MoveSelection(new(overview.SelectionItems().Where(i=>selected.Contains(i.Key)).ToArray(),200,0));
                var bounds=tile.Bounds;bounds.W+=100;Assert.True(rig.Workspace.ResizeZone(tile.Id,bounds));
                var moved=overview.Annotations.Items.Single();overview.Annotations.Put(moved with { Text="Edited" });
                var pass=(OverviewOverlay)Control.FromHandle(overview.MonitorHandles[0])!;
                void Undo()=>pass.OnKey!(pass,new(Keys.Control|Keys.Z));
                void Redo()=>pass.OnKey!(pass,new(Keys.Control|Keys.Y));
                Undo();Assert.Equal("Original",overview.Annotations.Items.Single().Text);
                Undo();Assert.Equal(1800,tile.Bounds.W);Assert.Equal(300,tile.Bounds.X);
                Undo();Assert.Equal(100,tile.Bounds.X);Assert.Equal(note,overview.Annotations.Items.Single());
                Undo();Assert.Equal(0,tile.Bounds.X);Assert.Equal(100,item.Bounds.X);
                Undo();Assert.Empty(overview.Annotations.Items);
                for(int i=0;i<5;i++)Redo();Assert.Equal(300,tile.Bounds.X);Assert.Equal(1900,tile.Bounds.W);Assert.Equal("Edited",overview.Annotations.Items.Single().Text);
                Undo();Undo();overview.Annotations.Put(overview.Annotations.Items.Single() with { Text="New branch" });
                Assert.False(rig.Workspace.CanRedoZoneLayout);Assert.Equal(0,overview.Annotations.HistoryOrder(rig.Desktops.CurrentDesktopId,true));
                Undo();bounds=tile.Bounds;bounds.W+=40;Assert.True(rig.Workspace.ResizeZone(tile.Id,bounds));
                Assert.Equal(0,overview.Annotations.HistoryOrder(rig.Desktops.CurrentDesktopId,true));
            }
            finally { if(Directory.Exists(folder))Directory.Delete(folder,true); }
        });

    [Fact]
    [Trait("Category","NativeRendering")]
    public void SessionMoveCanUndoAfterMinimizingAndReopeningOverview()
        =>WorkspaceViewTests.OnSta(()=>
        {
            var folder=Path.Combine(Path.GetTempPath(),"session-selection-"+Guid.NewGuid().ToString("N"));
            try
            {
                using var source=new Form();var canvas=new Canvas();var api=new FakeWindowApi();var input=new FakeInputRouter();var config=new FakeAppConfig { EnableStartupIntro=false };
                api.AddWindow(source.Handle,100,200,600,400);canvas.SetWindow(source.Handle,100,200,600,400);
                using var wm=new WindowManager(canvas,api,config,input,new FakeClock());using var overview=new OverviewManager(canvas,wm,api,input,config);
                var desktops=new FakeVirtualDesktops();overview.AttachAnnotations(desktops,Path.Combine(folder,"annotations.json"));overview.TransitionTo(OverviewMode.Zooming);
                var window=Assert.Single(overview.SelectionItems(),i=>i.Key.Kind==CanvasItemKind.Window);
                overview.Selection.Replace([window.Key]);overview.MoveSelection(new([window],300,0));Assert.Equal(400,canvas.Windows[source.Handle].X);
                canvas.CollapseWindow(source.Handle);overview.TransitionTo(OverviewMode.Hidden);overview.TransitionTo(OverviewMode.Zooming);
                overview.SelectAnnotationMode(AnnotationMode.Select);
                Assert.True(overview.Annotations!.Undo(desktops.CurrentDesktopId));Assert.Equal(100,canvas.Windows[source.Handle].X);
                Assert.Equal(WindowState.Minimized,canvas.GetWindowState(source.Handle));Assert.Equal(WindowState.Minimized,api.NativeStateCalls[^1].state);
                Assert.Equal(canvas.WorldToScreen(canvas.Windows[source.Handle]),api.NativeStateCalls[^1].rect);
            }
            finally { if(Directory.Exists(folder))Directory.Delete(folder,true); }
        });

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
