using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using CanvasDesktop;
using CanvasDesktop.CanvasView;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class AnnotationToolbarTests
{
    [Fact]
    [Trait("Category", "NativeRendering")]
    public void FirstClickActivatesAToolAndOpensItsMenuAfterCanvasRegainsFocus()
        => WorkspaceViewTests.OnSta(() =>
        {
            var originalCursor = Cursor.Position;
            try
            {
                using var canvas = new Form();
                using var toolbar = new AnnotationToolbar();
                canvas.Show();
                toolbar.Show(canvas);
                var strip = toolbar.Controls.OfType<ToolStrip>().Single();
                var modes = new List<AnnotationMode>();
                toolbar.ModeRequested += modes.Add;

                void ClickOnce(ToolStripItem item)
                {
                    canvas.Activate();
                    Application.DoEvents();
                    var point = new Point(item.Bounds.Left + item.Width / 2, item.Bounds.Top + item.Height / 2);
                    Cursor.Position = strip.PointToScreen(point);
                    // ToolStrip normally returns MA_ACTIVATEANDEAT when its form is
                    // inactive. Windows then discards the entire first click.
                    var result = PInvoke.SendMessage((HWND)strip.Handle, 0x21,
                        (WPARAM)(nuint)toolbar.Handle, (LPARAM)((0x201 << 16) | 1));
                    Assert.Equal((nint)1, (nint)result.Value); // MA_ACTIVATE
                    toolbar.Activate();
                    var position = (LPARAM)((point.Y << 16) | (point.X & 0xffff));
                    PInvoke.SendMessage((HWND)strip.Handle, 0x201, (WPARAM)1, position);
                    PInvoke.SendMessage((HWND)strip.Handle, 0x202, 0, position);
                    Application.DoEvents();
                }

                ClickOnce(strip.Items.OfType<ToolStripButton>().Single(i => i.Text == "Text"));
                Assert.Equal(new[] { AnnotationMode.Text }, modes);
                ClickOnce(strip.Items.OfType<ToolStripButton>().Single(i => i.Text == "Select"));
                Assert.Equal(new[] { AnnotationMode.Text, AnnotationMode.Select }, modes);

                var shapes = strip.Items.OfType<ToolStripSplitButton>().Single();
                canvas.Activate(); Application.DoEvents();
                var shapePoint = new Point(shapes.Bounds.Left + shapes.ButtonBounds.Width / 2, shapes.Bounds.Top + shapes.Height / 2);
                Cursor.Position = strip.PointToScreen(shapePoint);
                Assert.Equal((nint)1, (nint)PInvoke.SendMessage((HWND)strip.Handle, 0x21,
                    (WPARAM)(nuint)toolbar.Handle, (LPARAM)((0x201 << 16) | 1)).Value);
                toolbar.Activate();
                var shapePosition = (LPARAM)((shapePoint.Y << 16) | (shapePoint.X & 0xffff));
                PInvoke.SendMessage((HWND)strip.Handle, 0x201, (WPARAM)1, shapePosition);
                PInvoke.SendMessage((HWND)strip.Handle, 0x202, 0, shapePosition);
                Assert.Equal(AnnotationMode.Rectangle, modes.Last());

                foreach (var menu in strip.Items.OfType<ToolStripDropDownButton>())
                {
                    ClickOnce(menu);
                    Assert.True(menu.DropDown.Visible, menu.Text);
                    toolbar.CloseDropDowns();
                    Assert.False(menu.DropDown.Visible);
                }
            }
            finally { Cursor.Position = originalCursor; }
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void CompactDockRemembersShapesAndKeepsEveryCommandReachable()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var dock = new AnnotationToolbar();
            dock.ModeRequested += mode => dock.UpdateState(mode, null);
            var strip = dock.Strip;
            Assert.Equal(new[] { "Select", "Shapes", "Text", "Undo", "Redo", "More" },
                strip.Items.Cast<ToolStripItem>().Where(i => i is ToolStripButton or ToolStripDropDownItem).Select(i => i.Name));
            var shapes = strip.Items.OfType<ToolStripSplitButton>().Single();
            shapes.DropDownItems.OfType<ToolStripMenuItem>().Single(i => i.Text == "Arrow").PerformClick();
            strip.Items.OfType<ToolStripButton>().Single(i => i.Name == "Text").PerformClick();
            shapes.PerformButtonClick();
            Assert.Equal("Arrow tool, active", shapes.AccessibleDescription);

            var more = strip.Items.OfType<ToolStripDropDownButton>().Single();
            int saved = 0, composed = 0; double radius = 0;
            dock.RetrySaveRequested += () => saved++;
            dock.ComposerRequested += () => composed++;
            dock.NearbyRequested += value => radius = value;
            more.DropDownItems.OfType<ToolStripMenuItem>().Single(i => i.Name == "Save now").PerformClick();
            more.DropDownItems.OfType<ToolStripMenuItem>().Single(i => i.Name == "AI Compose").PerformClick();
            dock.SetSelectionRadius(2100);
            more.DropDownItems.OfType<ToolStripMenuItem>().Single(i => i.Name == "Nearby")
                .DropDownItems.OfType<ToolStripMenuItem>().Single().PerformClick();
            Assert.Equal(1, saved); Assert.Equal(1, composed); Assert.Equal(2100, radius);
            Assert.False(strip.Items["Undo"]!.Enabled);
            Assert.False(strip.Items["Redo"]!.Enabled);
            dock.UpdateState(new AnnotationToolbarState(AnnotationMode.Select, CanUndo: true, CanRedo: true));
            Assert.True(strip.Items["Undo"]!.Enabled); Assert.True(strip.Items["Redo"]!.Enabled);
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void ContextFollowsToolAndSelectedStyleAndHidesWithDock()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var dock = new AnnotationToolbar { Location = new(300, 700) };
            dock.SetAvailableWidth(800); dock.Show();
            Assert.False(dock.PropertiesSurface.Visible);
            dock.UpdateState(new AnnotationToolbarState(AnnotationMode.Rectangle, Stroke: 6));
            var properties = dock.PropertiesSurface;
            Assert.True(properties.Visible);
            Assert.True(properties.Strip.Items["Stroke"]!.Available);
            Assert.False(properties.Strip.Items["Size"]!.Available);
            Assert.Equal("6 px", properties.Strip.Items["Stroke"]!.Text);
            Assert.Equal(dock.Top - dock.Pixel(8), properties.Bottom);

            var note = new CanvasAnnotation(Guid.NewGuid(), Guid.NewGuid(), AnnotationKind.Text, new(0, 0), new(200, 100),
                "#C084FC", Text: "Selected note", FontSize: 56, Emphasis: AnnotationTextEmphasis.Bold);
            dock.UpdateState(new AnnotationToolbarState(AnnotationMode.Select, Selected: note, SelectedCount: 1));
            Assert.True(properties.Strip.Items["Size"]!.Available);
            Assert.False(properties.Strip.Items["Stroke"]!.Available);
            Assert.Equal("Size 56", properties.Strip.Items["Size"]!.Text);
            Assert.True(((ToolStripDropDownButton)properties.Strip.Items["Style"]!).DropDownItems
                .OfType<ToolStripMenuItem>().Single(i => i.Text == "Bold").Checked);
            var colors = (ToolStripDropDownButton)properties.Strip.Items["Color"]!;
            colors.ShowDropDown();
            Assert.True(dock.HasOpenDropDown);
            dock.Hide();
            Assert.False(properties.Visible); Assert.False(dock.HasOpenDropDown);
            dock.Show(); Assert.True(properties.Visible);
            dock.UpdateState(AnnotationMode.Select, null);
            Assert.False(properties.Visible);
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void NarrowDockKeepsToolsVisibleAndSaveFailuresDiscoverable()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var dock = new AnnotationToolbar();
            dock.UpdateState(new AnnotationToolbarState(AnnotationMode.Text, "Unable to save", 12));
            dock.SetAvailableWidth(320);
            Assert.InRange(dock.Width, 1, 320);
            foreach (ToolStripItem item in dock.Strip.Items)
                if (item is ToolStripButton or ToolStripDropDownItem)
                {
                    Assert.True(item.Available);
                    Assert.InRange(item.Bounds.Right, 1, dock.Strip.Width);
                    Assert.True(item.Height >= 40);
                }
            var warning = dock.Strip.Items.OfType<ToolStripLabel>().Single();
            Assert.True(warning.Available); Assert.Equal("Unable to save", warning.ToolTipText);
            var more = dock.Strip.Items.OfType<ToolStripDropDownButton>().Single();
            Assert.Contains(more.DropDownItems.OfType<ToolStripMenuItem>(), item => item.Text == "Retry saving");
            dock.SetAvailableWidth(1000);
            Assert.Equal(ToolStripItemDisplayStyle.ImageAndText, dock.Strip.Items["Select"]!.DisplayStyle);
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void ContextCommandsEditRealSelectedAnnotationsAndKeepHistoryWorking()
        => WorkspaceViewTests.OnSta(() =>
        {
            string folder = Path.Combine(Path.GetTempPath(), "canvas-dock-style-" + Guid.NewGuid().ToString("N"));
            try
            {
                var canvas = new Canvas(); var api = new FakeWindowApi(); var input = new FakeInputRouter();
                var config = new FakeAppConfig { EnableStartupIntro = false };
                using var wm = new WindowManager(canvas, api, config, input, new FakeClock());
                using var overview = new OverviewManager(canvas, wm, api, input, config, dwm: new FakeDwmThumbnailApi());
                var desktops = new FakeVirtualDesktops();
                overview.AttachAnnotations(desktops, Path.Combine(folder, "annotations.json"));
                overview.TransitionTo(OverviewMode.Zooming);
                var note = new CanvasAnnotation(Guid.NewGuid(), desktops.CurrentDesktopId, AnnotationKind.Text,
                    new(10, 10), new(240, 120), Text: "Dock style test");
                overview.Annotations!.Put(note);
                overview.Selection.Replace([new(CanvasItemKind.Annotation, note.Id)]);
                overview.SelectAnnotationMode(AnnotationMode.Select);
                var dock = (AnnotationToolbar)typeof(OverviewManager).GetField("_annotationToolbar", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(overview)!;
                void Choose(string property, string value)
                    => ((ToolStripDropDownButton)dock.PropertiesSurface.Strip.Items[property]!).DropDownItems
                        .OfType<ToolStripMenuItem>().Single(i => i.Text == value).PerformClick();
                Choose("Color", "Mint"); Choose("Size", "56"); Choose("Style", "Bold");
                Choose("Style", "Align center"); Choose("Fill", "Outline");
                var changed = Assert.Single(overview.Annotations.Items);
                Assert.Equal("#34D399", changed.Color); Assert.Equal(56, changed.FontSize);
                Assert.Equal(AnnotationTextEmphasis.Bold, changed.Emphasis);
                Assert.Equal(AnnotationTextAlign.Center, changed.TextAlign);
                Assert.Equal(AnnotationCardFill.None, changed.CardFill);
                var more = dock.Strip.Items.OfType<ToolStripDropDownButton>().Single();
                Assert.True(more.DropDownItems["Edit annotation"]!.Enabled);
                more.DropDownItems["Delete annotation"]!.PerformClick();
                Assert.Empty(overview.Annotations.Items); Assert.False(dock.PropertiesSurface.Visible);
                Assert.True(dock.Strip.Items["Undo"]!.Enabled);
                dock.Strip.Items["Undo"]!.PerformClick();
                Assert.Equal(changed, Assert.Single(overview.Annotations.Items));
                Assert.True(dock.Strip.Items["Redo"]!.Enabled);
                dock.Strip.Items["Redo"]!.PerformClick();
                Assert.Empty(overview.Annotations.Items);
            }
            finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void ToolbarFollowsTaskbarSettingImmediatelyAndAcrossCanvasModeChanges()
        => WorkspaceViewTests.OnSta(() =>
        {
            string folder = Path.Combine(Path.GetTempPath(), "canvas-toolbelt-" + Guid.NewGuid().ToString("N"));
            try
            {
                var canvas = new Canvas(); var api = new FakeWindowApi(); var input = new FakeInputRouter();
                var config = new FakeAppConfig { EnableStartupIntro = false };
                using var wm = new WindowManager(canvas, api, config, input, new FakeClock());
                using var overview = new OverviewManager(canvas, wm, api, input, config, dwm: new FakeDwmThumbnailApi());
                overview.AttachAnnotations(new FakeVirtualDesktops(), Path.Combine(folder, "annotations.json"));
                overview.TransitionTo(OverviewMode.Zooming);

                void Check(bool taskbarVisible)
                {
                    var toolbar = (AnnotationToolbar)typeof(OverviewManager)
                        .GetField("_annotationToolbar", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(overview)!;
                    var pass = (OverviewOverlay)toolbar.Owner!;
                    var area = taskbarVisible ? pass.Screen.WorkingArea : pass.Screen.Bounds;
                    Assert.True(toolbar.Visible);
                    Assert.Equal(area.Bottom, toolbar.Bottom);
                    Assert.Equal(Math.Max(area.Left, area.Left + (area.Width - toolbar.Width) / 2), toolbar.Left);
                }

                Check(true);
                config.ShowTaskbarInCanvas = false; overview.RefreshConfig(); Check(false);
                overview.TransitionTo(OverviewMode.Panning);
                overview.TransitionTo(OverviewMode.Zooming); Check(false);
                config.ShowTaskbarInCanvas = true; overview.RefreshConfig(); Check(true);
                overview.TransitionTo(OverviewMode.Hidden);
                overview.TransitionTo(OverviewMode.Zooming); Check(true);
            }
            finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        });
}
