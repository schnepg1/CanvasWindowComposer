using System;
using System.Text.Json;
using System.Linq;

namespace CanvasDesktop;

internal interface IWorkspaceStore
{
    WorkspaceDocument Load(out string? warning);
    void Save(WorkspaceDocument document);
}

/// <summary>Owns load failure protection and save-on-change, independently of UI.</summary>
internal sealed class WorkspacePersistence
{
    private static readonly JsonSerializerOptions FingerprintOptions = new() { IncludeFields = true };
    private readonly IWorkspaceStore _store;
    private readonly bool _canSave;
    private string? _lastSaved;
    public WorkspaceDocument Document { get; }
    public string? LoadWarning { get; }
    public string? LastError { get; private set; }

    public WorkspacePersistence(IWorkspaceStore store)
    {
        _store = store;
        try
        {
            Document = store.Load(out var warning);
            LoadWarning = warning;
            _canSave = true;
        }
        catch (Exception ex)
        {
            Document = new();
            LastError = ex.Message;
            // A failed load must never replace the user's existing workspace.
            _canSave = false;
        }
    }

    public bool Save()
    {
        if (!_canSave) return false;
        try
        {
            var saved = SavedState(Document);
            string current = JsonSerializer.Serialize(saved, FingerprintOptions);
            if (current != _lastSaved)
            {
                _store.Save(saved);
                _lastSaved = current; // Advance only after a successful atomic save.
            }
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastError = "Workspace could not be saved: " + ex.Message;
            return false;
        }
    }

    internal static WorkspaceDocument SavedState(WorkspaceDocument document)
    {
        var items = document.Items.Where(i => i.Placement != WorkspacePlacement.Temporary).ToList();
        var ids = items.Select(i => i.Id).ToHashSet();
        return new WorkspaceDocument
        {
            Version = document.Version, PlacementPolicyInitialized = document.PlacementPolicyInitialized,
            InventoryOpen = document.InventoryOpen,
            PendingPlacementAdoptions = new(document.PendingPlacementAdoptions),
            TemporaryAreas = new(document.TemporaryAreas), Cameras = new(document.Cameras), Items = items,
            Zones = document.Zones.Select(z => new WorkspaceZone { Id = z.Id, DesktopId = z.DesktopId,
                Name = z.Name, Color = z.Color, Bounds = z.Bounds, IsTemporaryArea = z.IsTemporaryArea,
                ItemIds = z.IsTemporaryArea ? new() : z.ItemIds.Where(ids.Contains).ToList() }).ToList()
        };
    }
}
