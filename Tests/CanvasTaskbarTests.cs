using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Forms;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class CanvasTaskbarTests
{
    [Theory]
    [InlineData(0, 0, 1920, 1080, 0, 0, 1920, 1032, 0, 0, 1920, 1032)]
    [InlineData(-1920, -200, 1920, 1080, -1872, -200, 1872, 1080, 48, 0, 1872, 1080)]
    [InlineData(200, -1080, 1920, 1080, 200, -1032, 1920, 1032, 0, 48, 1920, 1032)]
    [InlineData(0, 0, 1920, 1080, 0, 0, 1872, 1080, 0, 0, 1872, 1080)]
    [InlineData(1920, 0, 1920, 1080, 1920, 0, 1920, 1080, 0, 0, 1920, 1080)]
    public void TaskbarSpaceUsesMonitorLocalPhysicalCoordinates(int x, int y, int w, int h,
        int ax, int ay, int aw, int ah, int ex, int ey, int ew, int eh)
        => Assert.Equal(new Rectangle(ex, ey, ew, eh),
            OverviewTaskbarRegion.LocalWorkingArea(new(x, y, w, h), new(ax, ay, aw, ah)));

    [Fact]
    public void TaskbarDefaultsOnForExistingConfigsAndRoundTripsBothWays()
    {
        string directory = Path.Combine(Path.GetTempPath(), "canvas-taskbar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "config.ini"), "DisableSearch=false\n");
            using var config = new AppConfig(directory: directory); config.Load();
            Assert.True(config.ShowTaskbarInCanvas);
            var original = config.Preferences;
            foreach (bool visible in new[] { false, true })
            {
                config.SavePreferences(original with { ShowTaskbarInCanvas = visible });
                using var reopened = new AppConfig(directory: directory); reopened.Load();
                Assert.Equal(original with { ShowTaskbarInCanvas = visible }, reopened.Preferences);
                Assert.Equal(visible, reopened.ShowTaskbarInCanvas);
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void SavingTaskbarVisibilityUpdatesBothCanvasSurfacesAndSurvivesModeChanges()
        => WorkspaceViewTests.OnSta(() =>
        {
            var canvas = new Canvas(); var api = new FakeWindowApi(); var input = new FakeInputRouter();
            var config = new FakeAppConfig { EnableStartupIntro = false };
            using var wm = new WindowManager(canvas, api, config, input, new FakeClock());
            using var overview = new OverviewManager(canvas, wm, api, input, config, dwm: new FakeDwmThumbnailApi());
            overview.TransitionTo(OverviewMode.Zooming);
            void Check(bool visible)
            {
                foreach (var handle in overview.MonitorHandles)
                {
                    var pass = (OverviewOverlay)Control.FromHandle(handle)!;
                    var expected = OverviewTaskbarRegion.LocalWorkingArea(pass.Bounds, pass.Screen.WorkingArea);
                    AssertRegion(handle, visible ? expected : null);
                    var annotations = (OverviewAnnotationWindow)typeof(OverviewOverlay)
                        .GetField("_annotations", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(pass)!;
                    Assert.NotNull(annotations);
                    AssertRegion(new WindowInteropHelper(annotations).Handle, visible ? expected : null);
                }
            }
            Check(true);
            config.ShowTaskbarInCanvas = false; overview.RefreshConfig(); Check(false);
            overview.TransitionTo(OverviewMode.Panning); Check(false);
            config.ShowTaskbarInCanvas = true; overview.RefreshConfig();
            overview.TransitionTo(OverviewMode.Zooming); Check(true);
            overview.TransitionTo(OverviewMode.Hidden);
            overview.TransitionTo(OverviewMode.Zooming); Check(true);
        });

    private static void AssertRegion(IntPtr window, Rectangle? expected)
    {
        var region = CreateRectRgn(0, 0, 0, 0);
        try
        {
            int kind = GetWindowRgn(window, region);
            Assert.Equal(expected.HasValue, kind != 0);
            Assert.Equal(expected.HasValue, GetProp(window, "NonRudeHWND") != IntPtr.Zero);
            if (expected is { } rect)
            {
                GetRgnBox(region, out var actual);
                Assert.Equal(rect, Rectangle.FromLTRB(actual.Left, actual.Top, actual.Right, actual.Bottom));
            }
        }
        finally { DeleteObject(region); }
    }

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void GroupedPreviewIsExposedThroughBothCanvasSurfacesAndRestoredAfterDismissal()
        => WorkspaceViewTests.OnSta(() =>
        {
            var canvas = new Canvas(); var api = new FakeWindowApi(); var input = new FakeInputRouter();
            var config = new FakeAppConfig { EnableStartupIntro = false };
            using var wm = new WindowManager(canvas, api, config, input, new FakeClock());
            using var overview = new OverviewManager(canvas, wm, api, input, config, dwm: new FakeDwmThumbnailApi());
            overview.TransitionTo(OverviewMode.Zooming);
            foreach (var handle in overview.MonitorHandles)
            {
                var pass = (OverviewOverlay)Control.FromHandle(handle)!;
                var area = pass.Screen.WorkingArea;
                var popup = new Rectangle(area.Left + 40, area.Top + 40, 280, 160);
                var annotations = (OverviewAnnotationWindow)typeof(OverviewOverlay)
                    .GetField("_annotations", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(pass)!;
                var surfaces = new[] { handle, new WindowInteropHelper(annotations).Handle };

                pass.SetTaskbarPreviews([popup]);
                // A render must retain the cutout in the annotation HWND too.
                overview.RefreshConfig();
                foreach (var surface in surfaces)
                    AssertRegionContains(surface, pass.Bounds, popup.Location, false);

                var moved = new Rectangle(popup.X + 320, popup.Y, popup.Width, popup.Height);
                pass.SetTaskbarPreviews([moved]);
                foreach (var surface in surfaces)
                {
                    AssertRegionContains(surface, pass.Bounds, popup.Location, true);
                    AssertRegionContains(surface, pass.Bounds, moved.Location, false);
                }

                config.ShowTaskbarInCanvas = false; overview.RefreshConfig();
                foreach (var surface in surfaces) AssertRegion(surface, null);
                config.ShowTaskbarInCanvas = true; overview.RefreshConfig();
                pass.SetTaskbarPreviews([]);
                foreach (var surface in surfaces)
                    AssertRegion(surface, OverviewTaskbarRegion.LocalWorkingArea(pass.Bounds, area));
            }
        });

    [Theory]
    [InlineData(-1920, -200)]
    [InlineData(200, -1080)]
    [Trait("Category", "NativeRendering")]
    public void PreviewCutoutsUseWindowLocalCoordinatesAndIgnoreOtherMonitors(int x, int y)
        => WorkspaceViewTests.OnSta(() =>
        {
            using var form = new Form();
            var bounds = new Rectangle(x, y, 1920, 1080);
            var working = new Rectangle(x, y, 1920, 1032);
            var popup = new Rectangle(x + 100, y + 800, 400, 280);
            var region = new OverviewTaskbarRegion();
            region.Apply(form.Handle, bounds, working, true, [popup, new Rectangle(5000, 5000, 200, 200)]);
            AssertRegionContains(form.Handle, bounds, new Point(x + 101, y + 801), false);
            AssertRegionContains(form.Handle, bounds, new Point(x + 99, y + 801), true);
            region.Apply(form.Handle, bounds, working, true, []);
            AssertRegionContains(form.Handle, bounds, new Point(x + 101, y + 801), true);
        });

    private static void AssertRegionContains(IntPtr window, Rectangle bounds, Point point, bool expected)
    {
        var region = CreateRectRgn(0, 0, 0, 0);
        try
        {
            Assert.NotEqual(0, GetWindowRgn(window, region));
            Assert.Equal(expected, PtInRegion(region, point.X - bounds.X, point.Y - bounds.Y));
        }
        finally { DeleteObject(region); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr window, IntPtr region);
    [DllImport("gdi32.dll")] private static extern int GetRgnBox(IntPtr region, out Rect rect);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool PtInRegion(IntPtr region, int x, int y);
    [DllImport("user32.dll", EntryPoint = "GetPropW", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetProp(IntPtr window, string name);
}
