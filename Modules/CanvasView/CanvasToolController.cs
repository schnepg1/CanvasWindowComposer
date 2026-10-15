namespace CanvasDesktop.CanvasView;

public enum CanvasPointerButton { None, Left, Middle, Right }
[Flags]
public enum CanvasModifiers { None = 0, Shift = 1, Control = 2, Alt = 4 }
public enum CanvasCursor { Default, Crosshair, ResizeNorthWestSouthEast, ResizeNorthEastSouthWest }
public enum CanvasInteractionKind { None, Tool, Pan, Window }

public readonly record struct CanvasPointer(int SurfaceId, CanvasPoint Position,
    CanvasPointerButton Button, CanvasModifiers Modifiers, CanvasViewTransform View)
{
    public CanvasPoint World => View.LocalToWorld(Position);
}

/// <summary>A tool receives data only. Capture, native windows and rendering belong to its host.</summary>
public interface ICanvasTool
{
    bool HasPendingInteraction { get; }
    bool PointerDown(CanvasPointer pointer);
    void PointerMove(CanvasPointer pointer);
    void PointerUp(CanvasPointer pointer);
    void Cancel();
    CanvasCursor CursorAt(CanvasPointer pointer) => CanvasCursor.Default;
}

/// <summary>
/// One pointer owner across tools and host navigation. The initiating surface and
/// button retain ownership until release/cancel; secondary input cannot steal it.
/// State clears before callbacks, so native capture-loss and reentrant commits are safe.
/// </summary>
public sealed class CanvasToolController
{
    private ICanvasTool? _tool;
    private CanvasPointerButton _button;
    public CanvasInteractionKind Kind { get; private set; }
    public int? CapturedSurface { get; private set; }
    public bool Active => Kind != CanvasInteractionKind.None;
    public bool ToolHasPendingInteraction => _tool?.HasPendingInteraction == true;
    public bool HasPendingInteraction => Active || ToolHasPendingInteraction;

    public void SetTool(ICanvasTool? tool)
    {
        Cancel();
        _tool = tool;
    }

    /// <returns>True when a tool or existing owner consumes the down event.</returns>
    public bool TryBeginTool(CanvasPointer pointer)
    {
        if (Active) return true;
        if (pointer.Button == CanvasPointerButton.None || _tool?.PointerDown(pointer) != true) return false;
        Begin(CanvasInteractionKind.Tool, pointer);
        return true;
    }

    public bool TryBeginNavigation(CanvasInteractionKind kind, CanvasPointer pointer)
    {
        if (kind is not (CanvasInteractionKind.Pan or CanvasInteractionKind.Window))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (Active || pointer.Button == CanvasPointerButton.None) return false;
        Begin(kind, pointer);
        return true;
    }

    private void Begin(CanvasInteractionKind kind, CanvasPointer pointer)
    {
        Kind = kind; CapturedSurface = pointer.SurfaceId; _button = pointer.Button;
    }

    public bool OwnsSurface(int surfaceId) => Active && CapturedSurface == surfaceId;

    public bool MoveTool(CanvasPointer pointer)
    {
        if (Kind != CanvasInteractionKind.Tool) return false;
        if (OwnsSurface(pointer.SurfaceId)) _tool!.PointerMove(pointer);
        return true;
    }

    public bool CompleteTool(CanvasPointer pointer)
    {
        if (Kind != CanvasInteractionKind.Tool) return false;
        if (MatchesRelease(pointer))
        {
            var tool = _tool!;
            ResetOwner();
            tool.PointerUp(pointer);
        }
        return true;
    }

    public CanvasInteractionKind EndNavigation(CanvasPointer pointer)
    {
        if (Kind is not (CanvasInteractionKind.Pan or CanvasInteractionKind.Window) || !MatchesRelease(pointer))
            return CanvasInteractionKind.None;
        var kind = Kind;
        ResetOwner();
        return kind;
    }

    public CanvasCursor CursorAt(CanvasPointer pointer) => _tool?.CursorAt(pointer) ?? CanvasCursor.Default;

    public bool Cancel()
    {
        bool pending = HasPendingInteraction;
        ResetOwner();
        _tool?.Cancel();
        return pending;
    }

    private bool MatchesRelease(CanvasPointer pointer) => OwnsSurface(pointer.SurfaceId) && pointer.Button == _button;
    private void ResetOwner()
    {
        Kind = CanvasInteractionKind.None; CapturedSurface = null; _button = CanvasPointerButton.None;
    }
}
