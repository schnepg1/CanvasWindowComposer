using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using CanvasDesktop;
using CanvasDesktop.CanvasView;
using Xunit;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class OverviewThumbnailSettleTests
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
        public readonly HashSet<IntPtr> CachedHandles = new();
        public IntPtr[] PreparedLiveSources = [];

        public CanvasCamera CurrentCamera => new(Camera.X, Camera.Y, Camera.Zoom);

        public Rig()
        {
            Camera = new(Screens);
            Windows = new(Canvas, Api);
            Thumbnails = new([Pass], Windows, Camera, new(), Api, Screens,
                handle => CachedHandles.Contains(handle),
                handle => (IntPtr)(handle.ToInt64() + 10000),
                Dwm, prepareLiveSources: handles => PreparedLiveSources = handles.ToArray());
        }

        public void AddWindow(int id, int worldX, int worldY, int width = 50, int height = 40, bool pinned = false)
        {
            var hwnd = (IntPtr)id;
            Api.AddWindow(hwnd, worldX, worldY, width, height);
            Canvas.SetWindow(hwnd, worldX, worldY, width, height);
            if (pinned)
            {
                Canvas.SetPinnedToScreen(hwnd, true);
            }
        }

        public void RefreshWindows(bool includeScreenFixed = false)
        {
            Api.EnumOrder = Canvas.Windows.Keys.ToList();
            Windows.Refresh(includeScreenFixedWindows: includeScreenFixed);
        }

        public IEnumerable<IntPtr> VisibleAppSources => Dwm.VisibleSources.Where(s => s.ToInt64() >= 1);

        public void Dispose()
        {
            Thumbnails.Hide();
            Pass.Dispose();
        }
    }

    [Fact]
    public void SmallWindowCountAwakensAllImmediately()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var r = new Rig();
            for (int i = 1; i <= 5; i++)
            {
                r.AddWindow(i, i * 60, 50);
            }
            r.RefreshWindows();

            // Motion active
            r.Thumbnails.SimplifyDuringMotion = true;
            r.Thumbnails.Reconcile(r.CurrentCamera);
            Assert.Empty(r.VisibleAppSources);

            // Motion stops, begin settle with focal point at center
            var focal = new CanvasPoint(200, 50);
            r.Thumbnails.SimplifyDuringMotion = false;
            r.Thumbnails.BeginSettle(focal, r.CurrentCamera);

            // Small window count (5 <= 8) should bypass settle staggering completely
            Assert.False(r.Thumbnails.IsSettling);

            r.Thumbnails.Reconcile(r.CurrentCamera);
            Assert.Equal(5, r.VisibleAppSources.Count());
        });

    [Fact]
    public void LargeWindowCountStaggersAwakeningByDistance()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var r = new Rig();
            var focal = new CanvasPoint(200, 200);

            // Create 24 windows placed along a line with increasing distance from focal
            // IDs 1..24 placed at x = 200 + i * 20
            for (int i = 1; i <= 24; i++)
            {
                r.AddWindow(i, 200 + i * 20, 200, width: 15, height: 15);
            }
            r.RefreshWindows();

            // Motion active
            r.Thumbnails.SimplifyDuringMotion = true;
            r.Thumbnails.Reconcile(r.CurrentCamera);
            Assert.Empty(r.VisibleAppSources);

            // Motion stops -> Frame 0
            r.Thumbnails.SimplifyDuringMotion = false;
            r.Thumbnails.BeginSettle(focal, r.CurrentCamera);

            Assert.True(r.Thumbnails.IsSettling);
            Assert.Equal(0, r.Thumbnails.SettleBatchIndex);

            r.Thumbnails.Reconcile(r.CurrentCamera);
            // Frame 0 should awaken exactly closest 8 windows (IDs 1..8)
            Assert.Equal(8, r.VisibleAppSources.Count());
            for (int id = 1; id <= 8; id++)
            {
                Assert.Contains((IntPtr)id, r.VisibleAppSources);
            }
            for (int id = 9; id <= 24; id++)
            {
                Assert.DoesNotContain((IntPtr)id, r.VisibleAppSources);
            }

            // Frame 1
            bool moreBatches = r.Thumbnails.AdvanceSettle();
            Assert.True(moreBatches);
            Assert.True(r.Thumbnails.IsSettling);
            Assert.Equal(1, r.Thumbnails.SettleBatchIndex);

            r.Thumbnails.Reconcile(r.CurrentCamera);
            // Frame 1 should now have 16 windows visible (IDs 1..16)
            Assert.Equal(16, r.VisibleAppSources.Count());
            for (int id = 1; id <= 16; id++)
            {
                Assert.Contains((IntPtr)id, r.VisibleAppSources);
            }
            for (int id = 17; id <= 24; id++)
            {
                Assert.DoesNotContain((IntPtr)id, r.VisibleAppSources);
            }

            // Frame 2
            moreBatches = r.Thumbnails.AdvanceSettle();
            Assert.False(moreBatches); // Finished all batches
            Assert.False(r.Thumbnails.IsSettling);

            r.Thumbnails.Reconcile(r.CurrentCamera);
            // Frame 2: all 24 windows visible
            Assert.Equal(24, r.VisibleAppSources.Count());
            for (int id = 1; id <= 24; id++)
            {
                Assert.Contains((IntPtr)id, r.VisibleAppSources);
            }
        });

    [Fact]
    public void SettlePreservesExistingLiveSourcesWhileStaggeringNewOnes()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var r = new Rig();
            var focal = new CanvasPoint(200, 200);
            var alreadyLive = Enumerable.Range(1, 12).Select(id => (IntPtr)id).ToArray();
            for (int id = 1; id <= 12; id++)
                r.AddWindow(id, 490 + id * 15, 200, width: 15, height: 15);
            r.RefreshWindows();
            r.Thumbnails.Reconcile(r.CurrentCamera);
            Assert.Equal(12, r.VisibleAppSources.Count());

            for (int id = 13; id <= 24; id++)
                r.AddWindow(id, 200 + (id - 13) * 15, 200, width: 15, height: 15);
            r.RefreshWindows();
            r.Thumbnails.BeginSettle(focal, r.CurrentCamera);
            Assert.True(r.Thumbnails.IsSettling);
            r.Thumbnails.Reconcile(r.CurrentCamera);

            Assert.Equal(20, r.VisibleAppSources.Count());
            Assert.All(alreadyLive, handle => Assert.Contains(handle, r.VisibleAppSources));
            Assert.All(alreadyLive, handle => Assert.Contains(handle, r.PreparedLiveSources));

            Assert.False(r.Thumbnails.AdvanceSettle());
            r.Thumbnails.Reconcile(r.CurrentCamera);
            Assert.Equal(24, r.VisibleAppSources.Count());
        });

    [Fact]
    public void ScreenFixedAndCachedWindowsBypassStaggering()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var r = new Rig();
            var focal = new CanvasPoint(200, 200);

            // Place all windows within screen bounds (e.g. 0..800)
            // Add 16 normal windows
            for (int i = 1; i <= 16; i++)
            {
                r.AddWindow(i, 200 + i * 20, 200, width: 15, height: 15);
            }
            // Pinned windows (IDs 101, 102) within screen bounds
            r.AddWindow(101, 30, 30, width: 20, height: 20, pinned: true);
            r.AddWindow(102, 60, 30, width: 20, height: 20, pinned: true);

            // Cached windows (IDs 201, 202) within screen bounds
            r.AddWindow(201, 30, 60, width: 20, height: 20);
            r.AddWindow(202, 60, 60, width: 20, height: 20);
            r.CachedHandles.Add((IntPtr)201);
            r.CachedHandles.Add((IntPtr)202);

            r.RefreshWindows(includeScreenFixed: true);

            r.Thumbnails.SimplifyDuringMotion = true;
            r.Thumbnails.Reconcile(r.CurrentCamera);

            // During motion, pinned and cached are already visible
            Assert.Contains((IntPtr)101, r.VisibleAppSources);
            Assert.Contains((IntPtr)102, r.VisibleAppSources);
            // Cached source uses mapped handle (+ 10000)
            Assert.Contains((IntPtr)10201, r.VisibleAppSources);
            Assert.Contains((IntPtr)10202, r.VisibleAppSources);

            // Motion stops, begin settle
            r.Thumbnails.SimplifyDuringMotion = false;
            r.Thumbnails.BeginSettle(focal, r.CurrentCamera);
            r.Thumbnails.Reconcile(r.CurrentCamera);

            // Frame 0: 8 normal windows + 2 pinned + 2 cached = 12 visible
            Assert.Equal(12, r.VisibleAppSources.Count());
            for (int id = 1; id <= 8; id++)
            {
                Assert.Contains((IntPtr)id, r.VisibleAppSources);
            }
            Assert.Contains((IntPtr)101, r.VisibleAppSources);
            Assert.Contains((IntPtr)102, r.VisibleAppSources);
            Assert.Contains((IntPtr)10201, r.VisibleAppSources);
            Assert.Contains((IntPtr)10202, r.VisibleAppSources);
        });

    [Fact]
    public void MotionInterruptsSettleCleanly()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var r = new Rig();
            var focal = new CanvasPoint(200, 200);

            for (int i = 1; i <= 20; i++)
            {
                r.AddWindow(i, 200 + i * 20, 200, width: 15, height: 15);
            }
            r.RefreshWindows();

            // Motion ceases, settle begins
            r.Thumbnails.SimplifyDuringMotion = false;
            r.Thumbnails.BeginSettle(focal, r.CurrentCamera);
            Assert.True(r.Thumbnails.IsSettling);

            r.Thumbnails.Reconcile(r.CurrentCamera);
            Assert.Equal(8, r.VisibleAppSources.Count());

            // User starts moving camera again mid-settle
            r.Thumbnails.SimplifyDuringMotion = true;

            // Settle should immediately be canceled
            Assert.False(r.Thumbnails.IsSettling);

            r.Thumbnails.Reconcile(r.CurrentCamera);
            // Motion simplification should suppress all normal thumbnails
            Assert.Empty(r.VisibleAppSources);
        });

    [Fact]
    public void CancelSettleRestoresImmediateVisibility()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var r = new Rig();
            var focal = new CanvasPoint(200, 200);

            for (int i = 1; i <= 20; i++)
            {
                r.AddWindow(i, 200 + i * 20, 200, width: 15, height: 15);
            }
            r.RefreshWindows();

            r.Thumbnails.SimplifyDuringMotion = false;
            r.Thumbnails.BeginSettle(focal, r.CurrentCamera);
            Assert.True(r.Thumbnails.IsSettling);

            r.Thumbnails.Reconcile(r.CurrentCamera);
            Assert.Equal(8, r.VisibleAppSources.Count());

            // Explicitly cancel settle while motion remains inactive
            r.Thumbnails.CancelSettle();
            Assert.False(r.Thumbnails.IsSettling);

            // Reconcile should now display all 20 windows
            r.Thumbnails.Reconcile(r.CurrentCamera);
            Assert.Equal(20, r.VisibleAppSources.Count());
        });
}
