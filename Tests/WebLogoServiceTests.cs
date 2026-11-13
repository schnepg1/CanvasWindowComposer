using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CanvasDesktop;
using Xunit;

namespace CanvasDesktop.Tests;

public class WebLogoServiceTests : IDisposable
{
    private readonly string _testCacheDir;

    public WebLogoServiceTests()
    {
        _testCacheDir = Path.Combine(Path.GetTempPath(), "CanvasDesktop_Tests_Service_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testCacheDir);
        WebLogoStore.CacheDirectory = _testCacheDir;

        // Seed cache for "youtube.com" with a dummy bitmap so network fetch is skipped
        using var seed = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(seed))
        {
            g.Clear(Color.Red);
        }
        using var ms = new MemoryStream();
        seed.Save(ms, ImageFormat.Png);
        WebLogoStore.SaveToDiskCache("youtube.com", ms.ToArray());
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

    private static WorkspaceItemView CreateBrowserItem(string title, string exe, string url = "")
    {
        return new WorkspaceItemView(
            Id: Guid.NewGuid(),
            Title: title,
            ExecutablePath: exe,
            Target: "",
            OpenOnStartup: false,
            Bounds: new WorldRect { W = 400, H = 300 },
            State: "Normal",
            WebResource: string.IsNullOrEmpty(url) ? null : new WebResource(url));
    }

    [Fact]
    public async Task ResolvesAndCachesBadgedArtworkAsync()
    {
        var item = CreateBrowserItem(
            title: "Lo-Fi Beats - YouTube - Google Chrome",
            exe: @"C:\Program Files\Google\Chrome\Application\chrome.exe");

        string expectedKey = WebDestinationParser.GetCacheKey(item);
        var tcs = new TaskCompletionSource<string>();

        void OnResolved(string key)
        {
            if (key == expectedKey)
                tcs.TrySetResult(key);
        }

        WebLogoService.Default.ArtworkResolved += OnResolved;
        try
        {
            // Initial call returns synchronously without blocking
            var initial = PlaceholderArtworkCache.Default.GetArtwork(item);
            Assert.NotNull(initial);

            // Wait for background resolution
            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(5000));
            Assert.Same(tcs.Task, completedTask);

            // Verify cache now has the badged artwork
            bool found = PlaceholderArtworkCache.Default.TryGet(expectedKey, out var updated);
            Assert.True(found);
            Assert.NotNull(updated);
            Assert.NotNull(updated.Symbol);
            Assert.NotNull(updated.OverviewLogo);
        }
        finally
        {
            WebLogoService.Default.ArtworkResolved -= OnResolved;
        }
    }

    [Fact]
    public void NonBrowserItemDoesNotQueueResolution()
    {
        var item = CreateBrowserItem(
            title: "Editor",
            exe: @"C:\Apps\Notepad.exe");

        // Should return fast without queuing web resolution
        var artwork = PlaceholderArtworkCache.Default.GetArtwork(item);
        Assert.NotNull(artwork);
    }
}
