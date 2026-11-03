using CanvasDesktop;
using System.Drawing;
using System.Windows.Forms;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class CanvasPeekIntegrationTests
{
    private sealed class Rig : IDisposable
    {
        public readonly Form Source = new();
        public readonly Canvas Canvas = new();
        public readonly FakeWindowApi Api = new();
        public readonly FakeInputRouter Input = new();
        public readonly FakeAppConfig Config = new() { EnableStartupIntro = false, ReduceStartupMotion = true };
        public readonly FakeClock Clock = new();
        public readonly FakeDwmThumbnailApi Dwm = new();
        public readonly OverviewWindowInteractionTests.PointerApi Pointer = new();
        public readonly WindowManager Wm;
        public readonly OverviewManager Overview;
        public readonly OverviewOverlay Pass;
        public Rig(bool nativeInteraction = false)
        {
            var b = Screen.PrimaryScreen!.Bounds;
            Source.StartPosition = FormStartPosition.Manual;
            Source.Bounds = new(b.X + 100, b.Y + 100, 800, 520);
            if (nativeInteraction) Source.Show();
            Api.AddWindow(Source.Handle, b.X + 100, b.Y + 100, 800, 520, title: "Editor · feature/canvas-peek");
            Canvas.SetWindow(Source.Handle, b.X + 100, b.Y + 100, 800, 520);
            Wm = new(Canvas, Api, Config, Input, Clock);
            Overview = new(Canvas, Wm, Api, Input, Config, gestureClock: Clock,
                dwm: nativeInteraction ? new DwmThumbnailApi() : Dwm,
                interactionApi: nativeInteraction ? new Win32WindowInteractionApi() : Pointer);
            Overview.TransitionTo(OverviewMode.Zooming);
            Pass = (OverviewOverlay)Control.FromHandle(Overview.MonitorHandles[0])!;
        }
        public Point SourcePoint
        {
            get
            {
                var world = Canvas.Windows[Source.Handle]; var camera = Overview.ViewCamera;
                return new((int)((world.X + world.W / 2 - camera.X) * camera.Zoom),
                    (int)((world.Y + world.H / 2 - camera.Y) * camera.Zoom));
            }
        }
        public Point ContentPoint
        {
            get
            {
                var peek = Overview.PeekWindow!; var content = peek.ContentBounds;
                return peek.PointToScreen(new(content.Left + content.Width / 2, content.Top + content.Height / 2));
            }
        }
        public void ZoomOut()
        {
            Pass.OnWheel!(Pass, 0, 0, -900, false, false, false);
            Overview.RefreshZoneLayout();
        }
        public void Hover()
        {
            var point = SourcePoint;
            Pass.OnMouseMoved!(Pass, new(MouseButtons.None, 0, point.X - Pass.OriginX, point.Y - Pass.OriginY, 0));
        }
        public void Dispatch(InteractionMouseEvent input)
        { Overview.QueueInteractionMouse(input); Application.DoEvents(); }
        public void Return() => Overview.PeekWindow!.Controls.OfType<Button>().Single(b => b.Text == "Return").PerformClick();
        public void Dispose() { Overview.Dispose(); Wm.Dispose(); Source.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "NativePreview")]
    public void StationaryHoverOpensAfterDelayEvenWhenPlaceholderTiersAreDisabled(bool cards)
        => WorkspaceViewTests.OnSta(() =>
        {
            using var h = new Rig();
            h.Config.EnableDetailedCardsTier = h.Config.EnableSimpleCardsTier = cards;
            h.ZoomOut(); h.Hover();
            var before = h.Overview.ViewCamera;
            h.Clock.Advance(999); h.Overview.TickWindowPeek(h.SourcePoint);
            Assert.Null(h.Overview.PeekWindow);
            h.Clock.Advance(1); h.Overview.TickWindowPeek(h.SourcePoint);
            Assert.NotNull(h.Overview.PeekWindow);
            Assert.True(h.Overview.PeekWindow.Live);
            Assert.False(h.Overview.WindowInteractionActive);
            Assert.True(h.Input.EscHotkeyEnabled);
            Assert.Equal(before, h.Overview.ViewCamera);
            Assert.Empty(h.Pointer.Messages);
            Assert.Equal((nint)3, (nint)PInvoke.SendMessage((HWND)h.Overview.PeekWindow.Handle, 0x21, 0, 0).Value);
        });

    [Fact]
    [Trait("Category", "NativePreview")]
    public void PointerTravelHasGraceAndEscapeDismissesOnlyThePeek()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var h = new Rig(); h.ZoomOut();
            Assert.True(h.Overview.ShowWindowPeek(h.Pass, h.Source.Handle));
            var outside = new Point(h.Pass.Screen.Bounds.Right + 10, h.Pass.Screen.Bounds.Bottom + 10);
            h.Overview.TickWindowPeek(outside);
            h.Clock.Advance(399); h.Overview.TickWindowPeek(outside);
            Assert.NotNull(h.Overview.PeekWindow);
            h.Overview.TickWindowPeek(h.ContentPoint);
            h.Clock.Advance(500); h.Overview.TickWindowPeek(h.ContentPoint);
            Assert.NotNull(h.Overview.PeekWindow);
            h.Overview.HandleEscape();
            Assert.Null(h.Overview.PeekWindow);
            Assert.Equal(OverviewMode.Zooming, h.Overview.CurrentMode);
        });

    [Fact]
    [Trait("Category", "NativePreview")]
    public void WorkHereIsStickyAndKeepsEscapeNumbersWheelAndCameraWithTheApp()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var h = new Rig(); h.ZoomOut();
            var before = h.Overview.ViewCamera; var original = h.Canvas.Windows[h.Source.Handle];
            int moves = h.Api.SetPositionCalls.Count;
            Assert.True(h.Overview.ShowWindowPeek(h.Pass, h.Source.Handle));
            h.Overview.BeginPeekInteraction();
            Assert.True(h.Overview.PeekWorking); Assert.False(h.Input.EscHotkeyEnabled);
            h.Clock.Advance(5000); h.Overview.TickWindowPeek(new(-40000, -40000));
            h.Overview.HandleEscape();
            h.Pass.OnKey!(h.Pass, new(Keys.D1));
            Assert.True(h.Overview.PeekWorking);
            h.Dispatch(new(h.ContentPoint, MouseButtons.Left, true, false));
            h.Dispatch(new(h.ContentPoint, MouseButtons.Left, false, true));
            h.Dispatch(new(h.ContentPoint, MouseButtons.None, false, false, Wheel: true, Delta: -120, Modifiers: Keys.Control));
            Assert.Contains(h.Pointer.Messages, m => m.Id == 0x201);
            Assert.Contains(h.Pointer.Messages, m => m.Id == 0x202);
            Assert.Contains(h.Pointer.Messages, m => m.Id == 0x20A && (m.Flags & 8) != 0);
            h.Return();
            Assert.Null(h.Overview.PeekWindow); Assert.False(h.Overview.WindowInteractionActive);
            Assert.False(h.Pass.WindowInteractionActive); Assert.True(h.Input.EscHotkeyEnabled);
            Assert.Equal(before, h.Overview.ViewCamera); Assert.Equal(original, h.Canvas.Windows[h.Source.Handle]);
            Assert.Equal(moves, h.Api.SetPositionCalls.Count);
        });

    [Fact]
    [Trait("Category", "NativePreview")]
    public void KeyboardActivationConsumesTheCompleteEnterGesture()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var h = new Rig();
            Assert.True(h.Overview.ShowWindowPeek(h.Pass, h.Source.Handle));
            var down = new KeyEventArgs(Keys.Enter);
            h.Pass.OnKey!(h.Pass, down);
            Assert.True(down.SuppressKeyPress); Assert.False(h.Overview.PeekWorking);
            h.Pass.OnKeyReleased!(h.Pass, new(Keys.Enter));
            Assert.True(h.Overview.PeekWorking); Assert.Empty(h.Pointer.Messages);
        });

    [Fact]
    [Trait("Category", "NativePreview")]
    public void RejectedInputRestoresActivatingCanvasStylesAndReleasesTheHook()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var h = new Rig();
            Assert.True(h.Overview.ShowWindowPeek(h.Pass, h.Source.Handle)); h.Overview.BeginPeekInteraction();
            h.Pointer.Accept = false;
            h.Dispatch(new(h.ContentPoint, MouseButtons.Left, true, false));
            Assert.False(h.Overview.WindowInteractionActive); Assert.False(h.Overview.PeekWorking);
            Assert.False(h.Pass.WindowInteractionActive); Assert.True(h.Input.EscHotkeyEnabled);
            Assert.NotNull(h.Overview.PeekWindow);
        });

    [Theory]
    [InlineData("destroyed")]
    [InlineData("minimized")]
    [InlineData("recycled")]
    [InlineData("external focus")]
    [InlineData("close canvas")]
    [Trait("Category", "NativePreview")]
    public void LifecycleChangesReleaseThePreviewAndInteraction(string change)
        => WorkspaceViewTests.OnSta(() =>
        {
            using var h = new Rig();
            Assert.True(h.Overview.ShowWindowPeek(h.Pass, h.Source.Handle)); h.Overview.BeginPeekInteraction();
            var destination = h.Overview.PeekWindow!.Handle;
            var thumb = h.Dwm.Registrations.Single(r => r.Destination == destination).Thumbnail;
            switch (change)
            {
                case "destroyed": h.Input.RaiseWindowDestroyed(h.Source.Handle); break;
                case "minimized": h.Input.RaiseWindowMinimized(h.Source.Handle); break;
                case "recycled": h.Api.Windows[h.Source.Handle].ProcessId++; h.Overview.TickWindowPeek(h.ContentPoint); break;
                case "external focus": h.Input.RaiseWindowFocused((IntPtr)999); break;
                case "close canvas": h.Overview.TransitionTo(OverviewMode.Hidden); break;
            }
            Assert.Null(h.Overview.PeekWindow); Assert.False(h.Overview.WindowInteractionActive);
            Assert.False(h.Pass.WindowInteractionActive); Assert.Contains(thumb, h.Dwm.Released);
        });

    [Fact]
    [Trait("Category", "NativePreview")]
    public void OwnedDialogsStayInteractiveAndReturnToTheOriginalSource()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var h = new Rig();
            using var dialog = new Form { Owner = h.Source };
            h.Pointer.Owners[dialog.Handle] = h.Source.Handle;
            h.Api.AddWindow(dialog.Handle, 200, 200, 400, 300, title: "Save changes");
            Assert.True(h.Overview.ShowWindowPeek(h.Pass, h.Source.Handle)); h.Overview.BeginPeekInteraction();
            h.Input.RaiseWindowFocused(dialog.Handle);
            Assert.True(h.Overview.PeekWorking); Assert.True(h.Overview.PeekWindow!.ShowingOwnedDialog);
            Assert.False(h.Input.EscHotkeyEnabled);
            Assert.Contains(dialog.Handle, h.Dwm.Sources);
            h.Input.RaiseWindowFocused(h.Source.Handle);
            Assert.True(h.Overview.PeekWorking); Assert.False(h.Overview.PeekWindow.ShowingOwnedDialog);
            Assert.False(h.Input.EscHotkeyEnabled);
            h.Return(); Assert.True(h.Input.EscHotkeyEnabled);
        });

    [Fact]
    [Trait("Category", "NativePreview")]
    public void ActivationReleaseOutsideDoesNotConsumeTheNextPreviewClick()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var h = new Rig(); h.Overview.SetInteractMode(true);
            Assert.True(h.Overview.ShowWindowPeek(h.Pass, h.Source.Handle));
            var peek = h.Overview.PeekWindow!;
            var local = peek.PointToClient(h.ContentPoint);
            int packed = (ushort)local.X | ((ushort)local.Y << 16);
            PInvoke.SendMessage((HWND)peek.Handle, 0x201, 1, packed);
            Assert.True(h.Overview.PeekWorking);
            h.Dispatch(new(new(-40000, -40000), MouseButtons.Left, false, true));
            h.Dispatch(new(h.ContentPoint, MouseButtons.Left, true, false));
            h.Dispatch(new(h.ContentPoint, MouseButtons.Left, false, true));
            Assert.Single(h.Pointer.Messages, m => m.Id == 0x201);
            Assert.Single(h.Pointer.Messages, m => m.Id == 0x202);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "NativePreview")]
    public void ModalAvailabilityOrderingHandsOffWithoutReclaimingEscape(bool dialogWasShown)
        => WorkspaceViewTests.OnSta(() =>
        {
            using var h = new Rig();
            using var dialog = new Form { Owner = h.Source };
            h.Pointer.Owners[dialog.Handle] = h.Source.Handle;
            h.Api.AddWindow(dialog.Handle, 200, 200, 400, 300, title: "Save changes");
            Assert.True(h.Overview.ShowWindowPeek(h.Pass, h.Source.Handle)); h.Overview.BeginPeekInteraction();
            if (dialogWasShown) h.Input.RaiseWindowFocused(dialog.Handle);
            h.Pointer.Available = false;
            if (dialogWasShown) h.Input.RaiseWindowDestroyed(dialog.Handle);
            else h.Overview.TickWindowPeek(h.ContentPoint);
            Assert.Null(h.Overview.PeekWindow); Assert.False(h.Overview.WindowInteractionActive);
            Assert.Equal(OverviewMode.Hidden, h.Overview.CurrentMode); Assert.False(h.Input.EscHotkeyEnabled);
        });

    [Fact]
    [Trait("Category", "NativePreview")]
    public void UnavailableOwnedForegroundWindowHandsOffWithoutTakingEscape()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var h = new Rig();
            using var dialog = new Form();
            h.Pointer.Owners[dialog.Handle] = h.Source.Handle;
            h.Api.AddWindow(dialog.Handle, 200, 200, 400, 300, title: "Save changes");
            Assert.True(h.Overview.ShowWindowPeek(h.Pass, h.Source.Handle)); h.Overview.BeginPeekInteraction();
            h.Pointer.Available = false;
            h.Input.RaiseWindowFocused(dialog.Handle);
            Assert.Null(h.Overview.PeekWindow); Assert.Equal(OverviewMode.Hidden, h.Overview.CurrentMode);
            Assert.False(h.Input.EscHotkeyEnabled);
        });

    [Theory]
    [InlineData("move")]
    [InlineData("release")]
    [InlineData("wheel")]
    [Trait("Category", "NativePreview")]
    public void PointerDispatchDuringModalCreationHandsOffBeforeReclaimingEscape(string gesture)
        => WorkspaceViewTests.OnSta(() =>
        {
            using var h = new Rig(); Assert.True(h.Overview.ShowWindowPeek(h.Pass, h.Source.Handle));
            h.Overview.BeginPeekInteraction(); var point = h.ContentPoint;
            h.Pointer.Available = false;
            h.Dispatch(new(point, gesture == "release" ? MouseButtons.Left : MouseButtons.None, false,
                gesture == "release", Wheel: gesture == "wheel", Delta: -120));
            Assert.Null(h.Overview.PeekWindow); Assert.Equal(OverviewMode.Hidden, h.Overview.CurrentMode);
            Assert.False(h.Input.EscHotkeyEnabled);
        });

    [Theory]
    [InlineData(MouseButtons.Left)]
    [InlineData(MouseButtons.Right)]
    [InlineData(MouseButtons.Middle)]
    [Trait("Category", "NativePreview")]
    public void ChromeOwnsItsGestureThroughReleaseOverAppContent(MouseButtons button)
        => WorkspaceViewTests.OnSta(() =>
        {
            using var h = new Rig();
            Assert.True(h.Overview.ShowWindowPeek(h.Pass, h.Source.Handle)); h.Overview.BeginPeekInteraction();
            var header = h.Overview.PeekWindow!.PointToScreen(new(20, 10));
            h.Dispatch(new(header, button, true, false));
            h.Dispatch(new(h.ContentPoint, button, false, true));
            Assert.Empty(h.Pointer.Messages);
            h.Dispatch(new(h.ContentPoint, MouseButtons.Left, true, false));
            h.Dispatch(new(h.ContentPoint, MouseButtons.Left, false, true));
            Assert.Single(h.Pointer.Messages, m => m.Id == 0x201);
            Assert.Single(h.Pointer.Messages, m => m.Id == 0x202);
        });

    [Fact]
    [Trait("Category", "NativePreview")]
    public void DwmFailureAndDisabledPreferenceNeverLeaveAnEmptyPopup()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var h = new Rig();
            h.Dwm.FailedSources.Add(h.Source.Handle);
            Assert.False(h.Overview.ShowWindowPeek(h.Pass, h.Source.Handle));
            Assert.Null(h.Overview.PeekWindow); Assert.True(h.Input.EscHotkeyEnabled);
            h.Dwm.FailedSources.Clear();
            Assert.True(h.Overview.ShowWindowPeek(h.Pass, h.Source.Handle));
            h.Config.EnableCanvasPeek = false; h.Overview.RefreshConfig();
            Assert.Null(h.Overview.PeekWindow);
            Assert.False(h.Overview.ShowWindowPeek(h.Pass, h.Source.Handle));
        });

    [Theory]
    [InlineData(.25)]
    [InlineData(.05)]
    [Trait("Category", "NativePreview")]
    public void DetailedAndSimpleCardsResolveTheirRunningSourceButLaunchersNeverOpen(double zoom)
        => WorkspaceViewTests.OnSta(() =>
        {
            var config = new FakeAppConfig { EnableStartupIntro = false, ReduceStartupMotion = true };
            using var h = new WorkspaceHarness(config: config);
            using var source = new Form();
            h.Api.AddWindow(source.Handle, 100, 100, 800, 600, pid: 7, title: "Running project");
            h.Workspace.Start();
            var dwm = new FakeDwmThumbnailApi();
            using var overview = new OverviewManager(h.Canvas, h.Wm, h.Api, h.Input, config, gestureClock: h.Clock,
                dwm: dwm, interactionApi: new OverviewWindowInteractionTests.PointerApi());
            overview.AttachPlaceholderWorkspace(h.Workspace); overview.TransitionTo(OverviewMode.Zooming);
            var pass = (OverviewOverlay)Control.FromHandle(overview.MonitorHandles[0])!;
            pass.OnWheel!(pass, 0, 0, (int)((zoom - 1) * 1200), false, false, false);
            overview.RefreshZoneLayout();
            var card = h.Workspace.GetOverviewSnapshot().RepresentedWindows[source.Handle];
            Assert.True(overview.ShowWindowPeek(pass, card));
            Assert.Contains(dwm.Registrations, r => r.Source == source.Handle && r.Destination == overview.PeekWindow!.Handle);
            overview.HandleEscape();
            h.Workspace.CloseToPlaceholder(h.Workspace.GetOverviewSnapshot().ItemIds[source.Handle]);
            h.Api.Windows.Remove(source.Handle);
            h.Input.RaiseWindowDestroyed(source.Handle);
            Assert.False(overview.ShowWindowPeek(pass, h.Cards.Created.Last().Handle));
            Assert.Empty(h.Launcher.Opened);
        });

    [Fact]
    [Trait("Category", "NativePreview")]
    public void NativeAppButtonReceivesPeekClicksWithoutMovingOrResizingTheSource()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var h = new Rig(nativeInteraction: true);
            using var button = new Button { Dock = DockStyle.Fill, Text = "Run next agent step",
                BackColor = Color.FromArgb(30, 65, 80), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            h.Source.Controls.Add(button); h.Source.PerformLayout();
            int clicked = 0; button.Click += (_, _) => clicked++;
            var bounds = h.Source.Bounds; var camera = h.Overview.ViewCamera;
            var foreground = PInvoke.GetForegroundWindow();
            Assert.True(h.Overview.ShowWindowPeek(h.Pass, h.Source.Handle));
            Assert.Equal(foreground, PInvoke.GetForegroundWindow());
            var rendered = System.Diagnostics.Stopwatch.StartNew();
            while (rendered.Elapsed < TimeSpan.FromMilliseconds(200)) { Application.DoEvents(); Thread.Yield(); }
            var peek = h.Overview.PeekWindow!;
            using (var image = new Bitmap(peek.Width, peek.Height))
            {
                using var graphics = Graphics.FromImage(image);
                graphics.CopyFromScreen(peek.Location, Point.Empty, image.Size);
                var folder = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TestResults");
                Directory.CreateDirectory(folder);
                image.Save(Path.Combine(folder, "canvas-peek-native-content.png"));
            }
            h.Overview.BeginPeekInteraction();
            Assert.True(h.Overview.PeekWorking);
            Assert.Equal((HWND)h.Source.Handle, PInvoke.GetForegroundWindow());
            h.Dispatch(new(h.ContentPoint, MouseButtons.Left, true, false));
            h.Dispatch(new(h.ContentPoint, MouseButtons.Left, false, true));
            var timeout = System.Diagnostics.Stopwatch.StartNew();
            while (clicked == 0 && timeout.Elapsed < TimeSpan.FromSeconds(2))
            { Application.DoEvents(); Thread.Yield(); }
            Assert.Equal(1, clicked);
            Assert.Equal(bounds, h.Source.Bounds); Assert.Equal(camera, h.Overview.ViewCamera);
            h.Dispatch(new(h.ContentPoint, MouseButtons.Left, true, false));
            h.Dispatch(new(new(peek.Left - 20, h.ContentPoint.Y), MouseButtons.Left, false, true));
            Application.DoEvents();
            Assert.Equal(1, clicked); // Dragging out must not invoke the button.
            h.Dispatch(new(h.ContentPoint, MouseButtons.Left, true, false));
            h.Return();
            Application.DoEvents(); Assert.Equal(1, clicked); // Cancelling must not invoke either.
            Assert.Equal(bounds, h.Source.Bounds); Assert.False(h.Overview.WindowInteractionActive);
        });

    [Fact]
    [Trait("Category", "NativePreview")]
    public void PeekChromeRendersReadableActionsInPreviewAndWorkingStates()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var h = new Rig(); Assert.True(h.Overview.ShowWindowPeek(h.Pass, h.Source.Handle));
            var folder = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TestResults");
            Directory.CreateDirectory(folder);
            void Save(string state)
            {
                var peek = h.Overview.PeekWindow!;
                Assert.All(peek.Controls.OfType<Button>(), b => Assert.True(peek.ClientRectangle.Contains(b.Bounds)));
                using var image = new Bitmap(peek.Width, peek.Height);
                peek.DrawToBitmap(image, peek.ClientRectangle);
                image.Save(Path.Combine(folder, "canvas-peek-" + state + ".png"));
                Assert.NotEqual(image.GetPixel(1, 1), image.GetPixel(10, peek.Height / 2));
            }
            Save("preview"); h.Overview.BeginPeekInteraction(); Save("working");
        });
}
