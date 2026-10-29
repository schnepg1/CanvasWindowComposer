using System;
using System.Collections.Generic;

namespace CanvasDesktop;

// A detached snapshot: forms cannot mutate the persisted model by editing a row.
internal sealed record WorkspaceItemView(Guid Id, string Title, string ExecutablePath,
    string Target, bool OpenOnStartup, WorldRect Bounds, string State, string Name = "", bool IsFolder = false,
    string ChromeProfilePath = "", string? LaunchMessage = null, WebResource? WebResource = null,
    bool ExcludeFromAutomaticMinimization = false, bool Temporary = false)
{
    public string DisplayTarget => WebResource?.Url ?? Target;
    public string BrowserDetails => WebResource == null ? "" : BrowserSupport.Name(ExecutablePath)
        + (WebResource.ProfilePath.Length > 0 ? " · " + System.IO.Path.GetFileName(WebResource.ProfilePath) : " · Default profile");
}

internal sealed record WorkspaceLinkCandidate(Guid Token, string Description);

internal interface IWorkspaceNavigationCommands
{
    IReadOnlyList<WorkspaceItemView> Items { get; }
    string? Status { get; }
    event Action? Changed;
    void Open(Guid id);
}

internal interface IWorkspaceCommands : IWorkspaceNavigationCommands
{
    bool CollectInInventory(Guid id);
    void KeepPlace(Guid id);
    void FocusItem(Guid id);
    void CloseToPlaceholder(Guid id);
    void Forget(Guid id);
    void SetAutoOpen(Guid id, bool value);
    void SetAutomaticMinimizationExcluded(Guid id, bool value);
    void SetTarget(Guid id, string selected, bool folder);
    void SetName(Guid id, string name);
    bool SetChromeProfile(Guid id, string profilePath);
    bool SaveWebResource(Guid? id, string executable, string name, WebResource resource, WorldRect? bounds = null);
    IReadOnlyList<WorkspaceLinkCandidate> LinkCandidates(Guid id);
    string? LinkCandidateHint(Guid id) => null;
    bool LinkWindow(Guid id, Guid candidateToken);
}

internal interface IWorkspacePlaceholder : IDisposable
{
    IntPtr Handle { get; }
    event Action? PlacementChanged;
    void UpdateItem(WorkspaceItemView item);
    void ShowAt(WindowRect rect);
}

internal interface IWorkspacePlaceholderFactory
{
    IWorkspacePlaceholder Create(IWorkspaceCommands workspace, WorkspaceItemView item);
}

internal interface IWorkspaceWindowActions
{
    ScreenRect Viewport { get; }
    void Focus(IntPtr handle, bool restore);
    void RequestClose(IntPtr handle);
}

internal interface IWorkspaceIdentity
{
    Dictionary<IntPtr, string> ReadExplorerFolders();
    WorkspaceProcessIdentity? InspectProcess(uint pid);
    string? ReadWindowAppId(IntPtr handle);
    string? ReadVsCodeTarget(WorkspaceWindowIdentity window, string executablePath, string title);
}
