using System;
using System.Drawing;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Forms;
using CanvasDesktop;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Xunit;

namespace CanvasDesktop.Tests;

public class WindowPreviewTests
{
    private static AutomaticParkingToken Key(int handle = 42) => new(new((IntPtr)handle, 7, 100), Guid.NewGuid());
    private sealed class FakeCapture : IWindowPreviewCapture
    {
        public Func<Size, Bitmap?> Produce = size => new Bitmap(size.Width, size.Height);
        public Bitmap? Capture(IntPtr handle, Size sourceSize, Size targetSize) => Produce(targetSize);
    }
    private sealed class Surface : ICachedPreviewSurface
    {
        public IntPtr Handle { get; } = (IntPtr)987;
        public bool Disposed;
        public void Dispose() => Disposed = true;
    }
    private static void Finish(WindowPreviewCache cache)
    {
        Assert.True(SpinWait.SpinUntil(() => { cache.DrainCompleted(); return !cache.CapturePending; }, 5000));
    }
    private static void CaptureImage(WindowPreviewCache cache, AutomaticParkingToken key, int size = 10)
    {
        cache.Observe(key.Identity.Handle, key);
        Assert.True(cache.RequestCapture(key, new(size, size)));
        Finish(cache);
        Assert.True(cache.HasImage(key));
    }

    [Theory]
    [InlineData(3840, 2160, 1024, 576)]
    [InlineData(1000, 2000, 288, 576)]
    [InlineData(100, 50, 100, 50)]
    [InlineData(0, 100, 0, 0)]
    [InlineData(int.MaxValue, 1, 1024, 1)]
    public void ResolutionIsCappedWithoutUpscaling(int w, int h, int expectedW, int expectedH)
        => Assert.Equal(new Size(expectedW, expectedH), WindowPreviewCache.Fit(new(w, h)));

    [Fact]
    public void ImageBudgetEvictsLeastRecentlyUsedAndCountsInflightReservation()
    {
        using var cache = new WindowPreviewCache(new FakeCapture(), _ => new Surface(), budgetBytes: 800);
        var a = Key(1); var b = Key(2); var c = Key(3);
        CaptureImage(cache, a); CaptureImage(cache, b);
        Assert.Equal(800, cache.ImageBytes);
        Assert.NotEqual(IntPtr.Zero, cache.GetSource(a)); // touch A
        CaptureImage(cache, c);
        Assert.True(cache.HasImage(a));
        Assert.False(cache.HasImage(b));
        Assert.True(cache.HasImage(c));
        Assert.Equal(800, cache.ImageBytes);
    }

