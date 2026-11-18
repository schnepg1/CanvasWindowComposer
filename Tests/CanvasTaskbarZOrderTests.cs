using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Windows.Interop;
using CanvasDesktop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class CanvasTaskbarZOrderTests
{
    [Fact]
    [Trait("Category", "NativeRendering")]
    public void CanvasSurfacesPanelsAndMinimapStayBelowTaskbarAcrossActivationPanelRaiseAndConfigToggle()
        => WorkspaceViewTests.OnSta(() =>
        {
            IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
            Assert.NotEqual(IntPtr.Zero, taskbar);
            Assert.True(IsWindowVisible(taskbar), "Expected Shell_TrayWnd to be visible on desktop.");
            Assert.True(CanvasZOrder.IsTopmostWindow(taskbar), "Expected Shell_TrayWnd to have WS_EX_TOPMOST.");

            string tempDir = Path.Combine(Path.GetTempPath(), "canvas-zorder-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var canvas = new Canvas();
                var api = new FakeWindowApi();
                var input = new FakeInputRouter();
                var config = new FakeAppConfig { EnableStartupIntro = false, ShowTaskbarInCanvas = true };
                var desktops = new FakeVirtualDesktops();
                using var wm = new WindowManager(canvas, api, config, input, new FakeClock());
                using var overview = new OverviewManager(canvas, wm, api, input, config, dwm: new FakeDwmThumbnailApi());
                using var stateCache = new DesktopStateCache(canvas, wm, overview, desktops);
                overview.AttachAnnotations(desktops, Path.Combine(tempDir, "annotations.json"), stateCache);
                using var minimap = new MinimapOverlay(canvas, input, stateCache, overview: overview);

                overview.TransitionTo(OverviewMode.Zooming);
                minimap.NotifyCanvasChanged();
                Application.DoEvents();

                var passHandle = overview.MonitorHandles[0];
                var pass = (OverviewOverlay)Control.FromHandle(passHandle)!;
                var annotations = (OverviewAnnotationWindow)typeof(OverviewOverlay)
                    .GetField("_annotations", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(pass)!;
                var annotationsHandle = new WindowInteropHelper(annotations).Handle;
                var zoomControls = pass.ZoomControls;
                var dock = overview.UnifiedDock;
                Assert.NotNull(dock);
                Assert.True(dock!.IsDockVisible);
                var dockHandle = new WindowInteropHelper(dock).Handle;
                Assert.NotEqual(IntPtr.Zero, dockHandle);
                Assert.True(IsWindowVisible(dockHandle));
                Assert.True(minimap.Visible);
                var minimapHandle = minimap.Handle;

                void AssertAllBelowTaskbarsAndOrdered()
                {
                    var taskbars = CollectVisibleTopmostTaskbars();
                    Assert.Contains(taskbar, taskbars);

                    Assert.True(CanvasZOrder.IsTopmostWindow(passHandle));
                    Assert.True(CanvasZOrder.IsTopmostWindow(annotationsHandle));
                    Assert.True(CanvasZOrder.IsTopmostWindow(dockHandle));
                    Assert.True(CanvasZOrder.IsTopmostWindow(minimapHandle));

                    foreach (var tb in taskbars)
                    {
                        Assert.True(CanvasZOrder.IsWindowAbove(tb, passHandle), "OverviewOverlay must be below taskbar.");
                        Assert.True(CanvasZOrder.IsWindowAbove(tb, annotationsHandle), "OverviewAnnotationWindow must be below taskbar.");
                        if (zoomControls is { Visible: true, IsHandleCreated: true })
                            Assert.True(CanvasZOrder.IsWindowAbove(tb, zoomControls.Handle), "CanvasZoomControls must be below taskbar.");
                        Assert.True(CanvasZOrder.IsWindowAbove(tb, dockHandle), "UnifiedDockWindow must be below taskbar.");
                        Assert.True(CanvasZOrder.IsWindowAbove(tb, minimapHandle), "MinimapOverlay must be below taskbar.");
                    }

                    Assert.True(CanvasZOrder.IsWindowAbove(annotationsHandle, passHandle), "OverviewAnnotationWindow must be above OverviewOverlay.");
                    Assert.True(CanvasZOrder.IsWindowAbove(dockHandle, passHandle), "UnifiedDockWindow must be above OverviewOverlay.");
                    Assert.True(CanvasZOrder.IsWindowAbove(dockHandle, annotationsHandle), "UnifiedDockWindow must be above OverviewAnnotationWindow.");
                    Assert.True(CanvasZOrder.IsWindowAbove(minimapHandle, dockHandle), "MinimapOverlay must be above UnifiedDockWindow.");
                    Assert.True(CanvasZOrder.IsWindowAbove(minimapHandle, passHandle), "MinimapOverlay must be above OverviewOverlay.");
                }

                // 1. Initial entry into Zooming with ShowTaskbarInCanvas = true.
                AssertAllBelowTaskbarsAndOrdered();

                // 2. Activate OverviewOverlay and raise Canvas panels; must remain strictly below taskbar.
                pass.Activate();
                Application.DoEvents();
                InvokeRaiseCanvasPanels(overview);
                Application.DoEvents();
                AssertAllBelowTaskbarsAndOrdered();

                // 3. Toggle ShowTaskbarInCanvas = false -> Canvas surfaces move above Shell_TrayWnd.
                config.ShowTaskbarInCanvas = false;
                overview.RefreshConfig();
                Application.DoEvents();
                Assert.True(CanvasZOrder.IsWindowAbove(passHandle, taskbar), "OverviewOverlay must be above Shell_TrayWnd when ShowTaskbarInCanvas is false.");
                Assert.True(CanvasZOrder.IsWindowAbove(dockHandle, taskbar), "UnifiedDockWindow must be above Shell_TrayWnd when ShowTaskbarInCanvas is false.");
                Assert.True(CanvasZOrder.IsWindowAbove(minimapHandle, taskbar), "MinimapOverlay must be above Shell_TrayWnd when ShowTaskbarInCanvas is false.");

                // 4. Toggle ShowTaskbarInCanvas = true -> everything returns strictly below taskbar.
                config.ShowTaskbarInCanvas = true;
                overview.RefreshConfig();
                Application.DoEvents();
                AssertAllBelowTaskbarsAndOrdered();
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void ShellTrayPopupShownBelowCanvasTriggersReanchorBelowPopup()
        => WorkspaceViewTests.OnSta(() =>
        {
            const string popupClass = "TopLevelWindowForOverflowXamlIsland";
            WindowProc procedure = DefWindowProc;
            var instance = GetModuleHandle(null);
            var definition = new WindowClass
            {
                Procedure = Marshal.GetFunctionPointerForDelegate(procedure),
                Instance = instance,
                Name = popupClass
            };
            bool registered = RegisterClass(ref definition) != 0;
            IntPtr popup = IntPtr.Zero;
            string tempDir = Path.Combine(Path.GetTempPath(), "canvas-zorder-popup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                const uint wsPopupVisible = 0x90000000;
                const uint wsExTopmostToolNoActivate = 0x08000088;
                popup = CreateWindowEx(wsExTopmostToolNoActivate, popupClass, "Test overflow popup", wsPopupVisible,
                    -30000, -30000, 320, 200, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
                Assert.NotEqual(IntPtr.Zero, popup);
                Assert.True(IsWindowVisible(popup));
                Assert.True(CanvasZOrder.IsTopmostWindow(popup));

                var canvas = new Canvas();
                var api = new FakeWindowApi();
                var input = new FakeInputRouter();
                var config = new FakeAppConfig { EnableStartupIntro = false, ShowTaskbarInCanvas = true };
                var desktops = new FakeVirtualDesktops();
                using var wm = new WindowManager(canvas, api, config, input, new FakeClock());
                using var overview = new OverviewManager(canvas, wm, api, input, config, dwm: new FakeDwmThumbnailApi());
                using var stateCache = new DesktopStateCache(canvas, wm, overview, desktops);
                overview.AttachAnnotations(desktops, Path.Combine(tempDir, "annotations.json"), stateCache);
                using var minimap = new MinimapOverlay(canvas, input, stateCache, overview: overview);

                overview.TransitionTo(OverviewMode.Zooming);
                minimap.NotifyCanvasChanged();
                Application.DoEvents();

                var passHandle = overview.MonitorHandles[0];
                var dockHandle = new WindowInteropHelper(overview.UnifiedDock!).Handle;
                var minimapHandle = minimap.Handle;

                var previews = (OverviewTaskbarPreviews)typeof(OverviewManager)
                    .GetField("_taskbarPreviews", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(overview)!;
                var callback = (WINEVENTPROC)typeof(OverviewTaskbarPreviews)
                    .GetField("_callback", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(previews)!;

                // Case A: Place the shell tray popup BELOW OverviewOverlay, then simulate EVENT_OBJECT_SHOW.
                Assert.True(SetWindowPos(popup, passHandle, 0, 0, 0, 0, 0x0013)); // SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE
                Assert.True(CanvasZOrder.IsWindowAbove(passHandle, popup));

                callback(default, PInvoke.EVENT_OBJECT_SHOW, (HWND)popup, (int)OBJECT_IDENTIFIER.OBJID_WINDOW, 0, 0, 0);
                Application.DoEvents();

                Assert.True(CanvasZOrder.IsWindowAbove(popup, passHandle), "OverviewOverlay must re-anchor below shell tray popup.");
                Assert.True(CanvasZOrder.IsWindowAbove(popup, dockHandle), "UnifiedDockWindow must re-anchor below shell tray popup.");
                Assert.True(CanvasZOrder.IsWindowAbove(popup, minimapHandle), "MinimapOverlay must re-anchor below shell tray popup.");
                Assert.True(CanvasZOrder.IsWindowAbove(minimapHandle, dockHandle));
                Assert.True(CanvasZOrder.IsWindowAbove(dockHandle, passHandle));

                // Case B: Place the shell tray popup BELOW ONLY MinimapOverlay (above UnifiedDock and OverviewOverlay).
                Assert.True(SetWindowPos(popup, minimapHandle, 0, 0, 0, 0, 0x0013));
                Assert.True(CanvasZOrder.IsWindowAbove(minimapHandle, popup));
                Assert.False(CanvasZOrder.IsWindowAbove(passHandle, popup));

                callback(default, PInvoke.EVENT_OBJECT_SHOW, (HWND)popup, (int)OBJECT_IDENTIFIER.OBJID_WINDOW, 0, 0, 0);
                Application.DoEvents();

                Assert.True(CanvasZOrder.IsWindowAbove(popup, minimapHandle), "MinimapOverlay alone above popup must also re-anchor below popup.");
                Assert.True(CanvasZOrder.IsWindowAbove(popup, dockHandle));
                Assert.True(CanvasZOrder.IsWindowAbove(popup, passHandle));
            }
            finally
            {
                if (popup != IntPtr.Zero) DestroyWindow(popup);
                if (registered) UnregisterClass(popupClass, instance);
                GC.KeepAlive(procedure);
                try { Directory.Delete(tempDir, true); } catch { }
            }
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void RegisteredPriorityTrayMenuStaysAboveCanvasAndMinimapEvenWhenTaskbarHidden()
        => WorkspaceViewTests.OnSta(() =>
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "canvas-zorder-menu-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var canvas = new Canvas();
                var api = new FakeWindowApi();
                var input = new FakeInputRouter();
                var config = new FakeAppConfig { EnableStartupIntro = false, ShowTaskbarInCanvas = false };
                var desktops = new FakeVirtualDesktops();
                using var wm = new WindowManager(canvas, api, config, input, new FakeClock());
                using var overview = new OverviewManager(canvas, wm, api, input, config, dwm: new FakeDwmThumbnailApi());
                using var stateCache = new DesktopStateCache(canvas, wm, overview, desktops);
                overview.AttachAnnotations(desktops, Path.Combine(tempDir, "annotations.json"), stateCache);
                using var minimap = new MinimapOverlay(canvas, input, stateCache, overview: overview);

                overview.TransitionTo(OverviewMode.Zooming);
                minimap.NotifyCanvasChanged();
                Application.DoEvents();

                var passHandle = overview.MonitorHandles[0];
                var dockHandle = new WindowInteropHelper(overview.UnifiedDock!).Handle;
                var minimapHandle = minimap.Handle;

                using var menu = new ContextMenuStrip();
                var sub = new ToolStripMenuItem("Submenu");
                sub.DropDownItems.Add("Child");
                menu.Items.Add("Settings");
                menu.Items.Add(sub);
                CanvasZOrder.RegisterPriorityMenu(menu);

                menu.Show(new Point(40, 40));
                Application.DoEvents();

                Assert.True(menu.Visible);
                Assert.True(CanvasZOrder.ActivePriorityPopupCount >= 1);
                IntPtr menuHandle = menu.Handle;

                InvokeRaiseCanvasPanels(overview);
                minimap.NotifyCanvasChanged();
                Application.DoEvents();

                Assert.True(CanvasZOrder.IsWindowAbove(menuHandle, passHandle), "Registered tray menu must stay above OverviewOverlay.");
                Assert.True(CanvasZOrder.IsWindowAbove(menuHandle, dockHandle), "Registered tray menu must stay above UnifiedDockWindow.");
                Assert.True(CanvasZOrder.IsWindowAbove(menuHandle, minimapHandle), "Registered tray menu must stay above MinimapOverlay.");

                menu.Close();
                Application.DoEvents();
                Assert.Equal(0, CanvasZOrder.ActivePriorityPopupCount);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        });

    private static void InvokeRaiseCanvasPanels(OverviewManager overview)
        => typeof(OverviewManager)
            .GetMethod("RaiseCanvasPanels", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(overview, null);

    private static List<IntPtr> CollectVisibleTopmostTaskbars()
    {
        var list = new List<IntPtr>();
        HWND cur = PInvoke.GetWindow(PInvoke.GetDesktopWindow(), GET_WINDOW_CMD.GW_CHILD);
        Span<char> className = stackalloc char[128];
        for (int i = 0; !cur.IsNull && i < 512; i++)
        {
            int ex = PInvoke.GetWindowLong(cur, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
            if ((ex & (int)WINDOW_EX_STYLE.WS_EX_TOPMOST) == 0) break;
            if (PInvoke.IsWindowVisible(cur))
            {
                int len = PInvoke.GetClassName(cur, className);
                if (len > 0 && CanvasZOrder.IsTaskbarClass(className[..len]))
                    list.Add(cur);
            }
            cur = PInvoke.GetWindow(cur, GET_WINDOW_CMD.GW_HWNDNEXT);
        }
        return list;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Style;
        public IntPtr Procedure;
        public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public string? Menu;
        public string Name;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);
    [DllImport("user32.dll", EntryPoint = "RegisterClassW", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClass(ref WindowClass definition);
    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(string name, IntPtr instance);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string name, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after,
        int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
}
