using System;
using System.Threading;
using Windows.Win32.UI.WindowsAndMessaging;
using Xunit;

namespace CanvasDesktop.Tests;

public class DesktopViewportTests
{
    [Fact]
    public void Fit_PreservesContainedWindowOnNegativeOriginMonitor()
    {
        var rect = new WindowRect(-1700, 100, 800, 600);
        Assert.Equal(rect, DesktopViewport.Fit(rect, new[] { (0, 0, 1920, 1040), (-1920, 0, 1920, 1080) }));
    }

    [Fact]
    public void Fit_UsesNearestMonitorAndExcludesTaskbar()
    {
        Assert.Equal(new WindowRect(-1920, 440, 800, 600),
            DesktopViewport.Fit(new WindowRect(-6000, 2000, 800, 600),
                new[] { (0, 0, 1920, 1040), (-1920, 0, 1920, 1040) }));
    }

    [Fact]
    public void Fit_ChoosesLargestOverlapBeforeDistance()
    {
        Assert.Equal(new WindowRect(0, 100, 1000, 600),
            DesktopViewport.Fit(new WindowRect(-100, 100, 1000, 600),
                new[] { (-1920, 0, 1920, 1040), (0, 0, 1920, 1040) }));
    }

    [Fact]
    public void Fit_ResizesOversizedWindowsAndHandlesMissingScreens()
    {
        Assert.Equal(new WindowRect(0, 0, 1920, 1080),
            DesktopViewport.Fit(new WindowRect(5000, -5000, 4000, 3000), Array.Empty<(int, int, int, int)>()));
        Assert.Equal(new WindowRect(0, 0, 1920, 1080),
            DesktopViewport.Fit(new WindowRect(5000, -5000, 4000, 3000), new[] { (1, 1, 0, 0) }));
    }

    [Theory]
    [InlineData(int.MaxValue, int.MinValue)]
    [InlineData(int.MinValue, int.MaxValue)]
    public void Fit_HandlesExtremeCoordinatesWithoutOverflow(int x, int y)
    {
        var rect = DesktopViewport.Fit(new WindowRect(x, y, 800, 600), new[] { (-1920, 0, 1920, 1040) });
        Assert.InRange(rect.X, -1920, -800);
        Assert.InRange(rect.Y, 0, 440);
    }

    [Fact]
    public void Exit_UnclipsAndFitsRealWindows_WithoutResettingCamera()
    {
        var canvas = new Canvas(); var api = new FakeWindowApi(); var input = new FakeInputRouter();
        using var wm = new WindowManager(canvas, api, new FakeAppConfig(), input);
        var handle = (IntPtr)123;
        api.AddWindow(handle, 5000, 5000, 800, 600);
        canvas.SetWindow(handle, 9000, -7000, 800, 600);
        canvas.SetCamera(2000, -3000);
        wm.Reproject();
        Assert.Contains(handle, api.ClippedWindows);
        wm.RestoreDesktopViewport();
        var rect = api.GetWindowRect(handle);
        Assert.InRange(rect.x, 0, 1120);
        Assert.InRange(rect.y, 0, 480);
        Assert.Empty(api.ClippedWindows);
        Assert.Empty(canvas.Windows);
        Assert.Equal(2000, canvas.CamX);

        int calls = api.SetPositionCalls.Count;
        input.RaiseWindowShown(handle);
        canvas.SetCamera(9000, 9000);
        canvas.Commit();
        wm.RestoreDesktopViewport(); // idempotent
        Assert.Empty(canvas.Windows);
        Assert.Equal(calls, api.SetPositionCalls.Count);
    }

