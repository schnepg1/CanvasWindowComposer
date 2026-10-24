using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CanvasDesktop;

internal interface IWindowPreviewCapture
{
    Bitmap? Capture(IntPtr handle, Size sourceSize, Size targetSize);
    long ReservationBytes(Size sourceSize, Size targetSize) => (long)targetSize.Width * targetSize.Height * 4;
}

/// <summary>Best-effort WM_PRINT capture. Only call on the bounded background worker.</summary>
internal sealed class WindowPreviewCapture : IWindowPreviewCapture
{
    // Full-content printing ignores GDI scaling. Reserve its bounded scratch
    // bitmap in the cache budget as well as the final thumbnail.
    internal const long MaxScratchBytes = 24 * 1024 * 1024;
    private static long ScratchBytes(Size size)
    {
        long pixels = (long)size.Width * size.Height;
        return size.Width > 0 && size.Height > 0 && pixels <= MaxScratchBytes / 4 ? pixels * 4 : 0;
    }
    public long ReservationBytes(Size sourceSize, Size targetSize)
        => (long)targetSize.Width * targetSize.Height * 4 + ScratchBytes(sourceSize);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
    [DllImport("gdi32.dll")]
    private static extern int SetMapMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowExtEx(IntPtr dc, int x, int y, IntPtr previous);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetViewportExtEx(IntPtr dc, int x, int y, IntPtr previous);

    public Bitmap? Capture(IntPtr handle, Size sourceSize, Size targetSize)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        bool success = false;
        try
        {
            var image = CaptureCore(handle, sourceSize, targetSize);
            success = image != null;
            return image;
        }
        finally { PerformanceDiagnostics.Shared.Complete("CaptureWindowPreview", handle, started, success, null); }
    }

    private static Bitmap? CaptureCore(IntPtr handle, Size sourceSize, Size targetSize)
    {
        var image = new Bitmap(targetSize.Width, targetSize.Height, PixelFormat.Format32bppRgb);
        try
        {
            using var graphics = Graphics.FromImage(image);
            graphics.Clear(Color.FromArgb(32, 32, 32));
            // GDI mapping scales WM_PRINT output directly into the capped bitmap;
            // never allocate a full-size source-window image.
            var dc = graphics.GetHdc();
            bool success;
            try
            {
                success = SetMapMode(dc, 8 /* MM_ANISOTROPIC */) != 0
                    && SetWindowExtEx(dc, sourceSize.Width, sourceSize.Height, IntPtr.Zero)
                    && SetViewportExtEx(dc, targetSize.Width, targetSize.Height, IntPtr.Zero)
                    && PrintWindow(handle, dc, 0);
            }
            finally { graphics.ReleaseHdc(dc); }
            // WM_PRINT success does not prove usable pixels (GPU/protected content
            // may return an entirely black or untouched frame).
            if (success && HasCapturedPixels(image)) return image;
        }
        catch { image.Dispose(); throw; }
        image.Dispose();
        return CaptureFullContent(handle, sourceSize, targetSize);
    }

    internal static Bitmap? CaptureFullContent(IntPtr handle, Size sourceSize, Size targetSize)
    {
        if (ScratchBytes(sourceSize) == 0) return null;
        using var source = new Bitmap(sourceSize.Width, sourceSize.Height, PixelFormat.Format32bppRgb);
        using (var graphics = Graphics.FromImage(source))
        {
            graphics.Clear(Color.FromArgb(32, 32, 32));
            var dc = graphics.GetHdc();
            bool success;
            try { success = PrintWindow(handle, dc, 2 /* PW_RENDERFULLCONTENT */); }
            finally { graphics.ReleaseHdc(dc); }
            if (!success) return null;
        }
        if (!HasCapturedPixels(source)) return null;
        var image = new Bitmap(targetSize.Width, targetSize.Height, PixelFormat.Format32bppRgb);
        try
        {
            using var graphics = Graphics.FromImage(image);
            graphics.DrawImage(source, new Rectangle(Point.Empty, targetSize));
            return image;
        }
        catch { image.Dispose(); throw; }
    }

    private static unsafe bool HasCapturedPixels(Bitmap image)
    {
        var bits = image.LockBits(new Rectangle(Point.Empty, image.Size), ImageLockMode.ReadOnly,
            PixelFormat.Format32bppRgb);
        try
        {
            for (int y = 0; y < image.Height; y++)
            {
                var row = (uint*)((byte*)bits.Scan0 + y * bits.Stride);
                for (int x = 0; x < image.Width; x++)
                {
                    uint rgb = row[x] & 0x00ffffff;
                    if (rgb != 0 && rgb != 0x202020) return true;
                }
            }
            return false;
        }
        finally { image.UnlockBits(bits); }
    }
}
