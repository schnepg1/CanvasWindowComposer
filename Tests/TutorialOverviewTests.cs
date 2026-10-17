using CanvasDesktop;
using CanvasDesktop.CanvasView;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class TutorialOverviewTests
{
    [Fact]
    [Trait("Category","NativeRendering")]
    public void FirstIntroLandsOnReadableNotesAndRevisitDoesNotDuplicateOrChangePersonalNotes()
        =>WorkspaceViewTests.OnSta(()=>
        {
            string folder=Path.Combine(Path.GetTempPath(),"canvas-tutorial-ui-"+Guid.NewGuid().ToString("N"));
            try
            {
                Assert.True(GridRenderer.CompileShaders());
                var canvas=new Canvas();canvas.SetCamera(1000,-500);var saved=canvas.Camera;
                var api=new FakeWindowApi();var input=new FakeInputRouter();var config=new FakeAppConfig();
                using var wm=new WindowManager(canvas,api,config,input,new FakeClock());
                using var overview=new OverviewManager(canvas,wm,api,input,config,prefersReducedStartupMotion:()=>false);
                overview.AttachAnnotations(new FakeVirtualDesktops(),Path.Combine(folder,"annotations.json"));
                overview.AttachTutorial(Path.Combine(folder,"tutorial.json"));
                Assert.True(overview.TryStartStartupIntro());
                overview.AdvanceStartupIntro(3.5);
                Assert.True(overview.TutorialNotesVisible);
                Assert.Equal(overview.TutorialReadingView(overview.CurrentTutorial!.Layout),overview.OverviewView);
                Assert.False(wm.SuspendAutomaticParking);
                var ids=overview.CurrentTutorial.Layout.Items.Select(a=>a.Id).ToArray();
                overview.HideTutorialNotes();Assert.False(overview.TutorialNotesVisible);
                overview.ShowTutorialNotes();Assert.True(overview.TutorialNotesVisible);
                Assert.Equal(ids,overview.CurrentTutorial.Layout.Items.Select(a=>a.Id));
                Assert.Empty(overview.Annotations!.Items);Assert.False(overview.Annotations.Undo(Guid.Empty));
                overview.TransitionTo(OverviewMode.Hidden);
                Assert.Equal(saved,canvas.Camera);
                Assert.False(new TutorialPersistence(Path.Combine(folder,"tutorial.json")).CanPresent);
                Assert.Empty(api.MinimizeCalls);Assert.Empty(api.ParkingRestoreCalls);
            }
            finally { if(Directory.Exists(folder))Directory.Delete(folder,true); }
        });

    [Fact]
    public void TutorialFlightEndsAtItsTargetAndReducedMotionIsStationary()
    {
        var saved=new CanvasCamera(100,-100,.7);var landing=new CanvasCamera(3000,800,.95);
        var flight=new StartupIntroFlight(saved,new(-1920,0,1920,1080),[],landing:landing);
        var frame=flight.Sample(3.5);
        Assert.Equal(landing,new(frame.X,frame.Y,frame.Zoom));
        var before=flight.Sample(3.49999);
        Assert.Equal(landing.X,before.X,4);Assert.Equal(landing.Y,before.Y,4);Assert.Equal(landing.Zoom,before.Zoom,4);
        var reduced=new StartupIntroFlight(saved,new(0,0,1920,1080),[],true,landing);
        Assert.Equal(reduced.Sample(0).X,reduced.Sample(.2).X);
        Assert.Equal(landing.X,reduced.Sample(0).X);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void AllInstructionTextFitsWithoutTrimmingAndLayerPaintsAtDpi(double scale)
        =>WorkspaceViewTests.OnSta(()=>
        {
            var layout=TutorialLayout.Create(Guid.Empty,new(0,0,1920,1080),[],[],scale);
            foreach(var item in layout.Items.Where(a=>a.Text.Length>0))
            {
                var local=item with { A=new(item.A.X/scale,item.A.Y/scale),B=new(item.B.X/scale,item.B.Y/scale) };
                var bounds=AnnotationVisual.TextBounds(local);
                var text=new FormattedText(item.Text,System.Globalization.CultureInfo.CurrentUICulture,FlowDirection.LeftToRight,
                    AnnotationVisual.Typeface,AnnotationVisual.FontSize,Brushes.White,scale) { MaxTextWidth=bounds.Width };
                Assert.True(text.Height<=bounds.Height,$"Text clips: {item.Text}\n{text.Height} > {bounds.Height}");
            }
            var layer=new TutorialAnnotationLayer(()=>new(Guid.Empty,layout),()=>true);
            var size=new Size(800,600);var visual=new DrawingVisual();
            var view=new CanvasViewTransform(layout.Welcome.X-20,layout.Welcome.Y-20,1,new(0,0,800,600),scale,scale);
            var snapshot=OverviewAnnotationSnapshot.Create([],view.CameraX,view.CameraY,1,new(0,0,800,600),null,false);
            using(var dc=visual.RenderOpen())layer.Draw(dc,new(snapshot,view,new DpiScale(scale,scale),size));
            var bitmap=new RenderTargetBitmap(800,600,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);
            var bytes=new byte[800*600*4];bitmap.CopyPixels(bytes,800*4,0);
            Assert.Contains(bytes,b=>b!=0);
        });
}
