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
    [Trait("Category","NativeRendering")]
    public void InlineEditorReceivesNativeTextInputFromTheFormsMessageLoop()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var owner = new System.Windows.Forms.Form();
            owner.Show();
            var item = new CanvasAnnotation(Guid.NewGuid(),Guid.NewGuid(),AnnotationKind.Text,new(200,200),new(440,248));
            using var editor = new AnnotationTextEditor(item);
            editor.SetPlacement(new CanvasViewTransform(0,0,1,new(0,0,1920,1080)),new(0,0,1920,1080));
            editor.Show(owner); editor.Activate(); editor.Editor.Focus();
            System.Windows.Forms.Application.DoEvents();
            // Address the editor HWND directly so a foreground change elsewhere on the
            // shared desktop cannot send this test's keystrokes into another application.
            var handle = (Windows.Win32.Foundation.HWND)new System.Windows.Interop.WindowInteropHelper(editor).Handle;
            foreach (char character in "CATEGORY")
                Windows.Win32.PInvoke.PostMessage(handle,0x0102,(Windows.Win32.Foundation.WPARAM)(uint)character,0);
            System.Windows.Forms.Application.DoEvents();
            Assert.Equal("CATEGORY",editor.Editor.Text);
            bool? saved = null;
            editor.Finished += value => saved = value;
            editor.Complete(true);
            Assert.True(saved);
        });

    [Fact]
    public void ScaledTitlePreservesItsLineCountAndStillRendersWhenZoomedOut()
        => WorkspaceViewTests.OnSta(() =>
        {
            var title = new CanvasAnnotation(Guid.NewGuid(),Guid.NewGuid(),AnnotationKind.Text,
                new(50,50),new(290,98),Text:"MY PROJECTS",FontSize:18,CardFill:AnnotationCardFill.None);
            title = AnnotationVisual.FitText(title);
            var enlarged = AnnotationGeometry.Resize(title,2,new(title.Bounds.Width*24,title.Bounds.Height*24),false);
            enlarged = AnnotationVisual.FitText(enlarged);
            Assert.Equal(AnnotationTextLayout.Layout(title,title.Bounds.Width-16)/title.FontSize,
                AnnotationTextLayout.Layout(enlarged,enlarged.Bounds.Width-16)/enlarged.FontSize,3);
            var doc = new AnnotationDocument([enlarged]);
            var surface = new OverviewAnnotationSurface();
            surface.SetDrawingLayers([new CanvasAnnotationLayer(doc,new AnnotationCanvasTool(doc,()=>title.DesktopId),()=>title.DesktopId,()=>true)]);
            surface.Update(OverviewAnnotationSnapshot.Create([],0,0,.08,new(0,0,600,240),null,false,showHints:false));
            surface.Measure(new Size(600,240)); surface.Arrange(new Rect(0,0,600,240)); surface.UpdateLayout();
            var bitmap = new RenderTargetBitmap(600,240,96,96,PixelFormats.Pbgra32); bitmap.Render(surface);
            var pixels = new byte[600*240*4]; bitmap.CopyPixels(pixels,2400,0);
            Assert.True(Enumerable.Range(0,600*240).Count(i=>pixels[i*4+3]>100)>500);
        });

    [Fact]
    public void NotesBeyondTheScreenKeepTheCaretReachableWithoutShrinkingText()
        => WorkspaceViewTests.OnSta(() =>
        {
            var item = new CanvasAnnotation(Guid.NewGuid(),Guid.NewGuid(),AnnotationKind.Text,
                new(200,300),new(440,348),Text:"Start");
            using var editor = new AnnotationTextEditor(item);
            editor.SetPlacement(new CanvasViewTransform(0,0,1,new(0,0,1000,600)),new(0,0,1000,600));
            editor.Show(); System.Windows.Forms.Application.DoEvents();
            editor.Editor.Text = string.Join("\n",Enumerable.Repeat("Another line",80));
            editor.Editor.CaretIndex = editor.Editor.Text.Length;
            System.Windows.Forms.Application.DoEvents();
            var viewport = Assert.IsType<System.Windows.Controls.ScrollViewer>(editor.Content);
            viewport.UpdateLayout();
            Assert.True(viewport.VerticalOffset>0);
            Assert.True(editor.CurrentItem.Bounds.Height>editor.Height*VisualTreeHelper.GetDpi(editor).DpiScaleY);
            Assert.Equal(item.FontSize,editor.FontSize);
        });

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
            surface.Update(OverviewAnnotationSnapshot.Create([area],0,0,1,new(0,0,1100,750),null,false,showHints:true)
                with { WindowStats = "Active: 3  ·  Minimized: 2 (auto: 1)", MinimizationStatus = "Auto-minimize: on" });
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

    [Theory]
    [InlineData(AnnotationKind.Arrow)]
    [InlineData(AnnotationKind.Rectangle)]
    [InlineData(AnnotationKind.Ellipse)]
    [InlineData(AnnotationKind.Text)]
    public void WholeArtworkTracksCameraScaleAndPosition(AnnotationKind kind)
        => WorkspaceViewTests.OnSta(()=>
        {
            var desktop=Guid.NewGuid();
            var item=new CanvasAnnotation(Guid.NewGuid(),desktop,kind,new(50,60),new(250,180),Text:"Zoom with me");
            var document=new AnnotationDocument([item]);
            var surface=new OverviewAnnotationSurface();
            surface.SetDrawingLayers([new CanvasAnnotationLayer(document,new AnnotationCanvasTool(document,()=>desktop),()=>desktop,()=>true)]);
            surface.Measure(new Size(300,220));surface.Arrange(new Rect(0,0,300,220));
            (int Width,int Height,int Left) Extent(double x,double y,double zoom)
            {
                surface.Update(OverviewAnnotationSnapshot.Create([],x,y,zoom,new(0,0,300,220),null,false,showHints:false));
                surface.UpdateLayout();var bitmap=new RenderTargetBitmap(300,220,96,96,PixelFormats.Pbgra32);bitmap.Render(surface);
                var bytes=new byte[300*220*4];bitmap.CopyPixels(bytes,1200,0);
                var points=Enumerable.Range(0,300*220).Where(i=>bytes[i*4+3]>0).ToArray();
                return (points.Max(i=>i%300)-points.Min(i=>i%300)+1,points.Max(i=>i/300)-points.Min(i=>i/300)+1,points.Min(i=>i%300));
            }
            var full=Extent(0,0,1);var small=Extent(0,0,.5);var panned=Extent(20,0,.5);
            Assert.InRange(small.Width,full.Width*.45,full.Width*.55);
            Assert.InRange(small.Height,full.Height*.45,full.Height*.55);
            Assert.InRange(small.Left-panned.Left,9,11);
        });

    [Fact]
    public void InlineEditorKeepsCanvasWidthAndZoomWhileTextGrows()
        => WorkspaceViewTests.OnSta(() =>
        {
            var item = new CanvasAnnotation(Guid.NewGuid(), Guid.NewGuid(), AnnotationKind.Text,
                new(100,120), new(340,240), Text:"A title");
            using var editor = new AnnotationTextEditor(item);
            editor.SetPlacement(new CanvasViewTransform(20,40,.75,new(0,0,1920,1080)), new(0,0,1920,1080));
            editor.Show(); System.Windows.Forms.Application.DoEvents();
            var root = (FrameworkElement)editor.Content;
            root.UpdateLayout();
            Assert.Equal(WindowStyle.None, editor.WindowStyle);
            Assert.Equal(ResizeMode.NoResize, editor.ResizeMode);
            Assert.Equal(AnnotationVisual.TextBounds(item).Width, editor.Editor.ActualWidth);
            double initialHeight = editor.CurrentItem.Bounds.Height;
            editor.Editor.Text = string.Join("\n", Enumerable.Repeat("Text grows as I type and wraps within this width.", 12));
            System.Windows.Forms.Application.DoEvents(); root.UpdateLayout();
            Assert.True(editor.CurrentItem.Bounds.Height > initialHeight);
            Assert.Equal(item.Bounds.Width, editor.CurrentItem.Bounds.Width);
            Assert.Equal(item.A, editor.CurrentItem.A);
            Assert.Equal(item.Bounds.Width * .75, editor.Width * VisualTreeHelper.GetDpi(editor).DpiScaleX, 1);
            var last = editor.Editor.GetRectFromCharacterIndex(editor.Editor.Text.Length);
            Assert.True(last.Bottom <= editor.Editor.ActualHeight + 1);
            if (Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") is {} workspace)
            {
                Directory.CreateDirectory(Path.Combine(workspace,"TestResults"));
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth),(int)Math.Ceiling(root.ActualHeight),96,96,PixelFormats.Pbgra32);
                bitmap.Render(root); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(workspace,"TestResults","annotation-editor-preview.png")); encoder.Save(output);
            }
        });

    [Theory]
    [InlineData("A larger title")]
    [InlineData("First line\nSecond line\n")]
    [InlineData("LongWordWithoutSpacesLongWordWithoutSpacesLongWordWithoutSpaces")]
    [InlineData("???? ??\nUnicode text")]
    public void TextAndFontChangesNeverHideTheLastLine(string text)
        => WorkspaceViewTests.OnSta(() =>
        {
            var item = new CanvasAnnotation(Guid.NewGuid(),Guid.NewGuid(),AnnotationKind.Text,
                new(50,60),new(290,108),Text:text,FontSize:24);
            using var editor = new AnnotationTextEditor(item);
            editor.Show(); System.Windows.Forms.Application.DoEvents();
            double height = editor.CurrentItem.Bounds.Height;
            editor.FontSize = 72;
            System.Windows.Forms.Application.DoEvents(); ((FrameworkElement)editor.Content).UpdateLayout();
            Assert.Equal(72,editor.Editor.FontSize);
            Assert.Equal(item.Bounds.Width,editor.CurrentItem.Bounds.Width);
            Assert.True(editor.CurrentItem.Bounds.Height >= height);
            Assert.True(editor.Editor.GetRectFromCharacterIndex(text.Length).Bottom <= editor.Editor.ActualHeight + 1, $"Last line: {editor.Editor.GetRectFromCharacterIndex(text.Length)}, editor height: {editor.Editor.ActualHeight}, lines: {editor.Editor.LineCount}, extent: {editor.Editor.ExtentHeight}, viewport: {editor.Editor.ViewportHeight}");
            editor.SetFontSize(1); Assert.Equal(AnnotationDocument.MinimumFontSize,editor.FontSize);
            editor.SetFontSize(AnnotationDocument.MaximumFontSize+1000); Assert.Equal(AnnotationDocument.MaximumFontSize,editor.FontSize);
        });

    [Fact]
    public void TextStyleControlsChangeRenderedArtwork()
        => WorkspaceViewTests.OnSta(() =>
        {
            var desktop=Guid.NewGuid();
            var item=new CanvasAnnotation(Guid.NewGuid(),desktop,AnnotationKind.Text,new(20,20),new(340,240),Text:"Style me",FontSize:28);
            byte[] Render(CanvasAnnotation candidate)
            {
                var document=new AnnotationDocument([candidate]);
                var surface=new OverviewAnnotationSurface();
                surface.SetDrawingLayers([new CanvasAnnotationLayer(document,new AnnotationCanvasTool(document,()=>desktop),()=>desktop,()=>true)]);
                surface.Update(OverviewAnnotationSnapshot.Create([],0,0,1,new(0,0,360,260),null,false,showHints:false));
                surface.Measure(new Size(360,260));surface.Arrange(new Rect(0,0,360,260));surface.UpdateLayout();
                var bitmap=new RenderTargetBitmap(360,260,96,96,PixelFormats.Pbgra32);bitmap.Render(surface);
                var pixels=new byte[360*260*4];bitmap.CopyPixels(pixels,1440,0);return pixels;
            }
            static int Gold(byte[] pixels,int fromX,int toX)
                =>Enumerable.Range(0,360*260).Count(i=> { int x=i%360; return x>=fromX && x<toX && pixels[i*4+2]>200 && pixels[i*4+1]>140; });
            var left=Render(item with { TextAlign=AnnotationTextAlign.Left });
            var right=Render(item with { TextAlign=AnnotationTextAlign.Right });
            Assert.True(Gold(left,0,180)>Gold(left,180,360));
            Assert.True(Gold(right,180,360)>Gold(right,0,180));
            var normal=Render(item);
            var bold=Render(item with { Emphasis=AnnotationTextEmphasis.Bold });
            Assert.True(Gold(bold,0,360)>Gold(normal,0,360));
            static int Alpha(byte[] pixels,int x,int y)=>pixels[(y*360+x)*4+3];
            Assert.True(Alpha(Render(item),60,210)>200);
            Assert.Equal(42,Alpha(Render(item with { CardFill=AnnotationCardFill.Tinted }),60,210));
            Assert.Equal(0,Alpha(Render(item with { CardFill=AnnotationCardFill.None }),60,210));
        });

    [Fact]
    public void EditorExposesStyleControlsAndKeepsKeyboardShortcutsConsistent()
        => WorkspaceViewTests.OnSta(() =>
        {
            var item=new CanvasAnnotation(Guid.NewGuid(),Guid.NewGuid(),AnnotationKind.Text,new(20,20),new(340,240),
                Text:"Style me",FontSize:24,TextAlign:AnnotationTextAlign.Center,
                Emphasis:AnnotationTextEmphasis.BoldItalic,CardFill:AnnotationCardFill.Tinted);
            using var editor=new AnnotationTextEditor(item);
            editor.Show();System.Windows.Forms.Application.DoEvents();
            Assert.Equal(AnnotationTextAlign.Center,editor.TextAlign);
            Assert.Equal(AnnotationTextEmphasis.BoldItalic,editor.Emphasis);
            Assert.Equal(AnnotationCardFill.Tinted,editor.CardFill);
            Assert.Equal(System.Windows.TextAlignment.Center,editor.Editor.TextAlignment);
            Assert.Equal(FontWeights.Bold,editor.Editor.FontWeight);
            Assert.Equal(FontStyles.Italic,editor.Editor.FontStyle);
            editor.SetTextAlign(AnnotationTextAlign.Right);
            Assert.Equal(System.Windows.TextAlignment.Right,editor.Editor.TextAlignment);
            editor.SetEmphasis(AnnotationTextEmphasis.Bold);
            Assert.Equal(FontWeights.Bold,editor.Editor.FontWeight);
            Assert.Equal(FontStyles.Normal,editor.Editor.FontStyle);
            editor.ToggleBold();Assert.Equal(AnnotationTextEmphasis.Normal,editor.Emphasis);
            editor.ToggleItalic();Assert.Equal(AnnotationTextEmphasis.Italic,editor.Emphasis);
            editor.ToggleBold();Assert.Equal(AnnotationTextEmphasis.BoldItalic,editor.Emphasis);
            editor.SetCardFill(AnnotationCardFill.None);Assert.Equal(AnnotationCardFill.None,editor.CardFill);
            Assert.Throws<ArgumentException>(()=>editor.SetTextAlign((AnnotationTextAlign)99));
            Assert.Throws<ArgumentException>(()=>editor.SetEmphasis((AnnotationTextEmphasis)99));
            Assert.Throws<ArgumentException>(()=>editor.SetCardFill((AnnotationCardFill)99));
        });

    [Fact]
    public void LargerTextSizeDrawsMoreCardPixels()
        => WorkspaceViewTests.OnSta(()=>
        {
            var desktop=Guid.NewGuid();
            var item=new CanvasAnnotation(Guid.NewGuid(),desktop,AnnotationKind.Text,new(20,20),new(300,180),Text:"Scale me");
            int Render(double fontSize)
            {
                var document=new AnnotationDocument([item with { FontSize=fontSize }]);
                var surface=new OverviewAnnotationSurface();
                surface.SetDrawingLayers([new CanvasAnnotationLayer(document,new AnnotationCanvasTool(document,()=>desktop),()=>desktop,()=>true)]);
                surface.Update(OverviewAnnotationSnapshot.Create([],0,0,1,new(0,0,340,220),null,false,showHints:false));
                surface.Measure(new Size(340,220));surface.Arrange(new Rect(0,0,340,220));surface.UpdateLayout();
                var bitmap=new RenderTargetBitmap(340,220,96,96,PixelFormats.Pbgra32);bitmap.Render(surface);
                var pixels=new byte[340*220*4];bitmap.CopyPixels(pixels,1360,0);
                return Enumerable.Range(0,340*220).Count(i=>pixels[i*4+2]>200 && pixels[i*4+1]>140);
            }
            Assert.True(Render(40)>Render(10));
        });

}
