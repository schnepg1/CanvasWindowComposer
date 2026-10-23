using System;
using System.Collections.Generic;

namespace CanvasDesktop;

// A detached snapshot: forms cannot mutate the persisted model by editing a row.
internal sealed record WorkspaceItemView(Guid Id, string Title, string ExecutablePath,
    string Target, bool OpenOnStartup, WorldRect Bounds, string State, string Name = "", bool IsFolder = false,
    string ChromeProfilePath = "");

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
    void FocusItem(Guid id);
    void CloseToPlaceholder(Guid id);
    void Forget(Guid id);
    void SetAutoOpen(Guid id, bool value);
    void SetTarget(Guid id, string selected, bool folder);
    void SetName(Guid id, string name);
    bool SetChromeProfile(Guid id, string profilePath);
    IReadOnlyList<WorkspaceLinkCandidate> LinkCandidates(Guid id);
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
}
