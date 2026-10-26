using CanvasDesktop;
using CanvasDesktop.CanvasView;
using System.Windows.Forms;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public sealed class CameraBookmarkOverviewTests
{
    private static void WithOverview(Action<OverviewManager, Canvas, FakeVirtualDesktops, string> action,
        Action<Canvas, FakeWindowApi>? setup = null, bool reducedMotion = true)
        => WorkspaceViewTests.OnSta(() =>
        {
            string folder = Path.Combine(Path.GetTempPath(), "camera-bookmark-overview-" + Guid.NewGuid().ToString("N"));
            try
            {
                Assert.True(GridRenderer.CompileShaders());
                var canvas = new Canvas(); canvas.SetCamera(700.25, -300.5);
                var api = new FakeWindowApi(); var input = new FakeInputRouter();
                var desktops = new FakeVirtualDesktops { CurrentDesktopId = Guid.NewGuid() };
                var config = new FakeAppConfig { EnableStartupIntro = false, ReduceStartupMotion = reducedMotion };
                setup?.Invoke(canvas, api);
                using var wm = new WindowManager(canvas, api, config, input, new FakeClock());
                using var overview = new OverviewManager(canvas, wm, api, input, config, prefersReducedStartupMotion: () => false);
                overview.AttachAnnotations(desktops, Path.Combine(folder, "annotations.json"));
                overview.TransitionTo(OverviewMode.Zooming);
                action(overview, canvas, desktops, folder);
            }
            finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        });

    private static KeyEventArgs Press(OverviewManager overview, Keys keys)
    {
        var pass = (OverviewOverlay)Control.FromHandle(overview.MonitorHandles[0])!;
        var args = new KeyEventArgs(keys); pass.OnKey!(pass, args); return args;
    }

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void RecallCancelsWindowNavigationSoLaterAnimationFramesCannotMoveTheSavedView()
        => WithOverview((overview, canvas, desktops, folder) =>
        {
            var saved = overview.ViewCamera; var world = canvas.Windows[(IntPtr)42];
            Press(overview, Keys.Control | Keys.D2);
            Assert.True(overview.NavigateToWindow((IntPtr)42)); Assert.True(overview.WindowNavigationActive);
            overview.AdvanceWindowNavigation(WindowNavigationFlight.Duration / 2);
            Assert.NotEqual(saved, overview.ViewCamera);
            Press(overview, Keys.D2); Assert.False(overview.WindowNavigationActive); Assert.Equal(saved, overview.ViewCamera);
            overview.AdvanceWindowNavigation(WindowNavigationFlight.Duration);
            Assert.Equal(saved, overview.ViewCamera); Assert.Equal(world, canvas.Windows[(IntPtr)42]);
        }, setup: (canvas, api) =>
        {
            api.AddWindow((IntPtr)42, 100, 200, 600, 400, title: "Bookmark test app");
            canvas.SetWindow((IntPtr)42, 8000, -4000, 600, 400);
        }, reducedMotion: false);

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void KeysSaveRecallOverwriteAndKeepTheCanvasOpenWithoutChangingTheMainCamera()
        => WithOverview((overview, canvas, desktops, folder) =>
        {
            Press(overview, Keys.Subtract); var saved = overview.ViewCamera;
            Assert.InRange(saved.Zoom, .02, .99);
            var save = Press(overview, Keys.Control | Keys.D1); Assert.True(save.Handled); Assert.True(save.SuppressKeyPress);
            canvas.SetCamera(-900, 500); overview.SyncCamera(); var desktopCamera = canvas.Camera;
            Press(overview, Keys.NumPad1);
            Assert.Equal(saved, overview.ViewCamera); Assert.Equal(desktopCamera, canvas.Camera);
            Assert.Equal(OverviewMode.Zooming, overview.CurrentMode);
            Press(overview, Keys.Add); var replacement = overview.ViewCamera;
            Press(overview, Keys.Control | Keys.NumPad1);
            canvas.SetCamera(3000, 4000); overview.SyncCamera(); Press(overview, Keys.D1);
            Assert.Equal(replacement, overview.ViewCamera);
            Assert.True(new CameraBookmarkStore(Path.Combine(folder, "camera-bookmarks.json")).TryGet(desktops.CurrentDesktopId, 1, out var persisted));
            Assert.Equal(replacement, persisted);
            overview.TransitionTo(OverviewMode.Hidden); overview.TransitionTo(OverviewMode.Zooming); Press(overview, Keys.D1);
            Assert.Equal(replacement, overview.ViewCamera);
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void NoteEditingAndHiddenCanvasLeaveNumberKeysAlone()
        => WithOverview((overview, canvas, desktops, folder) =>
        {
            var note = new CanvasAnnotation(Guid.NewGuid(), desktops.CurrentDesktopId, AnnotationKind.Text,
                new(100, 100), new(200, 100), Text: "Numbers belong here");
            overview.Annotations!.Put(note); overview.AnnotationTool!.Select(note.Id);
            Press(overview, Keys.Enter); Application.DoEvents();
            Assert.NotNull(overview.AnnotationEditor);
            var current = overview.ViewCamera;
            Assert.False(Press(overview, Keys.Control | Keys.D1).Handled);
            Assert.False(Press(overview, Keys.D1).Handled);
            Assert.Equal(current, overview.ViewCamera);
            Assert.False(File.Exists(Path.Combine(folder, "camera-bookmarks.json")));
            overview.HandleEscape(); overview.TransitionTo(OverviewMode.Hidden);
            Assert.False(Press(overview, Keys.Control | Keys.D1).Handled);
            Assert.False(File.Exists(Path.Combine(folder, "camera-bookmarks.json")));
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void EmptySlotsAndDesktopSwitchesKeepTheCurrentViewAndModifiersDoNotOverwrite()
        => WithOverview((overview, canvas, desktops, folder) =>
        {
            Press(overview, Keys.Control | Keys.D0); var first = overview.ViewCamera;
            canvas.SetCamera(2000, -2000); overview.SyncCamera(); var changed = overview.ViewCamera;
            Assert.False(Press(overview, Keys.Control | Keys.Shift | Keys.D0).Handled);
            Press(overview, Keys.D9); Assert.Equal(changed, overview.ViewCamera);
            var originalDesktop = desktops.CurrentDesktopId;
            desktops.SwitchTo(Guid.NewGuid()); var second = overview.ViewCamera;
            Press(overview, Keys.D0); Assert.Equal(second, overview.ViewCamera);
            desktops.SwitchTo(originalDesktop); Press(overview, Keys.D0); Assert.Equal(first, overview.ViewCamera);
        });
}
