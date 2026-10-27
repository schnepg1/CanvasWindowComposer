using System.Diagnostics;
using System.Collections.Concurrent;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using CanvasDesktop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

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
                using var app = new MouseMoveProbe
                {
                    StartPosition = FormStartPosition.Manual,
                    Location = new(start.X - 50, start.Y - 50), Size = new(240, 160), TopMost = true
                };
                app.Show(); app.Activate(); app.Capture = true;
                var forwarded = new ConcurrentQueue<InteractionMouseEvent>();
                using (var hook = new WindowInteractionMouseHook(_ => true, forwarded.Enqueue, () => { }))
                {
                    // The production hook forwards before updating the cursor. A
                    // second hook, installed last, observes its completed return.
                    using var completed = new ManualResetEventSlim();
                    var tag = new UIntPtr(0x43414E56);
                    Point target = default;
                    nint suppression = 0;
                    int observed = 0;
                    HOOKPROC observer = (code, message, data) =>
                    {
                        bool tagged = false;
                        Point point = default;
                        if (code >= 0 && (uint)message.Value == 0x200)
                        {
                            var mouse = Marshal.PtrToStructure<MSLLHOOKSTRUCT>((IntPtr)data.Value);
                            tagged = mouse.dwExtraInfo == tag;
                            point = mouse.pt;
                        }
                        var result = PInvoke.CallNextHookEx(HHOOK.Null, code, message, data);
                        if (tagged)
                        {
                            target = point; suppression = result.Value; observed++;
                            completed.Set();
                        }
                        return result;
                    };
                    using (var observation = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_MOUSE_LL,
                        observer, PInvoke.GetModuleHandle((string?)null), 0))
                    {
                        Assert.False(observation.IsInvalid);
                        // Positive control: queued setup messages bypass low-level
                        // hooks and must be drained before measuring injected input.
                        int setupMoves = app.Moves.Count;
                        Assert.True(PInvoke.PostMessage((HWND)app.Handle, 0x200, 0, (LPARAM)(50 | (50 << 16))));
                        PumpMouseQuiet(() => app.Moves.Count + forwarded.Count);
                        Assert.True(app.Moves.Count > setupMoves);
                        Assert.True(app.Capture);
                        app.Phase = "injected";
                        forwarded.Clear();
                        Assert.True(PInvoke.GetCursorPos(out start));
                        // No clicks or keyboard input are injected into the desktop.
                        mouse_event(1, 4, 0, 0, tag);
                        PumpUntil(() => completed.IsSet);
                        // Keep both hooks installed while any delayed native move
                        // would arrive. Never filter measured events by point/tag.
                        PumpMouseQuiet(() => app.Moves.Count + forwarded.Count);
                        string evidence = string.Join(" | ", app.Moves);
                        Assert.Equal(1, observed);
                        Assert.NotEqual((nint)0, suppression);
                        Assert.True(forwarded.Any(e => e.Screen == target && e.Button == MouseButtons.None));
                        Assert.True(PInvoke.GetCursorPos(out var end));
                        Assert.NotEqual(start, end);
                        Assert.Equal(target, end);
                        Assert.True(app.Capture);
                        Assert.True(app.Moves.All(move => move.Phase != "injected"), evidence);
                    }
                    GC.KeepAlive(observer);
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
    [Trait("Category", "NativeRendering")]
    public void ForwardedWheelActuallyScrollsNativeList()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var app = new Form { StartPosition = FormStartPosition.Manual, Location = new(100, 100), Size = new(400, 300) };
            using var list = new ListBox { Dock = DockStyle.Fill };
            for (int i = 0; i < 200; i++) list.Items.Add("Row " + i);
            app.Controls.Add(list); app.Show(); list.Focus(); Application.DoEvents();
            var bridge = new OverviewWindowInteraction(new Win32WindowInteractionApi());
            try
            {
                Assert.True(bridge.Begin(app.Handle));
                var origin = list.PointToScreen(Point.Empty);
                var geometry = new WindowInteractionGeometry(new Rectangle(0, 0, 200, 200), new Rectangle(origin, list.ClientSize));
                Assert.Equal(0, list.TopIndex);
                Assert.True(bridge.Wheel(geometry, new Point(100, 100), -120, false, 0));
                PumpUntil(() => list.TopIndex > 0);
            }
            finally { bridge.End(); }
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

    private sealed record MouseMoveObservation(string Phase, Point Client, int MessageTime, nint ExtraInfo, bool Captured);
    private sealed class MouseMoveProbe : Form
    {
        public string Phase = "setup";
        public readonly List<MouseMoveObservation> Moves = new();
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x200)
            {
                int packed = (int)message.LParam;
                Moves.Add(new(Phase, new(unchecked((short)packed), unchecked((short)(packed >> 16))),
                    GetMessageTime(), GetMessageExtraInfo(), Capture));
            }
            base.WndProc(ref message);
        }
    }

    private static void PumpMouseQuiet(Func<int> count)
    {
        var clock = Stopwatch.StartNew();
        int previous = count(); long changed = 0;
        while (clock.ElapsedMilliseconds < 500)
        {
            Application.DoEvents();
            int current = count();
            if (current != previous) { previous = current; changed = clock.ElapsedMilliseconds; }
            if (clock.ElapsedMilliseconds - changed >= 40) return;
            Thread.Sleep(1);
        }
        Assert.Fail("Mouse input did not settle within the bounded observation interval.");
    }

    [DllImport("user32.dll")]
    private static extern int GetMessageTime();
    [DllImport("user32.dll")]
    private static extern nint GetMessageExtraInfo();

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
}