    [Fact]
    public void Exit_PreservesVisiblePinnedWindowAndDoesNotMoveUnmanagedOrOwnWindows()
    {
        var canvas = new Canvas(); var api = new FakeWindowApi();
        using var wm = new WindowManager(canvas, api, new FakeAppConfig(), new FakeInputRouter());
        api.AddWindow((IntPtr)1, 200, 100, 800, 600);
        canvas.SetWindow((IntPtr)1, 5000, 5000, 800, 600);
        canvas.SetPinnedToScreen((IntPtr)1, true);
        api.AddWindow((IntPtr)2, 5000, 5000, 800, 600); // not enrolled
        api.AddWindow((IntPtr)3, 5000, 5000, 800, 600, pid: (uint)Environment.ProcessId);
        canvas.SetWindow((IntPtr)3, 5000, 5000, 800, 600);
        wm.RestoreDesktopViewport();
        Assert.Empty(api.SetPositionCalls);
        Assert.Equal(200, api.GetWindowRect((IntPtr)1).x);
        Assert.Equal(5000, api.GetWindowRect((IntPtr)2).x);
        Assert.Equal(5000, api.GetWindowRect((IntPtr)3).x);
    }

    [Theory]
    [InlineData((int)WindowState.Minimized, (int)WINDOW_STYLE.WS_MINIMIZE)]
    [InlineData((int)WindowState.Maximized, (int)WINDOW_STYLE.WS_MAXIMIZE)]
    public void Exit_PreservesNativeStateAndRepairsRestoreBounds(int state, int style)
    {
        var canvas = new Canvas(); var api = new FakeWindowApi();
        using var wm = new WindowManager(canvas, api, new FakeAppConfig(), new FakeInputRouter());
        api.AddWindow((IntPtr)1, -5000, 6000, 800, 600, style: style);
        canvas.SetWindow((IntPtr)1, -5000, 6000, 800, 600);
        wm.RestoreDesktopViewport();
        var call = Assert.Single(api.NativeStateCalls);
        Assert.Equal((WindowState)state, call.state);
        Assert.Equal(new WindowRect(0, 480, 800, 600), call.rect);
        Assert.Equal(style, api.GetWindowStyle((IntPtr)1));
    }

    [Fact]
    public void Exit_RestoresPreviouslyVisitedDesktopButSkipsRecycledAndClosedHandles()
    {
        var canvas = new Canvas(); var api = new FakeWindowApi();
        using var wm = new WindowManager(canvas, api, new FakeAppConfig(), new FakeInputRouter());
        for (int n = 1; n <= 3; n++)
        {
            api.AddWindow((IntPtr)n, 0, 0, 800, 600, pid: (uint)(100 + n));
            wm.RegisterWindow((IntPtr)n);
            canvas.SetWindow((IntPtr)n, 5000, 6000, 800, 600);
        }
        wm.Reset(); // switch away: keep old desktop identities for final recovery
        api.Windows[(IntPtr)2].ProcessId = 202;
        api.Windows[(IntPtr)3].Visible = false;
        wm.RestoreDesktopViewport();
        Assert.Equal(1120, api.GetWindowRect((IntPtr)1).x);
        Assert.Equal(5000, api.GetWindowRect((IntPtr)2).x);
        Assert.Equal(5000, api.GetWindowRect((IntPtr)3).x);
    }

    [Fact]
    public void Exit_StopsAsyncProjectionBeforeFinalNativeRecovery()
    {
        var canvas = new Canvas(); var api = new FakeWindowApi();
        using var wm = new WindowManager(canvas, api, new FakeAppConfig { DisableGreedyDraw = true },
            new FakeInputRouter(), useAsyncProjection: true);
        api.AddWindow((IntPtr)1, 0, 0, 800, 600);
        canvas.SetWindow((IntPtr)1, 9000, 9000, 800, 600);
        wm.Reproject();
        wm.RestoreDesktopViewport();
        var rect = api.GetWindowRect((IntPtr)1);
        Assert.InRange(rect.x, 0, 1120);
        Assert.InRange(rect.y, 0, 480);
        canvas.Pan(5000, 5000);
        canvas.Commit();
        Assert.Equal(rect, api.GetWindowRect((IntPtr)1));
    }
}
