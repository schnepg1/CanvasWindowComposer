using CanvasDesktop.CanvasView;

namespace CanvasDesktop.Tests;

public class ZoneCanvasToolTests
{
    private static CanvasPointer Pointer(double x, double y, CanvasModifiers modifiers = CanvasModifiers.None)
        => new(0, new(x, y), CanvasPointerButton.Left, modifiers, new(0, 0, 1, new(0, 0, 1920, 1080)));

    [Fact]
    public void ShiftDrawingTakesPriorityOverCornersAndCommitsOnlyOnRelease()
    {
        var zone = new ZoneView(Guid.NewGuid(), "Area", "#60A5FA", new WorldRect { X = 100, Y = 200, W = 800, H = 600 });
        var tool = new ZoneCanvasTool(() => true, () => [zone], _ => []);
        var controller = new CanvasToolController(); controller.SetTool(tool);
        var edits = new List<ZoneGestureResult>(); tool.Completed += edits.Add;
        Assert.True(controller.TryBeginTool(Pointer(100, 200, CanvasModifiers.Shift)));
        controller.MoveTool(Pointer(300, 400)); Assert.Empty(edits);
        controller.CompleteTool(Pointer(300, 400));
        var edit = Assert.Single(edits); Assert.Equal(ZoneGestureKind.Draw, edit.Kind);
        Assert.Equal(200, edit.Bounds.W); Assert.Equal(100, zone.Bounds.X);
    }

    [Fact]
    public void ResizeAndMoveUseDetachedGeometryAndCancelLeavesTheZoneAndMembersUnchanged()
    {
        var bounds = new WorldRect { X = 100, Y = 200, W = 800, H = 600 };
        var zone = new ZoneView(Guid.NewGuid(), "Area", "#60A5FA", bounds);
        var member = new WorldRect { X = 300, Y = 400, W = 400, H = 300 };
        var tool = new ZoneCanvasTool(() => true, () => [zone], _ => [member]);
        var controller = new CanvasToolController(); controller.SetTool(tool);
        var edits = new List<ZoneGestureResult>(); tool.Completed += edits.Add;
        controller.TryBeginTool(Pointer(100, 200));
        Assert.Equal(ZoneGestureKind.Resize, tool.Gesture.Kind);
        controller.MoveTool(Pointer(150, 250)); controller.Cancel();
        controller.TryBeginTool(Pointer(120, 220));
        Assert.Equal(ZoneGestureKind.Move, tool.Gesture.Kind);
        controller.MoveTool(Pointer(220, 320));
        Assert.Equal(400, Assert.Single(tool.Gesture.Ghosts).X);
        controller.Cancel(); controller.CompleteTool(Pointer(220, 320));
        Assert.Empty(edits); Assert.Equal(bounds, zone.Bounds); Assert.Equal(300, member.X);
    }

    [Fact]
    public void UnhandledInputFallsThroughAndDisabledToolCannotDraw()
    {
        var tool = new ZoneCanvasTool(() => false, () => [], _ => []); tool.Arm();
        Assert.False(tool.PointerDown(Pointer(100, 200, CanvasModifiers.Shift)));
        var enabled = new ZoneCanvasTool(() => true, () => [], _ => []);
        Assert.False(enabled.PointerDown(Pointer(100, 200)));
    }
}
