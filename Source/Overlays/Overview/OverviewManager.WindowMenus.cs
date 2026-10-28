using System;
using System.Drawing;
using System.Windows.Forms;

namespace CanvasDesktop;

// Window menu routing and ownership.
internal sealed partial class OverviewManager
{
    private WorkspaceContextMenu? _windowContextMenu;
    private ContextMenuStrip? _activeCardMenu;
    private ContextMenuStrip? _interactionMenu;

    private bool TryShowWindowContextMenu(OverviewOverlay pass, Point location)
    {
        var (wx, wy) = _camera.WorldFromVirtual(location.X + pass.OriginX, location.Y + pass.OriginY);
        int hit = HitOverviewWindow(pass, location.X, location.Y, wx, wy);
        if (hit < 0) return false;
        // Resolving a workspace entry can refresh/reorder the overview. Retain
        // the clicked HWND before publishing workspace changes.
        var window = _windows.Windows[hit].HWnd;
        if (Control.FromHandle(window) is PlaceholderForm card)
        {
            PInvoke.SetForegroundWindow((HWND)pass.Handle);
            _activeCardMenu = card.ContextMenuStrip; // Borrowed; the card owns disposal.
            card.ShowContextMenu(pass, location);
        }
        else if (_placeholderWorkspace?.GetWindowContextItem(window) is { } id)
        {
            _windowContextMenu?.Dispose();
            _windowContextMenu = new WorkspaceContextMenu(_placeholderWorkspace, id);
            AddWindowInteractionCommand(_windowContextMenu, pass, window);
            PInvoke.SetForegroundWindow((HWND)pass.Handle);
            _windowContextMenu.Show(pass, location);
        }
        else ShowWindowInteractionMenu(pass, window, location);
        return true;
    }

    internal void AddWindowInteractionCommand(ContextMenuStrip menu, OverviewOverlay pass, IntPtr window)
    {
        // Saved live windows also use WorkspaceContextMenu. Do not let that
        // branch hide the interaction action; placeholders/cached cards cannot forward input.
        if (!CanInteract(pass, window)) return;
        var interact = new ToolStripMenuItem("Interact with window (mouse and keyboard)");
        interact.Click += (_, _) => pass.BeginInvoke(() =>
        {
            if (!_disposed && CurrentMode == OverviewMode.Zooming) BeginWindowInteraction(pass, window);
        });
        menu.Items.Insert(0, interact);
        menu.Items.Insert(1, new ToolStripSeparator());
    }

    private void ShowWindowInteractionMenu(OverviewOverlay pass, IntPtr window, Point location)
    {
        _interactionMenu?.Dispose();
        var menu = _interactionMenu = new ContextMenuStrip();
        var interact = menu.Items.Add("Interact with window (mouse and keyboard)");
        interact.Enabled = CanInteract(pass, window);
        interact.Click += (_, _) => pass.BeginInvoke(() =>
        {
            if (!_disposed && CurrentMode == OverviewMode.Zooming) BeginWindowInteraction(pass, window);
        });
        menu.Items.Add("Open window at desktop size", null, (_, _) =>
        {
            foreach (var entry in _windows.Windows)
                if (entry.HWnd == window) { GoToWindow(window, entry.World); break; }
        });
        PInvoke.SetForegroundWindow((HWND)pass.Handle);
        menu.Show(pass, location);
    }

    public bool CloseActiveMenus()
    {
        bool hadMenu = false;
        if (_activeCardMenu != null && _activeCardMenu.Visible)
        {
            _activeCardMenu.Close();
            _activeCardMenu = null;
            hadMenu = true;
        }
        if (_windowContextMenu != null && _windowContextMenu.Visible)
        {
            _windowContextMenu.Close();
            _windowContextMenu = null;
            hadMenu = true;
        }
        if (_interactionMenu != null && _interactionMenu.Visible)
        {
            _interactionMenu.Close();
            _interactionMenu = null;
            hadMenu = true;
        }
        if (_annotationToolbar != null && _annotationToolbar.HasOpenDropDown)
        {
            _annotationToolbar.CloseDropDowns();
            hadMenu = true;
        }
        return hadMenu;
    }
}
