using System.Windows.Forms;
using System.Runtime.InteropServices;
using CanvasDesktop;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;

namespace CanvasDesktop.Tests;

public class RenderingNativeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "NativeRendering")]
    public void OwnedSurfacePresentsAndResizesWithNoRetainedBackBufferReferences(bool flip)
        => WorkspaceViewTests.OnSta(() =>
        {
            using var host = new Form(); // unshown test HWND, no user windows or input hooks
            using var surface = D3DRenderSurface.Create(host.Handle, 128, 80, flip);
            Assert.NotNull(surface);
            Assert.Equal(flip, surface.UsesFlipModel);
            Assert.Equal(flip ? 2u : 1u, surface.SwapChain.Description.BufferCount);
            surface.Context.ClearRenderTargetView(surface.Target, new Vortice.Mathematics.Color4(1, 0, 0, 1));
            using (var source = surface.SwapChain.GetBuffer<ID3D11Texture2D>(0))
            {
                var desc = source.Description;
                desc.Usage = ResourceUsage.Staging; desc.BindFlags = BindFlags.None;
                desc.CPUAccessFlags = CpuAccessFlags.Read; desc.MiscFlags = ResourceOptionFlags.None;
                using var readback = surface.Device.CreateTexture2D(desc);
                surface.Context.CopyResource(readback, source);
                var pixels = surface.Context.Map(readback, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    Assert.Equal(255, Marshal.ReadByte(pixels.DataPointer, 0));
                    Assert.Equal(0, Marshal.ReadByte(pixels.DataPointer, 2));
                }
                finally { surface.Context.Unmap(readback, 0); }
            }
            surface.Context.OMSetRenderTargets(surface.Target); // Resize must unbind this reference.
            Assert.True(surface.SwapChain.Present(0, PresentFlags.None).Success);
            surface.Resize(96, 64);
            using var buffer = surface.SwapChain.GetBuffer<ID3D11Texture2D>(0);
            Assert.Equal(96u, buffer.Description.Width); Assert.Equal(64u, buffer.Description.Height);
            surface.Context.ClearRenderTargetView(surface.Target, new Vortice.Mathematics.Color4(0, 0, 1, 1));
            Assert.True(surface.SwapChain.Present(0, PresentFlags.None).Success);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "NativeRendering")]
    public void DwmAcceptsLiveAndCachedSourcesAlongsideEitherPresentationBackend(bool flip)
        => WorkspaceViewTests.OnSta(() =>
        {
            using var host = new Form(); using var source = new Form();
            using var surface = D3DRenderSurface.Create(host.Handle, 128, 80, flip);
            Assert.NotNull(surface);
            using var image = new System.Drawing.Bitmap(64, 40);
            using var cached = new CachedPreviewWindow(image);
            var dwm = new DwmThumbnailApi(new PerformanceDiagnostics());
            foreach (var hwnd in new[] { source.Handle, cached.Handle })
            {
                Assert.True(dwm.Register((HWND)host.Handle, (HWND)hwnd, out var thumbnail).Succeeded);
                try
                {
                    Assert.True(dwm.Update(thumbnail, new DWM_THUMBNAIL_PROPERTIES
                    {
                        dwFlags = 1 | 8, fVisible = true,
                        rcDestination = new RECT { left = 8, top = 8, right = 120, bottom = 72 }
                    }).Succeeded);
                    surface.Context.ClearRenderTargetView(surface.Target, new Vortice.Mathematics.Color4(0, 0, 0, 1));
                    Assert.True(surface.SwapChain.Present(0, PresentFlags.None).Success);
                }
                finally { Assert.True(dwm.Unregister(thumbnail).Succeeded); }
            }
            // This verifies native acceptance, not pixels of the final DWM composition.
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "NativeRendering")]
    public void ProductionGridAndMinimapInitializeResizeAndRelease(bool flip)
        => WorkspaceViewTests.OnSta(() =>
        {
            using var gridHost = new Form(); using var mapHost = new Form();
            Assert.True(GridRenderer.CompileShaders()); Assert.True(MinimapRenderer.CompileShaders());
            using var grid = new GridRenderer(flip);
            Assert.True(grid.Initialize(gridHost.Handle, 128, 80)); Assert.Equal(flip, grid.UsesFlipModel);
            grid.Render(0, 0, 1); grid.Resize(96, 64); grid.Render(10, 20, 1);
            grid.LowQuality = true; grid.Render(10, 20, 0.25);
            grid.LowQuality = false; grid.Render(10, 20, 0.25);
            using var map = new MinimapRenderer(flip);
            Assert.True(map.Initialize(mapHost.Handle, 128, 80, 8, 8, 112, 64));
            Assert.Equal(flip, map.UsesFlipModel);
            map.UpdateSnapshot([], null, (0, 0, 1920, 1080)); map.Resize(96, 64);
            map.StartThread(); map.Start(); map.Stop();
        });

    [Fact]
    public void InvalidSurfaceDimensionsFailClosed()
    {
        Assert.Null(D3DRenderSurface.Create(IntPtr.Zero, 128, 80, true));
        Assert.Null(D3DRenderSurface.Create((IntPtr)1, 0, 80, true));
    }
}
