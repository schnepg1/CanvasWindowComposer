using CanvasDesktop;
using System.Windows.Forms;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class WindowNavigationIntegrationTests
{
    private static readonly IntPtr App = (IntPtr)42;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "NativeRendering")]
    public void ShellSelectionKeepsCanvasOpenCenteredAtItsCurrentZoom(bool animated)
        => WithOverview(animated, (canvas, overview, api, input, screens) =>
        {
            var before = overview.ViewCamera;
            var desktop = canvas.Camera;
            var world = canvas.Windows[App];
            input.RaiseWindowSelected(App);
            Assert.Equal(animated, overview.WindowNavigationActive);
            if (animated)
            {
                Assert.Equal(before, overview.ViewCamera);
                overview.AdvanceWindowNavigation(WindowNavigationFlight.Duration / 2);
                Assert.NotEqual(before.X, overview.ViewCamera.X);
                overview.AdvanceWindowNavigation(WindowNavigationFlight.Duration);
            }
            Assert.False(overview.WindowNavigationActive);
            Assert.Equal(OverviewMode.Zooming, overview.CurrentMode);
            Assert.Equal(before.Zoom, overview.ViewCamera.Zoom);
            Assert.Equal(WindowNavigationFlight.Target(world, before.Zoom, screens.PrimaryWorkingArea), overview.ViewCamera);
            Assert.Equal(desktop, canvas.Camera);
            Assert.Equal(world, canvas.Windows[App]);
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void TaskbarMinimizeSelectionRestoresAppAndNavigatesWithoutChangingSavedPosition()
        => WithOverview(false, (canvas, overview, api, input, screens) =>
        {
            var world = canvas.Windows[App];
            api.Windows[App].Style |= (int)WINDOW_STYLE.WS_MINIMIZE;
            input.RaiseWindowMinimized(App);
            Assert.Equal(WindowState.Minimized, canvas.Windows[App].State);
            input.RaiseWindowSelected(App);
            Assert.Equal(App, Assert.Single(api.NavigationRestores));
            Assert.Equal(0, api.Windows[App].Style & (int)WINDOW_STYLE.WS_MINIMIZE);
            var after = canvas.Windows[App];
            Assert.Equal((world.X, world.Y, world.W, world.H), (after.X, after.Y, after.W, after.H));
            Assert.Equal(WindowState.Normal, after.State);
            Assert.Equal(OverviewMode.Zooming, overview.CurrentMode);
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void AutomaticRestoreOrIncidentalFocusDoesNotMoveOverviewCamera()
        => WithOverview(true, (canvas, overview, api, input, screens) =>
        {
            var camera = overview.ViewCamera;
            input.RaiseWindowRestored(App);
            input.RaiseWindowFocused(App);
            Assert.False(overview.WindowNavigationActive);
            Assert.Equal(camera, overview.ViewCamera);
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void SelectedMaximizedAppIsRepresentedAtItsSavedCanvasBounds()
        => WithOverview(false, (canvas, overview, api, input, screens) =>
        {
            api.Windows[App].Style |= (int)WINDOW_STYLE.WS_MAXIMIZE;
            canvas.MaximizeWindow(App);
            var saved = canvas.Windows[App];
            input.RaiseWindowSelected(App);
            var windows = (OverviewWindowList)typeof(OverviewManager).GetField("_windows",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(overview)!;
            var entry = Assert.Single(windows.Windows, entry => entry.HWnd == App);
            Assert.False(entry.ScreenFixed);
            Assert.Equal(saved, entry.World);
            Assert.Equal(WindowState.Maximized, canvas.Windows[App].State);
            Assert.NotEqual(0, api.Windows[App].Style & (int)WINDOW_STYLE.WS_MAXIMIZE);
            Assert.Empty(api.NavigationRestores);
            Assert.Equal(OverviewMode.Zooming, overview.CurrentMode);
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void NewSelectionReplacesFlightAndManualScrollCancelsIt()
        => WithOverview(true, (canvas, overview, api, input, screens) =>
        {
            IntPtr second = (IntPtr)43;
            api.AddWindow(second, 20, 30, 800, 600, title: "Other app");
            canvas.SetWindow(second, -5000, -2000, 800, 600);
            input.RaiseWindowSelected(App);
            overview.AdvanceWindowNavigation(WindowNavigationFlight.Duration / 2);
            input.RaiseWindowSelected(second);
            overview.AdvanceWindowNavigation(WindowNavigationFlight.Duration);
            Assert.Equal(WindowNavigationFlight.Target(canvas.Windows[second], .25, screens.PrimaryWorkingArea), overview.ViewCamera);
            input.RaiseWindowSelected(App);
            var pass = (OverviewOverlay)Control.FromHandle(overview.MonitorHandles[0])!;
            pass.OnWheel!(pass, 200, 200, 120, false, false, false);
            var afterScroll = overview.ViewCamera;
            overview.AdvanceWindowNavigation(WindowNavigationFlight.Duration);
            Assert.False(overview.WindowNavigationActive);
            Assert.Equal(afterScroll, overview.ViewCamera);
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void ClosingCanvasOrLosingTheTargetStopsTheFlight()
        => WithOverview(true, (canvas, overview, api, input, screens) =>
        {
            input.RaiseWindowSelected(App);
            input.RaiseWindowDestroyed(App);
            var camera = overview.ViewCamera;
            overview.AdvanceWindowNavigation(.1);
            Assert.False(overview.WindowNavigationActive);
            Assert.Equal(camera, overview.ViewCamera);
            canvas.SetWindow(App, 5000, 3000, 800, 600);
            input.RaiseWindowSelected(App);
            overview.TransitionTo(OverviewMode.Hidden);
            Assert.False(overview.WindowNavigationActive);
            var desktop = canvas.Camera;
            overview.AdvanceWindowNavigation(WindowNavigationFlight.Duration);
            Assert.Equal(desktop, canvas.Camera);
        });

    private static void WithOverview(bool animated,
        Action<Canvas, OverviewManager, FakeWindowApi, FakeInputRouter, FakeScreens> action)
        => WorkspaceViewTests.OnSta(() =>
        {
            Assert.True(GridRenderer.CompileShaders());
            var canvas = new Canvas();
            canvas.LoadState(new() { CamX = -1000, CamY = -500, Zoom = .25, Windows = new() });
            var api = new FakeWindowApi(); var input = new FakeInputRouter(); var screens = new FakeScreens();
            var config = new FakeAppConfig { AnimateWindowNavigation = animated, EnableStartupIntro = false };
            using var wm = new WindowManager(canvas, api, config, input, new FakeClock());
            api.AddWindow(App, 100, 200, 800, 600, title: "App");
            canvas.SetWindow(App, 7000, 4000, 800, 600);
            using var overview = new OverviewManager(canvas, wm, api, input, config, screens,
                dwm: new FakeDwmThumbnailApi(), prefersReducedStartupMotion: () => false);
            using var foreground = new ForegroundCoordinator(canvas, overview, input, new FakeClock(10000), screens);
            overview.TransitionTo(OverviewMode.Zooming);
            action(canvas, overview, api, input, screens);
        });
}
