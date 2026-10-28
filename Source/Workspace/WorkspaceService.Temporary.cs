using System;
using System.Collections.Generic;
using System.Linq;

namespace CanvasDesktop;

internal sealed record TemporaryAreaView(WorldRect Bounds, int Count, Guid Id, string Name, string Color);

internal sealed partial class WorkspaceService
{
    private readonly HashSet<WorkspaceWindowIdentity> _migrationWindows = new();
    private int _arrivals;
    private bool _temporaryAreaChanged;
    internal Func<IReadOnlyList<WorldRect>>? AdditionalPlacementObstacles { get; set; }
    public event Action<TemporaryAreaView>? TemporaryWindowsArrived;

    public TemporaryAreaView? TemporaryArea
    {
        get
        {
            int count = Document.Items.Count(i => i.DesktopId == _desktop && i.Placement == WorkspacePlacement.Temporary);
            var zone = TemporaryZone;
            return zone != null ? new(zone.Bounds, count, zone.Id, zone.Name, zone.Color) : null;
        }
    }

    private WorkspaceZone? TemporaryZone => Document.Zones.FirstOrDefault(z => z.DesktopId == _desktop && z.IsTemporaryArea);

    public TemporaryAreaView? EnsureTemporaryArea()
    {
        if (_disposed || !_started || _switching) return null;
        GetOrCreateTemporaryZone(PlacementObstacles(IntPtr.Zero));
        Save(); PublishTemporaryAreaChange(); Changed?.Invoke();
        return TemporaryArea;
    }

    private void MigrateTemporaryAreas()
    {
        foreach (var (desktop, anchor) in Document.TemporaryAreas)
            if (!Document.Zones.Any(z => z.DesktopId == desktop && z.IsTemporaryArea))
                Document.Zones.Add(new() { DesktopId = desktop, IsTemporaryArea = true, Name = "Temporary area", Color = "#94A3B8",
                    Bounds = new() { X = anchor.X, Y = anchor.Y, W = TemporaryAreaLayout.Width, H = 1200 } });
        Document.TemporaryAreas.Clear();
    }

    private void InitializePlacementPolicy()
    {
        _migrationWindows.UnionWith(Document.PendingPlacementAdoptions.Select(a => a.ToIdentity()));
        if (Document.PlacementPolicyInitialized) return;
        // Adopt only windows present at the upgrade boundary, including windows on
        // other desktops and identities whose folder/project lookup completes later.
        _api.EnumWindows(handle =>
        {
            if (_api.GetWindowProcessId(handle) != (uint)Environment.ProcessId
                && _windowInspector.Inspect(handle) is { } observed && _migrationWindows.Add(observed.Identity))
                Document.PendingPlacementAdoptions.Add(new(handle.ToInt64(), observed.Identity.ProcessId,
                    observed.Identity.ProcessStartedAtUtcTicks));
            return true;
        });
        Document.PlacementPolicyInitialized = true;
    }

    private void CompletePlacementAdoptions()
    {
        foreach (var adoption in Document.PendingPlacementAdoptions.ToArray())
        {
            var identity = adoption.ToIdentity();
            uint pid = _api.GetWindowProcessId(identity.Handle);
            var observed = pid == identity.ProcessId ? _windowInspector.Inspect(identity.Handle) : null;
            // An inspection outage is not evidence of closure. Retain its checkpoint
            // until the window is captured, explicitly forgotten, closed or replaced.
            var live = _bindings.At(identity.Handle);
            bool captured = observed?.Identity == identity && live?.Identity == identity
                && (live.Forgotten || live.ItemId is { } id && Find(id)?.Placement == WorkspacePlacement.Deliberate
                    && ValidBinding(identity.Handle) != null);
            if (!captured && pid == identity.ProcessId && (observed == null || observed.Value.Identity == identity)) continue;
            Document.PendingPlacementAdoptions.Remove(adoption);
            _migrationWindows.Remove(identity);
        }
    }

    private IReadOnlyList<WorldRect> PlacementObstacles(IntPtr except)
        => Document.Items.Where(i => i.DesktopId == _desktop && !i.Bounds.PinnedToScreen).Select(i => i.Bounds)
            .Concat(Document.Zones.Where(z => z.DesktopId == _desktop && !z.IsTemporaryArea).Select(z => z.Bounds))
            .Concat(_canvas.Windows.Where(p => p.Key != except && !p.Value.PinnedToScreen).Select(p => p.Value))
            .Concat(AdditionalPlacementObstacles?.Invoke() ?? Array.Empty<WorldRect>())
            .Where(ZoneGeometry.ValidBounds).ToArray();

    private WorldRect PlaceTemporary(WorkspaceLiveWindow live, WorldRect world)
    {
        var occupied = PlacementObstacles(live.Identity.Handle);
        var zone = GetOrCreateTemporaryZone(occupied);
        var placed = TemporaryAreaLayout.Place(zone.Bounds, world, occupied);
        // Maximized arrivals get a normal canvas footprint. Minimized arrivals stay asleep.
        if (placed.State == WindowState.Maximized) placed.State = WindowState.Normal;
        var area = zone.Bounds;
        area.W = Math.Max(area.W, placed.X + placed.W + TemporaryAreaLayout.Gap - area.X);
        area.H = Math.Max(area.H, placed.Y + placed.H + TemporaryAreaLayout.Gap - area.Y);
        zone.Bounds = area;
        _temporaryAreaChanged = true;
        return placed;
    }

