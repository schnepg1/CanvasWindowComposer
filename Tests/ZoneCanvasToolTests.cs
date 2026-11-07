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
        // Unselected zone does not show corner handles or allow resize
        Assert.False(controller.TryBeginTool(Pointer(100, 200)));
        Assert.Equal(CanvasCursor.Default, tool.CursorAt(Pointer(100, 200)));
        // Clicking header selects the zone and begins move
        Assert.True(controller.TryBeginTool(Pointer(120, 180)));
        Assert.Equal(zone.Id, tool.SelectedZoneId);
        controller.Cancel();
        // Now that the zone is selected, corner handle is active
        Assert.Equal(CanvasCursor.ResizeNorthWestSouthEast, tool.CursorAt(Pointer(100, 200)));
        Assert.True(controller.TryBeginTool(Pointer(100, 200)));
        Assert.Equal(ZoneGestureKind.Resize, tool.Gesture.Kind);
        controller.MoveTool(Pointer(150, 250)); controller.Cancel();
        // Clicking unhandled point deselects the zone
        Assert.False(controller.TryBeginTool(Pointer(120, 220)));
        Assert.Null(tool.SelectedZoneId);
        Assert.False(controller.TryBeginTool(Pointer(100, 200)));
        // Clicking header selects again
        controller.TryBeginTool(Pointer(120, 180));
        Assert.Equal(ZoneGestureKind.Move, tool.Gesture.Kind);
        controller.MoveTool(Pointer(220, 320));
        Assert.Equal(400, Assert.Single(tool.Gesture.Ghosts).X);
        controller.Cancel(); controller.CompleteTool(Pointer(220, 320));
        Assert.Empty(edits); Assert.Equal(bounds, zone.Bounds); Assert.Equal(300, member.X);
    }

    [Fact]
    public void CornerResizeHandlesAreOnlyInteractiveWhenZoneIsSelected()
    {
        var bounds = new WorldRect { X = 100, Y = 200, W = 800, H = 600 };
        var zone = new ZoneView(Guid.NewGuid(), "My Project", "#60A5FA", bounds);
        var tool = new ZoneCanvasTool(() => true, () => [zone], _ => []);
        var controller = new CanvasToolController(); controller.SetTool(tool);
        int selectionEvents = 0;
        tool.SelectionChanged += () => selectionEvents++;

        // 1. Initial state: no zone selected
        Assert.Null(tool.SelectedZoneId);
        Assert.Equal(CanvasCursor.Default, tool.CursorAt(Pointer(100, 200)));
        Assert.False(tool.IsBoundaryGesture(Pointer(100, 200)));
        Assert.False(controller.TryBeginTool(Pointer(100, 200)));

        // 2. Click zone name (header) -> selects zone
        Assert.True(controller.TryBeginTool(Pointer(120, 180)));
        Assert.Equal(zone.Id, tool.SelectedZoneId);
        Assert.Equal(1, selectionEvents);
        controller.Cancel();

        // 3. Now selected -> resize handles interactive
        Assert.Equal(CanvasCursor.ResizeNorthWestSouthEast, tool.CursorAt(Pointer(100, 200)));
        Assert.True(tool.IsBoundaryGesture(Pointer(100, 200)));
        Assert.True(controller.TryBeginTool(Pointer(100, 200)));
        Assert.Equal(ZoneGestureKind.Resize, tool.Gesture.Kind);
        controller.Cancel();

        // 4. Click outside -> deselects zone, handles disappear
        Assert.False(controller.TryBeginTool(Pointer(120, 220)));
        Assert.Null(tool.SelectedZoneId);
        Assert.Equal(2, selectionEvents);
        Assert.Equal(CanvasCursor.Default, tool.CursorAt(Pointer(100, 200)));
        Assert.False(tool.IsBoundaryGesture(Pointer(100, 200)));

        // 5. SelectZone and Deselect programmatic API
        tool.SelectZone(zone.Id);
        Assert.Equal(zone.Id, tool.SelectedZoneId);
        Assert.Equal(3, selectionEvents);
        tool.Deselect();
        Assert.Null(tool.SelectedZoneId);
        Assert.Equal(4, selectionEvents);
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
