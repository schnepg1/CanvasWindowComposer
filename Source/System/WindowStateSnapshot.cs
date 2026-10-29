using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CanvasDesktop
{
    // Also compiled by Tools/Capture-WindowDiagnostics.ps1 while an older Canvas is running.
    // Read-only native evidence: no window activation, input injection or shell restart.
    public static class WindowStateSnapshot
    {
        public sealed class WindowInfo
        {
            public long Handle, Owner;
            public int ZOrder, ProcessId, ThreadId, Style, ExtendedStyle, Cloaked, RegionKind;
            public string ClassName = "", ProcessName = "", Title = "";
            public bool Visible, Minimized, Hung;
            public Rect Bounds, RegionBounds;
            public GuiInfo Gui = new GuiInfo();
        }

        public sealed class Snapshot
        {
            public DateTime CapturedUtc;
            public long Foreground, CursorWindow;
            public Point Cursor;
            public GuiInfo ForegroundGui = new GuiInfo();
            public List<WindowInfo> Windows = new List<WindowInfo>();
            public List<HitInfo> PreviewHits = new List<HitInfo>();
        }

        public sealed class HitInfo { public long Preview, HitWindow; public Point Point; }

        public sealed class GuiInfo
        {
            public bool Readable;
            public uint Flags;
            public long Active, Focus, Capture, MenuOwner, MoveSize;
        }

        [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo
        {
            public int Size;
            public uint Flags;
            public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
            public Rect CaretBounds;
        }

        public static Snapshot Capture(bool includeTitles = false)
        {
            var windows = new List<WindowInfo>();
            var names = new Dictionary<uint, string>();
            var gui = new Dictionary<uint, GuiInfo>();
            EnumWindows(delegate(IntPtr window, IntPtr parameter)
            {
                uint processId;
                uint threadId = GetWindowThreadProcessId(window, out processId);
                var cls = new StringBuilder(256);
                GetClassName(window, cls, cls.Capacity);
                bool visible = IsWindowVisible(window);
                // Hidden application helper HWNDs are numerous and unrelated to
                // taskbar eligibility. Preserve hidden shell choosers explicitly.
                if (!visible && cls.ToString() != "TaskListThumbnailWnd"
                    && cls.ToString() != "Shell_TrayWnd" && cls.ToString() != "Shell_SecondaryTrayWnd") return true;
                if (!names.ContainsKey(processId))
                {
                    string name;
                    try { using (var process = Process.GetProcessById((int)processId)) name = process.ProcessName; }
                    catch (ArgumentException) { name = "exited"; }
                    catch (InvalidOperationException) { name = "exited"; }
                    catch (System.ComponentModel.Win32Exception) { name = "unavailable"; }
                    names[processId] = name;
                }
                if (!gui.ContainsKey(threadId)) gui[threadId] = ReadGui(threadId);
                Rect bounds, regionBounds;
                GetWindowRect(window, out bounds);
                int cloaked;
                DwmGetWindowAttribute(window, 14, out cloaked, sizeof(int));
                var region = CreateRectRgn(0, 0, 0, 0);
                int kind = 0;
                regionBounds = new Rect();
                if (region != IntPtr.Zero)
                {
                    try { kind = GetWindowRgn(window, region); if (kind != 0) GetRgnBox(region, out regionBounds); }
                    finally { DeleteObject(region); }
                }
                string title = "";
                if (includeTitles)
                {
                    var text = new StringBuilder(512); GetWindowText(window, text, text.Capacity); title = text.ToString();
                }
                windows.Add(new WindowInfo { Handle = window.ToInt64(), Owner = GetWindow(window, 4).ToInt64(),
                    ZOrder = windows.Count, ProcessId = (int)processId, ThreadId = (int)threadId,
                    ClassName = cls.ToString(), ProcessName = names[processId], Title = title,
                    Visible = visible, Minimized = IsIconic(window), Hung = IsHungAppWindow(window),
                    Style = GetWindowLong(window, -16), ExtendedStyle = GetWindowLong(window, -20),
                    Bounds = bounds, Cloaked = cloaked, RegionKind = kind, RegionBounds = regionBounds, Gui = gui[threadId] });
                return true;
            }, IntPtr.Zero);
            Point cursor;
            GetCursorPos(out cursor);
            var hits = new List<HitInfo>();
            foreach (var window in windows)
            {
                if (!window.Visible || window.ClassName != "TaskListThumbnailWnd") continue;
                var b = window.Bounds;
                for (int x = 1; x <= 3; x++)
                {
                    var point = new Point { X = b.Left + (b.Right - b.Left) * x / 4, Y = b.Top + (b.Bottom - b.Top) / 2 };
                    hits.Add(new HitInfo { Preview = window.Handle, Point = point, HitWindow = WindowFromPoint(point).ToInt64() });
                }
            }
            return new Snapshot { CapturedUtc = DateTime.UtcNow, Foreground = GetForegroundWindow().ToInt64(),
                Cursor = cursor, CursorWindow = WindowFromPoint(cursor).ToInt64(), ForegroundGui = ReadGui(0), Windows = windows, PreviewHits = hits };
        }

        private static GuiInfo ReadGui(uint thread)
        {
            var info = new GuiThreadInfo { Size = Marshal.SizeOf(typeof(GuiThreadInfo)) };
            bool ok = GetGUIThreadInfo(thread, ref info);
            return new GuiInfo { Readable = ok, Flags = info.Flags, Active = info.Active.ToInt64(),
                Focus = info.Focus.ToInt64(), Capture = info.Capture.ToInt64(), MenuOwner = info.MenuOwner.ToInt64(), MoveSize = info.MoveSize.ToInt64() };
        }

        private delegate bool EnumCallback(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int length);
        [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int length);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsHungAppWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, int command);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
        [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr window, IntPtr region);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
        [DllImport("gdi32.dll")] private static extern int GetRgnBox(IntPtr region, out Rect bounds);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
    }
}
