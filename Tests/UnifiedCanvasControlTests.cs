using CanvasDesktop;
using CanvasDesktop.CanvasView;
using System.Windows.Forms;
using Xunit;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class UnifiedCanvasControlTests
{
    [Fact]
    [Trait("Category", "NativeRendering")]
    public void ToolbarHasUnifiedSelectAndNoSeparateWindowsButton()
    {
        var toolbar = new AnnotationToolbar();
        var buttons = toolbar.Controls.OfType<ToolStrip>().Single().Items.OfType<ToolStripButton>().Select(b => b.Text).ToArray();
        Assert.DoesNotContain("Windows", buttons);
        Assert.Contains("Select", buttons);
        Assert.Contains("Arrow", buttons);
        Assert.Contains("Rectangle", buttons);
        Assert.Contains("Ellipse", buttons);
        Assert.Contains("Text", buttons);
    }

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void OverviewDefaultsToUnifiedSelectModeAndMarqueeOnEmptySpace()
        => WorkspaceViewTests.OnSta(() =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "unified-control-" + Guid.NewGuid().ToString("N"));
            try
            {
                var canvas = new Canvas();
                var api = new FakeWindowApi();
                var input = new FakeInputRouter();
                var config = new FakeAppConfig { EnableStartupIntro = false };
                using var wm = new WindowManager(canvas, api, config, input, new FakeClock());
                using var overview = new OverviewManager(canvas, wm, api, input, config);
                var desktops = new FakeVirtualDesktops();
                overview.AttachAnnotations(desktops, Path.Combine(folder, "annotations.json"));
                overview.TransitionTo(OverviewMode.Zooming);

                // Default mode should be Select (the unified control)
                Assert.Equal(AnnotationMode.Select, overview.AnnotationTool!.Mode);

                var pass = (OverviewOverlay)Control.FromHandle(overview.MonitorHandles[0])!;

                // Left click on empty space starts marquee selection (Tool interaction)
                pass.OnMouseButtonDown!(pass, new(MouseButtons.Left, 1, 500, 500, 0));
                Assert.True(overview.SelectionTool!.HasPendingInteraction);
                pass.OnMouseButtonUp!(pass, new(MouseButtons.Left, 1, 500, 500, 0));
                Assert.False(overview.SelectionTool!.HasPendingInteraction);

                // Right click drag starts pan navigation, not selection marquee
                pass.OnMouseButtonDown!(pass, new(MouseButtons.Right, 1, 500, 500, 0));
                Assert.False(overview.SelectionTool!.HasPendingInteraction);
                pass.OnMouseButtonUp!(pass, new(MouseButtons.Right, 1, 500, 500, 0));

                // Middle click drag starts pan navigation
                pass.OnMouseButtonDown!(pass, new(MouseButtons.Middle, 1, 500, 500, 0));
                Assert.False(overview.SelectionTool!.HasPendingInteraction);
                pass.OnMouseButtonUp!(pass, new(MouseButtons.Middle, 1, 500, 500, 0));
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void EscapeWorkflowReturnsToUnifiedSelectThenClearsThenCloses()
        => WorkspaceViewTests.OnSta(() =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "unified-escape-" + Guid.NewGuid().ToString("N"));
            try
            {
                var canvas = new Canvas();
                var api = new FakeWindowApi();
                var input = new FakeInputRouter();
                var config = new FakeAppConfig { EnableStartupIntro = false };
                using var wm = new WindowManager(canvas, api, config, input, new FakeClock());
                using var overview = new OverviewManager(canvas, wm, api, input, config);
                var desktops = new FakeVirtualDesktops();
                overview.AttachAnnotations(desktops, Path.Combine(folder, "annotations.json"));
                overview.TransitionTo(OverviewMode.Zooming);

                // Switch to a drawing mode
                overview.SelectAnnotationMode(AnnotationMode.Arrow);
                Assert.Equal(AnnotationMode.Arrow, overview.AnnotationTool!.Mode);

                // Escape from drawing tool returns to Select mode
                overview.HandleEscape();
                Assert.Equal(AnnotationMode.Select, overview.AnnotationTool!.Mode);
                Assert.Equal(OverviewMode.Zooming, overview.CurrentMode);

                // Add a note and select it
                var note = new CanvasAnnotation(Guid.NewGuid(), desktops.CurrentDesktopId, AnnotationKind.Text, new(100, 100), new(300, 200), Text: "Sample");
                overview.Annotations!.Put(note);
                overview.Selection.Replace([new(CanvasItemKind.Annotation, note.Id)]);
                Assert.Single(overview.Selection.Ids);

                // Escape with selection clears selection
                overview.HandleEscape();
                Assert.Empty(overview.Selection.Ids);
                Assert.Equal(OverviewMode.Zooming, overview.CurrentMode);

                // Escape with nothing selected closes overview
                overview.HandleEscape();
                Assert.Equal(OverviewMode.Hidden, overview.CurrentMode);
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        });
}
