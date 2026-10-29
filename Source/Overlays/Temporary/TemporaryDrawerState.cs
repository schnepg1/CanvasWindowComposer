using System;
using System.Collections.Generic;
using System.Linq;

namespace CanvasDesktop;

/// <summary>UI-independent arrival ordering and peek lifetime. Times are monotonic milliseconds.</summary>
internal sealed class TemporaryDrawerState
{
    internal const long PeekMilliseconds = 5000;
    private Guid? _area;
    private long _until;
    private bool _hovered, _dragging;
    public bool IsOpen { get; private set; }
    public IReadOnlyList<WorkspaceItemView> Items { get; private set; } = Array.Empty<WorkspaceItemView>();

    public IReadOnlyList<Guid> Update(Guid? area, IReadOnlyList<WorkspaceItemView> items, long now)
    {
        if (_area != area) { Items = Array.Empty<WorkspaceItemView>(); Close(); _area = area; }
        var old = Items.Select(i => i.Id).ToHashSet();
        var arrivals = items.Where(i => !old.Contains(i.Id)).Reverse().ToArray();
        var current = items.ToDictionary(i => i.Id);
        Items = arrivals.Concat(Items.Where(i => current.ContainsKey(i.Id)).Select(i => current[i.Id])).ToArray();
        if (arrivals.Length > 0) Open(now);
        return arrivals.Select(i => i.Id).ToArray();
    }

    public void Open(long now) { IsOpen = true; _until = now + PeekMilliseconds; }
    public void Close() => IsOpen = false;
    public void SetHovered(bool hovered, long now)
    {
        if (_hovered == hovered) return;
        _hovered = hovered;
        if (!hovered) _until = now + PeekMilliseconds;
    }
    public void SetDragging(bool dragging, long now)
    {
        _dragging = dragging;
        if (!dragging) _until = now + PeekMilliseconds;
    }
    public void Tick(long now)
    {
        if (IsOpen && !_hovered && !_dragging && now >= _until) Close();
    }
}
