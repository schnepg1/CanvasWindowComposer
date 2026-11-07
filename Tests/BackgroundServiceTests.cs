using CanvasDesktop;

namespace CanvasDesktop.Tests;

public sealed class BackgroundServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "canvas-background-service-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_directory, "backgrounds.json");
    private Guid _desktop = Guid.NewGuid();
    private readonly List<ZoneView> _zones = new();
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    private BackgroundService Service() => new(new BackgroundPersistence(FilePath), () => _desktop, () => _zones);
    private BackgroundSurface Surface(string name = "Study", int z = 0) => new(Guid.NewGuid(), _desktop, name,
        "builtin:study", new WorldRect { X = -3000, Y = 500, W = 10000, H = 6000 }, ZIndex: z);

    [Fact]
    public void FreelySizedSurfacesAreLockedByDefaultAndRoundTripWithoutScreenLimits()
    {
        var service = Service();
        var surface = Surface() with { Bounds = new WorldRect { X = -1e12, Y = 1e12, W = 1e10, H = 5e9 } };
        Assert.True(service.Add(surface));
        Assert.True(Assert.Single(service.Items).Locked);
        Assert.Equal(surface, Assert.Single(Service().Items));
        Assert.False(service.Add(surface));
    }

    [Fact]
    public void AttachedBackgroundFollowsAreaThenDetachesAtItsLatestBoundsWhenAreaIsRemoved()
    {
        var zone = new ZoneView(Guid.NewGuid(), "Reading", "#FFFFFF", new WorldRect { W = 4000, H = 3000 });
        _zones.Add(zone);
        var service = Service();
        var surface = Surface() with { ZoneId = zone.Id };
        Assert.True(service.Add(surface));
        Assert.Equal(zone.Bounds, Assert.Single(service.Items).Bounds);
        var moved = zone with { Bounds = new WorldRect { X = 12000, Y = -500, W = 6000, H = 2500 } };
        _zones[0] = moved;
        service.ReconcileZones();
        Assert.Equal(moved.Bounds, Assert.Single(service.Items).Bounds);
        _zones.Clear();
        service.ReconcileZones();
        var detached = Assert.Single(service.Items);
        Assert.Null(detached.ZoneId);
        Assert.Equal(moved.Bounds, detached.Bounds);
        Assert.Equal(detached, Assert.Single(Service().Items));
    }

    [Fact]
    public void ExplicitDetachKeepsTheEffectivePlacementAndDoesNotEditTheArea()
    {
        var zone = new ZoneView(Guid.NewGuid(), "Work", "#FFFFFF", new WorldRect { X = 700, W = 2000, H = 1000 });
        _zones.Add(zone);
        var service = Service();
        Assert.True(service.Add(Surface() with { ZoneId = zone.Id }));
        var attached = Assert.Single(service.Items);
        Assert.True(service.Update(attached with { ZoneId = null }));
        _zones[0] = zone with { Bounds = new WorldRect { X = 10000, W = 5000, H = 2000 } };
        service.ReconcileZones();
        Assert.Equal(zone.Bounds, Assert.Single(service.Items).Bounds);
        Assert.Null(Assert.Single(service.Items).ZoneId);
    }

    [Fact]
    public void UndoOfUnrelatedEditsAndRemovalRetainsLatestDetachedProjectBounds()
    {
        var zone = new ZoneView(Guid.NewGuid(), "Room", "#FFFFFF", new() { W = 3000, H = 2000 });
        _zones.Add(zone); var service = Service();
        var linked = Surface() with { ZoneId = zone.Id };
        var other = Surface("Other");
        service.Add(linked); service.Add(other); service.Update(other with { Name = "Renamed" });
        var moved = zone with { Bounds = new() { X = 12000, Y = 900, W = 6000, H = 4000 } };
        _zones[0] = moved; service.ReconcileZones();
        _zones.Clear(); service.ReconcileZones();
        Assert.True(service.Undo());
        var detached = service.Items.Single(item => item.Id == linked.Id);
        Assert.Null(detached.ZoneId); Assert.Equal(moved.Bounds, detached.Bounds);
        Assert.Equal("Other", service.Items.Single(item => item.Id == other.Id).Name);
        Assert.True(service.Redo());
        Assert.Equal(moved.Bounds, service.Items.Single(item => item.Id == linked.Id).Bounds);
    }

    [Fact]
    public void UnknownAndTemporaryAreaReferencesAreRejected()
    {
        var service = Service();
        Assert.False(service.Add(Surface() with { ZoneId = Guid.NewGuid() }));
        var temporary = new ZoneView(Guid.NewGuid(), "Temporary", "#FFFFFF", new WorldRect { W = 100, H = 100 }) { IsTemporaryArea = true };
        _zones.Add(temporary);
        Assert.False(service.Add(Surface() with { ZoneId = temporary.Id }));
        Assert.Empty(service.Items);
    }

    [Fact]
    public void DesktopIsolationAppliesToCommandsHistoryAndStacking()
    {
        var service = Service();
        var a = Surface("Behind", 2); var b = Surface("Front", 9);
        Assert.True(service.Add(b)); Assert.True(service.Add(a));
        Assert.Equal(new[] { a.Id, b.Id }, service.Items.Select(item => item.Id));
        var firstDesktop = _desktop;
        _desktop = Guid.NewGuid(); service.NotifyDesktopChanged();
        Assert.Empty(service.Items); Assert.False(service.CanUndo);
        Assert.False(service.Update(a)); Assert.False(service.Remove(a.Id));
        var other = Surface("Other desktop"); Assert.True(service.Add(other));
        _desktop = firstDesktop; service.NotifyDesktopChanged();
        Assert.True(service.Undo()); Assert.Equal(b.Id, Assert.Single(service.Items).Id);
        _desktop = other.DesktopId; service.NotifyDesktopChanged();
        Assert.Equal(other.Id, Assert.Single(service.Items).Id);
    }

    [Fact]
    public void HistoryRestoresEditsDeletionAndCreationAndNewEditsClearRedo()
    {
        var service = Service(); var original = Surface();
        Assert.True(service.Add(original));
        Assert.True(service.Update(original with { CropX = .1, CropY = .8, Dim = .3, Opacity = .7, Fit = BackgroundFit.Fit, Locked = false }));
        Assert.True(service.Remove(original.Id)); Assert.Empty(service.Items);
        Assert.True(service.Undo()); Assert.Equal(.1, Assert.Single(service.Items).CropX);
        Assert.True(service.Undo()); Assert.Equal(original, Assert.Single(service.Items));
        Assert.True(service.Undo()); Assert.Empty(service.Items);
        Assert.True(service.Redo()); Assert.Equal(original, Assert.Single(service.Items));
        Assert.True(service.Update(original with { Name = "Changed" }));
        Assert.False(service.CanRedo);
    }

    [Fact]
    public void UndoNeverRecreatesALinkToADeletedArea()
    {
        var zone = new ZoneView(Guid.NewGuid(), "Reading", "#FFFFFF", new WorldRect { W = 3000, H = 2000 });
        _zones.Add(zone); var service = Service();
        Assert.True(service.Add(Surface() with { ZoneId = zone.Id }));
        Assert.True(service.Remove(Assert.Single(service.Items).Id));
        _zones.Clear(); service.ReconcileZones();
        Assert.True(service.Undo());
        Assert.Null(Assert.Single(service.Items).ZoneId);
        Assert.Equal(zone.Bounds, Assert.Single(service.Items).Bounds);
    }

    [Fact]
    public void HistoryRetainsOnlyLatestFiftyEdits()
    {
        var service = Service(); var surface = Surface(); Assert.True(service.Add(surface));
        for (int i = 1; i <= 55; i++) Assert.True(service.Update(surface with { Name = "Study " + i }));
        for (int i = 0; i < 50; i++) Assert.True(service.Undo());
        Assert.False(service.Undo()); Assert.Equal("Study 5", Assert.Single(service.Items).Name);
    }

    [Fact]
    public void InvalidFramesAndSettingsAreRejectedWithoutChangingHistoryOrDocument()
    {
        var service = Service(); var item = Surface();
        Assert.False(service.Add(item with { Bounds = new WorldRect { X = double.NaN, W = 100, H = 100 } }));
        Assert.False(service.Add(item with { Bounds = new WorldRect { W = 0, H = 100 } }));
        Assert.False(service.Add(item with { Bounds = new WorldRect { X = double.MaxValue, W = double.MaxValue, H = 100 } }));
        Assert.False(service.Add(item with { CropX = double.NaN }));
        Assert.False(service.Add(item with { Opacity = 1.1 }));
        Assert.False(service.Add(item with { Dim = -.1 }));
        Assert.False(service.Add(item with { Name = new string('x', 121) }));
        Assert.False(service.Add(item with { AssetId = "../outside.png" }));
        Assert.False(service.Add(item with { Fit = (BackgroundFit)99 }));
        Assert.Empty(service.Items); Assert.False(service.CanUndo);
    }

    [Fact]
    public void FailedSaveKeepsInMemoryEditAndHistoryAndExplicitRetryPersistsIt()
    {
        var service = Service(); var item = Surface(); Assert.True(service.Add(item));
        Directory.CreateDirectory(FilePath + ".tmp");
        Assert.False(service.Update(item with { Name = "Unsaved" }));
        Assert.Equal("Unsaved", Assert.Single(service.Items).Name);
        Assert.NotNull(service.Status); Assert.True(service.CanUndo);
        Directory.Delete(FilePath + ".tmp");
        Assert.True(service.Save()); Assert.Null(service.Status);
        Assert.Equal("Unsaved", Assert.Single(Service().Items).Name);
    }

    [Fact]
    public void StackingOrderCommandsReorderItemsAndSupportUndoRedo()
    {
        var service = Service();
        var a = Surface("Bottom", 0);
        var b = Surface("Middle", 1);
        var c = Surface("Top", 2);
        Assert.True(service.Add(a));
        Assert.True(service.Add(b));
        Assert.True(service.Add(c));
        Assert.Equal(new[] { a.Id, b.Id, c.Id }, service.Items.Select(x => x.Id));

        Assert.True(service.SendToBack(c.Id));
        Assert.Equal(new[] { c.Id, a.Id, b.Id }, service.Items.Select(x => x.Id));
        Assert.Equal(0, service.Items[0].ZIndex);
        Assert.Equal(1, service.Items[1].ZIndex);
        Assert.Equal(2, service.Items[2].ZIndex);

        Assert.False(service.SendToBack(c.Id));

        Assert.True(service.BringForward(c.Id));
        Assert.Equal(new[] { a.Id, c.Id, b.Id }, service.Items.Select(x => x.Id));

        Assert.True(service.BringToFront(c.Id));
        Assert.Equal(new[] { a.Id, b.Id, c.Id }, service.Items.Select(x => x.Id));

        Assert.False(service.BringToFront(c.Id));

        Assert.True(service.SendBackward(c.Id));
        Assert.Equal(new[] { a.Id, c.Id, b.Id }, service.Items.Select(x => x.Id));

        Assert.True(service.Undo());
        Assert.Equal(new[] { a.Id, b.Id, c.Id }, service.Items.Select(x => x.Id));

        Assert.True(service.Redo());
        Assert.Equal(new[] { a.Id, c.Id, b.Id }, service.Items.Select(x => x.Id));
    }
}
