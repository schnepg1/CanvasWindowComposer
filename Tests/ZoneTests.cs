using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class ZoneTests
{
    [Fact]
    public void ZonesPersistWithWorkspaceAndViewsCannotMutateTheModel()
    {
        var store = new MemoryWorkspaceStore();
        var persistence = new WorkspacePersistence(store);
        var zones = new ZoneService(persistence.Document, () => Guid.Empty, persistence.Save);
        Assert.True(zones.Add(new WorldRect { X = -1200, Y = 2400, W = 800, H = 600 }, out var id));
        Assert.True(zones.Update(id, "  Research  ", "#a78bfa"));
        Assert.Equal(2, store.Saves);
        var view = Assert.Single(zones.Items);
        var changed = view with { Name = "Not persisted" };
        Assert.Equal("Research", store.Document.Zones[0].Name);
        Assert.Equal("#A78BFA", store.Document.Zones[0].Color);
        Assert.Equal(-1200, view.Bounds.X);
        Assert.NotEqual(changed.Name, view.Name);
    }

    [Fact]
    public void CommandsAreScopedToDesktopAndStaleIdsCannotResurrectZones()
    {
        var desktop = Guid.NewGuid();
        int saves = 0;
        var document = new WorkspaceDocument();
        var zones = new ZoneService(document, () => desktop, () => { saves++; return true; });
        zones.Add(new WorldRect { W = 100, H = 200 }, out var id);
        desktop = Guid.NewGuid();
        Assert.Empty(zones.Items);
        Assert.False(zones.Update(id, "Other desktop", "#60A5FA"));
        Assert.False(zones.Remove(id));
        desktop = document.Zones[0].DesktopId;
        Assert.True(zones.Remove(id));
        Assert.False(zones.Update(id, "Deleted", "#60A5FA"));
        Assert.Equal(2, saves);
        Assert.Empty(document.Zones);
    }

    [Fact]
    public void FailedZoneSaveRetainsChangeForPersistenceRetry()
    {
        var store = new MemoryWorkspaceStore { SaveError = new IOException("Disk full") };
        var persistence = new WorkspacePersistence(store);
        var zones = new ZoneService(persistence.Document, () => Guid.Empty, persistence.Save);
        Assert.False(zones.Add(new WorldRect { W = 800, H = 600 }, out var id));
        Assert.Equal(id, Assert.Single(zones.Items).Id);
        store.SaveError = null;
        Assert.True(persistence.Save());
        Assert.Equal(id, Assert.Single(store.Document.Zones).Id);
    }

    [Fact]
    public void InvalidInputDoesNotAlterSavedZones()
    {
        var document = new WorkspaceDocument();
        var zones = new ZoneService(document, () => Guid.Empty, () => true);
        Assert.False(zones.Add(new WorldRect { X = double.NaN, W = 100, H = 100 }, out _));
        Assert.False(zones.Add(new WorldRect { W = 0, H = 100 }, out _));
        zones.Add(new WorldRect { W = 100, H = 100 }, out var id);
        Assert.False(zones.Update(id, " ", "#60A5FA"));
        Assert.False(zones.Update(id, "Research", "#ZZZZZZ"));
        Assert.False(zones.Update(id, new string('x', 121), "#60A5FA"));
        Assert.Equal("Project area 1", Assert.Single(zones.Items).Name);
    }

    [Fact]
    public void ZoneFileRoundTripAndOlderWorkspaceCompatibility()
    {
        string directory = Path.Combine(Path.GetTempPath(), "canvas-zones-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "workspace.json");
            File.WriteAllText(path, "{\"Version\":1,\"Items\":[],\"Cameras\":{}}");
            var store = new WorkspaceStore(path);
            var document = store.Load(out _);
            Assert.Empty(document.Zones);
            var zones = new ZoneService(document, () => Guid.Empty, () => { store.Save(document); return true; });
            Assert.True(zones.Add(new WorldRect { X = -3000, Y = 4000, W = 2200, H = 1800 }, out var id));
            var loaded = store.Load(out _);
            Assert.Equal(id, Assert.Single(loaded.Zones).Id);
            Assert.Equal(-3000, loaded.Zones[0].Bounds.X);
            Assert.Empty(loaded.Items);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void InvalidZoneFileIsPreservedInsteadOfOverwritten()
    {
        string path = Path.Combine(Path.GetTempPath(), "canvas-invalid-zone-" + Guid.NewGuid() + ".json");
        const string invalid = "{\"Version\":1,\"Zones\":[{\"Name\":\"Area\",\"Color\":\"bad\",\"Bounds\":{\"W\":200,\"H\":100}}]}";
        try
        {
            File.WriteAllText(path, invalid);
            var persistence = new WorkspacePersistence(new WorkspaceStore(path));
            Assert.False(persistence.Save());
            Assert.Equal(invalid, File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DrawingInReverseDirectionProducesPositiveWorldBounds()
    {
        var rect = ZoneGeometry.FromPoints(700, 400, -300, -600);
        Assert.Equal(-300, rect.X); Assert.Equal(-600, rect.Y);
        Assert.Equal(1000, rect.W); Assert.Equal(1000, rect.H);
    }

    [Theory]
    [InlineData(0, 0, 1920, 1040)]
    [InlineData(-1920, 200, 1920, 1080)]
    [InlineData(1920, -400, 2560, 1440)]
    public void JumpFramesAreaInsideMonitorWithNegativeOrPositiveOffsets(int x, int y, int w, int h)
    {
        var area = new WorldRect { X = -2000, Y = 9000, W = 3000, H = 2000 };
        var fit = ZoneGeometry.Fit(area, new ScreenRect(x, y, w, h));
        double left = (area.X - fit.X) * fit.Zoom;
        double top = (area.Y - fit.Y) * fit.Zoom;
        Assert.InRange(left, x + 47.99, x + w);
        Assert.InRange(top, y + 47.99, y + h);
        Assert.True(left + area.W * fit.Zoom <= x + w - 47.99);
        Assert.True(top + area.H * fit.Zoom <= y + h - 47.99);
        Assert.Equal(x + w / 2.0, (area.X + area.W / 2 - fit.X) * fit.Zoom, 7);
    }
}
