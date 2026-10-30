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
            public string HitTestMethod = "PassiveRootGeometry";
            public DateTime CaptureStartedUtc, CapturedUtc;
            public double CaptureMilliseconds;
            public ulong UptimeMilliseconds;
            public uint? LastInputAgeMilliseconds;
            public long Foreground, CursorWindow;
            public Point Cursor;
            public GuiInfo ForegroundGui = new GuiInfo();
            public List<WindowInfo> Windows = new List<WindowInfo>();
            public List<HitInfo> PreviewHits = new List<HitInfo>();
            public List<ShellChildInfo> ShellChildren = new List<ShellChildInfo>();
        }

        public sealed class HitInfo { public long Preview, HitWindow; public Point Point; }
        public sealed class ShellChildInfo
        {
            public long Handle, Parent, Root;
            public string ClassName = "";
            public int ProcessId, ThreadId;
            public bool Visible;
            public Rect Bounds;
            public GuiInfo Gui = new GuiInfo();
        }
        public sealed class HitSurface
        {
            public long Handle;
            public string ClassName = "";
            public Rect Bounds;
            public Rect[] Region = new Rect[0];
            public bool HasRegion;
        }

        public sealed class GuiInfo
        {
            public bool Readable;
            public uint Flags;
            public long Active, Focus, Capture, MenuOwner, MoveSize;
        }

        [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct LastInputInfo { public uint Size, Time; }
        [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo
        {
            public int Size;
            public uint Flags;
            public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
            public Rect CaretBounds;
        }

        public static Snapshot Capture(bool includeTitles = false)
        {
            var startedUtc = DateTime.UtcNow;
            var elapsed = Stopwatch.StartNew();
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
                if (!visible && !IsShellSurfaceClass(cls.ToString())) return true;
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
            var surfaces = CaptureHitSurfaces();
            var shellChildren = new List<ShellChildInfo>();
            foreach (var window in windows)
            {
                if (window.ClassName != "Shell_TrayWnd" && window.ClassName != "Shell_SecondaryTrayWnd") continue;
                EnumChildWindows(new IntPtr(window.Handle), delegate(IntPtr child, IntPtr parameter)
                {
                    var name = new StringBuilder(128); GetClassName(child, name, name.Capacity);
                    Rect bounds; GetWindowRect(child, out bounds);
                    uint processId;
                    uint threadId = GetWindowThreadProcessId(child, out processId);
                    if (!gui.ContainsKey(threadId)) gui[threadId] = ReadGui(threadId);
                    shellChildren.Add(new ShellChildInfo { Handle = child.ToInt64(), Parent = GetParent(child).ToInt64(),
                        Root = window.Handle, ClassName = name.ToString(), Visible = IsWindowVisible(child), Bounds = bounds,
                        ProcessId = (int)processId, ThreadId = (int)threadId, Gui = gui[threadId] });
                    return shellChildren.Count < 256;
                }, IntPtr.Zero);
            }
            var hits = new List<HitInfo>();
            foreach (var window in windows)
            {
                if (!window.Visible || window.ClassName != "TaskListThumbnailWnd") continue;
                var b = window.Bounds;
                for (int x = 1; x <= 3; x++)
                {
                    var point = new Point { X = b.Left + (b.Right - b.Left) * x / 4, Y = b.Top + (b.Bottom - b.Top) / 2 };
                    hits.Add(new HitInfo { Preview = window.Handle, Point = point, HitWindow = HitWindow(surfaces, point) });
                }
            }
            var input = new LastInputInfo { Size = (uint)Marshal.SizeOf(typeof(LastInputInfo)) };
            uint? inputAge = GetLastInputInfo(ref input) ? (uint?)unchecked((uint)GetTickCount64() - input.Time) : null;
            return new Snapshot { CaptureStartedUtc = startedUtc, CapturedUtc = DateTime.UtcNow,
                CaptureMilliseconds = elapsed.Elapsed.TotalMilliseconds, UptimeMilliseconds = GetTickCount64(),
                LastInputAgeMilliseconds = inputAge, Foreground = GetForegroundWindow().ToInt64(),
                Cursor = cursor, CursorWindow = HitWindow(surfaces, cursor), ForegroundGui = ReadGui(0), Windows = windows,
                PreviewHits = hits, ShellChildren = shellChildren };
        }

        // Windows 11 also uses XAML hosts and transient popup/LivePreview HWNDs.
        // Keep their hidden state: five-second samples often miss the show interval.
        internal static bool IsShellSurfaceClass(string name)
        {
            return name == "TaskListThumbnailWnd" || name == "Shell_TrayWnd" || name == "Shell_SecondaryTrayWnd"
                || name == "XamlExplorerHostIslandWindow" || name == "Xaml_WindowedPopupClass"
                || name == "TopLevelWindowForOverflowXamlIsland" || name == "LivePreview";
        }

        // Geometry APIs do not send WM_NCHITTEST into Explorer or applications.
        // Keep this separate from the full diagnostic capture: the shell input
        // observer refreshes it off its hook thread, then classifies clicks in memory.
        public static HitSurface[] CaptureHitSurfaces()
        {
            var surfaces = new List<HitSurface>();
            EnumWindows(delegate(IntPtr window, IntPtr parameter)
            {
                if (!IsWindowVisible(window) || IsIconic(window) || (GetWindowLong(window, -20) & 0x20) != 0) return true;
                int cloaked;
                if (DwmGetWindowAttribute(window, 14, out cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                Rect bounds;
                if (!GetWindowRect(window, out bounds) || bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top) return true;
                var name = new StringBuilder(128); GetClassName(window, name, name.Capacity);
                var surface = new HitSurface { Handle = window.ToInt64(), ClassName = name.ToString(), Bounds = bounds };
                IntPtr region = CreateRectRgn(0, 0, 0, 0);
                if (region != IntPtr.Zero)
                {
                    try
                    {
                        if (GetWindowRgn(window, region) != 0)
                        {
                            uint bytes = GetRegionData(region, 0, IntPtr.Zero);
                            // Bound unusually complex regions. Unknown shapes are
                            // conservative occluders rather than guessed taskbar clicks.
                            if (bytes >= 32 && bytes <= 262144)
                            {
                                IntPtr data = Marshal.AllocHGlobal((int)bytes);
                                try
                                {
                                    if (GetRegionData(region, bytes, data) == bytes)
                                    {
                                        int count = Marshal.ReadInt32(data, 8);
                                        if (count >= 0 && count <= ((int)bytes - 32) / 16)
                                        {
                                            surface.HasRegion = true;
                                            surface.Region = new Rect[count];
                                            for (int i = 0; i < count; i++) surface.Region[i] = Marshal.PtrToStructure<Rect>(
                                                IntPtr.Add(data, 32 + i * 16));
                                        }
                                    }
                                }
                                finally { Marshal.FreeHGlobal(data); }
                            }
                            else surface.HasRegion = false;
                        }
                    }
                    finally { DeleteObject(region); }
                }
                surfaces.Add(surface); return true;
            }, IntPtr.Zero);
            return surfaces.ToArray();
        }

        public static int HitSurfaceIndex(HitSurface[] surfaces, Point point)
        {
            for (int i = 0; i < surfaces.Length; i++)
            {
                var s = surfaces[i]; var b = s.Bounds;
                if (point.X < b.Left || point.X >= b.Right || point.Y < b.Top || point.Y >= b.Bottom) continue;
                if (!s.HasRegion) return i;
                foreach (var r in s.Region)
                    if (point.X - b.Left >= r.Left && point.X - b.Left < r.Right
                        && point.Y - b.Top >= r.Top && point.Y - b.Top < r.Bottom) return i;
            }
            return -1;
        }

        private static long HitWindow(HitSurface[] surfaces, Point point)
        { int index = HitSurfaceIndex(surfaces, point); return index < 0 ? 0 : surfaces[index].Handle; }

        private static GuiInfo ReadGui(uint thread)
        {
            var info = new GuiThreadInfo { Size = Marshal.SizeOf(typeof(GuiThreadInfo)) };
            bool ok = GetGUIThreadInfo(thread, ref info);
            return new GuiInfo { Readable = ok, Flags = info.Flags, Active = info.Active.ToInt64(),
                Focus = info.Focus.ToInt64(), Capture = info.Capture.ToInt64(), MenuOwner = info.MenuOwner.ToInt64(), MoveSize = info.MoveSize.ToInt64() };
        }

        private delegate bool EnumCallback(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr window, EnumCallback callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
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
        [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInputInfo info);
        [DllImport("kernel32.dll")] private static extern ulong GetTickCount64();
        [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
        [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr window, IntPtr region);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
        [DllImport("gdi32.dll")] private static extern int GetRgnBox(IntPtr region, out Rect bounds);
        [DllImport("gdi32.dll")] private static extern uint GetRegionData(IntPtr region, uint size, IntPtr data);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
    }
}
