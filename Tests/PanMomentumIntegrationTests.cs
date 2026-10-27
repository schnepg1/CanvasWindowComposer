using CanvasDesktop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using System.Windows.Forms;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
[Trait("Category", "NativeRendering")]
public class PanMomentumIntegrationTests
{
    private static void Run(Action<Canvas, FakeWindowApi, FakeInputRouter, FakeClock, FakeAppConfig, WindowManager, OverviewManager, OverviewOverlay> check)
        => WorkspaceViewTests.OnSta(() =>
        {
            var canvas = new Canvas(); var api = new FakeWindowApi(); var input = new FakeInputRouter();
            var clock = new FakeClock(1000); var config = new FakeAppConfig { EnableStartupIntro = false };
            using var wm = new WindowManager(canvas, api, config, input, clock);
            using var overview = new OverviewManager(canvas, wm, api, input, config, gestureClock: clock, dwm: new FakeDwmThumbnailApi());
            overview.TransitionTo(OverviewMode.Zooming);
            var pass = (OverviewOverlay)Control.FromHandle(overview.MonitorHandles[0])!;
            check(canvas, api, input, clock, config, wm, overview, pass);
        });

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.5)]
    public void CanvasReleaseCoastsAtCurrentZoomAndStaysOpen(double zoom)
        => Run((canvas, api, input, clock, config, wm, overview, pass) =>
        {
            pass.OnWheel!(pass, 200, 200, (int)(1200 * (zoom - 1)), false, true, false);
            pass.OnMouseButtonDown!(pass, new(MouseButtons.Middle, 1, 500, 500, 0));
            clock.Advance(20); pass.OnMouseMoved!(pass, new(MouseButtons.Middle, 0, 540, 500, 0));
            clock.Advance(20); pass.OnMouseMoved!(pass, new(MouseButtons.Middle, 0, 580, 500, 0));
            pass.OnMouseButtonUp!(pass, new(MouseButtons.Middle, 1, 580, 500, 0));
            var released = overview.ViewCamera;
            Assert.True(overview.MomentumActive);
            clock.Advance(16); overview.AdvanceMomentum();
            Assert.True(overview.ViewCamera.X < released.X);
            clock.Advance(5000); overview.AdvanceMomentum();
            Assert.False(overview.MomentumActive);
            Assert.Equal(OverviewMode.Zooming, overview.CurrentMode);
            Assert.Equal(zoom, overview.ViewCamera.Zoom);
            Assert.Equal(0, canvas.CamX); // Main desktop camera is untouched.
        });

    [Fact]
    public void DenseDesktopPanDefersNativeMovesAndCommitsFinalCoastBeforeHiding()
        => Run((canvas, api, input, clock, config, wm, overview, pass) =>
        {
            for (int i = 1; i <= 40; i++)
            {
                api.AddWindow((IntPtr)i, 50 + i * 10, 100, 400, 300);
                canvas.SetWindow((IntPtr)i, 50 + i * 10, 100, 400, 300);
            }
            input.RaiseDragStarted();
            api.LastBatch.Clear();
            clock.Advance(20); input.QueuePanAndRaise(40, 0);
            clock.Advance(20); input.QueuePanAndRaise(40, 0);
            input.PendingDragEnded = true; input.RaiseInputAvailable();
            Assert.True(wm.SuspendProjection); Assert.True(wm.SuspendAutomaticParking);
            Assert.True(overview.MomentumActive); Assert.Empty(api.LastBatch);
            clock.Advance(16); overview.AdvanceMomentum();
            Assert.Empty(api.LastBatch);
            clock.Advance(5000); overview.AdvanceMomentum();
            Assert.Equal(OverviewMode.Hidden, overview.CurrentMode);
            Assert.False(wm.SuspendProjection); Assert.False(wm.SuspendAutomaticParking);
            Assert.Equal(40, api.LastBatch.Count);
            Assert.True(canvas.CamX < -80);
            foreach (var move in api.LastBatch) Assert.Equal(canvas.WorldToScreen(canvas.Windows[move.HWnd]), move.Rect);
        });

    [Theory]
    [InlineData(MouseButtons.Left)]
    [InlineData(MouseButtons.Right)]
    public void DesktopStoppingClickIsOwnedThroughReleaseAndCancelsFurtherMotion(MouseButtons button)
        => Run((canvas, api, input, clock, config, wm, overview, pass) =>
        {
            input.RaiseDragStarted();
            clock.Advance(20); input.QueuePanAndRaise(30, 0);
            clock.Advance(20); input.QueuePanAndRaise(30, 0);
            input.PendingDragEnded = true; input.RaiseInputAvailable();
            Assert.True(overview.MomentumActive);
            var ex = PInvoke.GetWindowLong((HWND)pass.Handle, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
            Assert.Equal(0, ex & (int)WINDOW_EX_STYLE.WS_EX_TRANSPARENT);
            pass.OnMouseButtonDown!(pass, new(button, 1, 300, 300, 0));
            input.RaiseButtonDown(); // Raw report of the same click must not hide early.
            Assert.Equal(OverviewMode.Panning, overview.CurrentMode);
            Assert.True(pass.Capture); Assert.False(overview.MomentumActive);
            var stopped = canvas.Camera; clock.Advance(1000); overview.AdvanceMomentum();
            Assert.Equal(stopped, canvas.Camera);
            pass.OnMouseButtonUp!(pass, new(button, 1, 300, 300, 0));
            Assert.Equal(OverviewMode.Hidden, overview.CurrentMode); Assert.False(pass.Capture);
        });

    [Fact]
    public void MomentumSwitchesAreIndependentAndApplyDuringCoast()
        => Run((canvas, api, input, clock, config, wm, overview, pass) =>
        {
            config.DesktopPanMomentum = false;
            input.RaiseDragStarted(); clock.Advance(20); input.QueuePanAndRaise(50, 0);
            input.PendingDragEnded = true; input.RaiseInputAvailable();
            Assert.Equal(OverviewMode.Hidden, overview.CurrentMode);
            overview.TransitionTo(OverviewMode.Zooming);
            pass.OnMouseButtonDown!(pass, new(MouseButtons.Middle, 1, 500, 500, 0));
            clock.Advance(20); pass.OnMouseMoved!(pass, new(MouseButtons.Middle, 0, 550, 500, 0));
            pass.OnMouseButtonUp!(pass, new(MouseButtons.Middle, 1, 550, 500, 0));
            Assert.True(overview.MomentumActive);
            config.CanvasPanMomentum = false; overview.RefreshConfig();
            var stopped = overview.ViewCamera; clock.Advance(100); overview.AdvanceMomentum();
            Assert.False(overview.MomentumActive); Assert.Equal(stopped, overview.ViewCamera);
            Assert.Equal(OverviewMode.Zooming, overview.CurrentMode);
            config.DesktopPanMomentum = true;
            input.RaiseDragStarted(); clock.Advance(20); input.QueuePanAndRaise(50, 0);
            input.PendingDragEnded = true; input.RaiseInputAvailable();
            Assert.True(overview.MomentumActive);
            config.DesktopPanMomentum = false; overview.RefreshConfig();
            Assert.Equal(OverviewMode.Hidden, overview.CurrentMode);
        });

    [Fact]
    public void WheelNewDragAndModeChangeCancelOldCanvasMomentum()
        => Run((canvas, api, input, clock, config, wm, overview, pass) =>
        {
            void Coast()
            {
                pass.OnMouseButtonDown!(pass, new(MouseButtons.Middle, 1, 500, 500, 0));
                clock.Advance(20); pass.OnMouseMoved!(pass, new(MouseButtons.Middle, 0, 550, 500, 0));
                pass.OnMouseButtonUp!(pass, new(MouseButtons.Middle, 1, 550, 500, 0));
                Assert.True(overview.MomentumActive);
            }
            Coast(); pass.OnWheel!(pass, 200, 200, -30, false, true, false);
            Assert.False(overview.MomentumActive);
            Coast(); pass.OnMouseButtonDown!(pass, new(MouseButtons.Middle, 1, 500, 500, 0));
            Assert.False(overview.MomentumActive);
            pass.OnMouseButtonUp!(pass, new(MouseButtons.Middle, 1, 500, 500, 0));
            Coast(); overview.TransitionTo(OverviewMode.Hidden);
            var stopped = canvas.Camera; clock.Advance(5000); overview.AdvanceMomentum();
            Assert.False(overview.MomentumActive); Assert.Equal(stopped, canvas.Camera);
        });
    private sealed class RealPlaceholderFactory(FakeWindowApi api) : IWorkspacePlaceholderFactory
    {
        public readonly List<PlaceholderForm> Forms = new();
        public IWorkspacePlaceholder Create(IWorkspaceCommands workspace, WorkspaceItemView item)
        {
            var form = new PlaceholderForm(workspace, item);
            api.AddWindow(form.Handle, 100, 100, 400, 300, title: item.Title);
            Forms.Add(form);
            return form;
        }
    }

    [Fact]
    public void PlaceholderOnlyWorkspaceAlsoDefersNativeProjectionDuringDesktopCoast()
        => WorkspaceViewTests.OnSta(() =>
        {
            var document = new WorkspaceDocument();
            for (int i = 0; i < 16; i++) document.Items.Add(new WorkspaceItem
            {
                Title = $"Saved place {i}", ExecutablePath = @"C:\Windows\explorer.exe", Target = @"C:\Projects",
                IsFolder = true, Bounds = new WorldRect { X = 100 + i * 20, Y = 100, W = 400, H = 300 }
            });
            var api = new FakeWindowApi(); var factory = new RealPlaceholderFactory(api);
            var config = new FakeAppConfig { EnableStartupIntro = false };
            using var rig = new WorkspaceHarness(document, api: api, config: config, placeholders: factory);
            rig.Workspace.Start(); Assert.Equal(16, factory.Forms.Count);
            using var overview = new OverviewManager(rig.Canvas, rig.Wm, api, rig.Input, config,
                gestureClock: rig.Clock, dwm: new FakeDwmThumbnailApi());
            overview.AttachPlaceholderWorkspace(rig.Workspace);
            rig.Input.RaiseDragStarted(); api.LastBatch.Clear();
            rig.Clock.Advance(20); rig.Input.QueuePanAndRaise(40, 0);
            rig.Clock.Advance(20); rig.Input.QueuePanAndRaise(40, 0);
            rig.Input.PendingDragEnded = true; rig.Input.RaiseInputAvailable();
            Assert.True(overview.MomentumActive); Assert.Empty(api.LastBatch);
            rig.Clock.Advance(16); overview.AdvanceMomentum(); Assert.Empty(api.LastBatch);
            rig.Clock.Advance(5000); overview.AdvanceMomentum();
            Assert.Equal(OverviewMode.Hidden, overview.CurrentMode);
            Assert.Equal(16, api.LastBatch.Count);
            Assert.Equal(16, rig.Workspace.GetPlaceholderTitles().Count);
        });

}
