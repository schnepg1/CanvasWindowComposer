using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class PlaceholderIconRenderingTests
{
    [Theory]
    [InlineData(.02)]
    [InlineData(.1)]
    [InlineData(.25)]
    public void DistantIconFillsAvailableHeightStaysCenteredAndHasNoTitle(double zoom)
        => WorkspaceViewTests.OnSta(() =>
        {
            var logo = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null,
                new byte[] { 0, 0, 255, 255 }, 4);
            logo.Freeze();
            var card = new PlaceholderAnnotation((IntPtr)1, "Title must disappear",
                new WorldRect { X = 400, Y = 400, W = 800, H = 400 },
                ImageOpacity: 0, LabelOpacity: 1, Logo: logo);
            byte[] pixels = Render(card, zoom);
            Assert.Equal(pixels, Render(card with { Title = "A different title" }, zoom));
            int left = (int)(400 * zoom), top = (int)(400 * zoom);
            int side = (int)(400 * zoom), center = (int)(800 * zoom);
            for (int y = top + 1; y < top + side - 1; y++)
                for (int x = center - side / 2 + 1; x < center + side / 2 - 1; x++)
                {
                    int offset = (y * 400 + x) * 4;
                    Assert.Equal(255, pixels[offset + 2]);
                    Assert.Equal(0, pixels[offset + 1]);
                    Assert.Equal(255, pixels[offset + 3]);
                }
            Assert.Equal(0, pixels[((top + side / 2) * 400 + left - 2) * 4 + 3]);
            Assert.Empty(PlaceholderLabelLayout.Get([card], 0, 0, zoom, new(0, 0, 400, 300), 1, 1));
            var covered = card with { Occluders = [new WorldRect { X = 800, Y = 400, W = 400, H = 400 }] };
            Assert.Equal(0, Render(covered, zoom)[((top + side / 2) * 400 + center + 2) * 4 + 3]);
        });

    [Fact]
    public void FailedExtractionKeepsTitleFallbackInsteadOfUsingAGenericIcon()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var artwork = new PlaceholderArtwork();
            var item = new WorkspaceItemView(Guid.NewGuid(), "Missing app", @"Z:\missing-icon\app.exe",
                "", false, new WorldRect { W = 800, H = 400 }, "Placeholder");
            artwork.UpdateItem(item);
            Assert.Null(artwork.OverviewLogo);
            string path = Path.Combine(Path.GetTempPath(), $"canvas-no-icon-{Guid.NewGuid():N}.exe");
            try
            {
                File.WriteAllBytes(path, []);
                artwork.UpdateItem(item with { ExecutablePath = path });
                Assert.Null(artwork.OverviewLogo); // An existing file with no icon must not use a shell default.
            }
            finally { File.Delete(path); }
            var card = new PlaceholderAnnotation((IntPtr)1, "Missing app",
                new WorldRect { X = 400, Y = 400, W = 800, H = 400 }, ImageOpacity: 0, LabelOpacity: 1);
            Assert.Single(PlaceholderLabelLayout.Get([card], 0, 0, .25, new(0, 0, 400, 300), 1, 1));
            Assert.False(Render(card, .25).SequenceEqual(Render(card with { Title = "" }, .25)));
        });

    [Fact]
    public void RestoringAWindowRetainsItsIconUntilTheLiveViewIsReady()
        => WorkspaceViewTests.OnSta(() =>
        {
            var logo = new DrawingImage(); logo.Freeze();
            var id = Guid.NewGuid();
            var card = new PlaceholderAnnotation((IntPtr)1, "Project", new WorldRect { W = 800, H = 400 }, Logo: logo);
            var transitions = new OverviewCardTransitions();
            transitions.Update([new(id, card, OverviewCardMode.Simple)], 0);
            var live = new OverviewCardInput(id, card with { Handle = (IntPtr)2, Logo = null }, OverviewCardMode.Live, false);
            Assert.Same(logo, Assert.Single(transitions.Update([live], 10)).Logo);
            transitions.Update([live with { LiveReady = true }], 20);
            Assert.Same(logo, Assert.Single(transitions.Update([live with { LiveReady = true }], 95)).Logo);
            Assert.Empty(transitions.Update([live with { LiveReady = true }], 170));
        });

    private static byte[] Render(PlaceholderAnnotation card, double zoom)
    {
        var surface = new OverviewAnnotationSurface();
        surface.Update(OverviewAnnotationSnapshot.Create([], 0, 0, zoom, new(0, 0, 400, 300),
            null, false, placeholders: [card], showHints: false));
        surface.Measure(new Size(400, 300)); surface.Arrange(new Rect(0, 0, 400, 300));
        var bitmap = new RenderTargetBitmap(400, 300, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var pixels = new byte[400 * 300 * 4]; bitmap.CopyPixels(pixels, 1600, 0);
        return pixels;
    }
}
