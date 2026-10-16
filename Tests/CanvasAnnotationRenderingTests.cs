using CanvasDesktop;
using CanvasDesktop.CanvasView;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class CanvasAnnotationRenderingTests
{
    [Fact]
    public void SpatialGroupOutlinesGhostsAndProximityCircleRenderTogether()
        =>WorkspaceViewTests.OnSta(()=>
        {
            var desktop=Guid.NewGuid();var note=new CanvasAnnotation(Guid.NewGuid(),desktop,AnnotationKind.Text,new(180,160),new(440,310),Text:"Next steps\nReview selection\nGenerate a roadmap");
            var document=new AnnotationDocument([note]);var annotations=new AnnotationCanvasTool(document,()=>desktop);
            var project=new CanvasSelectionItem(new(CanvasItemKind.Project,Guid.NewGuid()),new(80,90,820,500),"Guardian");
            var item=new CanvasSelectionItem(new(CanvasItemKind.Annotation,note.Id),note.Bounds,note.Text);
            var window=new CanvasSelectionItem(new(CanvasItemKind.Window,Guid.NewGuid()),new(520,180,300,230),"Editor");
            CanvasSelectionItem[] items=[project,item,window];var selection=new CanvasSelection();selection.SwitchDesktop(desktop);selection.Pick(project.Key,items,false);
            var tool=new CanvasSelectionTool(selection,()=>items,_=>item.Key,()=>true);
            var view=new CanvasViewTransform(0,0,1,new(0,0,1100,750));
            tool.PointerDown(new(0,new(250,230),CanvasPointerButton.Left,CanvasModifiers.None,view));
            tool.PointerMove(new(0,new(330,290),CanvasPointerButton.Left,CanvasModifiers.None,view));
            var surface=new OverviewAnnotationSurface();
            surface.SetDrawingLayers([new CanvasAnnotationLayer(document,annotations,()=>desktop,()=>true),new SelectionAnnotationLayer(selection,()=>items,tool,()=> (new CanvasPoint(520,360),320),()=>true)]);
            var area=new ZoneView(project.Key.Id,"Guardian","#60A5FA",new WorldRect { X=80,Y=90,W=820,H=500 });
            surface.Update(OverviewAnnotationSnapshot.Create([area],0,0,1,new(0,0,1100,750),null,false,showHints:true,toolHint:"3 selected · Drag selected: move group · Escape: cancel"));
            surface.Measure(new Size(1100,750));surface.Arrange(new Rect(0,0,1100,750));surface.UpdateLayout();
            var bitmap=new RenderTargetBitmap(1100,750,96,96,PixelFormats.Pbgra32);bitmap.Render(surface);
            var pixels=new byte[1100*750*4];bitmap.CopyPixels(pixels,4400,0);Assert.Contains(pixels,b=>b!=0);
            if(Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") is {} workspace)
            {
                Directory.CreateDirectory(Path.Combine(workspace,"TestResults"));var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output=File.Create(Path.Combine(workspace,"TestResults","spatial-selection-preview.png"));encoder.Save(output);
            }
        });
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
