using CanvasDesktop;
using System.Windows.Forms;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class OverviewThumbnailReuseTests
{
    private sealed class Rig : IDisposable
    {
        public readonly OverviewOverlay Pass = new(Screen.PrimaryScreen!);
        public readonly Canvas Canvas = new();
        public readonly FakeWindowApi Api = new();
        public readonly FakeScreens Screens = new();
        public readonly FakeDwmThumbnailApi Dwm = new();
        public readonly OverviewCamera Camera;
        public readonly OverviewWindowList Windows;
        public readonly OverviewThumbnails Thumbnails;
        public bool Cached;
        public IntPtr CachedSource = (IntPtr)987;
        public Rig()
        {
            Camera = new(Screens);
            Windows = new(Canvas, Api);
            Thumbnails = new([Pass], Windows, Camera, new(), Api, Screens,
                handle => Cached && handle == (IntPtr)1, _ => CachedSource, Dwm);
        }
        public void Add(int id)
        {
            var b = Pass.Screen.Bounds;
            Api.AddWindow((IntPtr)id, b.X + 20 * id, b.Y + 20, 100, 80);
            Canvas.SetWindow((IntPtr)id, b.X + 20 * id, b.Y + 20, 100, 80);
        }
        public void Target(params int[] bottomToTop)
        {
            Api.EnumOrder = bottomToTop.Reverse().Select(id => (IntPtr)id).ToList();
            Windows.Refresh();
            Thumbnails.Reconcile();
        }
        public int Registrations(int source) => Dwm.Registrations.Count(r => r.Source == (IntPtr)source);
        private static bool IsApplicationSource(IntPtr source) => source.ToInt64() is >= 1 and <= 3 or 987;
        public IEnumerable<IntPtr> ApplicationSources => Dwm.Sources.Where(IsApplicationSource);
        public IEnumerable<IntPtr> Stack => Dwm.Stack(Pass.Handle).Where(IsApplicationSource);
        public void Dispose() { Thumbnails.Hide(); Pass.Dispose(); }
    }

    [Fact]
    public void GestureCyclesHideAndReuseWithoutRegisteringOrPushingHiddenGeometry()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var h = new Rig(); h.Add(1); h.Add(2); h.Target(1, 2);
            int registrations = h.Dwm.Registrations.Count;
            for (int burst = 0; burst < 10; burst++)
            {
                h.Thumbnails.SimplifyDuringMotion = true;
                h.Thumbnails.Reconcile();
                Assert.Empty(h.Dwm.VisibleSources);
                Assert.False(h.Thumbnails.HasVisibleSource((IntPtr)1));
                int updates = h.Dwm.Updates.Count;
                h.Camera.SetTo(burst + 1, 0, .8);
                h.Thumbnails.Reconcile();
                Assert.Equal(updates, h.Dwm.Updates.Count);
                h.Thumbnails.SimplifyDuringMotion = false;
                h.Thumbnails.Reconcile();
                Assert.True(h.Thumbnails.HasLiveSource((IntPtr)1));
                Assert.True(h.Thumbnails.HasLiveSource((IntPtr)2));
            }
            Assert.Equal(registrations, h.Dwm.Registrations.Count);
            Assert.Empty(h.Dwm.Released);
            Assert.Equal(new[] { (IntPtr)1, (IntPtr)2 }, h.Stack);
            Assert.Empty(h.Api.SetPositionCalls);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnavailableLowerSourceDoesNotReregisterHealthyHigherSources(bool missingCache)
        => WorkspaceViewTests.OnSta(() =>
        {
            using var h = new Rig(); h.Add(1); h.Add(2); h.Add(3);
            if (missingCache) { h.Cached = true; h.CachedSource = IntPtr.Zero; }
            else h.Dwm.FailedSources.Add((IntPtr)1);
            h.Target(1, 2, 3);
            for (int frame = 0; frame < 30; frame++) h.Thumbnails.Reconcile();
            Assert.Equal(1, h.Registrations(2)); Assert.Equal(1, h.Registrations(3));
            Assert.Empty(h.Dwm.Released);
            Assert.False(h.Thumbnails.HasVisibleSource((IntPtr)1));
            h.Dwm.FailedSources.Clear(); h.CachedSource = (IntPtr)987;
            h.Thumbnails.Reconcile();
            Assert.True(h.Thumbnails.HasVisibleSource((IntPtr)1));
            Assert.Equal(new[] { missingCache ? (IntPtr)987 : (IntPtr)1, (IntPtr)2, (IntPtr)3 }, h.Stack);
            Assert.Equal(2, h.Registrations(2)); Assert.Equal(2, h.Registrations(3));
            h.Thumbnails.Reconcile();
            Assert.Equal(2, h.Registrations(2)); Assert.Equal(2, h.Registrations(3));
        });

    [Fact]
    public void ChangedSourceFailureRemovesStaleSourceButRetainsHealthyNeighbours()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var h = new Rig(); h.Add(1); h.Add(2); h.Add(3); h.Target(1, 2, 3);
            h.Cached = true; h.Dwm.FailedSources.Add((IntPtr)987);
            h.Thumbnails.Reconcile();
            Assert.DoesNotContain((IntPtr)1, h.Dwm.Sources);
            for (int frame = 0; frame < 30; frame++) h.Thumbnails.Reconcile();
            Assert.Equal(1, h.Registrations(2)); Assert.Equal(1, h.Registrations(3));
            Assert.Single(h.Dwm.Released);
            h.Dwm.FailedSources.Clear(); h.Thumbnails.Reconcile();
            Assert.Equal(new[] { (IntPtr)987, (IntPtr)2, (IntPtr)3 }, h.Stack);
            Assert.Equal(2, h.Registrations(2)); Assert.Equal(2, h.Registrations(3));
        });

    [Fact]
    public void HiddenEntriesLeavingTheViewAreReleasedAndNewHiddenEntriesAreNotRegistered()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var h = new Rig(); h.Add(1); h.Target(1);
            h.Thumbnails.SimplifyDuringMotion = true; h.Thumbnails.Reconcile();
            var b = h.Pass.Screen.Bounds;
            h.Canvas.SetWindow((IntPtr)1, b.Right + 100, b.Top + 20, 100, 80);
            h.Add(2); h.Target(1, 2);
            Assert.Empty(h.ApplicationSources);
            Assert.Single(h.Dwm.Released);
            Assert.Equal(0, h.Registrations(2));
            h.Thumbnails.SimplifyDuringMotion = false; h.Thumbnails.Reconcile();
            Assert.Equal(1, h.Registrations(2));
            Assert.True(h.Thumbnails.HasLiveSource((IntPtr)2));
        });

    [Fact]
    public void FailedHideFallsBackToUnregisterAndRestoresOnTheNextLiveFrame()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var h = new Rig(); h.Add(1); h.Target(1);
            h.Dwm.FailedHides.Add((IntPtr)1);
            h.Thumbnails.SimplifyDuringMotion = true; h.Thumbnails.Reconcile();
            Assert.Empty(h.ApplicationSources); Assert.Empty(h.Dwm.VisibleSources);
            h.Thumbnails.Reconcile(); Assert.Equal(1, h.Registrations(1));
            h.Thumbnails.SimplifyDuringMotion = false; h.Thumbnails.Reconcile();
            Assert.Equal(2, h.Registrations(1)); Assert.True(h.Thumbnails.HasLiveSource((IntPtr)1));
        });

    [Fact]
    public void BringingAlreadyTopmostSourceForwardDoesNotRebindAndReordersRetainCorrectStack()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var h = new Rig(); h.Add(1); h.Add(2); h.Add(3); h.Target(1, 2, 3);
            h.Thumbnails.BringToFront((IntPtr)3);
            Assert.Equal(1, h.Registrations(3));
            h.Target(1, 3, 2);
            Assert.Equal(new[] { (IntPtr)1, (IntPtr)3, (IntPtr)2 }, h.Stack);
            Assert.Equal(1, h.Registrations(1)); Assert.Equal(1, h.Registrations(3)); Assert.Equal(2, h.Registrations(2));
            h.Target(2, 1, 3);
            Assert.Equal(new[] { (IntPtr)2, (IntPtr)1, (IntPtr)3 }, h.Stack);
            Assert.True(h.Thumbnails.HasLiveSource((IntPtr)1));
        });
}
