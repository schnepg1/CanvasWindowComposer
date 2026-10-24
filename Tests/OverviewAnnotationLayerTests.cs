using CanvasDesktop;
using System.Windows;
using System.Windows.Media;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class OverviewAnnotationLayerTests
{
    private sealed class FillLayer : IOverviewAnnotationLayer
    {
        public void Draw(DrawingContext dc, OverviewAnnotationContext context)
            => dc.DrawRectangle(Brushes.Red, null, new Rect(0, 0, context.Size.Width, context.Size.Height));
    }

    [Fact]
    public void DrawingLayerPaintsAboveCardsAndBelowTheHudAndCanBeDetached()
        => WorkspaceViewTests.OnSta(() =>
        {
            var surface = new OverviewAnnotationSurface();
            surface.SetDrawingLayers([new FillLayer()]);
            surface.Update(OverviewAnnotationSnapshot.Create([], 0, 0, 1, new(0, 0, 800, 300), null, false,
                placeholders: [new((IntPtr)42, "Card", new WorldRect { X = 100, Y = 100, W = 200, H = 150 }, ImageOpacity: 0)]));
            surface.Measure(new Size(800, 300)); surface.Arrange(new Rect(0, 0, 800, 300));
            byte[] Render()
            {
                surface.UpdateLayout();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(800, 300, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(surface);
                var pixels = new byte[800 * 300 * 4]; bitmap.CopyPixels(pixels, 3200, 0); return pixels;
            }
            var pixels = Render();
            int cardPixel = (110 * 800 + 110) * 4;
            Assert.Equal(255, pixels[cardPixel + 2]); Assert.Equal(0, pixels[cardPixel]);
            int hudPixel = (22 * 800 + 40) * 4;
            Assert.NotEqual(255, pixels[hudPixel + 2]); // HUD background is above the red drawing layer.
            int statsPixel = (22 * 800 + 150) * 4;
            Assert.NotEqual(255, pixels[statsPixel + 2]); // Counts sit to the right of zoom on the same row.
            surface.SetDrawingLayers([]);
            pixels = Render();
            Assert.NotEqual(255, pixels[cardPixel + 2]);
        });

    [Fact]
    public void SceneSnapshotsDoNotRetainMutableInputCollections()
    {
        var zones = new List<ZoneView> { new(Guid.NewGuid(), "Area", "#60A5FA", new() { W = 100, H = 100 }) };
        var cards = new List<PlaceholderAnnotation> { new((IntPtr)1, "Card", new() { W = 200, H = 100 }) };
        var ghosts = new List<WorldRect> { new() { W = 100, H = 100 } };
        var frame = OverviewAnnotationSnapshot.Create(zones, 0, 0, 1, new(0, 0, 800, 300), null, false, ghosts, cards);
        zones.Clear(); cards.Clear(); ghosts.Clear();
        Assert.Single(frame.Zones); Assert.Single(frame.Placeholders); Assert.Single(frame.Ghosts);
    }
    private sealed class CameraRecorder : IOverviewAnnotationLayer
    {
        public CanvasDesktop.CanvasView.CanvasViewTransform? LastView { get; private set; }
        public void Draw(DrawingContext dc,OverviewAnnotationContext context) => LastView=context.View;
    }

    [Fact]
    [Trait("Category","NativeRendering")]
    public void CameraFramesRenderBeforeUpdateReturnsDuringInputBursts()
        => WorkspaceViewTests.OnSta(()=>
        {
            var layer=new CameraRecorder();var window=new OverviewAnnotationWindow(IntPtr.Zero);
            try
            {
                window.SetDrawingLayers([layer]);
                for (int i=0;i<24;i++)
                {
                    double zoom=1-i*.025;
                    window.Update(OverviewAnnotationSnapshot.Create([],i*3,i*2,zoom,new(0,0,300,220),null,false,showHints:false));
                    // No DoEvents, sleep, bitmap render or layout call in the test:
                    // the native-window camera update must flush its own WPF frame.
                    Assert.NotNull(layer.LastView);
                    Assert.Equal(zoom,layer.LastView.Value.Zoom);
                    Assert.Equal(i*3,layer.LastView.Value.CameraX);
                }
            }
            finally { window.Close(); }
        });

}
