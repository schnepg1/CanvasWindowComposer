using System;
using System.Collections.Generic;
using System.Linq;
using CanvasDesktop;

// Navigation demo only: member actions update in-memory state and never launch/focus an application.
internal sealed class PreviewNavigationWorkspace : IWorkspaceNavigationCommands
{
    private readonly WorkspaceDocument _document;
    private readonly HashSet<Guid> _open = new();
    public string? Status { get; private set; }
    public event Action? Changed;
    public PreviewNavigationWorkspace(WorkspaceDocument document)
    { _document = document; if (document.Items.Count > 0) _open.Add(document.Items[0].Id); }
    public IReadOnlyList<WorkspaceItemView> Items => _document.Items.Select(i => new WorkspaceItemView(i.Id,
        i.Name.Length > 0 ? i.Name : i.Title, i.ExecutablePath, i.Target, i.OpenOnStartup, i.Bounds,
        _open.Contains(i.Id) ? "Open" : "Placeholder", i.Name)).ToArray();
    public void Open(Guid id)
    {
        var item = _document.Items.FirstOrDefault(i => i.Id == id);
        if (item == null) return;
        _open.Add(id); Status = "Preview only: selected " + item.Title; Changed?.Invoke();
    }
}
