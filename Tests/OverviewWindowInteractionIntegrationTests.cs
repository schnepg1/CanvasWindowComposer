using CanvasDesktop;
using System.Drawing;
using System.Windows.Forms;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class OverviewWindowInteractionIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "NativeRendering")]
    public void EscapeStopsInteractionBeforeClosingOverview(bool global)
        => WorkspaceViewTests.OnSta(() => WithOverview((overview, pass, source, input, api) =>
        {
            Assert.True(overview.BeginWindowInteraction(pass, source.Handle));
            Assert.True(pass.WindowInteractionActive);
            pass.OnMouseButtonDown!(pass, new(MouseButtons.Left, 1, 150 - pass.OriginX, 150 - pass.OriginY, 0));
            Assert.Contains(api.Messages, m => m.Id == 0x201);
            if (global) input.RaiseEscPressed();
            else pass.OnKey!(pass, new(Keys.Escape));
            Assert.False(overview.WindowInteractionActive);
            Assert.False(pass.WindowInteractionActive);
            Assert.False(pass.Capture);
            Assert.Equal(OverviewMode.Zooming, overview.CurrentMode);
            Assert.Contains(api.Messages, m => m.Id == 0x202);
            overview.HandleEscape();
            Assert.Equal(OverviewMode.Hidden, overview.CurrentMode);
        }));

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void DoubleClickAndWheelGoToAppWithoutNavigatingOrMovingSource()
        => WorkspaceViewTests.OnSta(() => WithOverview((overview, pass, source, input, api) =>
        {
            Assert.True(overview.BeginWindowInteraction(pass, source.Handle));
            int x = 150 - pass.OriginX, y = 150 - pass.OriginY;
            pass.OnMouseButtonDown!(pass, new(MouseButtons.Left, 2, x, y, 0));
            pass.OnMouseDoubleClicked!(pass, new(MouseButtons.Left, 2, x, y, 0));
            pass.OnMouseButtonUp!(pass, new(MouseButtons.Left, 2, x, y, 0));
            pass.OnWheel!(pass, x, y, -120, false, false, false);
            Assert.Contains(api.Messages, m => m.Id == 0x203);
            Assert.Contains(api.Messages, m => m.Id == 0x20A);
            Assert.Equal(OverviewMode.Zooming, overview.CurrentMode);
            Assert.True(overview.WindowInteractionActive);
        }));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [Trait("Category", "NativeRendering")]
    public void TargetLossOrExternalFocusEndsInteraction(int kind)
        => WorkspaceViewTests.OnSta(() => WithOverview((overview, pass, source, input, api) =>
        {
            Assert.True(overview.BeginWindowInteraction(pass, source.Handle));
            if (kind == 0) input.RaiseWindowDestroyed(source.Handle);
            else if (kind == 1) input.RaiseWindowMinimized(source.Handle);
            else input.RaiseWindowFocused((IntPtr)900);
            Assert.False(overview.WindowInteractionActive);
            Assert.False(pass.WindowInteractionActive);
        }));

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void OverlayDoesNotActivateWhenClickedDuringInteraction()
        => WorkspaceViewTests.OnSta(() => WithOverview((overview, pass, source, input, api) =>
        {
            Assert.True(overview.BeginWindowInteraction(pass, source.Handle));
            var result = PInvoke.SendMessage((HWND)pass.Handle, 0x21, 0, 0);
            Assert.Equal((nint)3, (nint)result.Value);
            overview.TransitionTo(OverviewMode.Hidden);
            Assert.False(pass.WindowInteractionActive);
        }));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "NativeRendering")]
    public void ControlReleaseRestoresPersistentOrCanvasMode(bool persistent)
        => WorkspaceViewTests.OnSta(() => WithOverview((overview, pass, source, input, api) =>
        {
            if (persistent) Assert.True(overview.BeginWindowInteraction(pass, source.Handle));
            overview.UpdateMomentaryInteraction(Keys.Control, new(150, 150), pass.Handle, 1000);
            overview.UpdateMomentaryInteraction(Keys.Control, new(150, 150), pass.Handle, 1300);
            Assert.True(overview.WindowInteractionActive);
            overview.UpdateMomentaryInteraction(Keys.None, new(150, 150), source.Handle, 1400);
            Assert.Equal(persistent, overview.WindowInteractionActive);
            Assert.Equal(persistent, pass.WindowInteractionActive);
            Assert.Equal(OverviewMode.Zooming, overview.CurrentMode);
        }));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "NativeRendering")]
    public void TemporaryInteractionDoesNotRestartAfterFocusLossOrModeExit(bool leaveCanvas)
        => WorkspaceViewTests.OnSta(() => WithOverview((overview, pass, source, input, api) =>
        {
            overview.UpdateMomentaryInteraction(Keys.Control, new(150, 150), pass.Handle, 1000);
            overview.UpdateMomentaryInteraction(Keys.Control, new(150, 150), pass.Handle, 1300);
            Assert.True(overview.WindowInteractionActive);
            if (leaveCanvas) overview.TransitionTo(OverviewMode.Hidden);
            else input.RaiseWindowFocused((IntPtr)900);
            overview.UpdateMomentaryInteraction(Keys.Control, new(150, 150), pass.Handle, 1500);
            Assert.False(overview.WindowInteractionActive);
            Assert.False(pass.WindowInteractionActive);
        }));

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void QuickControlAndKeyboardChordsDoNotStartInteraction()
        => WorkspaceViewTests.OnSta(() => WithOverview((overview, pass, source, input, api) =>
        {
            overview.UpdateMomentaryInteraction(Keys.Control, new(150, 150), pass.Handle, 1000);
            overview.UpdateMomentaryInteraction(Keys.None, new(150, 150), pass.Handle, 1100);
            Assert.False(overview.WindowInteractionActive);
            overview.UpdateMomentaryInteraction(Keys.Control, new(150, 150), pass.Handle, 1200, otherKey: true);
            overview.UpdateMomentaryInteraction(Keys.Control, new(150, 150), pass.Handle, 1600);
            Assert.False(overview.WindowInteractionActive);
        }));

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void SharedMenuOffersInteractionOnlyForEligibleLiveGeometry()
        => WorkspaceViewTests.OnSta(() => WithOverview((overview, pass, source, input, api) =>
        {
            using var live = new ContextMenuStrip();
            overview.AddWindowInteractionCommand(live, pass, source.Handle);
            Assert.Contains(live.Items.Cast<ToolStripItem>(), i => i.Text == "Interact with window (mouse and keyboard)");
            using var missing = new ContextMenuStrip();
            overview.AddWindowInteractionCommand(missing, pass, (IntPtr)900);
            Assert.Empty(missing.Items.Cast<ToolStripItem>());
            overview.TransitionTo(OverviewMode.Hidden);
            using var hidden = new ContextMenuStrip();
            overview.AddWindowInteractionCommand(hidden, pass, source.Handle);
            Assert.Empty(hidden.Items.Cast<ToolStripItem>());
        }));

    [Theory]
    [InlineData(Keys.Oemplus)]
    [InlineData(Keys.OemMinus)]
    [InlineData(Keys.Add)]
    [InlineData(Keys.Subtract)]
    [InlineData(Keys.Left)]
    [InlineData(Keys.Delete)]
    [InlineData(Keys.Z)]
    public void CanvasChordKeysSuppressMomentaryInteraction(Keys key)
    {
        Assert.True(OverviewManager.IsMomentaryChordKey((int)key));
        Assert.False(OverviewManager.IsMomentaryChordKey((int)Keys.ControlKey));
        Assert.False(OverviewManager.IsMomentaryChordKey((int)Keys.LControlKey));
        Assert.False(OverviewManager.IsMomentaryChordKey((int)Keys.RControlKey));
    }

    private static void WithOverview(Action<OverviewManager, OverviewOverlay, Form, FakeInputRouter,
        OverviewWindowInteractionTests.PointerApi> action)
    {
        Assert.True(GridRenderer.CompileShaders());
        using var source = new Form();
        var canvas = new Canvas(); var api = new FakeWindowApi(); var input = new FakeInputRouter();
        var config = new FakeAppConfig { EnableStartupIntro = false };
        api.AddWindow(source.Handle, 100, 100, 400, 300);
        canvas.SetWindow(source.Handle, 100, 100, 400, 300);
        var pointerApi = new OverviewWindowInteractionTests.PointerApi();
        using var wm = new WindowManager(canvas, api, config, input, new FakeClock());
        using var overview = new OverviewManager(canvas, wm, api, input, config, interactionApi: pointerApi);
        overview.TransitionTo(OverviewMode.Zooming);
        var pass = (OverviewOverlay)Control.FromHandle(overview.MonitorHandles[0])!;
        action(overview, pass, source, input, pointerApi);
    }
}
