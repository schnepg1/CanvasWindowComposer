using CanvasDesktop;
using System.Windows.Forms;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class CanvasPerformanceIntegrationTests
{
    [Fact]
    [Trait("Category", "NativeRendering")]
    public void QualityChangesApplyToExistingPassesAndSurviveReopeningWithoutRestart()
        => WorkspaceViewTests.OnSta(() =>
        {
            Assert.True(GridRenderer.CompileShaders());
            var canvas = new Canvas(); var api = new FakeWindowApi(); var input = new FakeInputRouter();
            var config = new FakeAppConfig { EnableStartupIntro = false };
            using var wm = new WindowManager(canvas, api, config, input, new FakeClock());
            using var overview = new OverviewManager(canvas, wm, api, input, config);
            overview.TransitionTo(OverviewMode.Zooming);
            var grids = overview.MonitorHandles.Select(h => ((OverviewOverlay)Control.FromHandle(h)!).Grid!).ToArray();
            Assert.All(grids, grid => { Assert.NotNull(grid); Assert.False(grid.LowQuality); });
            config.LowQualityMode = true; overview.RefreshConfig();
            Assert.All(grids, grid => Assert.True(grid.LowQuality));
            overview.TransitionTo(OverviewMode.Hidden);
            config.LowQualityMode = false; overview.RefreshConfig();
            overview.TransitionTo(OverviewMode.Zooming);
            Assert.Equal(grids, overview.MonitorHandles.Select(h => ((OverviewOverlay)Control.FromHandle(h)!).Grid!).ToArray());
            Assert.All(grids, grid => Assert.False(grid.LowQuality));
        });
}
