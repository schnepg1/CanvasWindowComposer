using System;
using System.Collections.Generic;
using System.Linq;

namespace CanvasDesktop;

/// <summary>Desktop-scoped commands and bounded session history; no image or native window ownership.</summary>
internal sealed class BackgroundService
{
    private const int HistoryLimit = 50;
    private sealed record Edit(BackgroundSurface[] Before, BackgroundSurface[] After);
    private sealed class Timeline
    {
        public List<Edit> Undo { get; } = new();
        public List<Edit> Redo { get; } = new();
    }

    private readonly BackgroundPersistence _persistence;
    private readonly Func<Guid> _desktop;
    private readonly Func<IReadOnlyList<ZoneView>> _zones;
    private readonly Dictionary<Guid, Timeline> _history = new();
    public event Action? Changed;
    public Guid DesktopId => _desktop();
    public string? Status => _persistence.Status;
    public bool CanUndo => History.Undo.Count > 0;
    public bool CanRedo => History.Redo.Count > 0;
    private Timeline History
    {
        get
        {
            if (!_history.TryGetValue(DesktopId, out var timeline)) _history.Add(DesktopId, timeline = new());
            return timeline;
        }
    }

    public BackgroundService(BackgroundPersistence persistence, Func<Guid> desktop, Func<IReadOnlyList<ZoneView>> zones)
    { _persistence = persistence; _desktop = desktop; _zones = zones; }

    public IReadOnlyList<BackgroundSurface> Items
    {
        get
        {
            var zones = CurrentZones();
            return Snapshot().Select(item => Effective(item, zones, detachMissing: false))
                .OrderBy(item => item.ZIndex).ThenBy(item => item.Id).ToArray();
        }
    }

    public bool Add(BackgroundSurface item)
    {
        if (!ValidCommand(item) || _persistence.Items.Count >= BackgroundRules.MaximumSurfaces
            || _persistence.Items.Any(existing => existing.Id == item.Id)) return false;
        var before = Snapshot();
        _persistence.Items.Add(Effective(item with { Name = item.Name.Trim() }, CurrentZones(), detachMissing: false));
        return Record(before);
    }

    public bool Update(BackgroundSurface item)
    {
        if (item == null) return false;
        int index = _persistence.Items.FindIndex(existing => existing.Id == item.Id && existing.DesktopId == DesktopId);
        if (index < 0 || !ValidCommand(item)) return false;
        var before = Snapshot();
        _persistence.Items[index] = Effective(item with { Name = item.Name.Trim() }, CurrentZones(), detachMissing: false);
        return Record(before);
    }

    public bool Remove(Guid id)
    {
        int index = _persistence.Items.FindIndex(existing => existing.Id == id && existing.DesktopId == DesktopId);
        if (index < 0) return false;
        var before = Snapshot();
        _persistence.Items.RemoveAt(index);
        return Record(before);
    }

    public bool Undo() => Replay(redo: false);
    public bool Redo() => Replay(redo: true);

    /// <summary>Called after area edits. The saved last bounds make deleting an area lossless.</summary>
    public void ReconcileZones()
    {
        var zones = CurrentZones();
        bool changed = false;
        for (int i = 0; i < _persistence.Items.Count; i++)
        {
            var item = _persistence.Items[i];
            if (item.DesktopId != DesktopId || item.ZoneId == null) continue;
            var updated = Effective(item, zones, detachMissing: true);
            if (updated == item) continue;
            _persistence.Items[i] = updated;
            changed = true;
        }
        // Area geometry is owned by project history, not background history.
        // Rebase snapshots too, including removed placements, so undoing an
        // unrelated edit cannot resurrect an area's old pre-detachment frame.
        if (_history.TryGetValue(DesktopId, out var history))
        {
            void Rebase(List<Edit> edits)
            {
                for (int i = 0; i < edits.Count; i++)
                    edits[i] = new(
                        edits[i].Before.Select(item => Effective(item, zones, detachMissing: true)).ToArray(),
                        edits[i].After.Select(item => Effective(item, zones, detachMissing: true)).ToArray());
            }
            Rebase(history.Undo); Rebase(history.Redo);
        }
        if (changed) _persistence.Save();
        Changed?.Invoke();
    }

    public void NotifyDesktopChanged() => ReconcileZones();

    // Also exposes a retry after a failed save without requiring another edit.
    public bool Save() { bool saved = _persistence.Save(); Changed?.Invoke(); return saved; }

    private bool ValidCommand(BackgroundSurface item) => BackgroundRules.Valid(item) && item.DesktopId == DesktopId
        && (item.ZoneId == null || CurrentZones().ContainsKey(item.ZoneId.Value));

    private Dictionary<Guid, ZoneView> CurrentZones() => _zones().Where(zone => !zone.IsTemporaryArea)
        .GroupBy(zone => zone.Id).ToDictionary(group => group.Key, group => group.First());

    private static BackgroundSurface Effective(BackgroundSurface item, IReadOnlyDictionary<Guid, ZoneView> zones, bool detachMissing)
    {
        if (item.ZoneId is not Guid zoneId) return item;
        return zones.TryGetValue(zoneId, out var zone) ? item with { Bounds = zone.Bounds }
            : detachMissing ? item with { ZoneId = null } : item;
    }

    private BackgroundSurface[] Snapshot() => _persistence.Items.Where(item => item.DesktopId == DesktopId).ToArray();

    private bool Record(BackgroundSurface[] before)
    {
        var after = Snapshot();
        if (!before.SequenceEqual(after))
        {
            History.Undo.Add(new(before, after));
            if (History.Undo.Count > HistoryLimit) History.Undo.RemoveAt(0);
            History.Redo.Clear();
        }
        return Save();
    }

    private bool Replay(bool redo)
    {
        var history = History;
        var source = redo ? history.Redo : history.Undo;
        var destination = redo ? history.Undo : history.Redo;
        if (source.Count == 0) return false;
        var edit = source[^1];
        var target = redo ? edit.After : edit.Before;
        var zones = CurrentZones();
        var restored = target.Select(item => Effective(item, zones, detachMissing: true)).ToArray();
        // Other desktops may have filled the document since this edit was recorded.
        var otherIds = _persistence.Items.Where(item => item.DesktopId != DesktopId).Select(item => item.Id).ToHashSet();
        if (otherIds.Count + restored.Length > BackgroundRules.MaximumSurfaces || restored.Any(item => otherIds.Contains(item.Id)))
            return false;
        _persistence.Items.RemoveAll(item => item.DesktopId == DesktopId);
        _persistence.Items.AddRange(restored);
        source.RemoveAt(source.Count - 1);
        destination.Add(edit);
        return Save();
    }
}
