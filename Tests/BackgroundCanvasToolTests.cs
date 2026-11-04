using CanvasDesktop;
using CanvasDesktop.CanvasView;

namespace CanvasDesktop.Tests;

public class BackgroundCanvasToolTests : IDisposable
{
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"canvas-background-tool-"+Guid.NewGuid());
    private readonly Guid _desktop=Guid.NewGuid();
    private BackgroundService Service()
        => new(new BackgroundPersistence(Path.Combine(_directory,"backgrounds.json")),()=>_desktop,()=>[]);
    private BackgroundSurface Item(bool locked=false)
        => new(Guid.NewGuid(),_desktop,"Study","builtin:study",new(){X=100,Y=100,W=8000,H=4000},Locked:locked);
    private static CanvasPointer Pointer(double x,double y,CanvasModifiers modifiers=CanvasModifiers.None,int monitor=0)
        =>new(monitor,new(x,y),CanvasPointerButton.Left,modifiers,new CanvasViewTransform(0,0,1,default));

    [Fact]
    public void LockedAndInteriorBackgroundsNeverInterceptNormalContent()
    {
        var service=Service();var item=Item(true);service.Add(item);var tool=new BackgroundCanvasTool(service);
        Assert.False(tool.Select(item.Id));Assert.False(tool.PointerDown(Pointer(100,100)));
        service.Update(item with{Locked=false});Assert.True(tool.Select(item.Id));
        Assert.False(tool.PointerDown(Pointer(300,300)));Assert.True(tool.PointerDown(Pointer(400,100)));
    }
    [Fact]
    public void DragPreviewsWithoutMutatingDocumentAndCancelsWithoutHistory()
    {
        var service=Service();var item=Item();service.Add(item);var tool=new BackgroundCanvasTool(service);tool.Select(item.Id);
        tool.Completed+=b=>service.Update(b);
        Assert.True(tool.PointerDown(Pointer(400,100)));tool.PointerMove(Pointer(700,300));
        Assert.Equal(400,tool.Preview!.Bounds.X);Assert.Equal(100,service.Items[0].Bounds.X);
        tool.Cancel();Assert.Null(tool.Preview);Assert.Equal(100,service.Items[0].Bounds.X);
        tool.PointerDown(Pointer(400,100));tool.PointerUp(Pointer(700,300));
        Assert.Equal(400,service.Items[0].Bounds.X);Assert.True(service.Undo());Assert.Equal(100,service.Items[0].Bounds.X);
    }
    [Fact]
    public void ShiftResizeKeepsAspectRatioAndOppositeCorner()
    {
        var service=Service();var item=Item();service.Add(item);var tool=new BackgroundCanvasTool(service);tool.Select(item.Id);
        tool.PointerDown(Pointer(8100,4100));tool.PointerMove(Pointer(12100,5000,CanvasModifiers.Shift));
        var b=tool.Preview!.Bounds;Assert.Equal(2,b.W/b.H,8);Assert.Equal(100,b.X);Assert.Equal(100,b.Y);
    }
    public void Dispose() { if(Directory.Exists(_directory))Directory.Delete(_directory,true); }
}
