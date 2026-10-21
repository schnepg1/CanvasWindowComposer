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
    [Fact]
    public void TextSizeDefaultsValidatesAndIsEditableAsOneHistoryStep()
    {
        var item=Item(AnnotationKind.Text) with { Text="Note" };
        Assert.Equal(AnnotationDocument.DefaultFontSize,item.FontSize);
        var doc=new AnnotationDocument([item]);
        Assert.Throws<ArgumentException>(()=>doc.Put(item with { FontSize=AnnotationDocument.MinimumFontSize-1 }));
        Assert.Throws<ArgumentException>(()=>doc.Put(item with { FontSize=AnnotationDocument.MaximumFontSize+1 }));
        Assert.Throws<ArgumentException>(()=>doc.Put(item with { FontSize=double.NaN }));
        var tool=new AnnotationCanvasTool(doc,()=>Desktop);tool.SetMode(AnnotationMode.Select);tool.Select(item.Id);
        tool.SetFontSize(32);
        Assert.Equal(32,Assert.Single(doc.Items).FontSize);
        Assert.True(doc.Undo(Desktop));Assert.Equal(AnnotationDocument.DefaultFontSize,Assert.Single(doc.Items).FontSize);
        Assert.Throws<ArgumentException>(()=>tool.SetFontSize(4));
        Assert.Equal(32,tool.FontSize);
    }
    [Fact]
    public void TextStyleDefaultsValidateAndApplyWithoutResettingOtherFields()
    {
        var item=Item(AnnotationKind.Text) with
        {
            Text="Note", TextAlign=AnnotationTextAlign.Center,
            Emphasis=AnnotationTextEmphasis.Bold, CardFill=AnnotationCardFill.Tinted
        };
        Assert.Equal(AnnotationTextAlign.Center,item.TextAlign);
        Assert.Equal(AnnotationTextEmphasis.Bold,item.Emphasis);
        Assert.Equal(AnnotationCardFill.Tinted,item.CardFill);
        var doc=new AnnotationDocument([item]);
        Assert.Throws<ArgumentException>(()=>doc.Put(item with { TextAlign=(AnnotationTextAlign)99 }));
        Assert.Throws<ArgumentException>(()=>doc.Put(item with { Emphasis=(AnnotationTextEmphasis)99 }));
        Assert.Throws<ArgumentException>(()=>doc.Put(item with { CardFill=(AnnotationCardFill)99 }));
        var tool=new AnnotationCanvasTool(doc,()=>Desktop);tool.SetMode(AnnotationMode.Select);tool.Select(item.Id);
        tool.SetTextStyle(emphasis:AnnotationTextEmphasis.Italic);
        var changed=Assert.Single(doc.Items);
        Assert.Equal(AnnotationTextEmphasis.Italic,changed.Emphasis);
        Assert.Equal(item.TextAlign,changed.TextAlign);
        Assert.Equal(item.CardFill,changed.CardFill);
        Assert.True(doc.Undo(Desktop));Assert.Equal(item,Assert.Single(doc.Items));
        tool.SetTextStyle(align:AnnotationTextAlign.Right);
        changed=Assert.Single(doc.Items);
        Assert.Equal(AnnotationTextAlign.Right,changed.TextAlign);
        Assert.Equal(item.Emphasis,changed.Emphasis);
        Assert.Equal(AnnotationTextAlign.Right,tool.TextAlign);
        Assert.Throws<ArgumentException>(()=>tool.SetTextStyle(align:(AnnotationTextAlign)99));
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
    public void DraggingInTextModeKeepsTheDraggedCardSizeAndStyle()
    {
        var doc=new AnnotationDocument();var tool=new AnnotationCanvasTool(doc,()=>Desktop);tool.SetMode(AnnotationMode.Text);
        tool.SetTextStyle(AnnotationTextAlign.Right,AnnotationTextEmphasis.BoldItalic,AnnotationCardFill.None);
        CanvasAnnotation? requested=null;tool.EditTextRequested+=item=>requested=item;
        tool.PointerDown(Pointer(100,100));tool.PointerMove(Pointer(460,340));tool.PointerUp(Pointer(460,340));
        Assert.NotNull(requested);
        Assert.Equal(new CanvasRect(100,100,360,240),requested.Bounds);
        Assert.Equal(AnnotationTextAlign.Right,requested.TextAlign);
        Assert.Equal(AnnotationTextEmphasis.BoldItalic,requested.Emphasis);
        Assert.Equal(AnnotationCardFill.None,requested.CardFill);
        requested=null;
        tool.PointerDown(Pointer(500,400));tool.PointerUp(Pointer(510,410));
        Assert.NotNull(requested);
        // A drag below the minimum still produces a usable card.
        Assert.Equal(new CanvasRect(500,400,
            AnnotationCanvasTool.MinimumTextCardWidth,AnnotationCanvasTool.MinimumTextCardHeight),requested.Bounds);
        requested=null;
        tool.PointerDown(Pointer(700,500));tool.PointerUp(Pointer(600,430));
        Assert.NotNull(requested);Assert.Equal(new CanvasRect(600,430,100,70),requested.Bounds);
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
    [Theory]
    [InlineData(.25,1)]
    [InlineData(.5,1.5)]
    [InlineData(1,2)]
    [InlineData(2,1)]
    public void LargerResizeTargetsKeepGrabOffsetAcrossZoomAndDpi(double zoom,double dpi)
    {
        var view=new CanvasViewTransform(40,60,zoom,new(-1920,-200,1920,1080),dpi,dpi);
        foreach (var kind in Enum.GetValues<AnnotationKind>())
        {
            var item=new CanvasAnnotation(Guid.NewGuid(),Desktop,kind,new(1000,700),new(1600,1100),Text:"Note");
            var doc=new AnnotationDocument([item]);var tool=new AnnotationCanvasTool(doc,()=>Desktop);
            tool.SetMode(AnnotationMode.Select);tool.Select(item.Id);
            var grip=AnnotationGeometry.Handles(item)[kind==AnnotationKind.Arrow?1:2];
            var position=view.WorldToLocal(grip);
            // Outside the old 8-DIP target, inside the new 14-DIP target.
            var down=new CanvasPointer(0,new(position.X+10*dpi,position.Y),CanvasPointerButton.Left,CanvasModifiers.None,view);
            Assert.True(tool.HasHandleAt(down));
            tool.PointerDown(down);tool.PointerUp(down);
            Assert.Equal(item,Assert.Single(doc.Items));Assert.False(doc.Undo(Desktop));
            tool.PointerDown(down);
            tool.PointerUp(down with { Position=new(down.Position.X+60*zoom,down.Position.Y+40*zoom) });
            var resized=Assert.Single(doc.Items);
            Assert.Equal(new CanvasPoint(1660,1140),resized.B);
            Assert.Equal(item.A,resized.A);
            Assert.True(doc.Undo(Desktop));Assert.Equal(item,Assert.Single(doc.Items));
        }
    }

    [Theory]
    [InlineData(4,CanvasCursor.ResizeNorthSouth)]
    [InlineData(5,CanvasCursor.ResizeEastWest)]
    [InlineData(6,CanvasCursor.ResizeNorthSouth)]
    [InlineData(7,CanvasCursor.ResizeEastWest)]
    public void FullEdgesResizeOneAxisAndShowDirectionalCursor(int handle,CanvasCursor cursor)
    {
        var item=new CanvasAnnotation(Guid.NewGuid(),Desktop,AnnotationKind.Text,new(100,100),new(500,400),Text:"Note");
        var doc=new AnnotationDocument([item]);var tool=new AnnotationCanvasTool(doc,()=>Desktop);
        tool.SetMode(AnnotationMode.Select);tool.Select(item.Id);
        var grip=AnnotationGeometry.Handles(item)[handle];
        // Use the edge well away from both the corners and midpoint grips.
        var point=handle is 4 or 6?new CanvasPoint(grip.X+70,grip.Y+6):new CanvasPoint(grip.X+6,grip.Y+60);
        var down=Pointer(point.X,point.Y);
        Assert.True(tool.HasHandleAt(down));Assert.Equal(cursor,tool.CursorAt(down));
        tool.PointerDown(down);tool.PointerUp(Pointer(point.X+30,point.Y+20));
        var resized=Assert.Single(doc.Items).Bounds;
        Assert.Equal(handle is 4 or 6?400:handle==5?430:370,resized.Width);
        Assert.Equal(handle is 5 or 7?300:handle==6?320:280,resized.Height);
        tool.SetMode(AnnotationMode.Navigate);Assert.False(tool.HasHandleAt(down));
    }

    [Fact]
    public void ResizeCancellationAndCrossingTheOppositeCornerAreStable()
    {
        var item=Item(AnnotationKind.Text) with { Text="Note" };
        var doc=new AnnotationDocument([item]);var tool=new AnnotationCanvasTool(doc,()=>Desktop);
        tool.SetMode(AnnotationMode.Select);tool.Select(item.Id);
        tool.PointerDown(Pointer(10,20));tool.PointerMove(Pointer(140,150));tool.Cancel();
        Assert.Equal(item,Assert.Single(doc.Items));Assert.False(doc.Undo(Desktop));
        tool.PointerDown(Pointer(10,20));tool.PointerUp(Pointer(140,150));
        Assert.Equal(new CanvasRect(110,120,30,30),Assert.Single(doc.Items).Bounds);
        Assert.True(doc.Undo(Desktop));Assert.Equal(item,Assert.Single(doc.Items));
    }

}
