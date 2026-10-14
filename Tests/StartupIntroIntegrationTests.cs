using CanvasDesktop;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class StartupIntroIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "NativeRendering")]
    public void CompletionOrSkipPreservesSavedCameraEvenWhenOverviewClosesLater(bool skip)
        => WorkspaceViewTests.OnSta(() =>
        {
            Assert.True(GridRenderer.CompileShaders());
            var canvas = new Canvas();
            canvas.LoadState(new CanvasState { CamX = 1234, CamY = -345, Zoom = .7, Windows = new() });
            var api = new FakeWindowApi(); var input = new FakeInputRouter();
            var config = new FakeAppConfig();
            using var wm = new WindowManager(canvas, api, config, input, new FakeClock());
            using var overview = new OverviewManager(canvas, wm, api, input, config,
                prefersReducedStartupMotion: () => false);
            Assert.True(overview.TryStartStartupIntro());
            Assert.Equal(OverviewMode.Zooming, overview.CurrentMode);
            Assert.True(wm.SuspendAutomaticParking);
            overview.AdvanceStartupIntro(1.8);
            Assert.Equal(1234, canvas.CamX); Assert.Equal(-345, canvas.CamY); Assert.Equal(.7, canvas.Zoom);
            if (skip) Assert.True(overview.TrySkipStartupIntro());
            else overview.AdvanceStartupIntro(3.5);
            Assert.False(overview.IsStartupIntroActive);
            Assert.False(wm.SuspendAutomaticParking);
            Assert.False(overview.TryStartStartupIntro());
            overview.TransitionTo(OverviewMode.Hidden);
            Assert.Equal(1234, canvas.CamX); Assert.Equal(-345, canvas.CamY); Assert.Equal(.7, canvas.Zoom);
            Assert.Empty(api.MinimizeCalls); Assert.Empty(api.ParkingRestoreCalls);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "NativeRendering")]
    public void CancellationAndDisposalRestoreThePriorParkingGuardWithoutSavingFlight(bool dispose)
        => WorkspaceViewTests.OnSta(() =>
        {
            Assert.True(GridRenderer.CompileShaders());
            var canvas = new Canvas(); canvas.SetCamera(800, -200);
            var api = new FakeWindowApi(); var input = new FakeInputRouter(); var config = new FakeAppConfig();
            using var wm = new WindowManager(canvas, api, config, input, new FakeClock());
            wm.SuspendAutomaticParking = true;
            using var overview = new OverviewManager(canvas, wm, api, input, config,
                prefersReducedStartupMotion: () => false);
            Assert.True(overview.TryStartStartupIntro());
            overview.AdvanceStartupIntro(2);
            if (dispose) overview.Dispose();
            else overview.CancelStartupIntro();
            Assert.False(overview.IsStartupIntroActive);
            Assert.True(wm.SuspendAutomaticParking);
            Assert.Equal(OverviewMode.Hidden, overview.CurrentMode);
            Assert.Equal(800, canvas.CamX); Assert.Equal(-200, canvas.CamY);
            Assert.False(overview.StartupIntroInputSuppressed);
        });

    [Fact]
    public void DisabledIntroDoesNotAcquireParkingGuardOrOpenOverview()
        => WorkspaceViewTests.OnSta(() =>
        {
            var canvas = new Canvas(); var api = new FakeWindowApi(); var input = new FakeInputRouter();
            var config = new FakeAppConfig { EnableStartupIntro = false };
            using var wm = new WindowManager(canvas, api, config, input, new FakeClock());
            using var overview = new OverviewManager(canvas, wm, api, input, config);
            Assert.False(overview.TryStartStartupIntro());
            Assert.Equal(OverviewMode.Hidden, overview.CurrentMode);
            Assert.False(wm.SuspendAutomaticParking);
            Assert.Empty(overview.MonitorHandles);
        });
}
