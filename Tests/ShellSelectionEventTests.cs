using CanvasDesktop;
using System.Reflection;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class ShellSelectionEventTests
{
    [Fact]
    [Trait("Category", "NativeRendering")]
    public void DiagnosticsRetainHideEventsOutsideCanvasWithoutDispatchingAShowOrSelection()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var window = new System.Windows.Forms.Form();
            using var router = new Win32EventRouter();
            int shown = 0, selected = 0;
            router.WindowShown += _ => shown++;
            router.WindowSelected += _ => selected++;
            var dispatch = Callback(router);
            dispatch(default, PInvoke.EVENT_OBJECT_HIDE, (HWND)window.Handle,
                (int)OBJECT_IDENTIFIER.OBJID_WINDOW, 0, 0, 1000);
            dispatch(default, PInvoke.EVENT_OBJECT_SHOW, (HWND)window.Handle,
                (int)OBJECT_IDENTIFIER.OBJID_CLIENT, 1, 0, 1010);
            using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(router.Diagnostics));
            var events = json.RootElement.GetProperty("History").GetProperty("RecentEvents");
            Assert.Contains(events.EnumerateArray(), e => e.GetProperty("Event").GetUInt32() == PInvoke.EVENT_OBJECT_HIDE);
            Assert.DoesNotContain(events.EnumerateArray(), e => e.GetProperty("Event").GetUInt32() == PInvoke.EVENT_OBJECT_SHOW);
            Assert.Equal(0, shown); Assert.Equal(0, selected);
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void ReleasedAltTabSelectsOnForegroundWithoutWaitingForPoll()
        => WorkspaceViewTests.OnSta(() =>
        {
            IntPtr app = (IntPtr)42, shell = (IntPtr)43;
            var keyboard = new AltTabSelectionIntent();
            using var router = new Win32EventRouter(altTab: keyboard, canSelectWindow: window => window == app);
            var chosen = new List<IntPtr>();
            router.WindowSelected += chosen.Add;
            keyboard.Begin();
            var dispatch = Callback(router);
            dispatch(default, PInvoke.EVENT_SYSTEM_FOREGROUND, (HWND)app, 0, 0, 0, 1000);
            Assert.Empty(chosen);
            var generation = keyboard.Release()!.Value;
            dispatch(default, PInvoke.EVENT_SYSTEM_FOREGROUND, (HWND)shell, 0, 0, 0, 1010);
            Assert.Empty(chosen);
            dispatch(default, PInvoke.EVENT_SYSTEM_FOREGROUND, (HWND)app, 0, 0, 0, 1020);
            Assert.Equal(app, Assert.Single(chosen));
            Assert.False(keyboard.TryComplete(generation, out _));
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void TransientShellFocusDoesNotConsumeThePendingTaskbarClick()
        => WorkspaceViewTests.OnSta(() =>
        {
            IntPtr app = (IntPtr)42, shell = (IntPtr)43;
            var intent = new TaskbarSelectionIntent { Enabled = true };
            using var router = new Win32EventRouter(intent, canSelectWindow: window => window == app);
            var chosen = new List<IntPtr>();
            router.WindowSelected += chosen.Add;
            intent.Click(1000, app);
            var dispatch = Callback(router);
            dispatch(default, PInvoke.EVENT_SYSTEM_FOREGROUND, (HWND)shell, 0, 0, 0, 1010);
            Assert.Empty(chosen);
            dispatch(default, PInvoke.EVENT_SYSTEM_FOREGROUND, (HWND)app, 0, 0, 0, 1020);
            Assert.Equal(app, Assert.Single(chosen));
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void InvalidNativeSwitchTargetKeepsTheKeyboardFallbackAvailable()
        => WorkspaceViewTests.OnSta(() =>
        {
            IntPtr app = (IntPtr)42, shell = (IntPtr)43;
            var keyboard = new AltTabSelectionIntent();
            using var router = new Win32EventRouter(altTab: keyboard, canSelectWindow: window => window == app);
            var chosen = new List<IntPtr>();
            router.WindowSelected += chosen.Add;
            keyboard.Begin();
            var first = keyboard.Release()!.Value;
            Callback(router)(default, PInvoke.EVENT_SYSTEM_SWITCHEND, (HWND)shell, 0, 0, 0, 1000);
            Assert.Empty(chosen);
            Assert.True(keyboard.TryComplete(first, out bool selected));
            Assert.True(selected);
            keyboard.Begin();
            var second = keyboard.Release()!.Value;
            Callback(router)(default, PInvoke.EVENT_SYSTEM_SWITCHEND, (HWND)app, 0, 0, 0, 1100);
            Assert.Equal(app, Assert.Single(chosen));
            Assert.False(keyboard.TryComplete(second, out _));
        });

    private static WINEVENTPROC Callback(Win32EventRouter router)
        => (WINEVENTPROC)typeof(Win32EventRouter).GetField("_winEventProc", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(router)!;
}
