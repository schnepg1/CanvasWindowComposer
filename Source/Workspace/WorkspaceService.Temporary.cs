using System;
using System.Collections.Generic;
using System.Linq;

namespace CanvasDesktop;

internal sealed record TemporaryAreaView(WorldRect Bounds, int Count);

internal sealed partial class WorkspaceService
{
    private readonly HashSet<WorkspaceWindowIdentity> _migrationWindows = new();
    private readonly Dictionary<Guid, WorldRect> _temporaryBounds = new();
    private int _arrivals;
    internal Func<IReadOnlyList<WorldRect>>? AdditionalPlacementObstacles { get; set; }
    public event Action<TemporaryAreaView>? TemporaryWindowsArrived;

    public TemporaryAreaView? TemporaryArea
    {
        get
        {
            int count = Document.Items.Count(i => i.DesktopId == _desktop && i.Placement == WorkspacePlacement.Temporary);
            return count > 0 && _temporaryBounds.TryGetValue(_desktop, out var bounds) ? new(bounds, count) : null;
        }
    }

    private void InitializePlacementPolicy()
    {
        if (Document.PlacementPolicyInitialized) return;
        // Adopt only windows present at the upgrade boundary, including windows on
        // other desktops and identities whose folder/project lookup completes later.
        _api.EnumWindows(handle =>
        {
            if (_api.GetWindowProcessId(handle) != (uint)Environment.ProcessId
                && InspectWindow(handle) is { } observed) _migrationWindows.Add(observed.Identity);
            return true;
        });
        Document.PlacementPolicyInitialized = true;
    }

    private IReadOnlyList<WorldRect> PlacementObstacles(IntPtr except)
        => Document.Items.Where(i => i.DesktopId == _desktop && !i.Bounds.PinnedToScreen).Select(i => i.Bounds)
            .Concat(Document.Zones.Where(z => z.DesktopId == _desktop).Select(z => z.Bounds))
            .Concat(_canvas.Windows.Where(p => p.Key != except && !p.Value.PinnedToScreen).Select(p => p.Value))
            .Concat(AdditionalPlacementObstacles?.Invoke() ?? Array.Empty<WorldRect>())
            .Where(WorkspaceGeometry.ValidBounds).ToArray();

    private WorldRect PlaceTemporary(WorkspaceLiveWindow live, WorldRect world)
    {
        var occupied = PlacementObstacles(live.Identity.Handle);
        if (!Document.TemporaryAreas.TryGetValue(_desktop, out var anchor))
        {
            var viewport = _windowActions.Viewport;
            anchor = new WorkspaceTemporaryArea
            {
                X = Math.Max(_canvas.CamX + (viewport.X + viewport.Width) / _canvas.Zoom,
                    occupied.Select(b => b.X + b.W).DefaultIfEmpty(_canvas.CamX).Max()) + 320,
                Y = _canvas.CamY + viewport.Y / _canvas.Zoom
            };
            Document.TemporaryAreas[_desktop] = anchor;
        }
        var placed = TemporaryAreaLayout.Place(anchor, world, occupied);
        // Maximized arrivals get a normal canvas footprint. Minimized arrivals stay asleep.
        if (placed.State == WindowState.Maximized) placed.State = WindowState.Normal;
        var area = _temporaryBounds.GetValueOrDefault(_desktop,
            new WorldRect { X = anchor.X, Y = anchor.Y, W = TemporaryAreaLayout.Width, H = 1200 });
        area.W = Math.Max(area.W, placed.X + placed.W + TemporaryAreaLayout.Gap - area.X);
        area.H = Math.Max(area.H, placed.Y + placed.H + TemporaryAreaLayout.Gap - area.Y);
        _temporaryBounds[_desktop] = area;
        return placed;
    }

    private void FinishTemporaryCapture()
    {
        foreach (var item in Document.Items.Where(i => i.Placement == WorkspacePlacement.Temporary).ToArray())
            if (BoundWindow(item.Id) == null)
            {
                Document.Items.Remove(item);
                RemoveCard(item.Id);
            }
        if (_arrivals > 0 && TemporaryArea is { } area) TemporaryWindowsArrived?.Invoke(area);
        _arrivals = 0;
    }

    private bool HasDeliberatePlace(WorkspaceLiveWindow live)
        => live.ItemId is { } id && Find(id)?.Placement == WorkspacePlacement.Deliberate;

    private void StageTemporary(WorkspaceLiveWindow live, string exe, string title, string target, bool explorer)
    {
        if (_migrationWindows.Contains(live.Identity) || live.ItemId.HasValue || Document.Items.Count >= 2000 || !StillCurrent(live)) return;
        // Registration can precede restoration of the saved camera. Use the native
        // normal rectangle in the restored coordinate system for a new arrival.
        EnrollNativeGeometry(live.Identity.Handle);
        var world = _canvas.Windows[live.Identity.Handle];
        if (!WorkspaceGeometry.ValidBounds(world)) return;
        var item = new WorkspaceItem { DesktopId = _desktop, Title = title, ExecutablePath = exe, Target = target,
            AutoDetectedTarget = WorkspaceTargetIdentity.IsVsCode(exe) && target.Length > 0,
            IsFolder = explorer && target.Length > 0, Placement = WorkspacePlacement.Temporary,
            Bounds = PlaceTemporary(live, world) };
        Document.Items.Add(item);
        if (!_bindings.Bind(live, item.Id)) { Document.Items.Remove(item); return; }
        RestoreBinding(live, item);
        if (IsOpen(item)) _arrivals++;
    }

    private void DetachTemporary(WorkspaceLiveWindow live)
    {
        if (live.ItemId is not { } id || Find(id) is not { Placement: WorkspacePlacement.Temporary } item) return;
        _bindings.Unbind(live);
        Document.Items.Remove(item);
        RemoveCard(id);
    }

    public void KeepPlace(Guid id)
    {
        if (Find(id) is not { Placement: WorkspacePlacement.Temporary } item || BoundWindow(id) == null) return;
        if (_cards.TryGetValue(id, out var card)) SyncCardGeometry(item, card);
        else if (BoundWindow(id) is { } live && _canvas.Windows.TryGetValue(live.Identity.Handle, out var world)) item.Bounds = world;
        item.Placement = WorkspacePlacement.Deliberate;
        Save(); Changed?.Invoke();
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
            || !_canvas.Windows.TryGetValue(handle, out var world) || !_temporaryBounds.TryGetValue(_desktop, out var area)) return;
        if (!TemporaryAreaLayout.ContainsCenter(area, world)) KeepPlace(item.Id);
    }
}
