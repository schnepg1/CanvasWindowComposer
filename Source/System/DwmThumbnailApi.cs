using System;
using System.Diagnostics;

namespace CanvasDesktop;

/// <summary>Instrumented DWM acknowledgements and live-registration gauge, including shell previews.</summary>
internal sealed class DwmThumbnailApi
{
    private readonly PerformanceDiagnostics _diagnostics;
    public DwmThumbnailApi(PerformanceDiagnostics? diagnostics = null) => _diagnostics = diagnostics ?? PerformanceDiagnostics.Shared;

    public HRESULT Register(HWND destination, HWND source, out IntPtr thumbnail)
    {
        long started = Stopwatch.GetTimestamp();
        var result = PInvoke.DwmRegisterThumbnail(destination, source, out thumbnail);
        _diagnostics.Complete("DwmRegisterThumbnail", source, started, result.Succeeded, (int)result.Value);
        if (result.Succeeded) _diagnostics.ThumbnailRegistered(thumbnail);
        return result;
    }

    public HRESULT Unregister(IntPtr thumbnail)
    {
        long started = Stopwatch.GetTimestamp();
        var result = PInvoke.DwmUnregisterThumbnail(thumbnail);
        _diagnostics.Complete("DwmUnregisterThumbnail", IntPtr.Zero, started, result.Succeeded, (int)result.Value);
        if (result.Succeeded) _diagnostics.ThumbnailUnregistered(thumbnail);
        return result;
    }

    public HRESULT Update(IntPtr thumbnail, DWM_THUMBNAIL_PROPERTIES properties)
    {
        long started = Stopwatch.GetTimestamp();
        var result = PInvoke.DwmUpdateThumbnailProperties(thumbnail, properties);
        _diagnostics.Complete("DwmUpdateThumbnailProperties", IntPtr.Zero, started, result.Succeeded, (int)result.Value);
        return result;
    }
}
