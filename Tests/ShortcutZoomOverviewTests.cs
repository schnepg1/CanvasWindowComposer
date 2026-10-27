using CanvasDesktop;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class ShortcutZoomOverviewTests
{
    [Theory]
    [InlineData(true, .15, false, .85)]
    [InlineData(true, .30, true, .70)]
    [InlineData(false, .30, false, 1)]
    [InlineData(true, 0, false, 1)]
    [Trait("Category", "NativeRendering")]
    public void ShortcutOpensAtConfiguredZoomAndReopeningDoesNotAccumulate(bool enabled, double amount, bool fromPanning, double expected)
        => WorkspaceViewTests.OnSta(() =>
        {
            var canvas = new Canvas(); canvas.SetCamera(700.25, -300.5);
            var api = new FakeWindowApi(); var input = new FakeInputRouter();
            var config = new FakeAppConfig { EnableStartupIntro = false, EnableShortcutZoomOut = enabled, ShortcutZoomOutAmount = amount };
            using var wm = new WindowManager(canvas, api, config, input, new FakeClock());
            using var overview = new OverviewManager(canvas, wm, api, input, config);
            var desktop = canvas.Camera;
            if (fromPanning) overview.TransitionTo(OverviewMode.Panning);
            input.RaiseOverviewHotkey();
            Assert.Equal(OverviewMode.Zooming, overview.CurrentMode);
            Assert.Equal(expected, overview.ViewCamera.Zoom, 10);
            var first = overview.ViewCamera;
            Assert.Equal(desktop, canvas.Camera);
            input.RaiseOverviewHotkey();
            Assert.Equal(OverviewMode.Hidden, overview.CurrentMode);
            Assert.Equal(desktop.X, canvas.Camera.X, 8);
            Assert.Equal(desktop.Y, canvas.Camera.Y, 8);
            input.RaiseOverviewHotkey();
            Assert.Equal(first.Zoom, overview.ViewCamera.Zoom, 10);
            Assert.Equal(first.X, overview.ViewCamera.X, 8);
            Assert.Equal(first.Y, overview.ViewCamera.Y, 8);
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void ChangingTheToggleAffectsTheNextShortcutWithoutRestarting()
        => WorkspaceViewTests.OnSta(() =>
        {
            var canvas = new Canvas(); var api = new FakeWindowApi(); var input = new FakeInputRouter();
            var config = new FakeAppConfig { EnableStartupIntro = false, EnableShortcutZoomOut = false };
            using var wm = new WindowManager(canvas, api, config, input, new FakeClock());
            using var overview = new OverviewManager(canvas, wm, api, input, config);
            input.RaiseOverviewHotkey(); Assert.Equal(1, overview.ViewCamera.Zoom);
            input.RaiseOverviewHotkey();
            config.EnableShortcutZoomOut = true; config.ShortcutZoomOutAmount = .42;
            input.RaiseOverviewHotkey(); Assert.Equal(.58, overview.ViewCamera.Zoom, 10);
        });
}
