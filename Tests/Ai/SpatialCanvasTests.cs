using CanvasDesktop.CanvasView;

namespace CanvasDesktop.Tests;

public class SpatialCanvasTests
{
    private static CanvasSelectionItem Item(CanvasItemKind kind, double x, double y, double w=100, double h=100)
        => new(new(kind,Guid.NewGuid()),new(x,y,w,h));
    private static CanvasPointer P(double x,double y,CanvasModifiers modifiers=CanvasModifiers.None)
        => new(0,new(x,y),CanvasPointerButton.Left,modifiers,new CanvasViewTransform(0,0,1,new(0,0,1920,1080)));

    [Fact]
    public void ProjectSelectionIncludesAssignedAndSpatialContentsOnceAndSkipsNeighbours()
    {
        var inside=Item(CanvasItemKind.Annotation,100,100); var assigned=Item(CanvasItemKind.Window,3000,0);
        var outside=Item(CanvasItemKind.Window,950,0,500); var other=Item(CanvasItemKind.Project,50,50);
        var project=Item(CanvasItemKind.Project,0,0,1000,1000) with { Members=[assigned.Key,inside.Key] };
        var selection=new CanvasSelection(); var items=new[]{project,inside,assigned,outside,other};
        selection.Pick(project.Key,items,false);
        Assert.True(selection.Ids.SetEquals([project.Key,inside.Key,assigned.Key]));
        selection.Pick(project.Key,items,true); Assert.Empty(selection.Ids);
    }

    [Fact]
    public void MarqueeInsideProjectDoesNotSelectTheEntireTile()
    {
        var project=Item(CanvasItemKind.Project,-1000,-1000,3000,3000); var window=Item(CanvasItemKind.Window,10,10);
        var selection=new CanvasSelection(); var items=new[]{project,window};
        selection.Rectangle(new(0,0,200,200),items); Assert.Equal(window.Key,Assert.Single(selection.Ids));
        selection.Rectangle(new(-1001,-1001,3002,3002),items); Assert.True(selection.Ids.SetEquals(items.Select(i=>i.Key)));
    }

    [Fact]
    public void ControlClickAddsAndRemovesWithoutMovingTheRemainingGroup()
    {
        var a=Item(CanvasItemKind.Window,0,0); var b=Item(CanvasItemKind.Window,200,0); var items=new[]{a,b};
        var selection=new CanvasSelection(); var tool=new CanvasSelectionTool(selection,()=>items,p=>p.World.X<150?a.Key:b.Key,()=>true);
        int moves=0; tool.MoveRequested+=_=>moves++;
        tool.PointerDown(P(30,30));tool.PointerUp(P(30,30));
        tool.PointerDown(P(230,30,CanvasModifiers.Control));tool.PointerUp(P(300,100));
        Assert.True(selection.Ids.SetEquals([a.Key,b.Key])); Assert.Equal(0,moves);
        tool.PointerDown(P(30,30,CanvasModifiers.Control));tool.PointerUp(P(80,50));
        Assert.Equal(b.Key,Assert.Single(selection.Ids)); Assert.Equal(0,moves);
    }

    [Fact]
    public void GroupDragPreviewsAndCommitsExactlyOnceFromOriginalWorldCoordinates()
    {
        var a=Item(CanvasItemKind.Window,-200,0); var b=Item(CanvasItemKind.Annotation,20,40); var items=new[]{a,b};
        var selection=new CanvasSelection();selection.Replace(items.Select(i=>i.Key));
        var tool=new CanvasSelectionTool(selection,()=>items,_=>a.Key,()=>true);CanvasSelectionMove? move=null;
        tool.MoveRequested+=m=>move=m;
        tool.PointerDown(P(-150,20));tool.PointerMove(P(-50,70));
        Assert.Null(move);Assert.Equal(new CanvasPoint(100,50),tool.Offset);Assert.Equal(2,tool.Ghosts.Count);
        tool.PointerUp(P(-30,80));Assert.Equal(120,move!.Dx);Assert.Equal(60,move.Dy);Assert.Equal(items,move.Before);
        Assert.False(tool.HasPendingInteraction);Assert.Empty(tool.Ghosts);
    }

    [Fact]
    public void CancelledMarqueeRestoresPriorSelectionAndDesktopSwitchCannotRestoreIt()
    {
        var a=Item(CanvasItemKind.Annotation,0,0);var b=Item(CanvasItemKind.Window,200,200);var items=new[]{a,b};
        var selection=new CanvasSelection();selection.Replace([a.Key]);
        var tool=new CanvasSelectionTool(selection,()=>items,_=>null,()=>true);
        tool.PointerDown(P(180,180));tool.PointerMove(P(350,350));Assert.Equal(b.Key,Assert.Single(selection.Ids));
        tool.Cancel();Assert.Equal(a.Key,Assert.Single(selection.Ids));
        tool.PointerDown(P(180,180,CanvasModifiers.Control));tool.PointerUp(P(350,350));Assert.Equal(2,selection.Ids.Count);
        tool.PointerDown(P(0,0));selection.SwitchDesktop(Guid.NewGuid());tool.Cancel();Assert.Empty(selection.Ids);
    }

    [Fact]
    public void ProximityUsesCircleIntersectionInWorldUnitsAndDoesNotCaptureAnEnclosingProject()
    {
        var large=Item(CanvasItemKind.Window,-1000,-10,1010,20);var corner=Item(CanvasItemKind.Annotation,90,90,10,10);
        var project=Item(CanvasItemKind.Project,-5000,-5000,10000,10000);var selection=new CanvasSelection();
        selection.Nearby(new(0,0),100,[large,corner,project]);Assert.Equal(large.Key,Assert.Single(selection.Ids));
        Assert.Throws<ArgumentOutOfRangeException>(()=>selection.Nearby(new(0,0),double.NaN,[large]));
        Assert.Throws<ArgumentOutOfRangeException>(()=>selection.Nearby(new(0,0),0,[large]));
    }

