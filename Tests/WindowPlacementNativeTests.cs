using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

/// <summary>
/// Small native smoke harness: only creates unshown windows owned by this test process.
/// Exercises real USER32/GDI placement and clipping without moving any user windows.
/// Synthetic monitor coordinates make these tests independent of the host topology.
/// </summary>
public class WindowPlacementNativeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "NativePlacement")]
    public void NativeRestoreAndBatchProjection_ApplyRegionAndKeepCanvasCoordinates(bool disableClipping)
        => OnSta(() =>
        {
            using var window = new Form { FormBorderStyle = FormBorderStyle.None, AutoScaleMode = AutoScaleMode.None };
            var handle = window.Handle;
            var api = new Win32WindowApi(new FakeScreens());
            var canvas = new Canvas();
            using var wm = new WindowManager(canvas, api, new FakeAppConfig { DisableGreedyDraw = disableClipping }, new FakeInputRouter());
            var world = new WorldRect { X = 5000, Y = 5000, W = 800, H = 600 };

            // Explicit enrollment is also the path used by Canvas's own placeholder HWNDs.
            wm.RestorePlacement(handle, world);
            Assert.Equal(disableClipping ? (5000, 5000, 800, 600) : (1919, 1039, 800, 600), api.GetWindowRect(handle));
            Assert.Equal(disableClipping ? 0 : 1, RegionType(handle)); // ERROR/no region vs NULLREGION/empty.
            Assert.Equal(world, canvas.Windows[handle]);

            canvas.SetWindow(handle, 100, 200, 800, 600);
            wm.ReprojectSync();
            Assert.Equal((100, 200, 800, 600), api.GetWindowRect(handle));
            Assert.Equal(0, RegionType(handle));
            Assert.False(window.Visible);
        });

    [Fact]
    [Trait("Category", "NativePlacement")]
    public void InvalidNativeWindow_ReportsFailuresInsteadOfSilentSuccess()
    {
        var diagnostics = new PerformanceDiagnostics();
        var api = new Win32WindowApi(diagnostics: diagnostics);
        IntPtr invalid = new(-1);
        Assert.False(api.SetWindowPosition(invalid, 0, 0, 200, 100, 0x14).Accepted);
        Assert.False(api.ClipWindow(invalid).Accepted);
        Assert.False(api.UnclipWindow(invalid).Accepted);
        Assert.False(api.RestoreNativeState(invalid, new(0, 0, 200, 100), WindowState.Normal).Accepted);
        Assert.Equal(4, diagnostics.Snapshot().RecentFailures.Length);
    }

    [Fact]
    [Trait("Category", "NativePlacement")]
    public void NativeAsyncBatch_AcceptsValidWindowWithoutUnsupportedDeferredFlag()
        => OnSta(() =>
        {
            using var window = new Form();
            var diagnostics = new PerformanceDiagnostics();
            var api = new Win32WindowApi(diagnostics: diagnostics);
            var result = api.BatchMove([new(window.Handle, new(100, 100, 300, 200), true)], true, true);
            Assert.Equal(NativeOperationStatus.Queued, result.Summary.Status);
            Assert.Empty(diagnostics.Snapshot().RecentFailures);
            Assert.DoesNotContain(diagnostics.Snapshot().Operations, o => o.Operation == "DeferWindowPos");
        });

    private static int RegionType(IntPtr window)
    {
        var region = CreateRectRgn(0, 0, 0, 0);
        Assert.NotEqual(IntPtr.Zero, region);
        try { return GetWindowRgn(window, region); }
        finally { DeleteObject(region); }
    }

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetWindowRgn(IntPtr window, IntPtr region);
    [DllImport("gdi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr value);

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Native placement smoke test timed out.");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
