using CanvasDesktop.CanvasView;

namespace CanvasDesktop.Tests;

public class CanvasSelectionFocusTests
{
    private static readonly Guid Parent = Guid.NewGuid();
    private static readonly Guid Child = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();
    private static readonly Guid Window = Guid.NewGuid();

    private static IReadOnlyDictionary<Guid, Guid?> Hierarchy => new Dictionary<Guid, Guid?>
    {
        [Parent] = null, [Child] = Parent, [Other] = null
    };

    [Fact]
    public void ASingleSelectedProjectIsFocused()
        => Assert.Equal(Parent, CanvasSelection.FocusedProject([new(CanvasItemKind.Project, Parent)], () => Hierarchy));

    [Fact]
    public void AProjectStaysFocusedWhileItsMembersAreSelectedToo()
    {
        // Selecting a project also selects the windows inside it.
        var ids = new[] { new CanvasItemId(CanvasItemKind.Project, Parent), new(CanvasItemKind.Window, Window) };
        Assert.Equal(Parent, CanvasSelection.FocusedProject(ids, () => Hierarchy));
    }

    [Fact]
    public void TwoUnrelatedProjectsShareNoFocus()
    {
        var ids = new[] { new CanvasItemId(CanvasItemKind.Project, Parent), new(CanvasItemKind.Project, Other) };
        Assert.Null(CanvasSelection.FocusedProject(ids, () => Hierarchy));
    }

    [Fact]
    public void ANestedProjectSharesTheFocusOfItsParent()
    {
        var ids = new[] { new CanvasItemId(CanvasItemKind.Project, Parent), new(CanvasItemKind.Project, Child) };
        Assert.Equal(Parent, CanvasSelection.FocusedProject(ids, () => Hierarchy));
    }

    [Fact]
    public void ANestedProjectOnItsOwnIsFocused()
        => Assert.Equal(Child, CanvasSelection.FocusedProject([new(CanvasItemKind.Project, Child)], () => Hierarchy));

    [Fact]
    public void ASelectionWithoutAProjectHasNoFocus()
    {
        Assert.Null(CanvasSelection.FocusedProject([new(CanvasItemKind.Window, Window)], () => Hierarchy));
        Assert.Null(CanvasSelection.FocusedProject([], () => Hierarchy));
    }
}
