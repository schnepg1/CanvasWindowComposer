using CanvasDesktop.CanvasView;

namespace CanvasDesktop.Tests;

public class CanvasToolControllerTests
{
    private sealed class Tool : ICanvasTool
    {
        public bool HasPendingInteraction { get; set; }
        public bool Consume = true;
        public int Downs, Moves, Commits, Cancels;
        public Action? OnCommit;
        public bool PointerDown(CanvasPointer p) { Downs++; HasPendingInteraction = Consume; return Consume; }
        public void PointerMove(CanvasPointer p) => Moves++;
        public void PointerUp(CanvasPointer p) { HasPendingInteraction = false; Commits++; OnCommit?.Invoke(); }
        public void Cancel() { HasPendingInteraction = false; Cancels++; }
    }

    private static CanvasPointer Pointer(int surface = 0, CanvasPointerButton button = CanvasPointerButton.Left)
        => new(surface, new(20, 30), button, CanvasModifiers.None, new(0, 0, 1, default));

    [Fact]
    public void ToolOwnsTheEntireSequenceAndCommitsOnceDespiteSecondaryButtonsAndSurfaces()
    {
        var tool = new Tool(); var controller = new CanvasToolController(); controller.SetTool(tool);
        Assert.True(controller.TryBeginTool(Pointer()));
        Assert.True(controller.TryBeginTool(Pointer(1, CanvasPointerButton.Middle)));
        Assert.False(controller.TryBeginNavigation(CanvasInteractionKind.Pan, Pointer()));
        Assert.Equal(1, tool.Downs); Assert.Equal(0, controller.CapturedSurface);
        controller.MoveTool(Pointer(1)); Assert.Equal(0, tool.Moves);
        controller.MoveTool(Pointer()); Assert.Equal(1, tool.Moves);
        controller.CompleteTool(Pointer(1));
        controller.CompleteTool(Pointer(0, CanvasPointerButton.Right));
        Assert.True(controller.Active); Assert.Equal(0, tool.Commits);
        Assert.True(controller.CompleteTool(Pointer()));
        Assert.False(controller.CompleteTool(Pointer()));
        Assert.Equal(1, tool.Commits); Assert.Null(controller.CapturedSurface);
    }

    [Fact]
    public void OwnershipClearsBeforeCommitSoCaptureLossCannotCancelOrCommitTheSameGestureAgain()
    {
        var tool = new Tool(); var controller = new CanvasToolController(); controller.SetTool(tool);
        tool.OnCommit = () =>
        {
            Assert.False(controller.Active); Assert.Null(controller.CapturedSurface);
            Assert.False(controller.Cancel());
            Assert.False(controller.CompleteTool(Pointer()));
        };
        controller.TryBeginTool(Pointer()); controller.CompleteTool(Pointer());
        Assert.Equal(1, tool.Commits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancelDiscardsArmedOrActiveToolWithoutCommitting(bool active)
    {
        var tool = new Tool(); var controller = new CanvasToolController(); controller.SetTool(tool);
        if (active) controller.TryBeginTool(Pointer());
        else tool.HasPendingInteraction = true;
        Assert.True(controller.Cancel()); Assert.False(controller.HasPendingInteraction);
        Assert.False(controller.Cancel()); Assert.Null(controller.CapturedSurface);
        Assert.False(controller.CompleteTool(Pointer())); Assert.Equal(0, tool.Commits);
    }

    [Fact]
    public void SwitchingToolsCancelsOldPreviewAndReleasesItsSurface()
    {
        var old = new Tool(); var next = new Tool(); var controller = new CanvasToolController();
        controller.SetTool(old); controller.TryBeginTool(Pointer(2)); controller.SetTool(next);
        Assert.False(old.HasPendingInteraction); Assert.Null(controller.CapturedSurface);
        Assert.Equal(0, old.Commits);
        controller.TryBeginTool(Pointer(3)); controller.CompleteTool(Pointer(3));
        Assert.Equal(1, next.Commits);
    }

    [Theory]
    [InlineData(CanvasInteractionKind.Pan)]
    [InlineData(CanvasInteractionKind.Window)]
    public void NavigationOnlyEndsForItsInitiatingButtonAndCapturedSurface(CanvasInteractionKind kind)
    {
        var tool = new Tool { Consume = false }; var controller = new CanvasToolController(); controller.SetTool(tool);
        Assert.False(controller.TryBeginTool(Pointer()));
        Assert.True(controller.TryBeginNavigation(kind, Pointer()));
        Assert.True(controller.TryBeginTool(Pointer(1, CanvasPointerButton.Middle))); // Existing owner consumes it.
        Assert.Equal(1, tool.Downs);
        Assert.Equal(CanvasInteractionKind.None, controller.EndNavigation(Pointer(1)));
        Assert.Equal(CanvasInteractionKind.None, controller.EndNavigation(Pointer(0, CanvasPointerButton.Middle)));
        Assert.Equal(kind, controller.EndNavigation(Pointer()));
        Assert.False(controller.Active); Assert.Equal(0, tool.Commits);
    }

    [Fact]
    public void FailedCommitStillReleasesLogicalOwnership()
    {
        var tool = new Tool(); var controller = new CanvasToolController(); controller.SetTool(tool);
        tool.OnCommit = () => throw new InvalidOperationException("Commit failed");
        controller.TryBeginTool(Pointer());
        Assert.Throws<InvalidOperationException>(() => controller.CompleteTool(Pointer()));
        Assert.False(controller.Active); Assert.Null(controller.CapturedSurface);
    }
}
