using System;
using System.Drawing;
using System.Drawing.Imaging;
using CanvasDesktop;
using Xunit;

namespace CanvasDesktop.Tests;

public class PlaceholderArtworkCacheTests
{
    private static WorkspaceItemView CreateItem(Guid id, string title, string exe, bool isFolder = false)
    {
        return new WorkspaceItemView(
            Id: id,
            Title: title,
            ExecutablePath: exe,
            Target: "",
            OpenOnStartup: false,
            Bounds: new WorldRect { W = 400, H = 300 },
            State: "Normal",
            IsFolder: isFolder);
    }

    [Fact]
    public void DeduplicatesIdenticalExecutableIcons()
    {
        using var cache = new PlaceholderArtworkCache(10, 1024 * 1024);

        var item1 = CreateItem(Guid.NewGuid(), "Editor 1", @"C:\Apps\Editor.exe");
        var item2 = CreateItem(Guid.NewGuid(), "Editor 2", @"C:\Apps\Editor.exe");

        var art1 = cache.GetArtwork(item1);
        var art2 = cache.GetArtwork(item2);

        Assert.NotNull(art1);
        Assert.Same(art1, art2);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void FolderArtworkIsShared()
    {
        using var cache = new PlaceholderArtworkCache(10, 1024 * 1024);

        var folder1 = CreateItem(Guid.NewGuid(), "Projects", @"C:\Work\Projects", isFolder: true);
        var folder2 = CreateItem(Guid.NewGuid(), "Photos", @"D:\Media\Photos", isFolder: true);

        var art1 = cache.GetArtwork(folder1);
        var art2 = cache.GetArtwork(folder2);

        Assert.NotNull(art1);
        Assert.NotNull(art1.Symbol);
        Assert.NotNull(art1.OverviewLogo);
        Assert.Same(art1, art2);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void DisposesGdiBitmapOnEviction()
    {
        using var cache = new PlaceholderArtworkCache(maxCapacity: 1, maxMemoryBytes: 1024 * 1024);

        var bmp1 = new Bitmap(16, 16, PixelFormat.Format32bppArgb);
        var bmp2 = new Bitmap(16, 16, PixelFormat.Format32bppArgb);

        var shared1 = new SharedArtwork(bmp1, null, 100);
        var shared2 = new SharedArtwork(bmp2, null, 100);

        cache.Set("app1", shared1);
        Assert.Equal(1, cache.Count);

        // Evicts app1 when app2 is added due to capacity limit 1
        cache.Set("app2", shared2);

        Assert.Equal(1, cache.Count);
        // Accessing bmp1.Width on a disposed bitmap throws ArgumentException
        Assert.Throws<ArgumentException>(() => bmp1.Width);

        // bmp2 is still alive
        Assert.Equal(16, bmp2.Width);
    }
}
