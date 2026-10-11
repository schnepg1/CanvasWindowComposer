using System;
using System.IO;

namespace CanvasDesktop;

/// <summary>The production wiring; tests can replace every OS/UI boundary.</summary>
internal static class WorkspaceComposition
{
    public static WorkspaceService Create(Canvas canvas, WindowManager wm, IWindowApi api,
        IVirtualDesktops desktops, DesktopStateCache cache, string? path = null,
        IScreens? screens = null, IClock? clock = null)
        => new(canvas, wm, api, desktops, cache,
            new WorkspaceStore(path ?? Path.Combine(AppConfig.ConfigDir, "workspace.json")),
            new WinFormsPlaceholderFactory(), new WorkspaceWindowActions(screens ?? WinFormsScreens.Instance),
            new WorkspaceLauncher(), new WorkspaceIdentity(), clock ?? SystemClock.Instance);
}

internal sealed class WinFormsPlaceholderFactory : IWorkspacePlaceholderFactory
{
    public IWorkspacePlaceholder Create(IWorkspaceCommands workspace, WorkspaceItemView item)
        => new PlaceholderForm(workspace, item);
}

internal sealed class WorkspaceWindowActions : IWorkspaceWindowActions
{
    private readonly IScreens _screens;
    public WorkspaceWindowActions(IScreens screens) { _screens = screens; }
    public ScreenRect Viewport => _screens.PrimaryWorkingArea;
    public void Focus(IntPtr handle, bool restore)
    {
        if (restore) PInvoke.ShowWindow((HWND)handle, SHOW_WINDOW_CMD.SW_RESTORE);
        PInvoke.SetForegroundWindow((HWND)handle);
    }
    // Normal close preserves application save prompts and cancellation.
    public void RequestClose(IntPtr handle) => PInvoke.PostMessage((HWND)handle, 0x0010, 0, 0);
}
