using System.Drawing;
using System.Drawing.Imaging;
using CanvasDesktop;
using Xunit;

namespace CanvasDesktop.Tests;

public class WebLogoBadgeCompositorTests
{
    [Fact]
    public void CreatesValidBadgedArtworkWithBrowserIcon()
    {
        using var webLogo = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(webLogo))
        {
            g.Clear(Color.Red);
        }

        using var browserIcon = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(browserIcon))
        {
            g.Clear(Color.Blue);
        }

        using var artwork = WebLogoBadgeCompositor.CreateBadgedArtwork(webLogo, browserIcon, 256);

        Assert.NotNull(artwork);
        Assert.NotNull(artwork.Symbol);
        Assert.NotNull(artwork.OverviewLogo);

        Assert.Equal(256, artwork.Symbol.Width);
        Assert.Equal(256, artwork.Symbol.Height);
        Assert.Equal(128, artwork.OverviewLogo.PixelWidth);
        Assert.Equal(128, artwork.OverviewLogo.PixelHeight);
        Assert.True(artwork.OverviewLogo.IsFrozen);
        Assert.True(artwork.Bytes > 0);
    }

    [Fact]
    public void CreatesValidArtworkWithoutBrowserIcon()
    {
        using var webLogo = new Bitmap(128, 128, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(webLogo))
        {
            g.Clear(Color.Green);
        }

        using var artwork = WebLogoBadgeCompositor.CreateBadgedArtwork(webLogo, null, 256);

        Assert.NotNull(artwork);
        Assert.NotNull(artwork.Symbol);
        Assert.NotNull(artwork.OverviewLogo);
        Assert.Equal(256, artwork.Symbol.Width);
    }
}
