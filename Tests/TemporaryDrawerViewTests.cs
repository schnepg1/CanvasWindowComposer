using CanvasDesktop;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;
using Windows.Win32;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class TemporaryDrawerViewTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void DrawerRendersTheListAndItsPermanentRailAtDifferentDpi(int scale)
        => WorkspaceViewTests.OnSta(() =>
        {
            var panel = new TemporaryDrawerPanel(); panel.SetExpanded(true);
            var rows = new[] { TemporaryDrawerTests.Row("New research window"), TemporaryDrawerTests.Row("Terminal"),
                TemporaryDrawerTests.Row("Quick notes") with { State = "Minimized" } };
            panel.UpdateItems("Occasional apps", rows, [rows[0].Id], true);
            panel.Measure(new Size(382, 570)); panel.Arrange(new Rect(0, 0, 382, 570)); panel.UpdateLayout();
            var image = new RenderTargetBitmap(382 * scale, 570 * scale, 96 * scale, 96 * scale, PixelFormats.Pbgra32); image.Render(panel);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            string directory = Path.Combine(AppContext.BaseDirectory, "TestArtifacts"); Directory.CreateDirectory(directory);
            using var stream = File.Create(Path.Combine(directory, $"temporary-drawer-{scale}x.png")); encoder.Save(stream);
            var text = Descendants(panel).OfType<TextBlock>().Select(t => t.Text).ToArray();
            Assert.Contains("TEMPORARY", text); Assert.Contains("Occasional apps", text);
            Assert.Contains("New research window", text); Assert.Contains(text, t => t.Contains("Minimized"));
        });

    [Fact]
    [Trait("Category", "NativePreview")]
    public void DrawerStaysAtTheRightBorderWithoutTakingFocusAndFoldsBackToAClickableRail()
        => WorkspaceViewTests.OnSta(() =>
        {
            var work = Forms.Screen.PrimaryScreen!.WorkingArea;
            var screen = new ScreenRect(work.X, work.Y, work.Width, work.Height);
            using var drawer = new TemporaryDrawerWindow(screen);
            var foreground = PInvoke.GetForegroundWindow();
            drawer.Refresh(screen, null, [], true);
            Assert.True(drawer.IsVisible); Assert.False(drawer.State.IsOpen);
            Assert.Equal(work.Right, drawer.ScreenBounds.Right); Assert.InRange(drawer.ScreenBounds.Width, 36, 90);
            Assert.Equal(foreground, PInvoke.GetForegroundWindow());
            var area = new TemporaryAreaView(new() { W = 3000, H = 1200 }, 1, Guid.NewGuid(), "Temporary area", "#94A3B8");
            drawer.Refresh(screen, area, [TemporaryDrawerTests.Row("Arrival")], true);
            Assert.True(drawer.State.IsOpen); Assert.True(drawer.ScreenBounds.Width > 300);
            Assert.Equal(work.Right, drawer.ScreenBounds.Right); Assert.Equal(foreground, PInvoke.GetForegroundWindow());
            drawer.State.SetHovered(false, Environment.TickCount64);
            drawer.Advance(Environment.TickCount64 + 5001);
            Assert.False(drawer.State.IsOpen); Assert.True(drawer.IsVisible); Assert.InRange(drawer.ScreenBounds.Width, 36, 90);
            drawer.Refresh(screen, area with { Count = 0 }, [], true);
            Assert.Empty(drawer.State.Items); Assert.True(drawer.IsVisible);
            Descendants(drawer.Panel).OfType<Button>().Single(b => b.ToolTip?.ToString() == "Peek at temporary windows")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(drawer.State.IsOpen); // An empty drawer is still available.
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void DrawerRowCanBeDraggedAcrossTheCanvasWithoutMovingTheCamera()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var source = new Forms.Form();
            var config = new FakeAppConfig { EnableStartupIntro = false, ReduceStartupMotion = true };
            using var rig = new WorkspaceHarness(new WorkspaceDocument { PlacementPolicyInitialized = true }, config: config);
            rig.Api.AddWindow(source.Handle, 100, 100, 400, 300, title: "Move from drawer"); rig.Workspace.Start();
            Assert.True(GridRenderer.CompileShaders());
            using var overview = new OverviewManager(rig.Canvas, rig.Wm, rig.Api, rig.Input, config, new FakeScreens());
            overview.AttachPlaceholderWorkspace(rig.Workspace); overview.TransitionTo(OverviewMode.Zooming);
            overview.CenterViewOnMinimap(-8000, -4000);
            var drawer = overview.TemporaryDrawer!; PreparePointer(drawer);
            var before = overview.ViewCamera; var item = Assert.Single(rig.Workspace.TemporaryAreaItems);
            var point = drawer.Panel.PointFromScreen(new Point(350, 350));
            drawer.Panel.Drag.Begin(item.Id, new Point(100, 130));
            Assert.True(drawer.Panel.Drag.IsHeld, $"Visible={drawer.Panel.IsVisible}, enabled={drawer.Panel.IsEnabled}, captured={System.Windows.Input.Mouse.Captured}, width={drawer.Panel.ActualWidth}, windowVisible={drawer.IsVisible}, open={drawer.State.IsOpen}");
            drawer.Panel.Drag.Move(point, true); drawer.Panel.Drag.Release(point);
            Assert.False(drawer.Panel.Drag.IsHeld); Assert.Empty(rig.Workspace.TemporaryAreaItems);
            var placed = Assert.Single(rig.Workspace.Items);
            Assert.False(placed.Temporary); Assert.Equal(before, overview.ViewCamera);
            Assert.InRange(placed.Bounds.X, before.X, before.X + 1920 / before.Zoom);
            Assert.Single(rig.Store.Document.Items);
        });

    [Fact]
    [Trait("Category", "NativePreview")]
    public void CancellingOrReleasingInsideTheDrawerNeverPlacesAnEntry()
        => WorkspaceViewTests.OnSta(() =>
        {
            var work = Forms.Screen.PrimaryScreen!.WorkingArea;
            var screen = new ScreenRect(work.X, work.Y, work.Width, work.Height);
            using var drawer = new TemporaryDrawerWindow(screen); var item = TemporaryDrawerTests.Row("Keep here");
            drawer.Refresh(screen, new(new() { W = 3000, H = 1200 }, 1, Guid.NewGuid(), "Temporary area", "#94A3B8"), [item], true);
            PreparePointer(drawer);
            drawer.Panel.UpdateLayout(); int drops = 0; drawer.Panel.Drag.Dropped += (_, _) => drops++;
            drawer.Panel.Drag.Begin(item.Id, new Point(100, 100)); drawer.Panel.Drag.Move(new Point(150, 150), true);
            Assert.True(drawer.Panel.Drag.IsHeld);
            var added = TemporaryDrawerTests.Row("Arrived during drag");
            drawer.Panel.UpdateItems("Temporary area", [added, item], [added.Id], true);
            Assert.DoesNotContain(Descendants(drawer.Panel).OfType<TextBlock>(), t => t.Text == added.Title);
            drawer.Panel.Drag.Release(new Point(150, 150)); Assert.Equal(0, drops);
            Assert.Contains(Descendants(drawer.Panel).OfType<TextBlock>(), t => t.Text == added.Title);
            drawer.Panel.Drag.Begin(item.Id, new Point(100, 100)); drawer.Panel.Drag.Move(new Point(-200, 100), true);
            drawer.Panel.Drag.Cancel(); drawer.Panel.Drag.Release(new Point(-200, 100)); Assert.Equal(0, drops);
        });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }

    private static void PreparePointer(TemporaryDrawerWindow drawer)
    {
        drawer.Activate(); Forms.Application.DoEvents(); drawer.Panel.UpdateLayout();
        // Establish WPF's mouse input source without moving the user's pointer or injecting a desktop click.
        SendMessageW(new WindowInteropHelper(drawer).Handle, 0x0200, IntPtr.Zero, new IntPtr(100 | (100 << 16)));
    }
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr SendMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
