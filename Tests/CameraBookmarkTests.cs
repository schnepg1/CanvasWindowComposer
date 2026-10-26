using CanvasDesktop;
using System.Windows.Forms;

namespace CanvasDesktop.Tests;

public sealed class CameraBookmarkTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "camera-bookmarks-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_folder, "camera-bookmarks.json");
    public void Dispose() { if (Directory.Exists(_folder)) Directory.Delete(_folder, true); }

    [Theory]
    [InlineData(Keys.D0, 0)]
    [InlineData(Keys.D1, 1)]
    [InlineData(Keys.D9, 9)]
    [InlineData(Keys.NumPad0, 0)]
    [InlineData(Keys.NumPad5, 5)]
    [InlineData(Keys.NumPad9, 9)]
    public void NumberRowAndKeypadUseTheSameSlots(Keys key, int slot)
    {
        Assert.True(OverviewManager.TryCameraBookmarkKey(key, out int recalled, out bool save));
        Assert.Equal(slot, recalled); Assert.False(save);
        Assert.True(OverviewManager.TryCameraBookmarkKey(key | Keys.Control, out int stored, out save));
        Assert.Equal(slot, stored); Assert.True(save);
    }

    [Theory]
    [InlineData(Keys.Shift | Keys.D1)]
    [InlineData(Keys.Control | Keys.Shift | Keys.D1)]
    [InlineData(Keys.Alt | Keys.D1)]
    [InlineData(Keys.Control | Keys.Alt | Keys.NumPad2)]
    [InlineData(Keys.F1)]
    [InlineData(Keys.Home)]
    public void OtherShortcutsAreNotCameraCommands(Keys key)
        => Assert.False(OverviewManager.TryCameraBookmarkKey(key, out _, out _));

    [Fact]
    public void AllTenSlotsKeepExactCameraAndZoomAcrossReloadAndOverwrite()
    {
        var desktop = Guid.NewGuid(); var store = new CameraBookmarkStore(FilePath);
        for (int slot = 0; slot < 10; slot++) Assert.True(store.Save(desktop, slot, new(-1000.25 * slot, 300.75 * slot, .02 + .09 * slot)));
        store = new(FilePath);
        for (int slot = 0; slot < 10; slot++)
        {
            Assert.True(store.TryGet(desktop, slot, out var camera));
            Assert.Equal(new CanvasCamera(-1000.25 * slot, 300.75 * slot, .02 + .09 * slot), camera);
        }
        var replacement = new CanvasCamera(5432.125, -9876.5, 1);
        Assert.True(store.Save(desktop, 1, replacement));
        Assert.True(new CameraBookmarkStore(FilePath).TryGet(desktop, 1, out var restored));
        Assert.Equal(replacement, restored);
        Assert.True(store.TryGet(desktop, 0, out var unchanged)); Assert.Equal(.02, unchanged.Zoom);
    }

    [Fact]
    public void EmptySlotsAndOtherDesktopsDoNotRecallAnotherView()
    {
        var first = Guid.NewGuid(); var second = Guid.NewGuid(); var store = new CameraBookmarkStore(FilePath);
        Assert.False(store.TryGet(first, 1, out _)); Assert.False(File.Exists(FilePath));
        Assert.True(store.Save(first, 1, new(1, 2, .3)));
        Assert.False(store.TryGet(second, 1, out _)); Assert.False(store.TryGet(first, 2, out _));
        Assert.True(store.Save(second, 1, new(4, 5, .6)));
        store = new(FilePath);
        Assert.True(store.TryGet(first, 1, out var a)); Assert.Equal(.3, a.Zoom);
        Assert.True(store.TryGet(second, 1, out var b)); Assert.Equal(.6, b.Zoom);
    }

    [Theory]
    [InlineData("{ broken")]
    [InlineData("{\"Version\":2,\"Bookmarks\":[]}")]
    [InlineData("{\"Version\":1,\"Bookmarks\":[{\"Slot\":10,\"Camera\":{\"X\":1,\"Y\":2,\"Zoom\":0.5}}]}")]
    [InlineData("{\"Version\":1,\"Bookmarks\":[{\"Slot\":1,\"Camera\":{\"X\":1,\"Y\":2,\"Zoom\":0}}]}")]
    [InlineData("{\"Version\":1,\"Bookmarks\":[null]}")]
    public void InvalidOrFutureFileIsPreservedAndSessionBookmarksStillWork(string content)
    {
        Directory.CreateDirectory(_folder); File.WriteAllText(FilePath, content);
        var store = new CameraBookmarkStore(FilePath);
        Assert.NotNull(store.Status); Assert.False(store.TryGet(Guid.Empty, 1, out _));
        Assert.False(store.Save(Guid.Empty, 1, new(10, 20, .5)));
        Assert.Equal(content, File.ReadAllText(FilePath));
        Assert.True(store.TryGet(Guid.Empty, 1, out var camera)); Assert.Equal(new CanvasCamera(10, 20, .5), camera);
    }

    [Fact]
    public void FailedSaveKeepsSessionBookmarkAndCanBeRetried()
    {
        Directory.CreateDirectory(FilePath); // A directory cannot be replaced by a bookmark file.
        var store = new CameraBookmarkStore(FilePath); var camera = new CanvasCamera(123, -456, .4);
        Assert.False(store.Save(Guid.Empty, 3, camera)); Assert.NotNull(store.Status);
        Assert.True(store.TryGet(Guid.Empty, 3, out var session)); Assert.Equal(camera, session);
        Directory.Delete(FilePath);
        Assert.True(store.Save(Guid.Empty, 3, camera)); Assert.Null(store.Status);
        Assert.True(new CameraBookmarkStore(FilePath).TryGet(Guid.Empty, 3, out var restored)); Assert.Equal(camera, restored);
    }

    [Theory]
    [InlineData(-1, 0, 0, .5)]
    [InlineData(10, 0, 0, .5)]
    [InlineData(1, double.NaN, 0, .5)]
    [InlineData(1, 0, double.PositiveInfinity, .5)]
    [InlineData(1, 0, 0, 0)]
    [InlineData(1, 0, 0, 1.1)]
    public void InvalidCameraCannotEnterMemoryOrDisk(int slot, double x, double y, double zoom)
    {
        var store = new CameraBookmarkStore(FilePath);
        Assert.Throws<ArgumentException>(() => store.Save(Guid.Empty, slot, new(x, y, zoom)));
        Assert.False(File.Exists(FilePath));
    }
}
