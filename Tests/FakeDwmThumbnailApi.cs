using CanvasDesktop;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;

namespace CanvasDesktop.Tests;

internal sealed class FakeDwmThumbnailApi : IDwmThumbnailApi
{
    private int _next;
    private readonly Dictionary<IntPtr, IntPtr> _sources = new();
    public IEnumerable<IntPtr> Sources => _sources.Values;
    public HashSet<IntPtr> FailedSources { get; } = new();

    public HRESULT Register(HWND destination, HWND source, out IntPtr thumbnail)
    {
        thumbnail = IntPtr.Zero;
        if (FailedSources.Contains(source)) return new(-1);
        thumbnail = (IntPtr)(++_next);
        _sources[thumbnail] = source;
        return new(0);
    }
    public HRESULT Unregister(IntPtr thumbnail) { _sources.Remove(thumbnail); return new(0); }
    public HRESULT Update(IntPtr thumbnail, DWM_THUMBNAIL_PROPERTIES properties) => new(0);
}
