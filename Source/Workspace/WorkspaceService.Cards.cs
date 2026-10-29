using System;
using System.Collections.Generic;
using System.Linq;

namespace CanvasDesktop;

// Placeholder lifecycle, geometry synchronization and presentation snapshots.
internal sealed partial class WorkspaceService
{
    private bool _overviewCardMode;

    private readonly Dictionary<Guid, IWorkspacePlaceholder> _cards = new();

    // Only actual cards on the current desktop are annotated. Their geometry is
    // supplied by the overview, so labels follow a drag before the next save.
    public IReadOnlyDictionary<IntPtr, string> GetPlaceholderTitles()
    {
        var titles = new Dictionary<IntPtr, string>();
        foreach (var item in Document.Items)
            if (_cards.TryGetValue(item.Id, out var card))
                titles[card.Handle] = item.Name.Length > 0 ? item.Name : item.Title;
        return titles;
    }

    public Guid? LiveCardItem(IntPtr handle)
    {
        foreach (var (id, card) in _cards)
            if (card.Handle == handle && BoundWindow(id) != null) return id;
        return null;
    }

    public Guid? CardItem(IntPtr handle)
    {
        foreach (var (id, card) in _cards)
            if (card.Handle == handle) return id;
        return null;
    }

    public bool SetOverviewCardMode(bool enabled)
    {
        if (_disposed || !_started || _switching || _overviewCardMode == enabled) return false;
        _overviewCardMode = enabled;
        EnsureCards();
        return true;
    }

    private void EnsureCards()
    {
        InvalidateOverviewSnapshot();
        foreach (var id in _cards.Keys.Where(id => !Document.Items.Any(i => i.Id == id)).ToArray()) RemoveCard(id);
        foreach (var item in Document.Items.Where(i => i.DesktopId == _desktop).ToArray())
        {
            if (item.Bounds.InInventory) { RemoveCard(item.Id); continue; }
            var live = BoundWindow(item.Id);
            bool minimized = live != null && (_api.GetWindowStyle(live.Identity.Handle) & (int)WINDOW_STYLE.WS_MINIMIZE) != 0;
            bool preview = live != null && _overviewCardMode && !item.Bounds.PinnedToScreen
                && item.Bounds.State != WindowState.Maximized;
            if (live != null && !minimized && !preview && !_wm.UsesCachedPreview(live.Identity.Handle))
            { RemoveCard(item.Id); continue; }
            if (_cards.ContainsKey(item.Id))
            {
                var card = _cards[item.Id];
                card.UpdateItem(View(item));
                continue;
            }
            var form = _placeholderFactory.Create(this, View(item));
            _cards[item.Id] = form;
            // Position before Show avoids flashing a card at the desktop origin.
            var rect = _canvas.WorldToScreen(item.Bounds);
            form.ShowAt(rect);
            var bounds = item.Bounds;
            bounds.State = WindowState.Normal;
            bounds.PinnedToScreen = false;
            _wm.RestorePlacement(form.Handle, bounds);
            form.PlacementChanged += () =>
            {
                if (_disposed || _switching) return;
                _wm.ReconcileWindow(form.Handle);
                SyncCardGeometry(item, form);
            };
        }
    }

    private void SyncCardGeometry(WorkspaceItem item, IWorkspacePlaceholder card)
    {
        if (!_canvas.Windows.TryGetValue(card.Handle, out var world) || !WorkspaceGeometry.ValidBounds(world)) return;
        var bounds = item.Bounds;
        if (bounds.X == world.X && bounds.Y == world.Y && bounds.W == world.W && bounds.H == world.H) return;
        bounds.X = world.X; bounds.Y = world.Y; bounds.W = world.W; bounds.H = world.H;
        item.Bounds = bounds;
        if (BoundWindow(item.Id) is { } live)
            _canvas.SetWindow(live.Identity.Handle, bounds.X, bounds.Y, bounds.W, bounds.H);
    }

    private void RemoveCard(Guid id)
    {
        InvalidateOverviewSnapshot();
        if (!_cards.Remove(id, out var card)) return;
        try { _wm.RemoveWindow(card.Handle); }
        finally { card.Dispose(); }
    }

    public string ItemState(WorkspaceItem item) => _launching.ContainsKey(item.Id) ? "Opening…"
        : _queued.Contains(item.Id) ? "Queued" : BoundWindow(item.Id) is { } live
            ? (_api.GetWindowStyle(live.Identity.Handle) & (int)WINDOW_STYLE.WS_MINIMIZE) != 0
                ? _wm.UsesCachedPreview(live.Identity.Handle) ? "Sleeping" : "Minimized" : "Open"
        : _ambiguous.Contains(item.Id) ? "Needs linking" : "Placeholder";

    private WorkspaceItemView View(WorkspaceItem item) => new(item.Id, item.Name.Length > 0 ? item.Name : item.Title, item.ExecutablePath,
        item.Target, item.OpenOnStartup, item.Bounds, ItemState(item), item.Name, item.IsFolder, item.ChromeProfilePath,
        _launchMessages.GetValueOrDefault(item.Id), item.WebResource, item.ExcludeFromAutomaticMinimization,
        item.Placement == WorkspacePlacement.Temporary);
}
