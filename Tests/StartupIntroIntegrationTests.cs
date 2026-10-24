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
            if (skip)
            {
                var before = overview.ViewCamera;
                Assert.True(overview.TrySkipStartupIntro());
                Assert.True(overview.IsStartupIntroActive);
                Assert.True(wm.SuspendAutomaticParking);
                Assert.Equal(before, overview.ViewCamera);
                overview.AdvanceStartupIntro(2.2);
                Assert.True(overview.TrySkipStartupIntro()); // Key repeat must not restart the landing.
                overview.AdvanceStartupIntro(1.8 + StartupIntroLanding.DurationSeconds);
            }
            else overview.AdvanceStartupIntro(overview.StartupIntroDurationSeconds);
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
    [Trait("Category", "NativeRendering")]
    public void ProjectZonesWithoutLiveWindowsExtendTheTourAndKeepTheSavedCamera()
        => WorkspaceViewTests.OnSta(() =>
        {
            Assert.True(GridRenderer.CompileShaders());
            var canvas = new Canvas();
            var saved = canvas.Camera;
            var api = new FakeWindowApi(); var input = new FakeInputRouter(); var config = new FakeAppConfig();
            var document = new WorkspaceDocument { Zones = new()
            {
                new() { Bounds = new WorldRect { X = -100000, W = 2000, H = 1500 } },
                new() { Bounds = new WorldRect { X = 100000, W = 2000, H = 1500 } }
            } };
            using var wm = new WindowManager(canvas, api, config, input, new FakeClock());
            using var overview = new OverviewManager(canvas, wm, api, input, config,
                prefersReducedStartupMotion: () => false);
            overview.AttachZones(new ZoneService(document, () => Guid.Empty, () => true));
            Assert.True(overview.TryStartStartupIntro());
            double duration = overview.StartupIntroDurationSeconds;
            Assert.True(duration > StartupIntroFlight.MinimumDurationSeconds);
            overview.AdvanceStartupIntro(duration / 2);
            Assert.True(overview.IsStartupIntroActive);
            Assert.Equal(saved, canvas.Camera);
            overview.AdvanceStartupIntro(duration);
            Assert.False(overview.IsStartupIntroActive);
            Assert.Equal(saved, canvas.Camera);
            Assert.False(wm.SuspendAutomaticParking);
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
