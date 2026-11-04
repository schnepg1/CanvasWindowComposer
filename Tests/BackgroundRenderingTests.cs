using CanvasDesktop;
using CanvasDesktop.CanvasView;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class BackgroundRenderingTests
{
    [Fact]
    [Trait("Category","NativeRendering")]
    public void DesktopHostIsAChildWithPassiveStylesAndCanHideAndClose()
        => WorkspaceViewTests.OnSta(()=>
        {
            using var parent=new System.Windows.Forms.Form { ShowInTaskbar=false,Bounds=new System.Drawing.Rectangle(0,0,400,250) };
            _=parent.Handle;
            using var cache=new BackgroundImageCache(new BackgroundAssets(Path.GetTempPath()),new System.Threading.SynchronizationContext());
            var window=new DesktopBackgroundWindow(new(cache),()=>parent.Handle);
            var interop=new System.Windows.Interop.WindowInteropHelper(window);
            try
            {
                window.Present(new(0,0,400,250),new(0,0,1),[],false);
                Assert.Equal(parent.Handle,(IntPtr)Windows.Win32.PInvoke.GetParent((Windows.Win32.Foundation.HWND)interop.Handle));
                int style=Windows.Win32.PInvoke.GetWindowLong((Windows.Win32.Foundation.HWND)interop.Handle,
                    Windows.Win32.UI.WindowsAndMessaging.WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
                Assert.Equal(0x080000A0,style&0x080000A0); // no activate, tool window, click-through
                Assert.True(window.IsVisible);window.Hide();Assert.False(window.IsVisible);
                window.Present(new(0,0,400,250),new(500,200,1),[],true);Assert.True(window.IsVisible);
            }
            finally { window.Close(); }
            Assert.False(window.IsAlive);
        });

    [Fact]
    public void FillCropAndFitKeepProportions()
    {
        var frame=new Rect(0,0,100,100);
        Assert.Equal(new Rect(-50,0,200,100),BackgroundVisual.ImageBounds(frame,400,200,BackgroundFit.Fill,.5,.5));
        Assert.Equal(new Rect(-100,0,200,100),BackgroundVisual.ImageBounds(frame,400,200,BackgroundFit.Fill,1,.5));
        Assert.Equal(new Rect(0,25,100,50),BackgroundVisual.ImageBounds(frame,400,200,BackgroundFit.Fit,.5,.5));
        Assert.True(BackgroundVisual.ImageBounds(new Rect(0,0,1,1e308),400,200,BackgroundFit.Fill,.5,.5).IsEmpty);
    }
    [Fact]
    public void SharedPainterMovesWithCameraAndKeepsNativeContentClear()
        => WorkspaceViewTests.OnSta(()=>
        {
            string directory=Path.Combine(Path.GetTempPath(),"canvas-bg-render-"+Guid.NewGuid());Directory.CreateDirectory(directory);
            try
            {
                string path=Path.Combine(directory,"red.png");
                var bitmap=BitmapSource.Create(1,1,96,96,PixelFormats.Bgra32,null,new byte[]{0,0,255,255},4);
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(path))encoder.Save(file);
                var assets=new BackgroundAssets(Path.Combine(directory,"assets"));var asset=assets.Import(path);
                using var cache=new BackgroundImageCache(assets,new System.Threading.SynchronizationContext());
                var until=DateTime.UtcNow.AddSeconds(10);
                while(cache.TryGet(asset.Id,true)==null && DateTime.UtcNow<until)Thread.Sleep(10);
                Assert.NotNull(cache.TryGet(asset.Id,true));
                var painter=new BackgroundVisual(cache);
                var item=new BackgroundSurface(Guid.NewGuid(),Guid.NewGuid(),"Red",asset.Id,new(){X=100,Y=40,W=300,H=180});
                byte[] Render(double cameraX,IReadOnlyList<WorldRect>? holes=null)
                {
                    var visual=new DrawingVisual();using(var dc=visual.RenderOpen())painter.Draw(dc,[item],new(cameraX,0,1,default),new Size(500,250),false,holes);
                    var target=new RenderTargetBitmap(500,250,96,96,PixelFormats.Pbgra32);target.Render(visual);
                    byte[] pixels=new byte[500*250*4];target.CopyPixels(pixels,500*4,0);return pixels;
                }
                byte Alpha(byte[] pixels,int x,int y)=>pixels[(y*500+x)*4+3];
                var first=Render(0);Assert.Equal(255,Alpha(first,120,80));Assert.Equal(0,Alpha(first,50,80));
                var moved=Render(100);Assert.Equal(255,Alpha(moved,20,80));Assert.Equal(0,Alpha(moved,350,80));
                var clipped=Render(0,[new(){X=120,Y=70,W=80,H=80}]);
                Assert.Equal(0,Alpha(clipped,140,90));Assert.Equal(255,Alpha(clipped,220,90));

                var zone=new ZoneView(Guid.NewGuid(),"Room","#60A5FA",item.Bounds);
                var service=new BackgroundService(new BackgroundPersistence(Path.Combine(directory,"backgrounds.json")),
                    ()=>item.DesktopId,()=>[zone]);
                service.Add(item with { ZoneId=zone.Id });
                var layer=new BackgroundAnnotationLayer(service,painter,()=>false,new(service));
                var movedBounds=item.Bounds;movedBounds.X=200;
                var frame=OverviewAnnotationSnapshot.Create([zone with { Bounds=movedBounds }],0,0,1,
                    new(0,0,500,250),null,false);
                var drawing=new DrawingVisual();
                using(var dc=drawing.RenderOpen())layer.Draw(dc,new(frame,new(0,0,1,default),new DpiScale(1,1),new Size(500,250)));
                var preview=new RenderTargetBitmap(500,250,96,96,PixelFormats.Pbgra32);preview.Render(drawing);
                byte[] previewPixels=new byte[500*250*4];preview.CopyPixels(previewPixels,500*4,0);
                Assert.Equal(0,Alpha(previewPixels,120,80));Assert.Equal(255,Alpha(previewPixels,220,80));
                Assert.Equal(item.Bounds,service.Items.Single().Bounds); // Preview does not persist until the area gesture completes.
            }
            finally{Directory.Delete(directory,true);}
        });
}
