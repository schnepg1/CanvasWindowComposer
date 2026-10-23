using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using System.Windows.Media;
using CanvasDesktop;
using CanvasDesktop.CanvasView;
using Color = System.Windows.Media.Color;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class UiBugsAndShapesTests
{
    private static readonly Guid Desktop = Guid.NewGuid();

    [Fact]
    public void TextCardsSupportLargeFontSizesUpToMaximum()
    {
        Assert.True(AnnotationDocument.MaximumFontSize >= 400);

        var note = new CanvasAnnotation(Guid.NewGuid(), Desktop, AnnotationKind.Text,
            new(10, 10), new(300, 200), Text: "Large Title", FontSize: 18);
        var doc = new AnnotationDocument([note]);
        var tool = new AnnotationCanvasTool(doc, () => Desktop);
        tool.SetMode(AnnotationMode.Select);
        tool.Select(note.Id);

        // Test large text sizes
        tool.SetFontSize(120);
        Assert.Equal(120, Assert.Single(doc.Items).FontSize);

        tool.SetFontSize(200);
        Assert.Equal(200, Assert.Single(doc.Items).FontSize);

        tool.SetFontSize(400);
        Assert.Equal(400, Assert.Single(doc.Items).FontSize);

        // Beyond maximum should fail
        Assert.Throws<ArgumentException>(() => tool.SetFontSize(401));
    }

    [Fact]
    public void AnnotationTextEditorAllowsLargeFontSizes()
        => WorkspaceViewTests.OnSta(() =>
        {
            var note = new CanvasAnnotation(Guid.NewGuid(), Desktop, AnnotationKind.Text,
                new(10, 10), new(500, 300), Text: "Big Header", FontSize: 200);
            using var editor = new AnnotationTextEditor(note);
            Assert.Equal(200, editor.FontSize);

            editor.SetFontSize(400);
            Assert.Equal(400, editor.FontSize);
        });

    [Fact]
    public void SolidSettingIsCompletelyOpaqueNotTransparent()
    {
        var brush = Assert.IsType<SolidColorBrush>(AnnotationVisual.CardBackground);
        Assert.Equal((byte)255, brush.Color.A);
    }

    [Fact]
    public void SendToBackMarksShapeInBackgroundAndBringToFrontClearsIt()
    {
        var doc = new AnnotationDocument();
        var a = new CanvasAnnotation(Guid.NewGuid(), Desktop, AnnotationKind.Rectangle, new(0, 0), new(200, 200), "#FBBF24");
        var b = new CanvasAnnotation(Guid.NewGuid(), Desktop, AnnotationKind.Ellipse, new(50, 50), new(150, 150), "#60A5FA");
        doc.Put(a);
        doc.Put(b);

        // Initially neither is in background
        Assert.False(doc.Items.First(x => x.Id == b.Id).InBackground);

        // Send b to back (background)
        Assert.True(doc.SendToBack(Desktop, b.Id));
        var sentB = doc.Items.First(x => x.Id == b.Id);
        Assert.True(sentB.InBackground);

        // Bring b to front (foreground)
        Assert.True(doc.BringToFront(Desktop, b.Id));
        var frontB = doc.Items.First(x => x.Id == b.Id);
        Assert.False(frontB.InBackground);
    }

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void ShapeEditorSendToBackgroundSetsInBackgroundProperty()
        => WorkspaceViewTests.OnSta(() =>
        {
            var doc = new AnnotationDocument();
            var shape = new CanvasAnnotation(Guid.NewGuid(), Desktop, AnnotationKind.Rectangle, new(100, 100), new(300, 300));
            doc.Put(shape);

            using var editor = new AnnotationShapeEditor(shape, doc, Desktop);
            Assert.False(editor.CurrentItem.InBackground);

            // Click Send to Background
            editor.SendToBackButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.True(editor.CurrentItem.InBackground);
            Assert.Equal(ZOrderAction.SendToBack, editor.SelectedZOrder);

            // Click Bring to Foreground
            editor.BringToFrontButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.False(editor.CurrentItem.InBackground);
            Assert.Equal(ZOrderAction.BringToFront, editor.SelectedZOrder);
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void ToolbarDropdownClosesOnCloseDropDownsAndHiding()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var toolbar = new AnnotationToolbar();
            Assert.False(toolbar.HasOpenDropDown);

            // CloseDropDowns runs safely even when none is open
            toolbar.CloseDropDowns();
            Assert.False(toolbar.HasOpenDropDown);
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void OverviewManagerClosesActiveMenusOnEscapeAndHide()
        => WorkspaceViewTests.OnSta(() =>
        {
            var canvas = new Canvas();
            var api = new FakeWindowApi();
            var input = new FakeInputRouter();
            var config = new FakeAppConfig { EnableStartupIntro = false };
            using var wm = new WindowManager(canvas, api, config, input, new FakeClock());
            using var overview = new OverviewManager(canvas, wm, api, input, config);

            overview.TransitionTo(OverviewMode.Zooming);

            // CloseActiveMenus returns false when no menu is open
            Assert.False(overview.CloseActiveMenus());

            // HandleEscape with no menu transitions to hidden
            overview.HandleEscape();
            Assert.Equal(OverviewMode.Hidden, overview.CurrentMode);
        });
}
