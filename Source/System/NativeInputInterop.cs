using System;
using System.Runtime.InteropServices;

namespace CanvasDesktop;

/// <summary>
/// Win32 P/Invoke signatures and message constants for high-precision touchpad,
/// trackpad gestures, and input message source querying.
/// </summary>
internal static class NativeInputInterop
{
    public const int WM_GESTURE = 0x0119;
    public const int WM_GESTURENOTIFY = 0x011A;

    public const uint GID_BEGIN = 1;
    public const uint GID_END = 2;
    public const uint GID_ZOOM = 3;
    public const uint GID_PAN = 4;
    public const uint GID_ROTATE = 5;
    public const uint GID_TWOFINGERTAP = 6;
    public const uint GID_PRESSANDTAP = 7;

    public const uint GF_BEGIN = 0x00000001;
    public const uint GF_INERTIA = 0x00000002;
    public const uint GF_END = 0x00000004;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINTS
    {
        public short x;
        public short y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GESTUREINFO
    {
        public int cbSize;
        public uint dwFlags;
        public uint dwID;
        public IntPtr hwndTarget;
        public POINTS ptsLocation;
        public uint dwInstanceID;
        public uint dwSequenceID;
        public ulong ullArguments;
        public uint cbExtraArgs;
    }

    public enum INPUT_MESSAGE_DEVICE_TYPE
    {
        IMDT_UNAVAILABLE = 0,
        IMDT_KEYBOARD = 1,
        IMDT_MOUSE = 2,
        IMDT_TOUCH = 3,
        IMDT_PEN = 4,
        IMDT_TOUCHPAD = 5
    }

    public enum INPUT_MESSAGE_ORIGIN_ID
    {
        IMO_UNAVAILABLE = 0,
        IMO_HARDWARE = 1,
        IMO_INJECTED = 2,
        IMO_SYSTEM = 3
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT_MESSAGE_SOURCE
    {
        public INPUT_MESSAGE_DEVICE_TYPE deviceType;
        public INPUT_MESSAGE_ORIGIN_ID originId;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetGestureInfo(IntPtr hGestureInfo, ref GESTUREINFO pGestureInfo);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseGestureInfoHandle(IntPtr hGestureInfo);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCurrentInputMessageSource(out INPUT_MESSAGE_SOURCE inputMessageSource);
}