    [Fact]
    public void MarqueeUsesNegativeMonitorOriginZoomAndDpiWithoutChangingItsWorldScope()
    {
        var item=Item(CanvasItemKind.Annotation,-3000,-200,100,100);var selection=new CanvasSelection();
        var tool=new CanvasSelectionTool(selection,()=>[item],_=>null,()=>true);
        var view=new CanvasViewTransform(-4000,-500,.5,new(-1920,-200,1920,1080),2,1.5);
        CanvasPointer Pointer(CanvasPoint world)=>new(0,view.WorldToLocal(world),CanvasPointerButton.Left,CanvasModifiers.None,view);
        tool.PointerDown(Pointer(new(-3010,-210)));tool.PointerUp(Pointer(new(-2890,-90)));
        Assert.Equal(item.Key,Assert.Single(selection.Ids));
    }

    [Fact]
    public void CompoundHistoryReplaysBothHalvesAndRefusesStaleExternalStateAtomically()
    {
        var desktop=Guid.NewGuid();var note=new CanvasAnnotation(Guid.NewGuid(),desktop,AnnotationKind.Text,new(0,0),new(100,100),Text:"Note");
        var doc=new AnnotationDocument([note]);int windowX=0;
        bool Replay(bool redo) { if(windowX!=(redo?0:100))return false;windowX=redo?100:0;return true; }
        doc.ApplyBatch(0,desktop,[note.Translate(100,0)],Replay);Assert.Equal(100,windowX);
        windowX=200;Assert.False(doc.Undo(desktop));Assert.Equal(note.Translate(100,0),Assert.Single(doc.Items));
        windowX=100;Assert.True(doc.Undo(desktop));Assert.Equal(0,windowX);Assert.Equal(note,Assert.Single(doc.Items));
        Assert.False(doc.Undo(desktop));Assert.True(doc.Undo(desktop,true));Assert.Equal(100,windowX);
    }

    [Fact]
    public void ExternalOnlyMoveHasOneHistoryStepAndInvalidAnnotationsNeverMoveWindows()
    {
        var doc=new AnnotationDocument();var desktop=Guid.NewGuid();int calls=0;
        Assert.Throws<ArgumentException>(()=>doc.ApplyBatch(0,desktop,[new(Guid.Empty,desktop,AnnotationKind.Text,new(0,0),new(100,100),Text:"bad")],_=>{calls++;return true;}));
        Assert.Equal(0,calls);doc.ApplyBatch(0,desktop,[],_=>{calls++;return true;});
        Assert.Equal(1,calls);Assert.True(doc.Undo(desktop));Assert.Equal(2,calls);Assert.False(doc.Undo(desktop));
    }

    [Fact]
    public void RearrangingExistingObjectsKeepsIdentityWordingStyleSizeAndArrowEndpoints()
    {
        var desktop=Guid.NewGuid();var a=new CanvasAnnotation(Guid.NewGuid(),desktop,AnnotationKind.Text,new(800,700),new(1300,900),"#C084FC",6,"Exact wording");
        var b=a with { Id=Guid.NewGuid(),A=new(1500,700),B=new(2000,900),Text="Second" };
        var arrow=new CanvasAnnotation(Guid.NewGuid(),desktop,AnnotationKind.Arrow,new(1300,800),new(1500,800));
        var context=new ComposerContext(desktop,0,new(0,0),[a,b,arrow],[]) { PreserveWording=true };
        var plan=new ComposerPlan("Layout",[new(a.Id.ToString(),"note","Model rewrite ignored","#FFFFFF"),new(b.Id.ToString(),"note","Ignored","#FFFFFF")],[],[]);
        var preview=CanvasComposer.Prepare(context,plan);var arranged=preview.Items.Single(i=>i.Id==a.Id);
        Assert.Equal(a.Text,arranged.Text);Assert.Equal(a.Color,arranged.Color);Assert.Equal(a.Stroke,arranged.Stroke);Assert.Equal(a.Bounds.Width,arranged.Bounds.Width);
        var link=preview.Items.Single(i=>i.Id==arrow.Id);Assert.Equal(arranged.Bounds.X+arranged.Bounds.Width,link.A.X);
        Assert.False(CanvasSelection.Intersects(arranged.Bounds,preview.Items.Single(i=>i.Id==b.Id).Bounds));
        Assert.Throws<ArgumentException>(()=>CanvasComposer.Prepare(context,plan with { Nodes=[new("new","note","Copy","#FFFFFF")] }));
        Assert.Throws<ArgumentException>(()=>CanvasComposer.Prepare(context,plan with { Edits=[new(a.Id.ToString(),"rewrite")] }));
    }

    [Fact]
    public void UnsharedIdentitiesAndWindowReferenceIdsCannotBeUsedAsEditableNodes()
    {
        var reference=new ComposerReference(Guid.NewGuid(),"window","Editor",new(0,0,800,600));
        var context=new ComposerContext(Guid.NewGuid(),0,new(0,0),[],[]) { SharedReferences=[reference] };
        Assert.Throws<ArgumentException>(()=>CanvasComposer.Prepare(context,new("bad",[new(reference.Id.ToString(),"note","x","#FFFFFF")],[],[])));
        var preview=CanvasComposer.Prepare(context,new("Summary",[new("new","note","Summary","#FFFFFF")],[],[]));
        Assert.Equal(reference,Assert.Single(preview.References));Assert.NotEqual(reference.Id,Assert.Single(preview.Items).Id);
    }
}
