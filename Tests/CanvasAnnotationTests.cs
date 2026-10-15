using CanvasDesktop.CanvasView;

namespace CanvasDesktop.Tests;

public class CanvasAnnotationTests
{
    private static readonly Guid Desktop=Guid.NewGuid();
    private static CanvasAnnotation Item(AnnotationKind kind=AnnotationKind.Arrow) => new(Guid.NewGuid(),Desktop,kind,new(10,20),new(110,120));
    private static CanvasPointer Pointer(double x,double y,CanvasModifiers flags=CanvasModifiers.None,CanvasViewTransform? view=null)
        =>new(0,new(x,y),CanvasPointerButton.Left,flags,view??new CanvasViewTransform(0,0,1,new(0,0,1920,1080)));

    [Fact]
    public void InvalidEditsNeverChangeDocumentOrHistory()
    {
        var item=Item(); var doc=new AnnotationDocument([item]);
        Assert.Throws<ArgumentException>(()=>doc.Put(item with { A=new(double.NaN,0) }));
        Assert.Throws<ArgumentException>(()=>doc.Put(item with { Stroke=double.PositiveInfinity }));
        Assert.Throws<ArgumentException>(()=>doc.Put(item with { Color="#BAD" }));
        Assert.Throws<ArgumentException>(()=>doc.Put(item with { DesktopId=Guid.NewGuid() }));
        Assert.Equal(item,Assert.Single(doc.Items)); Assert.False(doc.Undo(Desktop));
        Assert.Throws<ArgumentException>(()=>new AnnotationDocument([item,item]));
    }
    [Fact]
    public void UndoRedoStayOnTheirDesktopAndNewEditClearsRedo()
    {
        var a=Item(); var b=Item() with { DesktopId=Guid.NewGuid() }; var doc=new AnnotationDocument();
        doc.Put(a);doc.Put(b);doc.Put(a.Translate(40,20));
        Assert.True(doc.Undo(Desktop));Assert.Contains(a,doc.Items);Assert.Contains(b,doc.Items);
        Assert.True(doc.Undo(Desktop,true));Assert.Contains(a.Translate(40,20),doc.Items);
        Assert.True(doc.Undo(Desktop));doc.Delete(Desktop,a.Id);
        Assert.False(doc.Undo(Desktop,true));Assert.Equal(b,Assert.Single(doc.Items));
    }
    [Fact]
    public void NoOpEditsDoNotCreateUndoSteps()
    {
        var item=Item();var doc=new AnnotationDocument([item]);doc.Put(item);doc.Delete(Desktop,Guid.NewGuid());
        Assert.False(doc.Undo(Desktop));
    }
    [Theory]
    [InlineData(AnnotationMode.Arrow,AnnotationKind.Arrow)]
    [InlineData(AnnotationMode.Rectangle,AnnotationKind.Rectangle)]
    [InlineData(AnnotationMode.Ellipse,AnnotationKind.Ellipse)]
    public void PreviewIsTransientAndReleaseCommitsExactlyOnce(AnnotationMode mode,AnnotationKind kind)
    {
        var doc=new AnnotationDocument();var tool=new AnnotationCanvasTool(doc,()=>Desktop);tool.SetMode(mode);
        Assert.True(tool.PointerDown(Pointer(100,100)));tool.PointerMove(Pointer(200,180));
        Assert.Empty(doc.Items);Assert.NotNull(tool.Preview);tool.PointerUp(Pointer(200,180));tool.PointerUp(Pointer(300,300));
        Assert.Equal(kind,Assert.Single(doc.Items).Kind);Assert.True(doc.Undo(Desktop));Assert.Empty(doc.Items);Assert.False(doc.Undo(Desktop));
    }
    [Fact]
    public void CancellationStaleReleaseAndDesktopSwitchDoNotCommit()
    {
        var desktop=Desktop;var doc=new AnnotationDocument();var tool=new AnnotationCanvasTool(doc,()=>desktop);tool.SetMode(AnnotationMode.Arrow);
        tool.PointerDown(Pointer(0,0));tool.PointerMove(Pointer(100,100));tool.Cancel();tool.PointerUp(Pointer(100,100));Assert.Empty(doc.Items);
        tool.PointerDown(Pointer(0,0));desktop=Guid.NewGuid();tool.PointerUp(Pointer(100,100));Assert.Empty(doc.Items);Assert.Null(tool.Preview);
    }
    [Fact]
    public void ShiftConstraintsAndReverseDirectionGeometryAreStable()
    {
        Assert.Equal(new CanvasPoint(-30,-30),AnnotationGeometry.Constrain(new(0,0),new(-10,-30),false));
        var end=AnnotationGeometry.Constrain(new(0,0),new(100,10),true);Assert.Equal(0,end.Y,5);
        var doc=new AnnotationDocument();var tool=new AnnotationCanvasTool(doc,()=>Desktop);tool.SetMode(AnnotationMode.Rectangle);
        tool.PointerDown(Pointer(100,100));tool.PointerUp(Pointer(20,60,CanvasModifiers.Shift));
        Assert.Equal(new CanvasRect(20,20,80,80),Assert.Single(doc.Items).Bounds);
    }
    [Fact]
    public void SelectionMovesAndResizesWithOneUndoStepPerGesture()
    {
        var item=Item(AnnotationKind.Rectangle);var doc=new AnnotationDocument([item]);var tool=new AnnotationCanvasTool(doc,()=>Desktop);tool.SetMode(AnnotationMode.Select);
        tool.PointerDown(Pointer(50,60));tool.PointerUp(Pointer(70,80));Assert.Equal(item.Translate(20,20),Assert.Single(doc.Items));
        tool.PointerDown(Pointer(130,140));tool.PointerUp(Pointer(180,170));Assert.Equal(150,Assert.Single(doc.Items).Bounds.Width);
        Assert.True(doc.Undo(Desktop));Assert.Equal(item.Translate(20,20),Assert.Single(doc.Items));
        Assert.True(doc.Undo(Desktop));Assert.Equal(item,Assert.Single(doc.Items));
    }
    [Fact]
    public void EndpointsCanBeEditedAndDegenerateResizeIsRejected()
    {
        var item=Item();var doc=new AnnotationDocument([item]);var tool=new AnnotationCanvasTool(doc,()=>Desktop);tool.SetMode(AnnotationMode.Select);tool.Select(item.Id);
        tool.PointerDown(Pointer(10,20));tool.PointerUp(Pointer(110,120));Assert.Equal(item,Assert.Single(doc.Items));
        tool.PointerDown(Pointer(10,20));tool.PointerUp(Pointer(0,50));Assert.Equal(new CanvasPoint(0,50),Assert.Single(doc.Items).A);
    }
    [Fact]
    public void NavigateAndMiddleButtonRemainAvailableToHost()
    {
        var tool=new AnnotationCanvasTool(new(),()=>Desktop);Assert.False(tool.PointerDown(Pointer(0,0)));
        tool.SetMode(AnnotationMode.Arrow);Assert.False(tool.PointerDown(Pointer(0,0) with { Button=CanvasPointerButton.Middle }));
    }
    [Fact]
    public void TextPlacementRequestsEditorWithoutCreatingEmptyNote()
    {
        var doc=new AnnotationDocument();var tool=new AnnotationCanvasTool(doc,()=>Desktop);tool.SetMode(AnnotationMode.Text);
        CanvasAnnotation? requested=null;tool.EditTextRequested+=item=>requested=item;
        tool.PointerDown(Pointer(50,60));tool.PointerUp(Pointer(50,60));Assert.Empty(doc.Items);Assert.False(tool.HasPendingInteraction);
        Assert.NotNull(requested);Assert.Equal(new CanvasRect(50,60,240,120),requested.Bounds);
    }
    [Fact]
    public void HitToleranceUsesDipsOnNegativeOriginMixedDpiMonitor()
    {
        var view=new CanvasViewTransform(0,0,.25,new(-1920,-200,1920,1080),2,1.5);
        var item=Item() with { A=new(0,0),B=new(1000,0) };
        Assert.True(AnnotationGeometry.Hit(item,new(400,36),view)); // 6 DIPs from the line.
        Assert.False(AnnotationGeometry.Hit(item,new(400,100),view));
    }
    [Fact]
    public void ItemLimitAlsoAppliesWhenUndoWouldRestoreDeletedItemsOnAnotherDesktop()
    {
        var items=Enumerable.Range(0,AnnotationDocument.MaximumItems).Select(_=>Item()).ToArray();var doc=new AnnotationDocument(items);
        doc.Delete(Desktop,items[0].Id);var other=Item() with { DesktopId=Guid.NewGuid() };doc.Put(other);
        Assert.False(doc.Undo(Desktop));Assert.Equal(AnnotationDocument.MaximumItems,doc.Items.Count);
    }
}
