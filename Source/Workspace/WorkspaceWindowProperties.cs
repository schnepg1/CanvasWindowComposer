using System;
using System.Runtime.InteropServices;

namespace CanvasDesktop;

/// <summary>Reads the window's shell identity; Chrome processes can contain several profiles.</summary>
internal static class WorkspaceWindowProperties
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { public Guid Format; public uint Id; }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, IntPtr value);
        [PreserveSig] int SetValue(ref PropertyKey key, IntPtr value);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetPropertyStoreForWindow(IntPtr window, ref Guid iid, out IPropertyStore store);
    [DllImport("propsys.dll", ExactSpelling = true)]
    private static extern int PropVariantToStringAlloc(IntPtr value, out IntPtr text);
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int PropVariantClear(IntPtr value);

    public static string? ReadAppId(IntPtr window)
    {
        IPropertyStore? store = null;
        // A PROPVARIANT fits in 24 bytes on x64 and 16 bytes on x86.
        IntPtr value = Marshal.AllocCoTaskMem(32), text = IntPtr.Zero;
        for (int i = 0; i < 32; i += 8) Marshal.WriteInt64(value, i, 0);
        try
        {
            Guid iid = typeof(IPropertyStore).GUID;
            var key = new PropertyKey { Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 };
            if (SHGetPropertyStoreForWindow(window, ref iid, out store) < 0 || store == null
                || store.GetValue(ref key, value) < 0 || PropVariantToStringAlloc(value, out text) < 0) return null;
            return Marshal.PtrToStringUni(text);
        }
        catch (COMException) { return null; }
        finally
        {
            if (text != IntPtr.Zero) Marshal.FreeCoTaskMem(text);
            PropVariantClear(value); Marshal.FreeCoTaskMem(value);
            if (store != null) Marshal.ReleaseComObject(store);
        }
    }
}
