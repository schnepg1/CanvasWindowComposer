using System;
using System.Text.Json;

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
            string current = JsonSerializer.Serialize(Document, FingerprintOptions);
            if (current != _lastSaved)
            {
                _store.Save(Document);
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
}
