using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class CanvasWindowPlacementTests
{
    [Theory]
    [InlineData((int)WindowState.Normal)]
    [InlineData((int)WindowState.Minimized)]
    [InlineData((int)WindowState.Maximized)]
    public void MoveAndUndoUpdateRestoreBoundsWhilePreservingCurrentState(int stateValue)
    {
        var state=(WindowState)stateValue;
        var canvas=new Canvas();var api=new FakeWindowApi();var handle=(IntPtr)42;
        api.AddWindow(handle,100,200,600,400);
        var before=new WorldRect { X=100,Y=200,W=600,H=400,State=state };canvas.RestoreWindow(handle,before);
        var after=before;after.X+=300;after.Y-=50;
        Assert.True(CanvasWindowPlacement.ApplySelection(canvas,api,[(handle,after)]));
        Assert.Equal(state,canvas.GetWindowState(handle));Assert.Equal(400,canvas.Windows[handle].X);
        if(state==WindowState.Normal) Assert.Empty(api.NativeStateCalls);
        else
        {
            var call=Assert.Single(api.NativeStateCalls);Assert.Equal(state,call.state);Assert.Equal(canvas.WorldToScreen(after),call.rect);
        }
        Assert.True(CanvasWindowPlacement.ApplySelection(canvas,api,[(handle,before)]));
        Assert.Equal(state,canvas.GetWindowState(handle));Assert.Equal(100,canvas.Windows[handle].X);
        if(state!=WindowState.Normal) Assert.Equal(canvas.WorldToScreen(before),api.NativeStateCalls[^1].rect);
    }

    [Fact]
    public void NativeFailureKeepsLogicalMoveAndAllowsRestorePlacementRetryOrUndo()
    {
        var canvas=new Canvas();var api=new FakeWindowApi();var handle=(IntPtr)42;api.AddWindow(handle,100,200,600,400);
        var before=new WorldRect { X=100,Y=200,W=600,H=400,State=WindowState.Minimized };canvas.RestoreWindow(handle,before);
        var after=before;after.X+=300;api.FailedMoves.Add(handle);
        Assert.False(CanvasWindowPlacement.ApplySelection(canvas,api,[(handle,after)]));Assert.Equal(400,canvas.Windows[handle].X);
        api.FailedMoves.Clear();Assert.True(CanvasWindowPlacement.ApplySelection(canvas,api,[(handle,before)]));
        Assert.Equal(100,canvas.Windows[handle].X);Assert.Equal(WindowState.Minimized,canvas.GetWindowState(handle));
    }
}
