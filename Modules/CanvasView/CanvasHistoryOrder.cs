namespace CanvasDesktop.CanvasView;

/// <summary>Session ordering shared by independently validated canvas histories.</summary>
public static class CanvasHistoryOrder
{
    private static long _next;
    public static long Next() => Interlocked.Increment(ref _next);
    public static bool PreferAnnotations(long annotations, long projects, bool redo)
        => annotations>0 && (projects==0 || (redo?annotations<projects:annotations>projects));
}
