using System.Diagnostics;
using System.Collections.Concurrent;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using CanvasDesktop;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class WindowInteractionNativeTests
{
    [Fact]
    [Trait("Category", "NativeRendering")]
    public void MouseHookMovesCursorButDoesNotLeakUnscaledMovesToCapturingApp()
        => WorkspaceViewTests.OnSta(() =>
        {
            Assert.True(PInvoke.GetCursorPos(out var original));
            try
            {
                var screen = Screen.PrimaryScreen!.WorkingArea;
                PInvoke.SetCursorPos(screen.X + screen.Width / 2, screen.Y + screen.Height / 2);
                Assert.True(PInvoke.GetCursorPos(out var start));
                using var app = new Form
                {
                    StartPosition = FormStartPosition.Manual,
                    Location = new(start.X - 50, start.Y - 50), Size = new(240, 160), TopMost = true
                };
                app.Show(); app.Activate(); app.Capture = true;
                Application.DoEvents();
                int nativeMoves = 0;
                app.MouseMove += (_, _) => nativeMoves++;
                var forwarded = new ConcurrentQueue<InteractionMouseEvent>();
                using (var hook = new WindowInteractionMouseHook(_ => true, forwarded.Enqueue, () => { }))
                {
                    // A tiny relative move exercises the real low-level hook.
                    // No clicks or keyboard input are injected into the desktop.
                    mouse_event(1, 4, 0, 0, UIntPtr.Zero);
                    PumpUntil(() => forwarded.Count > 0);
                    Assert.True(PInvoke.GetCursorPos(out var end));
                    Assert.NotEqual(start, end);
                    Assert.Equal(0, nativeMoves);
                }
                app.Capture = false;
            }
            finally { PInvoke.SetCursorPos(original.X, original.Y); }
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void NativeChildMappingDeliversClicksAndKeepsRealKeyboardFocus()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var app = new Form { StartPosition = FormStartPosition.Manual, Location = new(100, 100), Size = new(450, 300) };
            using var editor = new TextBox { Location = new(20, 20), Width = 250 };
            using var button = new Button { Location = new(20, 70), Text = "Test" };
            app.Controls.AddRange([editor, button]);
            app.Show();
            using var overlay = new OverviewOverlay(Screen.PrimaryScreen!);
            _ = overlay.Handle;
            overlay.WindowInteractionActive = true;
            overlay.SetModeStyle(false, true);
            var api = new Win32WindowInteractionApi();
            Assert.True(api.Activate(app.Handle));
            editor.Focus();
            Assert.True(editor.Focused);
            var point = button.PointToScreen(new(20, 12));
            Assert.True(api.TryResolveClient(app.Handle, point, out var child, out var local));
            Assert.Equal(button.Handle, child);
            Assert.Equal(new Point(20, 12), local);
            int clicked = 0; button.Click += (_, _) => clicked++;
            Assert.True(api.Post(child, 0x201, 1, local));
            Assert.True(api.Post(child, 0x202, 0, local));
            PumpUntil(() => clicked > 0);
            Assert.Equal(1, clicked);
            Assert.Equal((HWND)app.Handle, PInvoke.GetForegroundWindow());
            Assert.Equal((nint)3, (nint)PInvoke.SendMessage((HWND)overlay.Handle, 0x21, 0, 0).Value);
            Assert.Equal((HWND)app.Handle, PInvoke.GetForegroundWindow());
        });

    [Fact]
    public void UnrepresentableMessageCoordinatesAreRejected()
    {
        var api = new Win32WindowInteractionApi();
        Assert.False(api.Post((IntPtr)123, 0x200, 0, new(40000, 0)));
        Assert.False(api.Post((IntPtr)123, 0x200, 0, new(0, -40000)));
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(2))
        { Application.DoEvents(); Thread.Yield(); }
        Assert.True(condition());
        Application.DoEvents();
    }

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
}
