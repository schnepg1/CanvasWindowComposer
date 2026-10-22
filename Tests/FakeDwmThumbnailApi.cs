using CanvasDesktop;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;

namespace CanvasDesktop.Tests;

internal sealed class FakeDwmThumbnailApi : IDwmThumbnailApi
{
    private int _next;
    private readonly Dictionary<IntPtr, IntPtr> _sources = new();
    private readonly HashSet<IntPtr> _visible = new();
    public IEnumerable<IntPtr> Sources => _sources.Values;
    public IEnumerable<IntPtr> VisibleSources => _visible.Select(thumb => _sources[thumb]);
    public List<(IntPtr Destination, IntPtr Source, IntPtr Thumbnail)> Registrations { get; } = new();
    public List<(IntPtr Source, DWM_THUMBNAIL_PROPERTIES Properties)> Updates { get; } = new();
    public List<IntPtr> Released { get; } = new();
    public HashSet<IntPtr> FailedSources { get; } = new();
    public HashSet<IntPtr> FailedHides { get; } = new();
    public HashSet<IntPtr> FailedShows { get; } = new();

    public IEnumerable<IntPtr> Stack(IntPtr destination)
        => Registrations.Where(r => r.Destination == destination && _sources.ContainsKey(r.Thumbnail)).Select(r => r.Source);

    public HRESULT Register(HWND destination, HWND source, out IntPtr thumbnail)
    {
        thumbnail = IntPtr.Zero;
        if (FailedSources.Contains(source)) return new(-1);
        thumbnail = (IntPtr)(++_next);
        _sources[thumbnail] = source;
        Registrations.Add(((IntPtr)destination, (IntPtr)source, thumbnail));
        return new(0);
    }
    public HRESULT Unregister(IntPtr thumbnail)
    {
        Released.Add(thumbnail);
        _sources.Remove(thumbnail); _visible.Remove(thumbnail); return new(0);
    }
    public HRESULT Update(IntPtr thumbnail, DWM_THUMBNAIL_PROPERTIES properties)
    {
        var source = _sources[thumbnail];
        Updates.Add((source, properties));
        if ((properties.dwFlags & Windows.Win32.PInvoke.DWM_TNP_VISIBLE) != 0)
        {
            if (properties.fVisible)
            {
                if (FailedShows.Contains(source)) return new(-1);
                _visible.Add(thumbnail);
            }
            else
            {
                if (FailedHides.Contains(source)) return new(-1);
                _visible.Remove(thumbnail);
            }
        }
        return new(0);
    }
}
