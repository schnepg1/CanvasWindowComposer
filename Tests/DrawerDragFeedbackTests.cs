using CanvasDesktop;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class DrawerDragFeedbackTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreviewDoesNotMoveOrSaveContentAndMatchesTheFinalDrop(bool inventory)
    {
        using var rig = new WorkspaceHarness(new WorkspaceDocument { PlacementPolicyInitialized = true });
        rig.Api.AddWindow((IntPtr)42, 10, 20, 400, 300, title: "Carried window"); rig.Workspace.Start();
        var item = Assert.Single(rig.Workspace.Document.Items);
        if (inventory) Assert.True(rig.Workspace.CollectInInventory(item.Id));
        var view = new ArrivalViewport(new(-8000, 5000, .25), [new(0, 0, 1920, 1080)], [new(1500, 0, 420, 500)]);
        rig.Workspace.ArrivalViewportProvider = () => view;
        Assert.True(rig.Workspace.Zones.Add(new() { X = -8000, Y = 5000, W = 7680, H = 4320 }, out _));
        var obstacle = new WorldRect { X = -7600, Y = 5500, W = 400, H = 300 };
        rig.Workspace.AdditionalPlacementObstacles = () => [obstacle];
        var original = item.Bounds; int saves = rig.Store.Saves, native = rig.Api.NativeStateCalls.Count;
        string? status = rig.Workspace.Status; WorldRect preview = default;
        for (int i = 0; i < 10; i++)
        {
            Assert.True(inventory ? rig.Workspace.TryPreviewInventoryDrop(item.Id, view, out preview, obstacle.X, obstacle.Y)
                : rig.Workspace.TryPreviewTemporaryDrop(item.Id, view, out preview, obstacle.X, obstacle.Y));
            Assert.False(TemporaryAreaLayout.Overlaps(preview, obstacle, 96));
        }
        Assert.Equal(original, item.Bounds); Assert.Equal(saves, rig.Store.Saves);
        Assert.Equal(native, rig.Api.NativeStateCalls.Count); Assert.Equal(status, rig.Workspace.Status);
        Assert.True(inventory ? rig.Workspace.DropFromInventory(item.Id, obstacle.X, obstacle.Y)
            : rig.Workspace.PlaceFromTemporaryArea(item.Id, view, obstacle.X, obstacle.Y));
        Assert.Equal(preview, item.Bounds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BlockedPreviewDoesNotChangeTheItemOrReportADropError(bool inventory)
    {
        using var rig = new WorkspaceHarness(new WorkspaceDocument { PlacementPolicyInitialized = true });
        rig.Api.AddWindow((IntPtr)42, 10, 20, 400, 300); rig.Workspace.Start();
        var item = Assert.Single(rig.Workspace.Document.Items);
        if (inventory) rig.Workspace.CollectInInventory(item.Id);
        var view = new ArrivalViewport(new(0, 0, 1), [new(0, 0, 1920, 1080)]);
        rig.Workspace.AdditionalPlacementObstacles = () => [new() { W = 1920, H = 1080 }];
        var original = item.Bounds; int saves = rig.Store.Saves;
        Assert.False(inventory ? rig.Workspace.TryPreviewInventoryDrop(item.Id, view, out _)
            : rig.Workspace.TryPreviewTemporaryDrop(item.Id, view, out _));
        Assert.Equal(original, item.Bounds); Assert.Equal(saves, rig.Store.Saves); Assert.Null(rig.Workspace.Status);
    }

    [Fact]
    public void PointerMovesImmediatelyWhilePlacementQueriesAreCoalescedAndFeedbackClears()
        => WorkspaceViewTests.OnSta(() =>
        {
            var drag = new DrawerRowDrag(new Border()); var view = new FakeFeedbackView(); int queries = 0;
            var queried = new List<Point>(); var id = Guid.NewGuid();
            using var feedback = new DrawerDragFeedback(drag, (_, point) =>
            { queries++; queried.Add(point); return point.X < 0 ? null : new WindowRect((int)point.X, (int)point.Y, 100, 75); }, _ => "Notes", view);
            feedback.Move(new(id, new(10, 20), true));
            for (int i = 0; i < 20; i++) feedback.Move(new(id, new(100 + i, 200), true));
            Assert.Equal(1, queries); Assert.Equal(new Point(119, 200), view.Pointer);
            feedback.RefreshPreview();
            Assert.Equal(2, queries); Assert.Equal(new Point(119, 200), queried.Last());
            Assert.Equal(new WindowRect(119, 200, 100, 75), view.Landing);
            feedback.Move(new(id, new(30, 40), false)); Assert.Null(view.Landing); Assert.False(view.CanDrop);
            feedback.Move(new(id, new(-20, 40), true)); Assert.Null(view.Landing); Assert.False(view.CanDrop);
            feedback.Clear(); Assert.False(view.Visible);
            feedback.RefreshPreview(); Assert.Equal(3, queries);
            feedback.Dispose(); feedback.Dispose(); Assert.Equal(1, view.Disposals);
        });

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void BadgeRemainsInsideNegativeCoordinateMonitorsAtDifferentDpi(int dpi)
    {
        var monitor = new ScreenRect(-1920, -1080, 1920, 1080);
        foreach (var point in new[] { new Point(-1, -1), new Point(-1920, -1080), new Point(-50, -500) })
        {
            var badge = DrawerDragFeedbackView.BadgeBounds(point, monitor, dpi, dpi);
            Assert.True(badge.Left >= monitor.Left && badge.Right <= monitor.Right);
            Assert.True(badge.Top >= monitor.Top && badge.Bottom <= monitor.Bottom);
            Assert.False(point.X >= badge.Left && point.X < badge.Right && point.Y >= badge.Top && point.Y < badge.Bottom);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void FeedbackRendersAtDifferentDpi(int scale)
        => WorkspaceViewTests.OnSta(() =>
        {
            var scene = new Grid { Background = BackpackPanel.Ink("#0D1722") };
            scene.Children.Add(new DrawerLandingSurface { Width = 360, Height = 220, HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(30, 30, 0, 0) });
            var badge = new DrawerDragBadgeSurface { Width = 180, Height = 48, HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(260, 210, 0, 0) };
            badge.Update("Project notes", true, true); scene.Children.Add(badge);
            scene.Measure(new Size(480, 300)); scene.Arrange(new Rect(0, 0, 480, 300)); scene.UpdateLayout();
            var bitmap = new RenderTargetBitmap(480 * scale, 300 * scale, 96 * scale, 96 * scale, PixelFormats.Pbgra32); bitmap.Render(scene);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string path = Path.Combine(AppContext.BaseDirectory, "TestArtifacts"); Directory.CreateDirectory(path);
            using var stream = File.Create(Path.Combine(path, $"drawer-drag-feedback-{scale}x.png")); encoder.Save(stream);
        });

    private sealed class FakeFeedbackView : IDrawerDragFeedbackView
    {
        public Point Pointer;
        public WindowRect? Landing;
        public bool CanDrop, Visible;
        public int Disposals;
        public void ShowPointer(Point point, string title, bool outside, bool canDrop) { Pointer = point; CanDrop = canDrop; Visible = true; }
        public void ShowLanding(WindowRect? landing) => Landing = landing;
        public void Hide() { Visible = false; Landing = null; }
        public void Dispose() => Disposals++;
    }
}
