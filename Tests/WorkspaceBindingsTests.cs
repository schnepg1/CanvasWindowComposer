using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class WorkspaceBindingsTests
{
    [Fact]
    public void UnbindingReleasesItemIndexAndChangesRevision()
    {
        var bindings = new WorkspaceBindings();
        var first = bindings.Observe(new((IntPtr)42, 7, 100), out _);
        var next = bindings.Observe(new((IntPtr)43, 7, 100), out _);
        var id = Guid.NewGuid();
        Assert.True(bindings.Bind(first, id));
        long revision = bindings.Revision;
        bindings.Unbind(first);
        Assert.True(bindings.Revision > revision);
        Assert.Null(bindings.ForItem(id));
        Assert.True(bindings.Bind(next, id));
        Assert.Same(next, bindings.ForItem(id));
    }

    [Fact]
    public void ReplacingOrRemovingHandleReleasesItemWithoutLosingRemovedGeometryIdentity()
    {
        var bindings = new WorkspaceBindings();
        var old = bindings.Observe(new((IntPtr)42, 7, 100), out _);
        var id = Guid.NewGuid();
        bindings.Bind(old, id);
        var replacement = bindings.Observe(new((IntPtr)42, 7, 200), out var replaced);
        Assert.True(replaced);
        Assert.Null(bindings.ForItem(id));
        Assert.True(bindings.Bind(replacement, id));
        var removed = bindings.Remove((IntPtr)42);
        Assert.Equal(id, removed!.ItemId);
        Assert.Null(bindings.ForItem(id));
        var newer = bindings.Observe(new((IntPtr)43, 8, 300), out _);
        Assert.True(bindings.Bind(newer, id));
        bindings.Unbind(removed);
        Assert.Same(newer, bindings.ForItem(id));
        bindings.Clear();
        Assert.Null(bindings.ForItem(id));
    }
}
