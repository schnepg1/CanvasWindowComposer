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
        => WorkspaceViewTests.OnSta(() => WithOverview((overview, pass, source, input, api, second) =>
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
        => WorkspaceViewTests.OnSta(() => WithOverview((overview, pass, source, input, api, second) =>
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
        => WorkspaceViewTests.OnSta(() => WithOverview((overview, pass, source, input, api, second) =>
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
        => WorkspaceViewTests.OnSta(() => WithOverview((overview, pass, source, input, api, second) =>
        {
            Assert.True(overview.BeginWindowInteraction(pass, source.Handle));
            var result = PInvoke.SendMessage((HWND)pass.Handle, 0x21, 0, 0);
            Assert.Equal((nint)3, (nint)result.Value);
            overview.TransitionTo(OverviewMode.Hidden);
            Assert.False(pass.WindowInteractionActive);
        }));

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void InteractToggleForwardsFirstClickAndArrangeReturnsToCanvas()
        => WorkspaceViewTests.OnSta(() => WithOverview((overview, pass, source, input, api, second) =>
        {
            overview.SetInteractMode(true);
            int x = 150 - pass.OriginX, y = 180 - pass.OriginY;
            pass.OnMouseButtonDown!(pass, new(MouseButtons.Left, 1, x, y, 0));
            pass.OnMouseButtonUp!(pass, new(MouseButtons.Left, 1, x, y, 0));
            Assert.True(overview.WindowInteractionActive);
            Assert.Contains(api.Messages, m => m.Id == 0x201);
            Assert.Contains(api.Messages, m => m.Id == 0x202);
            overview.SetInteractMode(false);
            Assert.False(overview.WindowInteractionActive);
            int count = api.Messages.Count;
            pass.OnMouseButtonDown!(pass, new(MouseButtons.Left, 1, x, y, 0));
            pass.OnMouseButtonUp!(pass, new(MouseButtons.Left, 1, x, y, 0));
            Assert.Equal(count, api.Messages.Count);
        }));

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void HeaderDragsCanvasWindowWithoutSendingAppClicks()
        => WorkspaceViewTests.OnSta(() => WithOverview((overview, pass, source, input, api, second) =>
        {
            overview.SetInteractMode(true);
            int x = 150 - pass.OriginX, y = 110 - pass.OriginY;
            pass.OnMouseButtonDown!(pass, new(MouseButtons.Left, 1, x, y, 0));
            pass.OnMouseMoved!(pass, new(MouseButtons.None, 0, x + 40, y + 30, 0));
            pass.OnMouseButtonUp!(pass, new(MouseButtons.Left, 1, x + 40, y + 30, 0));
            Assert.False(overview.WindowInteractionActive);
            Assert.DoesNotContain(api.Messages, m => m.Id == 0x201);
            Assert.Equal(OverviewMode.Zooming, overview.CurrentMode);
        }));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "NativeRendering")]
    public void FocusLossAndCanvasExitReleaseSessionButPreserveModeChoice(bool leaveCanvas)
        => WorkspaceViewTests.OnSta(() => WithOverview((overview, pass, source, input, api, second) =>
        {
            overview.SetInteractMode(true);
            Assert.True(overview.BeginWindowInteraction(pass, source.Handle));
            if (leaveCanvas) overview.TransitionTo(OverviewMode.Hidden);
            else input.RaiseWindowFocused((IntPtr)900);
            Assert.False(overview.WindowInteractionActive);
            Assert.False(pass.WindowInteractionActive);
            Assert.True(overview.InteractMode);
        }));

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void EscapeSwitchesGlobalInteractionBackToArrange()
        => WorkspaceViewTests.OnSta(() => WithOverview((overview, pass, source, input, api, second) =>
        {
            overview.SetInteractMode(true);
            overview.HandleEscape();
            Assert.False(overview.InteractMode);
            Assert.Equal(OverviewMode.Zooming, overview.CurrentMode);
        }));

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void SharedMenuOffersInteractionOnlyForEligibleLiveGeometry()
        => WorkspaceViewTests.OnSta(() => WithOverview((overview, pass, source, input, api, second) =>
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
    [InlineData(96, 24)]
    [InlineData(144, 36)]
    [InlineData(192, 48)]
    public void DragHeaderUsesMonitorDpi(int dpi, int height)
    {
        var header = OverviewManager.InteractionHeader(new(100, 100, 300, 200), dpi);
        Assert.Equal(height, header.Height);
        Assert.True(header.Contains(150, 110));
        Assert.False(header.Contains(150, 180));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "NativeRendering")]
    public void QueuedTargetSwitchOrControlSelectionKeepsButtonUp(bool control)
        => WorkspaceViewTests.OnSta(() => WithOverview((overview, pass, source, input, api, second) =>
        {
            overview.SetInteractMode(true);
            Assert.True(overview.BeginWindowInteraction(pass, source.Handle));
            var keys = control ? Keys.Control : Keys.None;
            int clicksBefore = api.Messages.Count(m => m.Id == 0x201);
            overview.QueueInteractionMouse(new(new(650, 180), MouseButtons.Left, true, false, Modifiers: keys));
            overview.QueueInteractionMouse(new(new(650, 180), MouseButtons.Left, false, true, Modifiers: keys));
            Application.DoEvents();
            Assert.Equal(!control, overview.WindowInteractionActive);
            Assert.True(overview.InteractMode);
            Assert.Equal(clicksBefore + (control ? 0 : 1), api.Messages.Count(m => m.Id == 0x201));
            if (!control) Assert.Contains(api.Messages, m => m.Id == 0x202);
        }));

    [Fact]
    public void ControlsExposeBothModesAndRenderPreview()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var form = new CanvasControlsButton();
            form.Show();
            var buttons = form.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Button>().ToArray();
            bool? requested = null;
            form.InteractionModeRequested += enabled => requested = enabled;
            buttons.Single(b => b.Text == "Interact").PerformClick();
            Assert.True(requested);
            form.SetInteractMode(true);
            Assert.Contains("active", buttons.Single(b => b.Text == "Interact").AccessibleDescription);
            buttons.Single(b => b.Text == "Arrange").PerformClick();
            Assert.False(requested);
            using var image = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(image, new Rectangle(0, 0, image.Width, image.Height));
            image.Save(System.IO.Path.Combine(AppContext.BaseDirectory, "canvas-interact-toggle.png"));
        });

    private static void WithOverview(Action<OverviewManager, OverviewOverlay, Form, FakeInputRouter,
        OverviewWindowInteractionTests.PointerApi, Form> action)
    {
        Assert.True(GridRenderer.CompileShaders());
        using var source = new Form();
        using var second = new Form();
        var canvas = new Canvas(); var api = new FakeWindowApi(); var input = new FakeInputRouter();
        var config = new FakeAppConfig { EnableStartupIntro = false };
        api.AddWindow(source.Handle, 100, 100, 400, 300);
        canvas.SetWindow(source.Handle, 100, 100, 400, 300);
        api.AddWindow(second.Handle, 600, 100, 400, 300);
        canvas.SetWindow(second.Handle, 600, 100, 400, 300);
        var pointerApi = new OverviewWindowInteractionTests.PointerApi();
        using var wm = new WindowManager(canvas, api, config, input, new FakeClock());
        using var overview = new OverviewManager(canvas, wm, api, input, config, interactionApi: pointerApi);
        overview.TransitionTo(OverviewMode.Zooming);
        var pass = (OverviewOverlay)Control.FromHandle(overview.MonitorHandles[0])!;
        action(overview, pass, source, input, pointerApi, second);
    }
}
