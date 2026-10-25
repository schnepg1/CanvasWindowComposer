using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class OverviewCardImageQueueTests
{
    [Fact]
    public void InputFramesDoNotCaptureAndTicksPrepareAtMostTwoInFocalOrder()
    {
        var queue = new OverviewCardImageQueue(() => 0);
        var prepared = new List<int>();
        var ids = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToArray();
        queue.BeginFrame();
        for (int i = 0; i < ids.Length; i++)
        {
            int card = i;
            queue.RequestImage(ids[i], 6 - i, () => prepared.Add(card));
        }
        queue.EndFrame();
        Assert.Empty(prepared);
        Assert.Equal(2, queue.Process());
        Assert.Equal(new[] { 5, 4 }, prepared);
        Assert.True(queue.HasPending);
        Assert.Equal(2, queue.Process());
        Assert.Equal(new[] { 5, 4, 3, 2 }, prepared);
    }

    [Fact]
    public void SlowImageEndsTheTickAndZoomingOrPanningAwayCancelsRemainingWork()
    {
        double time = 0;
        var queue = new OverviewCardImageQueue(() => time);
        int calls = 0;
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        queue.BeginFrame();
        queue.RequestImage(first, 0, () => { calls++; time += 10; });
        queue.RequestImage(second, 1, () => calls++);
        queue.EndFrame();
        Assert.Equal(1, queue.Process());
        queue.BeginFrame(); queue.EndFrame();
        Assert.False(queue.HasPending);
        Assert.Equal(0, queue.Process());
        Assert.Equal(1, calls);
    }

    [Fact]
    public void WaitingForDetailKeepsTheExistingCardUntilTheImageIsReady()
        => WorkspaceViewTests.OnSta(() =>
        {
            var transitions = new OverviewCardTransitions(); var id = Guid.NewGuid();
            var card = new PlaceholderAnnotation((IntPtr)1, "Project", new WorldRect { W = 800, H = 550 });
            transitions.Update([new(id, card, OverviewCardMode.Simple)], 0);
            var waiting = new OverviewCardInput(id, card, OverviewCardMode.Detailed, DetailReady: false);
            Assert.Equal(0, Assert.Single(transitions.Update([waiting], 500)).ImageOpacity);
            Assert.False(transitions.IsAnimating);
            var image = new System.Windows.Media.DrawingImage(); image.Freeze();
            var ready = waiting with { DetailReady = true, Card = card with { Image = image } };
            transitions.Update([ready], 510);
            Assert.True(transitions.IsAnimating);
            var end = Assert.Single(transitions.Update([ready], 660));
            Assert.Equal(1, end.ImageOpacity); Assert.Same(image, end.Image);
            var simple = ready with { Mode = OverviewCardMode.Simple, Card = card };
            transitions.Update([simple], 670);
            Assert.Null(Assert.Single(transitions.Update([simple], 820)).Image);
            Assert.Equal(0, transitions.RetainedImages);
        });
}

[Collection("ShellViews")]
public class OverviewCardImageRenderingTests
{
    [Fact]
    public void OverviewImagesAreBoundedCachedForPanningAndTextRowsFitTheirContent()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var rig = new WorkspaceHarness();
            var item = new WorkspaceItemView(Guid.NewGuid(), "Project", @"C:\Windows\explorer.exe",
                @"C:\Work", false, new WorldRect { W = 1800, H = 1000 }, "Placeholder", IsFolder: true);
            using var form = new PlaceholderForm(rig.Workspace, item);
            form.ShowAt(new WindowRect(-10000, -10000, 1800, 1000));
            var image = form.GetOverviewImage(OverviewCardImageQueue.ImageMaxDimension);
            Assert.True(image.IsFrozen);
            Assert.InRange(image.PixelWidth, 1, OverviewCardImageQueue.ImageMaxDimension);
            Assert.InRange(image.PixelHeight, 1, OverviewCardImageQueue.ImageMaxDimension);
            var panel = form.Controls.OfType<System.Windows.Forms.TableLayoutPanel>().Single();
            foreach (var row in panel.Controls.OfType<System.Windows.Forms.Label>().Where(row => row.Visible))
            {
                Assert.True(row.Width < panel.Width / 2, "Short text must not paint a full-width row.");
                Assert.InRange(Math.Abs(row.Left + row.Width / 2 - panel.Width / 2), 0, 3);
            }
            form.Left += 100;
            var moved = item.Bounds; moved.X += 100;
            form.UpdateItem(item with { Bounds = moved });
            Assert.Same(image, form.GetOverviewImage(OverviewCardImageQueue.ImageMaxDimension));
            form.Width -= 100;
            Assert.Null(form.CachedOverviewImage);
            Assert.NotSame(image, form.GetOverviewImage(OverviewCardImageQueue.ImageMaxDimension));
            form.UpdateItem(item with { Target = @"C:\Changed" });
            Assert.Null(form.CachedOverviewImage);
        });
}
