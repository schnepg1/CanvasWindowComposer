using CanvasDesktop;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Win32;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class TemporaryAreaViewTests
{
    [Fact]
    public void TemporaryWindowMenuOffersAnExplicitKeepAction()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var rig = new WorkspaceHarness(new WorkspaceDocument { PlacementPolicyInitialized = true });
            rig.Api.AddWindow((IntPtr)42, 10, 20, 800, 600, title: "Quick notes");
            rig.Workspace.Start();
            var item = Assert.Single(rig.Workspace.Items);
            using var menu = new WorkspaceContextMenu(rig.Workspace, item.Id);
            var keep = menu.Items.Cast<ToolStripItem>().Single(i => i.Text == "Keep this place");
            Assert.True(keep.Available);
            Assert.False(menu.Items.Cast<ToolStripItem>().Single(i => i.Text == "Forget this saved place").Available);
            keep.PerformClick();
            Assert.False(Assert.Single(rig.Workspace.Items).Temporary);
            Assert.Single(rig.Store.Document.Items);
        });

    [Fact]
    [Trait("Category", "NativePreview")]
    public void ArrivalNoticeUsesTheRightEdgeWithoutTakingFocusAndHidesWhenEmpty()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var notice = new TemporaryAreaNotice(() => { });
            var work = Screen.PrimaryScreen!.WorkingArea;
            var foreground = PInvoke.GetForegroundWindow();
            notice.ShowArrivals(6, new(work.X, work.Y, work.Width, work.Height));
            Assert.True(notice.Visible);
            Assert.Equal(foreground, PInvoke.GetForegroundWindow());
            Assert.True(notice.Right <= work.Right && notice.Right >= work.Right - 80);
            Assert.Contains(notice.Controls.Cast<Control>(), c => c.Text == "6 windows in Temporary area");
            string directory = Path.Combine(AppContext.BaseDirectory, "TestArtifacts");
            Directory.CreateDirectory(directory);
            using var image = new Bitmap(notice.Width, notice.Height);
            notice.DrawToBitmap(image, notice.ClientRectangle);
            image.Save(Path.Combine(directory, "temporary-area-notice.png"));
            notice.UpdateCount(0);
            Assert.False(notice.Visible);
        });

    [Fact]
    public void TemporaryAreaRendersWithAReadableLabelAndLiveWindowCards()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var rig = new WorkspaceHarness(new WorkspaceDocument { PlacementPolicyInitialized = true });
            for (int i = 1; i <= 6; i++) rig.Api.AddWindow((IntPtr)i, 10, 20, 900, 650, title: "Temporary window " + i);
            rig.Workspace.Start();
            var area = rig.Workspace.TemporaryArea!;
            var fit = ZoneGeometry.Fit(area.Bounds, new(0, 0, 1200, 720));
            var surface = new OverviewAnnotationSurface();
            surface.Update(OverviewAnnotationSnapshot.Create(
                [new(Guid.Empty, "Temporary area · not saved", "#94A3B8", area.Bounds, area.Count)],
                fit.X, fit.Y, fit.Zoom, new(0, 0, 1200, 720), null, false,
                placeholders: rig.Workspace.Items.Select(i => new PlaceholderAnnotation((IntPtr)1, i.Title, i.Bounds, ImageOpacity: 0)).ToArray(),
                showHints: false));
            surface.Measure(new System.Windows.Size(1200, 720)); surface.Arrange(new Rect(0, 0, 1200, 720)); surface.UpdateLayout();
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 20, 30)), null, new Rect(0, 0, 1200, 720));
                dc.DrawRectangle(new VisualBrush(surface), null, new Rect(0, 0, 1200, 720));
            }
            var bitmap = new RenderTargetBitmap(1200, 720, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string directory = Path.Combine(AppContext.BaseDirectory, "TestArtifacts"); Directory.CreateDirectory(directory);
            using var stream = File.Create(Path.Combine(directory, "temporary-area.png")); encoder.Save(stream);
            Assert.True(stream.Length > 5000);
        });
}
