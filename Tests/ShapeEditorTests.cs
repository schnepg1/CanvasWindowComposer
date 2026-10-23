using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using CanvasDesktop;
using CanvasDesktop.CanvasView;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class ShapeEditorTests
{
    private static readonly Guid Desktop = Guid.NewGuid();

    [Fact]
    public void ZOrderManipulationAndSingleStepHistory()
    {
        var doc = new AnnotationDocument();
        var a = new CanvasAnnotation(Guid.NewGuid(), Desktop, AnnotationKind.Rectangle, new(0, 0), new(100, 100), "#FBBF24");
        var b = new CanvasAnnotation(Guid.NewGuid(), Desktop, AnnotationKind.Ellipse, new(100, 0), new(200, 100), "#60A5FA");
        var c = new CanvasAnnotation(Guid.NewGuid(), Desktop, AnnotationKind.Rectangle, new(200, 0), new(300, 100), "#34D399");

        doc.Put(a);
        doc.Put(b);
        doc.Put(c);

        // Initial order: a (back), b (middle), c (front)
        Assert.Equal([a.Id, b.Id, c.Id], doc.Items.Select(x => x.Id).ToArray());
        Assert.Equal((0, 3), doc.GetZOrder(Desktop, a.Id));
        Assert.Equal((1, 3), doc.GetZOrder(Desktop, b.Id));
        Assert.Equal((2, 3), doc.GetZOrder(Desktop, c.Id));

        // Bring a to front
        Assert.True(doc.BringToFront(Desktop, a.Id));
        Assert.Equal([b.Id, c.Id, a.Id], doc.Items.Select(x => x.Id).ToArray());

        // Send a to back
        Assert.True(doc.SendToBack(Desktop, a.Id));
        Assert.Equal([a.Id, b.Id, c.Id], doc.Items.Select(x => x.Id).ToArray());

        // Step forward: a moves past b
        Assert.True(doc.BringForward(Desktop, a.Id));
        Assert.Equal([b.Id, a.Id, c.Id], doc.Items.Select(x => x.Id).ToArray());

        // Step backward: a moves back before b
        Assert.True(doc.SendBackward(Desktop, a.Id));
        Assert.Equal([a.Id, b.Id, c.Id], doc.Items.Select(x => x.Id).ToArray());

        // Undo restores previous step
        Assert.True(doc.Undo(Desktop));
        Assert.Equal([b.Id, a.Id, c.Id], doc.Items.Select(x => x.Id).ToArray());

        // Update with properties AND z-order in one atomic step
        var updatedA = a with { Color = "#FFFFFF", Stroke = 6 };
        Assert.True(doc.Update(updatedA, ZOrderAction.BringToFront));
        Assert.Equal([b.Id, c.Id, a.Id], doc.Items.Select(x => x.Id).ToArray());
        Assert.Equal("#FFFFFF", doc.Items.Last().Color);
        Assert.Equal(6, doc.Items.Last().Stroke);

        // One undo restores both position and properties
        Assert.True(doc.Undo(Desktop));
        Assert.Equal([b.Id, a.Id, c.Id], doc.Items.Select(x => x.Id).ToArray());
        Assert.Equal("#FBBF24", doc.Items.First(x => x.Id == a.Id).Color);
    }

    [Fact]
    public void MultiDesktopZOrderIsolation()
    {
        var doc = new AnnotationDocument();
        var d1 = Guid.NewGuid();
        var d2 = Guid.NewGuid();

        var itemD1_1 = new CanvasAnnotation(Guid.NewGuid(), d1, AnnotationKind.Rectangle, new(0, 0), new(100, 100));
        var itemD2_1 = new CanvasAnnotation(Guid.NewGuid(), d2, AnnotationKind.Rectangle, new(0, 0), new(100, 100));
        var itemD1_2 = new CanvasAnnotation(Guid.NewGuid(), d1, AnnotationKind.Ellipse, new(100, 0), new(200, 100));
        var itemD2_2 = new CanvasAnnotation(Guid.NewGuid(), d2, AnnotationKind.Ellipse, new(100, 0), new(200, 100));

        doc.Put(itemD1_1);
        doc.Put(itemD2_1);
        doc.Put(itemD1_2);
        doc.Put(itemD2_2);

        // Send D1_2 to back on d1
        Assert.True(doc.SendToBack(d1, itemD1_2.Id));

        // Check that on d1, itemD1_2 is now before itemD1_1
        var d1Items = doc.Items.Where(x => x.DesktopId == d1).Select(x => x.Id).ToArray();
        Assert.Equal([itemD1_2.Id, itemD1_1.Id], d1Items);

        // Check that d2 relative order is completely untouched
        var d2Items = doc.Items.Where(x => x.DesktopId == d2).Select(x => x.Id).ToArray();
        Assert.Equal([itemD2_1.Id, itemD2_2.Id], d2Items);
    }

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void ShapeEditorUIAllowsStylingDepthAndDimensions()
        => WorkspaceViewTests.OnSta(() =>
        {
            var doc = new AnnotationDocument();
            var shape = new CanvasAnnotation(Guid.NewGuid(), Desktop, AnnotationKind.Rectangle, new(50, 50), new(250, 150), "#FBBF24", 3, "Initial");
            doc.Put(shape);

            using var editor = new AnnotationShapeEditor(shape, doc, Desktop);
            Assert.Equal(AnnotationKind.Rectangle, editor.CurrentItem.Kind);
            Assert.Equal("#FBBF24", editor.CurrentItem.Color);
            Assert.Equal(3, editor.CurrentItem.Stroke);
            Assert.Equal("Initial", editor.CurrentItem.Text);
            // Verify preview artwork dimensions are initialized immediately upon creation
            Assert.Equal(shape.Bounds.Width, editor.ArtworkPreview.Width);
            Assert.Equal(shape.Bounds.Height, editor.ArtworkPreview.Height);

            // Toggle kind to Ellipse
            editor.TypeEllipseButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal(AnnotationKind.Ellipse, editor.CurrentItem.Kind);

            // Change color using swatch
            var skySwatch = editor.ColorSwatchButtons.First(b => (b.ToolTip as string)?.StartsWith("Sky") == true);
            skySwatch.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal("#60A5FA", editor.CurrentItem.Color);

            // Change color using hex box
            editor.HexInput.Text = "#10B981";
            Assert.Equal("#10B981", editor.CurrentItem.Color);

            // Change stroke
            var stroke6Btn = editor.StrokePresetButtons.First(b => (b.Content as string) == "6px");
            stroke6Btn.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal(6, editor.CurrentItem.Stroke);

            // Select z-order Bring to Foreground
            editor.BringToFrontButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal(ZOrderAction.BringToFront, editor.SelectedZOrder);

            // Make square (1:1)
            editor.MakeSquareButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal(editor.CurrentItem.Bounds.Width, editor.CurrentItem.Bounds.Height);

            // Edit label
            editor.LabelInput.Text = "Updated Label";
            Assert.Equal("Updated Label", editor.CurrentItem.Text);

            // Save
            ShapeEditorResult? result = null;
            editor.Finished += r => result = r;
            editor.SaveBtn.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

            Assert.NotNull(result);
            Assert.True(result.Save);
            Assert.False(result.Delete);
            Assert.Equal(AnnotationKind.Ellipse, result.UpdatedItem.Kind);
            Assert.Equal("#10B981", result.UpdatedItem.Color);
            Assert.Equal(6, result.UpdatedItem.Stroke);
            Assert.Equal("Updated Label", result.UpdatedItem.Text);
            Assert.Equal(ZOrderAction.BringToFront, result.ZOrder);
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void OverviewManagerOpensShapeEditorOnDoubleClickAndEnterAndSavesEdits()
        => WorkspaceViewTests.OnSta(() =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "canvas-shape-editor-ui-" + Guid.NewGuid().ToString("N"));
            try
            {
                var canvas = new Canvas();
                var api = new FakeWindowApi();
                var input = new FakeInputRouter();
                var config = new FakeAppConfig { EnableStartupIntro = false };
                using var wm = new WindowManager(canvas, api, config, input, new FakeClock());
                using var overview = new OverviewManager(canvas, wm, api, input, config);

                var desktopId = Guid.NewGuid();
                var desktops = new FakeVirtualDesktops { CurrentDesktopId = desktopId };
                overview.AttachAnnotations(desktops, Path.Combine(folder, "annotations.json"));
                overview.TransitionTo(OverviewMode.Zooming);

                // Add background note and a foreground shape
                var note = new CanvasAnnotation(Guid.NewGuid(), desktopId, AnnotationKind.Text, new(100, 100), new(300, 200), Text: "Background Note");
                var shape = new CanvasAnnotation(Guid.NewGuid(), desktopId, AnnotationKind.Rectangle, new(400, 300), new(600, 450), "#FBBF24", 3);
                overview.Annotations!.Put(note);
                overview.Annotations.Put(shape);

                overview.SelectAnnotationMode(AnnotationMode.Select);
                var pass = (OverviewOverlay)Control.FromHandle(overview.MonitorHandles[0])!;

                // Double click the shape on the rectangle boundary (500, 300)
                pass.OnMouseDoubleClicked!(pass, new(MouseButtons.Left, 2, 500, 300, 0));
                Application.DoEvents();

                Assert.NotNull(overview.ShapeEditor);
                Assert.True(overview.CanvasInputSuspended);

                // Change color to Sky (#60A5FA) and Z-Order to SendToBack
                overview.ShapeEditor!.HexInput.Text = "#60A5FA";
                overview.ShapeEditor.SendToBackButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                overview.ShapeEditor.LabelInput.Text = "Labeled Shape";

                // Save
                overview.ShapeEditor.SaveBtn.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Application.DoEvents();

                Assert.Null(overview.ShapeEditor);
                var savedShape = overview.Annotations.Items.First(x => x.Id == shape.Id);
                Assert.Equal("#60A5FA", savedShape.Color);
                Assert.Equal("Labeled Shape", savedShape.Text);

                // Shape was moved to the background (index 0)
                Assert.Equal(shape.Id, overview.Annotations.Items[0].Id);
                Assert.Equal(note.Id, overview.Annotations.Items[1].Id);

                // Now press Enter to reopen the selected shape
                overview.AnnotationTool!.Select(shape.Id);
                pass.OnKey!(pass, new(Keys.Enter));
                Application.DoEvents();

                Assert.NotNull(overview.ShapeEditor);

                // Press Escape to cancel
                input.RaiseEscPressed();
                Application.DoEvents();
                Assert.Null(overview.ShapeEditor);
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void OverviewManagerDeletesShapeFromShapeEditor()
        => WorkspaceViewTests.OnSta(() =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "canvas-shape-delete-ui-" + Guid.NewGuid().ToString("N"));
            try
            {
                var canvas = new Canvas();
                var api = new FakeWindowApi();
                var input = new FakeInputRouter();
                var config = new FakeAppConfig { EnableStartupIntro = false };
                using var wm = new WindowManager(canvas, api, config, input, new FakeClock());
                using var overview = new OverviewManager(canvas, wm, api, input, config);

                var desktopId = Guid.NewGuid();
                var desktops = new FakeVirtualDesktops { CurrentDesktopId = desktopId };
                overview.AttachAnnotations(desktops, Path.Combine(folder, "annotations.json"));
                overview.TransitionTo(OverviewMode.Zooming);

                var shape = new CanvasAnnotation(Guid.NewGuid(), desktopId, AnnotationKind.Ellipse, new(200, 200), new(400, 350), "#FB7185", 3);
                overview.Annotations!.Put(shape);

                overview.SelectAnnotationMode(AnnotationMode.Select);
                var pass = (OverviewOverlay)Control.FromHandle(overview.MonitorHandles[0])!;

                overview.AnnotationTool!.Select(shape.Id);
                pass.OnKey!(pass, new(Keys.Enter));
                Application.DoEvents();

                Assert.NotNull(overview.ShapeEditor);

                // Click Delete button
                overview.ShapeEditor!.DeleteBtn.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Application.DoEvents();

                Assert.Null(overview.ShapeEditor);
                Assert.Empty(overview.Annotations.Items);
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        });
}
