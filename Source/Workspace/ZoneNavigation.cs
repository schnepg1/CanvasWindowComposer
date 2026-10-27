using System;
using System.Collections.Generic;
using System.Linq;

namespace CanvasDesktop;

internal sealed record ZoneNavigationArea(ZoneView Area, IReadOnlyList<Guid> MemberIds);
internal interface IZoneNavigationSource
{
    IReadOnlyList<ZoneNavigationArea> NavigationAreas { get; }
    event Action? Changed;
}

internal sealed record ZoneNavigationMember(Guid Id, string Title, string State, bool Pinned, string Details)
{
    public bool CanActivate => State is "Open" or "Placeholder" or "Minimized" or "Sleeping";
    public string ActionLabel => State == "Open" ? "Focus member" : State == "Placeholder" ? "Reopen member"
        : State is "Minimized" or "Sleeping" ? "Restore member" : State;
}
internal sealed record ZoneNavigationEntry(ZoneView Area, IReadOnlyList<ZoneNavigationMember> Members);

/// <summary>Read-only navigation projection and ID-based actions. No layout/history mutations or HWNDs.</summary>
internal sealed class ZoneNavigation : IDisposable
{
    private readonly IZoneNavigationSource _source;
    private readonly IWorkspaceNavigationCommands _workspace;
    private readonly Action<ZoneView> _jump;
    private readonly Action _beforeActivate;
    private bool _disposed;
    public event Action? Changed;
    public string? Status => _workspace.Status;

    public ZoneNavigation(IZoneNavigationSource source, IWorkspaceNavigationCommands workspace,
        Action<ZoneView> jump, Action beforeActivate)
    {
        _source = source; _workspace = workspace; _jump = jump; _beforeActivate = beforeActivate;
        source.Changed += Notify; workspace.Changed += Notify;
    }
    private void Notify() { if (!_disposed) Changed?.Invoke(); }

    public IReadOnlyList<ZoneNavigationEntry> Read(string query)
    {
        if (_disposed) return Array.Empty<ZoneNavigationEntry>();
        query = query.Trim();
        var places = _workspace.Items.ToDictionary(i => i.Id);
        var entries = new List<ZoneNavigationEntry>();
        foreach (var group in _source.NavigationAreas)
        {
            bool areaMatches = Contains(group.Area.Name, query);
            var members = group.MemberIds.Where(places.ContainsKey).Select(id => places[id])
                .Where(i => areaMatches || Contains(i.Title, query) || Contains(i.DisplayTarget, query) || Contains(i.ExecutablePath, query) || Contains(i.BrowserDetails, query))
                .Select(i => new ZoneNavigationMember(i.Id, i.Title, i.State, i.Bounds.PinnedToScreen,
                    i.DisplayTarget.Length > 0 ? i.DisplayTarget : i.ExecutablePath))
                .OrderBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(i => i.Id).ToArray();
            if (areaMatches || members.Length > 0) entries.Add(new(group.Area, members));
        }
        return entries.OrderBy(i => i.Area.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(i => i.Area.Id).ToArray();
    }

    private static bool Contains(string value, string query) => value.Contains(query, StringComparison.OrdinalIgnoreCase);

    public bool Jump(Guid areaId)
    {
        if (_disposed) return false;
        var area = _source.NavigationAreas.FirstOrDefault(a => a.Area.Id == areaId);
        if (area == null) return false;
        _jump(area.Area);
        return true;
    }

    public bool ActivateMember(Guid areaId, Guid memberId)
    {
        if (_disposed) return false;
        var area = _source.NavigationAreas.FirstOrDefault(a => a.Area.Id == areaId);
        if (area == null || !area.MemberIds.Contains(memberId)) return false;
        var member = _workspace.Items.FirstOrDefault(i => i.Id == memberId);
        if (member == null || member.State is not ("Open" or "Placeholder" or "Minimized" or "Sleeping")) return false;
        _beforeActivate();
        // Open resolves the current binding again: focus a live app, otherwise use
        // the existing launch/queue/identity policy for this saved place.
        _workspace.Open(memberId);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _source.Changed -= Notify; _workspace.Changed -= Notify;
    }
}
