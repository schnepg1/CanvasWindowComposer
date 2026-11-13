using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using CanvasDesktop;
using Xunit;

namespace CanvasDesktop.Tests;

public class WebLogoStoreTests : IDisposable
{
    private readonly string _testCacheDir;

    public WebLogoStoreTests()
    {
        _testCacheDir = Path.Combine(Path.GetTempPath(), "CanvasDesktop_Tests_Favicon_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testCacheDir);
        WebLogoStore.CacheDirectory = _testCacheDir;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testCacheDir))
                Directory.Delete(_testCacheDir, true);
        }
        catch { }
    }

    [Fact]
    public void DiskCacheRoundTripsSuccessfully()
    {
        using var original = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(original))
        {
            g.Clear(Color.Purple);
        }

        using var ms = new MemoryStream();
        original.Save(ms, ImageFormat.Png);
        byte[] bytes = ms.ToArray();

        WebLogoStore.SaveToDiskCache("example.com", bytes);

        using var loaded = WebLogoStore.LoadFromDiskCache("example.com");
        Assert.NotNull(loaded);
        Assert.Equal(32, loaded.Width);
        Assert.Equal(32, loaded.Height);
    }

    [Fact]
    public void CreatesValidBrandFallbacks()
    {
        using var ytFallback = WebLogoStore.CreateBrandFallback("YouTube", "youtube.com", 128);
        Assert.NotNull(ytFallback);
        Assert.Equal(128, ytFallback.Width);
        Assert.Equal(128, ytFallback.Height);

        using var ghFallback = WebLogoStore.CreateBrandFallback("GitHub", "github.com", 128);
        Assert.NotNull(ghFallback);
        Assert.Equal(128, ghFallback.Width);

        using var genericFallback = WebLogoStore.CreateBrandFallback("CustomApp", "customapp.io", 128);
        Assert.NotNull(genericFallback);
        Assert.Equal(128, genericFallback.Width);
    }

    [Fact]
    public void MissingDiskCacheReturnsNull()
    {
        var result = WebLogoStore.LoadFromDiskCache("nonexistent-domain-123.org");
        Assert.Null(result);
    }
}