    private WorkspaceZone GetOrCreateTemporaryZone(IReadOnlyList<WorldRect> occupied)
    {
        var zone = TemporaryZone;
        if (zone == null)
        {
            var viewport = _windowActions.Viewport;
            zone = new WorkspaceZone
            {
                DesktopId = _desktop, IsTemporaryArea = true, Name = "Temporary area", Color = "#94A3B8",
                Bounds = new() { X = Math.Max(_canvas.CamX + (viewport.X + viewport.Width) / _canvas.Zoom,
                    occupied.Select(b => b.X + b.W).DefaultIfEmpty(_canvas.CamX).Max()) + 320,
                    Y = _canvas.CamY + viewport.Y / _canvas.Zoom, W = TemporaryAreaLayout.Width, H = 1200 }
            };
            Document.Zones.Add(zone);
            _temporaryAreaChanged = true;
        }
        return zone;
    }

    private void FinishTemporaryCapture()
    {
        CompletePlacementAdoptions();
        foreach (var item in Document.Items.Where(i => i.Placement == WorkspacePlacement.Temporary).ToArray())
            if (BoundWindow(item.Id) == null)
            {
                Document.Items.Remove(item);
                foreach (var zone in Document.Zones.Where(z => z.IsTemporaryArea)) zone.ItemIds.Remove(item.Id);
                _temporaryAreaChanged = true;
                RemoveCard(item.Id);
            }
        PublishTemporaryAreaChange();
        if (_arrivals > 0 && TemporaryArea is { } area) TemporaryWindowsArrived?.Invoke(area);
        _arrivals = 0;
    }

    private void PublishTemporaryAreaChange()
    {
        if (!_temporaryAreaChanged) return;
        _temporaryAreaChanged = false;
        Zones.NotifyDesktopChanged();
    }

    private void RemoveTemporaryMembership(Guid id)
    {
        foreach (var zone in Document.Zones.Where(z => z.IsTemporaryArea))
            if (zone.ItemIds.Remove(id)) _temporaryAreaChanged = true;
    }

    private bool HasDeliberatePlace(WorkspaceLiveWindow live)
        => live.ItemId is { } id && Find(id)?.Placement == WorkspacePlacement.Deliberate;

    private void StageTemporary(WorkspaceLiveWindow live, WorkspaceWindowMetadata metadata)
    {
        if (_migrationWindows.Contains(live.Identity) || live.ItemId.HasValue || Document.Items.Count >= 2000 || !StillCurrent(live)) return;
        // Registration can precede restoration of the saved camera. Use the native
        // normal rectangle in the restored coordinate system for a new arrival.
        EnrollNativeGeometry(live.Identity.Handle);
        var world = _canvas.Windows[live.Identity.Handle];
        if (!WorkspaceGeometry.ValidBounds(world)) return;
        WorldRect placed;
        try { placed = PlaceTemporary(live, world); }
        catch (InvalidOperationException)
        {
            _status = "New windows could not be placed in the temporary area. Move the area closer to the workspace.";
            return;
        }
        var item = metadata.CreateItem(_desktop, placed, WorkspacePlacement.Temporary);
        Document.Items.Add(item);
        if (!_bindings.Bind(live, item.Id)) { Document.Items.Remove(item); return; }
        TemporaryZone!.ItemIds.Add(item.Id);
        RestoreBinding(live, item);
        if (IsOpen(item)) _arrivals++;
    }

    private void DetachTemporary(WorkspaceLiveWindow live)
    {
        if (live.ItemId is not { } id || Find(id) is not { Placement: WorkspacePlacement.Temporary } item) return;
        _bindings.Unbind(live);
        Document.Items.Remove(item);
        foreach (var zone in Document.Zones.Where(z => z.IsTemporaryArea)) zone.ItemIds.Remove(id);
        _temporaryAreaChanged = true;
        RemoveCard(id);
    }

    public void KeepPlace(Guid id)
    {
        if (Find(id) is not { Placement: WorkspacePlacement.Temporary } item || BoundWindow(id) == null) return;
        if (_cards.TryGetValue(id, out var card)) SyncCardGeometry(item, card);
        else if (BoundWindow(id) is { } live && _canvas.Windows.TryGetValue(live.Identity.Handle, out var world)) item.Bounds = world;
        item.Placement = WorkspacePlacement.Deliberate;
        foreach (var zone in Document.Zones.Where(z => z.IsTemporaryArea)) zone.ItemIds.Remove(id);
        _temporaryAreaChanged = true;
        Save(); PublishTemporaryAreaChange(); Changed?.Invoke();
    }

    private void OnWindowPinned(IntPtr handle)
    {
        if (_canvas.IsPinnedToScreen(handle) && ValidBinding(handle)?.ItemId is { } id) KeepPlace(id);
    }

    // Called by explicit canvas moves, not by focus, camera movement or projection.
    internal void CommitWindowPlacement(IntPtr handle)
    {
        var live = ValidBinding(handle);
        var id = live?.ItemId ?? CardItem(handle);
        if (id is not { } itemId || Find(itemId) is not { Placement: WorkspacePlacement.Temporary } item
            || !_canvas.Windows.TryGetValue(handle, out var world) || TemporaryZone is not { } area) return;
        if (!TemporaryAreaLayout.ContainsCenter(area.Bounds, world)) KeepPlace(item.Id);
    }
}
