using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using CanvasDesktop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using Xunit;

namespace CanvasDesktop.Tests;

public class SearchOverlayTests
{
    [Fact]
    public void SearchOverlayStaysOpenOnTransientDeactivate()
    {
        var canvas = new Canvas();
        var api = new FakeWindowApi();
        var input = new FakeInputRouter();
        var screens = new FakeScreens();
        var overview = new FakeOverviewController();
        using var wm = new WindowManager(canvas, api, new FakeAppConfig(), input);
        using var search = new SearchOverlay(canvas, wm, api, input, screens, overview);

        input.RaiseSearchHotkey();
        Assert.True(search.Visible);

        // Firing deactivate immediately after opening (e.g. within 300ms when releasing Alt+S)
        // should NOT close the overlay
        var onDeactivate = typeof(SearchOverlay).GetMethod("OnDeactivate", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(EventArgs)]);
        Assert.NotNull(onDeactivate);
        onDeactivate.Invoke(search, [EventArgs.Empty]);

        Assert.True(search.Visible);

        // After the 300ms guard period elapses, normal deactivation should close the overlay
        typeof(SearchOverlay).GetField("_openedTimestamp", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(search, Environment.TickCount64 - 500);
        onDeactivate.Invoke(search, [EventArgs.Empty]);

        Assert.False(search.Visible);
    }

    [Fact]
    public void SearchSelectionRoutesThroughOverviewNavigateToWindow()
    {
        var canvas = new Canvas();
        var api = new FakeWindowApi();
        var input = new FakeInputRouter();
        var screens = new FakeScreens();
        var overview = new FakeOverviewController();
        using var wm = new WindowManager(canvas, api, new FakeAppConfig(), input);
        using var search = new SearchOverlay(canvas, wm, api, input, screens, overview);

        IntPtr app = (IntPtr)42;
        api.AddWindow(app, 0, 0, 400, 300, pid: 1, title: "Test App");
        canvas.SetWindow(app, 5000, 5000, 400, 300);

        input.RaiseSearchHotkey();
        Assert.True(search.Visible);

        var selectCurrent = search.GetType().GetMethod("SelectCurrent", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(selectCurrent);
        selectCurrent.Invoke(search, null);

        Assert.False(search.Visible);
        Assert.Equal(app, Assert.Single(overview.NavigatedWindows));
    }

    [Fact]
    public void SearchOverlayInOverviewModeAssignsOwnerHandle()
    {
        var canvas = new Canvas();
        var api = new FakeWindowApi();
        var input = new FakeInputRouter();
        var screens = new FakeScreens();
        var overview = new FakeOverviewController();
        using var wm = new WindowManager(canvas, api, new FakeAppConfig(), input);
        using var search = new SearchOverlay(canvas, wm, api, input, screens, overview);

        // Simulate an overview monitor surface with a real Win32 HWND
        using var ownerForm = new Form();
        _ = ownerForm.Handle; // Ensure real HWND exists
        overview.Monitors.Add(ownerForm.Handle);
        overview.TransitionTo(OverviewMode.Zooming);

        input.RaiseSearchHotkey();
        Assert.True(search.Visible);

        // In Zooming mode, native owner should match the overview monitor handle
        var owner = SearchOverlay.GetWindowLongPtr(search.Handle, -8);
        Assert.Equal(ownerForm.Handle, owner);
    }
}
