using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class NestedZoneTests
{
    private static WorkspaceZone Area(string name, double x, double size) => new()
        { Name = name, Bounds = new WorldRect { X = x, Y = x, W = size, H = size } };

    private static (WorkspaceDocument Document, WorkspaceZone Parent, WorkspaceZone Child, WorkspaceZone Grandchild) Tree()
    {
        var parent = Area("Project", 0, 100000);
        var child = Area("Research", 100, 2000);
        var grandchild = Area("Notes", 200, 800);
        // Creation order must not affect the tree or paint/hit-test order.
        return (new WorkspaceDocument { Zones = [grandchild, child, parent] }, parent, child, grandchild);
    }

    [Fact]
    public void FullContainmentBuildsDesktopScopedTreeAndEqualOrPartialOverlapsAreIndependent()
    {
        var (doc, parent, child, grandchild) = Tree();
        var partial = Area("Overlap", 99900, 300);
        var equal = Area("Identical", 0, 100000);
        // Keep equal rectangles deterministic without relying on random IDs.
        parent.Id = new Guid("00000000-0000-0000-0000-000000000001");
        equal.Id = new Guid("00000000-0000-0000-0000-000000000002");
        var other = Area("Other desktop", -100, 200000); other.DesktopId = Guid.NewGuid();
        doc.Zones.AddRange([partial, equal, other]);
        var zones = new ZoneService(doc, () => Guid.Empty, () => true);
        var views = zones.Items;
        Assert.Equal(parent.Id, views.Single(v => v.Id == child.Id).ParentId);
        Assert.Equal(child.Id, views.Single(v => v.Id == grandchild.Id).ParentId);
        Assert.Equal(2, views.Single(v => v.Id == grandchild.Id).Depth);
        Assert.Null(views.Single(v => v.Id == parent.Id).ParentId);
        Assert.Null(views.Single(v => v.Id == equal.Id).ParentId);
        Assert.Null(views.Single(v => v.Id == partial.Id).ParentId);
        Assert.DoesNotContain(views, v => v.Id == other.Id);
        Assert.True(views.ToList().FindIndex(v => v.Id == parent.Id) < views.ToList().FindIndex(v => v.Id == child.Id));
    }

    [Fact]
    public void ParentMoveCarriesAllLevelsAndSharedUnpinnedMembersExactlyOnceWithUndoRedo()
    {
        var (doc, parent, child, grandchild) = Tree();
        var item = new WorkspaceItem { Title = "Editor", ExecutablePath = @"C:\Apps\Editor.exe",
            Bounds = new WorldRect { X = 300, Y = 400, W = 400, H = 300 } };
        var pinned = new WorkspaceItem { Title = "Pinned", ExecutablePath = item.ExecutablePath,
            Bounds = new WorldRect { X = 500, Y = 600, W = 400, H = 300, PinnedToScreen = true } };
        doc.Items.AddRange([item, pinned]);
        parent.ItemIds.Add(item.Id); child.ItemIds.Add(item.Id); grandchild.ItemIds.AddRange([item.Id, pinned.Id]);
        using var rig = new WorkspaceHarness(doc); rig.Workspace.Start();
        Assert.Single(rig.Workspace.Zones.MoveMembers(parent.Id));
        Assert.True(rig.Workspace.Zones.TryPlanMove(parent.Id, 100, -50, out var plan));
        Assert.Single(plan!.Items); Assert.Equal(2, plan.Areas.Count);
        int saves = rig.Store.Saves;
        Assert.True(rig.Workspace.MoveZone(parent.Id, 100, -50));
        Assert.Equal(100, parent.Bounds.X); Assert.Equal(200, child.Bounds.X); Assert.Equal(300, grandchild.Bounds.X);
        Assert.Equal(400, item.Bounds.X); Assert.Equal(350, item.Bounds.Y); Assert.Equal(500, pinned.Bounds.X);
        Assert.Equal(saves + 1, rig.Store.Saves);
        Assert.Equal(400, rig.Cards.Created.Single(c => c.Item.Id == item.Id).Item.Bounds.X);
        Assert.True(rig.Workspace.UndoZoneLayout());
        Assert.Equal(0, parent.Bounds.X); Assert.Equal(100, child.Bounds.X); Assert.Equal(200, grandchild.Bounds.X); Assert.Equal(300, item.Bounds.X);
        Assert.True(rig.Workspace.RedoZoneLayout());
        Assert.Equal(100, parent.Bounds.X); Assert.Equal(200, child.Bounds.X); Assert.Equal(300, grandchild.Bounds.X); Assert.Equal(400, item.Bounds.X);
    }

    [Fact]
    public void MovingChildOutsideDetachesAndMovingIntoAnotherAreaReparentsItsWholeSubtree()
    {
        var (doc, parent, child, grandchild) = Tree();
        var destination = Area("Destination", 200000, 100000); doc.Zones.Add(destination);
        using var rig = new WorkspaceHarness(doc); rig.Workspace.Start();
        Assert.True(rig.Workspace.MoveZone(child.Id, 150000, 150000));
        Assert.Null(rig.Workspace.Zones.Items.Single(v => v.Id == child.Id).ParentId);
        Assert.Equal(child.Id, rig.Workspace.Zones.Items.Single(v => v.Id == grandchild.Id).ParentId);
        Assert.True(rig.Workspace.MoveZone(child.Id, 50000, 50000));
        Assert.Equal(destination.Id, rig.Workspace.Zones.Items.Single(v => v.Id == child.Id).ParentId);
        Assert.Equal(0, parent.Bounds.X);
        Assert.True(rig.Workspace.UndoZoneLayout());
        Assert.Null(rig.Workspace.Zones.Items.Single(v => v.Id == child.Id).ParentId);
        Assert.True(rig.Workspace.UndoZoneLayout());
        Assert.Equal(parent.Id, rig.Workspace.Zones.Items.Single(v => v.Id == child.Id).ParentId);
    }

    [Theory]
    [InlineData("position")]
    [InlineData("membership")]
    [InlineData("remove")]
    public void ChangedDescendantRejectsUndoAtomically(string conflict)
    {
        var (doc, parent, child, grandchild) = Tree();
        using var rig = new WorkspaceHarness(doc); rig.Workspace.Start();
        Assert.True(rig.Workspace.MoveZone(parent.Id, 500, 0));
        if (conflict == "position") { var b = grandchild.Bounds; b.X++; grandchild.Bounds = b; }
        if (conflict == "membership") grandchild.ItemIds.Add(Guid.NewGuid());
        if (conflict == "remove") rig.Workspace.Zones.Remove(grandchild.Id);
        int saves = rig.Store.Saves;
        Assert.False(rig.Workspace.UndoZoneLayout());
        Assert.Equal(500, parent.Bounds.X); Assert.Equal(600, child.Bounds.X); Assert.Equal(saves, rig.Store.Saves);
    }

    [Fact]
    public void ResizingAndRemovingParentKeepDescendantsInPlace()
    {
        var (doc, parent, child, grandchild) = Tree();
        using var rig = new WorkspaceHarness(doc); rig.Workspace.Start();
        Assert.True(rig.Workspace.ResizeZone(parent.Id, new WorldRect { W = 50, H = 50 }));
        Assert.Equal(100, child.Bounds.X); Assert.Equal(200, grandchild.Bounds.X);
        Assert.Null(rig.Workspace.Zones.Items.Single(v => v.Id == child.Id).ParentId);
        Assert.True(rig.Workspace.UndoZoneLayout());
        Assert.Equal(parent.Id, rig.Workspace.Zones.Items.Single(v => v.Id == child.Id).ParentId);
        Assert.True(rig.Workspace.Zones.Remove(parent.Id));
        Assert.Equal(100, child.Bounds.X); Assert.Equal(child.Id, rig.Workspace.Zones.Items.Single(v => v.Id == grandchild.Id).ParentId);
    }

    [Fact]
    public void LargeAreasResizeMoveAndRoundTripWithoutWindowDimensionLimits()
    {
        string folder = Path.Combine(Path.GetTempPath(), "large-zones-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try
        {
            var document = new WorkspaceDocument(); var store = new WorkspaceStore(Path.Combine(folder, "workspace.json"));
            var zones = new ZoneService(document, () => Guid.Empty, () => { store.Save(document); return true; });
            Assert.True(zones.Add(new WorldRect { X = -20000000, Y = -20000000, W = 100000000, H = 100000000 }, out var id));
            Assert.True(zones.Add(new WorldRect { W = 1000, H = 1000 }, out var child));
            using var rig = new WorkspaceHarness(document); rig.Workspace.Start();
            var original = document.Zones.Single(z => z.Id == id).Bounds;
            Assert.True(ZoneResizeLayout.TryResize(original, ZoneResizeCorner.TopLeft, -100000000, -100000000, out var enlarged));
            Assert.True(rig.Workspace.ResizeZone(id, enlarged));
            Assert.True(rig.Workspace.MoveZone(id, 20000000, 20000000));
            Assert.True(rig.Workspace.UndoZoneLayout()); Assert.True(rig.Workspace.UndoZoneLayout());
            Assert.Equal(original, document.Zones.Single(z => z.Id == id).Bounds);
            var selection = rig.Workspace.SelectionSnapshot([], [id]);
            var moved = selection with { Areas = selection.Areas.Select(p => p with { Bounds = enlarged }).ToArray() };
            Assert.True(rig.Workspace.ApplySelectionPlacement(selection, moved));
            store.Save(document);
            var loaded = store.Load(out var warning); Assert.Null(warning);
            var views = new ZoneService(loaded, () => Guid.Empty, () => true).Items;
            Assert.Equal(enlarged, views.Single(v => v.Id == id).Bounds);
            Assert.Equal(id, views.Single(v => v.Id == child).ParentId);
            Assert.False(WorkspaceGeometry.ValidBounds(enlarged));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData(double.NaN, 100)]
    [InlineData(double.PositiveInfinity, 100)]
    [InlineData(100, double.PositiveInfinity)]
    [InlineData(100, 0)]
    [InlineData(double.MaxValue, double.MaxValue)]
    public void NonFiniteOrEmptyAreaGeometryIsRejected(double x, double width)
    {
        var zones = new ZoneService(new WorkspaceDocument(), () => Guid.Empty, () => true);
        Assert.False(zones.Add(new WorldRect { X = x, W = width, H = 100 }, out _));
        Assert.Empty(zones.Items);
    }
}
