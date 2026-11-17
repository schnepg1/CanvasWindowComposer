using CanvasDesktop;
using CanvasDesktop.CanvasView;
using System.Windows.Forms;
using MenuItem = System.Windows.Controls.MenuItem;
using Xunit;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class UnifiedCanvasControlTests
{
    [Fact]
    [Trait("Category", "NativeRendering")]
    public void DockHasUnifiedSelectAndShapesMenu()
        => WorkspaceViewTests.OnSta(() =>
        {
            var dock = new UnifiedDockControl();
            var toolLabels = dock.CreationToolsPanel.Children.OfType<System.Windows.Controls.Button>()
                .Select(button => button.Content as string)
                .ToArray();

            // Unified Select, a single Shapes button, Note and the AI agent placement tool exist.
            Assert.Contains("Select", toolLabels);
            Assert.Contains("Note", toolLabels);
            Assert.Contains("AI agent", toolLabels);
            // The Shapes button label mirrors the last used shape, so any of these is valid.
            Assert.Contains(toolLabels, label => label is "Shapes" or "Rectangle" or "Ellipse" or "Arrow");
            Assert.DoesNotContain("Windows", toolLabels);

            var shapesMenu = dock.OpenShapesMenu();
            Assert.NotNull(shapesMenu);
            var shapeItems = shapesMenu!.Items.OfType<MenuItem>().Select(i => (string)i.Header).ToArray();
            Assert.Contains("Arrow", shapeItems);
            Assert.Contains("Rectangle", shapeItems);
            Assert.Contains("Ellipse", shapeItems);
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void DockMoreMenuOffersOneProjectsEntry()
        => WorkspaceViewTests.OnSta(() =>
        {
            var dock = new UnifiedDockControl();
            bool requested = false;
            dock.ProjectsRequested += () => requested = true;

            var moreMenu = dock.OpenMoreMenu();
            Assert.NotNull(moreMenu);
            var headers = moreMenu!.Items.OfType<MenuItem>().Select(i => (string)i.Header).ToArray();

            Assert.Contains("Projects…", headers);
            Assert.DoesNotContain("Project Navigator…", headers);
            Assert.DoesNotContain("Project areas…", headers);

            var projectsItem = moreMenu.Items.OfType<MenuItem>().Single(i => (string)i.Header == "Projects…");
            Assert.Equal("P", projectsItem.InputGestureText);

            projectsItem.RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent));
            Assert.True(requested);
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void ControlsButtonAndPopupWorkflow()
        => WorkspaceViewTests.OnSta(() =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "controls-btn-" + Guid.NewGuid().ToString("N"));
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

                // Controls button should be visible in top right
                Assert.NotNull(overview.UnifiedDock);
                Assert.True(overview.UnifiedDock.IsDockVisible);

                // Click Controls button to open popup
                var btn = overview.UnifiedDock.Panel.ControlsButton;
                void ClickControls() => btn.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                ClickControls();

                Assert.NotNull(overview.ControlsPopup);
                Assert.True(overview.ControlsPopup.Visible);

                // Escape closes popup
                overview.HandleEscape();
                Assert.False(overview.ControlsPopup.Visible);
                Assert.Equal(OverviewMode.Zooming, overview.CurrentMode);

                // Leaving canvas view must hide both independent topmost windows.
                foreach (var target in new[] { OverviewMode.Hidden, OverviewMode.Panning })
                {
                    ClickControls();
                    Assert.True(overview.ControlsPopup.Visible);

                    overview.TransitionTo(target);
                    Assert.False(overview.UnifiedDock.IsDockVisible);
                    Assert.False(overview.ControlsPopup.Visible);

                    // A stale click must not reopen help outside canvas view.
                    ClickControls();
                    Assert.False(overview.ControlsPopup.Visible);

                    overview.TransitionTo(OverviewMode.Zooming);
                    Assert.True(overview.UnifiedDock.IsDockVisible);
                    Assert.False(overview.ControlsPopup.Visible);
                }
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        });

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
