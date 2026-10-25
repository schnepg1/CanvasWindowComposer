using CanvasDesktop;
using System.Reflection;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class ShellSelectionEventTests
{
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
