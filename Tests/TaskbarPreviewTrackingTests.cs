using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using CanvasDesktop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class TaskbarPreviewTrackingTests
{
    [Fact]
    [Trait("Category", "NativeRendering")]
    public void ExistingPreviewAndShellEventsUpdateBoundsAndClearDismissedOrDisabledPreviews()
        => WorkspaceViewTests.OnSta(() =>
        {
            // A real HWND with Explorer's chooser class exercises native class and
            // rectangle lookup without depending on the user's taskbar contents.
            WindowProc procedure = DefWindowProc;
            var instance = GetModuleHandle(null);
            var definition = new WindowClass
            {
                Procedure = Marshal.GetFunctionPointerForDelegate(procedure),
                Instance = instance, Name = "TaskListThumbnailWnd"
            };
            Assert.NotEqual((ushort)0, RegisterClass(ref definition));
            IntPtr popup = IntPtr.Zero;
            try
            {
                popup = CreateWindowEx(0x80, definition.Name, "Test chooser", 0x90000000,
                    -30000, -30000, 400, 200, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
                Assert.NotEqual(IntPtr.Zero, popup);
                var frames = new List<Rectangle[]>();
                using var tracker = new OverviewTaskbarPreviews(frames.Add);
                tracker.SetEnabled(true);
                Assert.Contains(new Rectangle(-30000, -30000, 400, 200), frames[^1]);
                var callback = (WINEVENTPROC)typeof(OverviewTaskbarPreviews)
                    .GetField("_callback", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tracker)!;
                void Dispatch(uint type, int child = 0) => callback(default, type, (HWND)popup,
                    (int)OBJECT_IDENTIFIER.OBJID_WINDOW, child, 0, 0);

                Dispatch(PInvoke.EVENT_OBJECT_HIDE, child: 1);
                Assert.NotEmpty(frames[^1]); // Child accessibility events are unrelated.
                Dispatch(PInvoke.EVENT_OBJECT_HIDE);
                Assert.Empty(frames[^1]);
                Dispatch(PInvoke.EVENT_OBJECT_SHOW);
                Assert.NotEmpty(frames[^1]);
                Assert.True(SetWindowPos(popup, IntPtr.Zero, -29000, -29000, 450, 250, 0x14));
                Dispatch(PInvoke.EVENT_OBJECT_LOCATIONCHANGE);
                Assert.Contains(new Rectangle(-29000, -29000, 450, 250), frames[^1]);
                Dispatch(PInvoke.EVENT_OBJECT_DESTROY);
                Assert.Empty(frames[^1]);
                Dispatch(PInvoke.EVENT_OBJECT_SHOW);
                tracker.SetEnabled(false);
                Assert.Empty(frames[^1]);
                int count = frames.Count;
                Dispatch(PInvoke.EVENT_OBJECT_SHOW);
                Assert.Equal(count, frames.Count);
                tracker.SetEnabled(true);
                Assert.NotEmpty(frames[^1]);
                using var other = new System.Windows.Forms.Form();
                callback(default, PInvoke.EVENT_OBJECT_SHOW, (HWND)other.Handle, 0, 0, 0, 0);
                Assert.Single(frames[^1]);
            }
            finally
            {
                if (popup != IntPtr.Zero) DestroyWindow(popup);
                UnregisterClass(definition.Name, instance);
                GC.KeepAlive(procedure);
            }
        });

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
}
