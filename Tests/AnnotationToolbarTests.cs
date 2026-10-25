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