    [Fact]
    public void HungCaptureCannotAccumulateJobsAndLateIdentityIsDiscarded()
    {
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var capture = new FakeCapture { Produce = size => { entered.Set(); release.Wait(); return new(size.Width, size.Height); } };
        using var cache = new WindowPreviewCache(capture, _ => new Surface(), budgetBytes: 800);
        var key = Key(); cache.Observe(key.Identity.Handle, key);
        try
        {
            Assert.True(cache.RequestCapture(key, new(10, 10)));
            Assert.True(entered.Wait(5000));
            Assert.Equal(400, cache.ImageBytes);
            Assert.False(cache.RequestCapture(key, new(10, 10)));
            cache.Observe(key.Identity.Handle, key with { Lifetime = Guid.NewGuid() });
        }
        finally { release.Set(); }
        Finish(cache);
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.ImageBytes);
        Assert.False(cache.HasImage(key));
    }

    [Fact]
    public void RemovalAndProcessReplacementDisposeImagesAndSurfaces()
    {
        var surface = new Surface();
        Bitmap? bitmap = null;
        using var cache = new WindowPreviewCache(new FakeCapture(), image => { bitmap = image; return surface; });
        var key = Key(); CaptureImage(cache, key);
        cache.GetSource(key);
        cache.Observe(key.Identity.Handle, key with { Identity = key.Identity with { ProcessStartedAtUtcTicks = 200 } });
        Assert.True(surface.Disposed);
        Assert.ThrowsAny<Exception>(() => bitmap!.GetPixel(0, 0));
        Assert.Equal(0, cache.ImageBytes);
        Assert.Equal(IntPtr.Zero, cache.GetSource(key));
        CaptureImage(cache, key);
        cache.Remove(key.Identity.Handle);
        Assert.False(cache.HasImage(key));
    }

    [Fact]
    public void ClosingOverviewReleasesSurfaceButRetainsImageForReopen()
    {
        var surfaces = new System.Collections.Generic.List<Surface>();
        using var cache = new WindowPreviewCache(new FakeCapture(), _ => { var s = new Surface(); surfaces.Add(s); return s; });
        var key = Key(); CaptureImage(cache, key);
        cache.GetSource(key); cache.GetSource(key);
        Assert.Single(surfaces);
        cache.ReleaseSurfaces();
        Assert.True(surfaces[0].Disposed);
        Assert.True(cache.HasImage(key));
        cache.GetSource(key);
        Assert.Equal(2, surfaces.Count);
        cache.Dispose(); cache.Dispose();
        Assert.True(surfaces[1].Disposed);
        Assert.Equal(0, cache.ImageBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedCaptureLeavesNoImageAndCanRetry(bool throws)
    {
        using var cache = new WindowPreviewCache(new FakeCapture { Produce = _ => throws ? throw new InvalidOperationException() : null });
        var key = Key(); cache.Observe(key.Identity.Handle, key);
        Assert.True(cache.RequestCapture(key, new(10, 10))); Finish(cache);
        Assert.False(cache.HasImage(key));
        Assert.Equal(0, cache.ImageBytes);
        Assert.True(cache.RequestCapture(key, new(10, 10))); Finish(cache);
    }

    [Fact]
    public void DisposalDoesNotWaitForBlockedCaptureAndLateBitmapIsReleased()
    {
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        Bitmap? image = null;
        var capture = new FakeCapture { Produce = size => { entered.Set(); release.Wait(); return image = new(size.Width, size.Height); } };
        using var cache = new WindowPreviewCache(capture);
        var key = Key(); cache.Observe(key.Identity.Handle, key);
        try
        {
            Assert.True(cache.RequestCapture(key, new(10, 10)));
            Assert.True(entered.Wait(5000));
            cache.Dispose();
            Assert.False(cache.RequestCapture(key, new(10, 10)));
        }
        finally { release.Set(); }
        Assert.True(SpinWait.SpinUntil(() => !cache.CapturePending, 5000));
        Assert.ThrowsAny<Exception>(() => image!.GetPixel(0, 0));
        Assert.Equal(0, cache.ImageBytes);
    }

    [Fact]
    public void MissingCachedImageUsesOneBoundedSharedPlaceholder()
    {
        int surfaces = 0;
        using var cache = new WindowPreviewCache(new FakeCapture(), _ => { surfaces++; return new Surface(); });
        var a = Key(1); var b = Key(2);
        cache.Observe(a.Identity.Handle, a); cache.Observe(b.Identity.Handle, b);
        Assert.NotEqual(IntPtr.Zero, cache.GetSource(a));
        Assert.Equal(cache.GetSource(a), cache.GetSource(b));
        Assert.Equal(1, surfaces);
        Assert.InRange(cache.ImageBytes, 1, WindowPreviewCache.DefaultBudgetBytes);
        Assert.False(cache.HasImage(a)); // Placeholder must not authorize native parking.
    }

    private sealed class Dwm : IDwmThumbnailApi
    {
        public readonly System.Collections.Generic.List<IntPtr> Sources = new();
        public readonly System.Collections.Generic.List<IntPtr> Released = new();
        public readonly System.Collections.Generic.List<DWM_THUMBNAIL_PROPERTIES> Updates = new();
        private int _next;
        public HRESULT Register(HWND destination, HWND source, out IntPtr thumbnail)
        { Sources.Add(source); thumbnail = (IntPtr)(++_next); return new(0); }
        public HRESULT Unregister(IntPtr thumbnail) { Released.Add(thumbnail); return new(0); }
        public HRESULT Update(IntPtr thumbnail, DWM_THUMBNAIL_PROPERTIES properties) { Updates.Add(properties); return new(0); }
    }

    [Fact]
    public void ParkingPreparationAndOverviewKeepLogicalLayoutAndNeverRestoreApplication()
        => OnSta(() =>
        {
            var canvas = new Canvas(); var api = new FakeWindowApi(); var clock = new FakeClock();
            using var cache = new WindowPreviewCache(new FakeCapture(), _ => new Surface());
            using var manager = new WindowManager(canvas, api, new FakeAppConfig(), new FakeInputRouter(), clock,
                inspectProcess: pid => new(pid, 100, "test.exe"), previews: cache);
            api.AddWindow((IntPtr)42, 5000, 100, 400, 300, pid: 7);
            manager.RegisterWindow((IntPtr)42);
            var world = canvas.Windows[(IntPtr)42];
            Assert.False(manager.CapturePreviewBeforeParking((IntPtr)42));
            clock.Now = 1500; manager.Tick();
            Assert.True(manager.CapturePreviewBeforeParking((IntPtr)42));
            Finish(cache);
            Assert.True(manager.HasCachedPreview((IntPtr)42));
            // Simulate the future step-5 native boundary without minimizing any real window.
            var tracker = (WindowVisibilityTracker)typeof(WindowManager)
                .GetField("_visibility", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(manager)!;
            var token = tracker.BeginParking((IntPtr)42)!.Value;
            api.Windows[(IntPtr)42].Style = (int)Windows.Win32.UI.WindowsAndMessaging.WINDOW_STYLE.WS_MINIMIZE;
            Assert.True(tracker.CompleteParking(token, NativeOperationResult.Success("Minimize", (IntPtr)42), true));
            manager.RefreshPreviewState();
            Assert.True(manager.UsesCachedPreview((IntPtr)42));
            var list = new OverviewWindowList(canvas, api); list.Refresh();
            Assert.Single(list.Windows);
            using var pass = new OverviewOverlay(Screen.PrimaryScreen!);
            var b = pass.Screen.Bounds;
            var camera = new OverviewCamera(new FakeScreens()); camera.SetTo(5000 - b.X, 100 - b.Y, 1);
            var dwm = new Dwm();
            var thumbnails = new OverviewThumbnails([pass], list, camera, new(), api, new FakeScreens(),
                manager.UsesCachedPreview, manager.GetCachedPreviewSource, dwm);
            try
            {
                thumbnails.Reconcile();
                Assert.Contains((IntPtr)987, dwm.Sources);
                Assert.DoesNotContain((IntPtr)42, dwm.Sources);
                Assert.Equal(world, canvas.Windows[(IntPtr)42]);
                Assert.Empty(api.NativeStateCalls);
                Assert.Empty(api.SetPositionCalls);
            }
            finally { thumbnails.Hide(); }
            api.Windows[(IntPtr)42].ProcessId = 8;
            manager.Tick();
            Assert.False(manager.HasCachedPreview((IntPtr)42));
            Assert.False(manager.UsesCachedPreview((IntPtr)42));
            Assert.Equal(0, cache.Count);
        });

    [Fact]
    public void EntryCountIsBoundedEvenForTinyImages()
    {
        using var cache = new WindowPreviewCache(new FakeCapture(), _ => new Surface());
        var oldest = Key(1); CaptureImage(cache, oldest, 1);
        for (int i = 2; i <= WindowPreviewCache.MaxEntries + 1; i++) CaptureImage(cache, Key(i), 1);
        Assert.Equal(WindowPreviewCache.MaxEntries, cache.Count);
        Assert.False(cache.HasImage(oldest));
    }

    [Fact]
    [Trait("Category", "NativePreview")]
    public void InvalidCaptureReturnsNoImage()
    {
        using var image = new WindowPreviewCapture().Capture(new IntPtr(-1), new(400, 300), new(100, 75));
        Assert.Null(image);
    }

    [Fact]
    [Trait("Category", "NativePreview")]
    public void EntirelyBlackNativeFrameIsUnavailable()
        => OnSta(() =>
        {
            using var original = new PaintedWindow(black: true);
            original.Show(); original.Update();
            using var image = new WindowPreviewCapture().Capture(original.Handle, original.Size, new(100, 50));
            Assert.Null(image);
        });

    [Fact]
    public void OverviewSwitchesLiveAndCachedSourcesInPlaceWithoutMovingOriginalWindow()
        => OnSta(() =>
        {
            using var pass = new OverviewOverlay(Screen.PrimaryScreen!);
            var canvas = new Canvas(); var api = new FakeWindowApi(); var screens = new FakeScreens();
            var b = pass.Screen.Bounds;
            api.AddWindow((IntPtr)42, b.X + 10, b.Y + 10, 400, 300);
            canvas.SetWindow((IntPtr)42, b.X + 10, b.Y + 10, 400, 300);
            var windows = new OverviewWindowList(canvas, api); windows.Refresh();
            bool cached = true;
            var dwm = new Dwm();
            var thumbnails = new OverviewThumbnails([pass], windows, new(screens), new(), api, screens,
                _ => cached, _ => (IntPtr)987, dwm);
            try
            {
                thumbnails.Reconcile();
                Assert.Contains((IntPtr)987, dwm.Sources);
                Assert.DoesNotContain((IntPtr)42, dwm.Sources);
                int registrations = dwm.Sources.Count;
                thumbnails.Reconcile();
                Assert.Equal(registrations, dwm.Sources.Count);
                thumbnails.InvalidateCachedSources();
                thumbnails.Reconcile();
                Assert.True(dwm.Sources.Count > registrations); // Same HWND can represent a new surface lifetime.
                thumbnails.BringToFront((IntPtr)42);
                Assert.DoesNotContain((IntPtr)42, dwm.Sources);
                cached = false;
                thumbnails.Reconcile();
                Assert.Contains((IntPtr)42, dwm.Sources);
                Assert.NotEmpty(dwm.Released);
                Assert.Empty(api.SetPositionCalls);
                Assert.Empty(api.NativeStateCalls);
            }
            finally { thumbnails.Hide(); }
        });

    [Fact]
    [Trait("Category", "NativePreview")]
    public void NativeCaptureScalesWholeOwnedWindowAndCachedSurfaceSupportsDwm()
        => OnSta(() =>
        {
            using var original = new PaintedWindow();
            var handle = original.Handle;
            original.Show(); original.Update(); // nonactivating, test-owned, entirely off desktop
            using var image = new WindowPreviewCapture().Capture(handle, original.Size, new(100, 50));
            Assert.NotNull(image);
            Assert.Equal(Color.Red.ToArgb(), image.GetPixel(5, 5).ToArgb());
            Assert.Equal(Color.Blue.ToArgb(), image.GetPixel(95, 45).ToArgb());
            using var surface = new CachedPreviewWindow(image);
            using var destination = new Form();
            var dwm = new DwmThumbnailApi(new PerformanceDiagnostics());
            Assert.True(dwm.Register((HWND)destination.Handle, (HWND)surface.Handle, out var thumbnail).Succeeded);
            try
            {
                Assert.True(dwm.Update(thumbnail, new DWM_THUMBNAIL_PROPERTIES
                {
                    dwFlags = 1 | 8, fVisible = true,
                    rcDestination = new RECT { left = 0, top = 0, right = 100, bottom = 50 }
                }).Succeeded);
                Assert.True(original.Visible);
                Assert.False(surface.ShowInTaskbar);
            }
            finally { Assert.True(dwm.Unregister(thumbnail).Succeeded); }
        });

    [Fact]
    public void OverviewPreparesOnlyVisibleLiveSourcesAndReleasesThemOnHide()
        => OnSta(() =>
        {
            using var pass = new OverviewOverlay(Screen.PrimaryScreen!);
            var canvas = new Canvas(); var api = new FakeWindowApi(); var screens = new FakeScreens();
            var b = pass.Screen.Bounds;
            for (int i = 1; i <= 3; i++)
            {
                int x = b.X + (i == 3 ? b.Width + 100 : i * 100);
                api.AddWindow((IntPtr)i, x, b.Y + 100, 100, 100);
                canvas.SetWindow((IntPtr)i, x, b.Y + 100, 100, 100);
            }
            var windows = new OverviewWindowList(canvas, api); windows.Refresh();
            bool cached = true;
            var wanted = new System.Collections.Generic.HashSet<IntPtr>();
            var camera = new OverviewCamera(screens);
            var dwm = new Dwm();
            var thumbnails = new OverviewThumbnails([pass], windows, camera, new(), api, screens,
                handle => cached && handle == (IntPtr)2, _ => (IntPtr)987, dwm,
                sources => { wanted.Clear(); wanted.UnionWith(sources); });
            try
            {
                thumbnails.Reconcile(); Assert.Equal(new[] { (IntPtr)1 }, wanted);
                int updates = dwm.Updates.Count;
                thumbnails.Reconcile(); Assert.Equal(updates, dwm.Updates.Count);
                cached = false; thumbnails.Reconcile(); Assert.Equal(2, wanted.Count);
                camera.SetTo(b.Width + 50, 0, 1);
                thumbnails.Reconcile(); Assert.DoesNotContain((IntPtr)1, wanted);
                Assert.Contains((IntPtr)3, wanted);
            }
            finally { thumbnails.Hide(); }
            Assert.Empty(wanted);
        });

    [Fact]
    public void ProxyFadeTemporarilyRetainsNativeUnderlayAndReleasesItWhenOpaque()
        => OnSta(() =>
        {
            using var pass = new OverviewOverlay(Screen.PrimaryScreen!);
            var canvas = new Canvas(); var api = new FakeWindowApi(); var screens = new FakeScreens();
            var b = pass.Screen.Bounds;
            foreach (var handle in new[] { (IntPtr)42, (IntPtr)99 })
            {
                api.AddWindow(handle, b.X + 100, b.Y + 100, 400, 300);
                canvas.SetWindow(handle, b.X + 100, b.Y + 100, 400, 300);
            }
            var windows = new OverviewWindowList(canvas, api);
            windows.Refresh(representedWindows: new Dictionary<IntPtr, IntPtr> { [(IntPtr)42] = (IntPtr)99 });
            bool fading = true;
            var dwm = new Dwm();
            var thumbnails = new OverviewThumbnails([pass], windows, new(screens), new(), api, screens,
                dwm: dwm, useNativeSource: _ => false,
                transitionSource: _ => fading ? (IntPtr)42 : IntPtr.Zero);
            try
            {
                thumbnails.Reconcile();
                Assert.Single(dwm.Sources.Where(source => source == (IntPtr)42));
                Assert.True(thumbnails.HasLiveSource((IntPtr)42));
                Assert.DoesNotContain((IntPtr)99, dwm.Sources); // No DWM card surface.
                int registrations = dwm.Sources.Count;
                thumbnails.Reconcile();
                Assert.Equal(registrations, dwm.Sources.Count); // No per-frame re-registration.
                fading = false;
                thumbnails.Reconcile();
                Assert.False(thumbnails.HasLiveSource((IntPtr)42));
                Assert.Single(dwm.Released);
                Assert.Empty(api.SetPositionCalls);
                Assert.Empty(api.NativeStateCalls);
            }
            finally { thumbnails.Hide(); }
        });

    private sealed class PaintedWindow : Form
    {
        private readonly bool _black;
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x08000080; return cp; }
        }
        public PaintedWindow(bool black = false)
        {
            _black = black;
            FormBorderStyle = FormBorderStyle.None; AutoScaleMode = AutoScaleMode.None; Size = new(400, 200);
            ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            var desktop = SystemInformation.VirtualScreen;
            Location = new(desktop.Left - 500, desktop.Top - 300);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            if (_black) { e.Graphics.Clear(Color.Black); return; }
            e.Graphics.FillRectangle(Brushes.Red, 0, 0, 200, 200);
            e.Graphics.FillRectangle(Brushes.Blue, 200, 0, 200, 200);
        }
    }
    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(10000));
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}

