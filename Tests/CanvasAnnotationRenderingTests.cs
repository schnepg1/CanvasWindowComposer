using CanvasDesktop;
using CanvasDesktop.CanvasView;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class CanvasAnnotationRenderingTests
{
    [Theory]
    [InlineData(AnnotationKind.Arrow)]
    [InlineData(AnnotationKind.Rectangle)]
    [InlineData(AnnotationKind.Ellipse)]
    [InlineData(AnnotationKind.Text)]
    public void DrawingLayerRendersEachKindAndFiltersOtherDesktops(AnnotationKind kind)
        =>WorkspaceViewTests.OnSta(()=>
        {
            var desktop=Guid.NewGuid();var current=desktop;
            var item=new CanvasAnnotation(Guid.NewGuid(),desktop,kind,new(50,60),new(200,160),"#FB7185",3,"Note");
            var doc=new AnnotationDocument([item]);var tool=new AnnotationCanvasTool(doc,()=>current);
            var surface=new OverviewAnnotationSurface();surface.SetDrawingLayers([new CanvasAnnotationLayer(doc,tool,()=>current,()=>true)]);
            surface.Update(OverviewAnnotationSnapshot.Create([],0,0,1,new(0,0,300,220),null,false,showHints:false));
            surface.Measure(new Size(300,220));surface.Arrange(new Rect(0,0,300,220));
            byte[] Render()
            {
                surface.InvalidateVisual();surface.UpdateLayout();var bitmap=new RenderTargetBitmap(300,220,96,96,PixelFormats.Pbgra32);
                bitmap.Render(surface);var bytes=new byte[300*220*4];bitmap.CopyPixels(bytes,1200,0);return bytes;
            }
            Assert.Contains(Render(),b=>b!=0);current=Guid.NewGuid();Assert.All(Render(),b=>Assert.Equal(0,b));
        });
    [Theory]
    [InlineData(AnnotationKind.Rectangle)]
    [InlineData(AnnotationKind.Ellipse)]
    public void ShapeLabelsAddVisibleTextWithinTheirGeometry(AnnotationKind kind)
        => WorkspaceViewTests.OnSta(() =>
        {
            var desktop = Guid.NewGuid();
            var item = new CanvasAnnotation(Guid.NewGuid(), desktop, kind, new(40,40), new(260,180), Text:"Finish");
            int Render(string text)
            {
                var document = new AnnotationDocument([item with { Text=text }]);
                var surface = new OverviewAnnotationSurface();
                surface.SetDrawingLayers([new CanvasAnnotationLayer(document, new AnnotationCanvasTool(document, () => desktop), () => desktop, () => true)]);
                surface.Update(OverviewAnnotationSnapshot.Create([],0,0,1,new(0,0,300,220),null,false,showHints:false));
                surface.Measure(new Size(300,220)); surface.Arrange(new Rect(0,0,300,220)); surface.UpdateLayout();
                var bitmap = new RenderTargetBitmap(300,220,96,96,PixelFormats.Pbgra32); bitmap.Render(surface);
                var pixels = new byte[300*220*4]; bitmap.CopyPixels(pixels,1200,0);
                return Enumerable.Range(0,300*220).Count(i => pixels[i*4+3]>0);
            }
            Assert.True(Render("Finish") > Render(""));
        });

}
