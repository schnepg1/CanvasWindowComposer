using System;
using System.Collections.Generic;

namespace CanvasDesktop;

/// <summary>Immutable geometry between topology/work-area notifications; safe across threads.</summary>
internal sealed class CachedScreens(IScreens source) : IScreens
{
    private sealed record Snapshot(IReadOnlyList<ScreenRect> Bounds, IReadOnlyList<ScreenRect> Working,
        ScreenRect Virtual, ScreenRect Primary, ScreenRect PrimaryWorking);
    private readonly object _gate = new();
    private Snapshot? _snapshot;
    private long _version;
    public long TopologyVersion { get { lock (_gate) return _version; } }
    public void Invalidate() { lock (_gate) { _snapshot = null; _version++; } }
    private Snapshot Get()
    {
        lock (_gate)
            return _snapshot ??= new(Copy(source.AllBounds), Copy(source.AllWorkingAreas),
                source.VirtualScreen, source.PrimaryBounds, source.PrimaryWorkingArea);
    }
    private static IReadOnlyList<ScreenRect> Copy(IReadOnlyList<ScreenRect> source)
    {
        var result = new ScreenRect[source.Count];
        for (int i = 0; i < result.Length; i++) result[i] = source[i];
        return Array.AsReadOnly(result);
    }
    public IReadOnlyList<ScreenRect> AllBounds => Get().Bounds;
    public IReadOnlyList<ScreenRect> AllWorkingAreas => Get().Working;
    public ScreenRect VirtualScreen => Get().Virtual;
    public ScreenRect PrimaryBounds => Get().Primary;
    public ScreenRect PrimaryWorkingArea => Get().PrimaryWorking;
}
