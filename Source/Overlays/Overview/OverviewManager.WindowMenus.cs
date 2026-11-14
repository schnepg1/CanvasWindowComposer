using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace CanvasDesktop;

// Window menu routing and ownership.
internal sealed partial class OverviewManager
{
    private WorkspaceContextMenu? _windowContextMenu;
    private ContextMenuStrip? _activeCardMenu;
    private ContextMenuStrip? _interactionMenu;
    private ContextMenuStrip? _annotationContextMenu;

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
            if (_activeCardMenu != null)
            {
                AddWindowPeekCommand(_activeCardMenu, pass, window, borrowed: true);
                AddMainPlaceholderFlight(_activeCardMenu, pass, window);
            }
            card.ShowContextMenu(pass, location);
        }
        else if (_placeholderWorkspace?.GetWindowContextItem(window) is { } id)
        {
            _windowContextMenu?.Dispose();
            _windowContextMenu = new WorkspaceContextMenu(_placeholderWorkspace, id, ExportWorkflowFromWindow);
            AddWindowInteractionCommand(_windowContextMenu, pass, window);
            AddWindowPeekCommand(_windowContextMenu, pass, window);
            PInvoke.SetForegroundWindow((HWND)pass.Handle);
            _windowContextMenu.Show(pass, location);
        }
        else ShowWindowInteractionMenu(pass, window, location);
        return true;
    }

    private void AddMainPlaceholderFlight(ContextMenuStrip menu, OverviewOverlay pass, IntPtr window)
    {
        if (_placeholderWorkspace?.ResolveMainNavigationWindow(window) is not { } main || main == window) return;
        var fly = new ToolStripMenuItem("Fly to main placeholder");
        fly.Click += (_, _) => pass.BeginInvoke(() => NavigateToWindow(window));
        menu.Items.Insert(0, fly);
        menu.Closed += Remove;

        void Remove(object? sender, ToolStripDropDownClosedEventArgs args)
        {
            menu.Closed -= Remove;
            menu.Items.Remove(fly);
            fly.Dispose();
        }
    }

    private void ExportWorkflowFromWindow(Guid windowId)
    {
        ExportCurrentWorkflowToClipboard(windowId);
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
        AddWindowPeekCommand(menu, pass, window);
        PInvoke.SetForegroundWindow((HWND)pass.Handle);
        menu.Show(pass, location);
    }

    internal void AddWindowPeekCommand(ContextMenuStrip menu, OverviewOverlay pass, IntPtr window, bool borrowed = false)
    {
        if (!_appConfig.EnableCanvasPeek || !TryResolvePeekSource(window, out _, out _)) return;
        var peek = new ToolStripMenuItem("Preview window (Canvas Peek)");
        peek.Click += (_, _) => pass.BeginInvoke(() =>
        {
            if (!_disposed && CurrentMode == OverviewMode.Zooming) ShowWindowPeek(pass, window);
        });
        menu.Items.Insert(0, peek);
        if (borrowed)
        {
            void Remove(object? sender, ToolStripDropDownClosedEventArgs args)
            {
                menu.Closed -= Remove;
                menu.Items.Remove(peek);
                peek.Dispose();
            }
            menu.Closed += Remove;
        }
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
        if (_annotationContextMenu != null && _annotationContextMenu.Visible)
        {
            _annotationContextMenu.Close();
            _annotationContextMenu = null;
            hadMenu = true;
        }
        if (_annotationToolbar != null && _annotationToolbar.HasOpenDropDown)
        {
            _annotationToolbar.CloseDropDowns();
            _unifiedDock?.Panel.CloseDropDowns();
            hadMenu = true;
        }
        return hadMenu;
    }

    private bool TryShowAnnotationContextMenu(OverviewOverlay pass, Point location)
    {
        var pointer = Pointer(pass, new MouseEventArgs(MouseButtons.Right, 1, location.X, location.Y, 0));
        if (_annotationDocument != null && _annotationTool != null)
        {
            var note = _annotationDocument.Items.LastOrDefault(a => a.DesktopId == _annotationDesktop()
                && !a.InInventory && a.Kind == CanvasDesktop.CanvasView.AnnotationKind.Text
                && CanvasDesktop.CanvasView.AnnotationGeometry.Hit(a, pointer.World, pointer.View));
            if (note != null)
            {
                _annotationContextMenu?.Dispose();
                var menu = _annotationContextMenu = new ContextMenuStrip();
                var putInBackpack = new ToolStripMenuItem("Put in backpack");
                putInBackpack.Click += (_, _) => pass.BeginInvoke(() =>
                {
                    if (!_disposed) CollectNoteInInventory(note.Id);
                });
                menu.Items.Add(putInBackpack);

                menu.Items.Add(new ToolStripSeparator());

                if (MissionAgentCard.IsCard(note))
                {
                    var card = new MissionAgentCard(note);
                    var openAgent = new ToolStripMenuItem("Open agent");
                    openAgent.Click += (_, _) => pass.BeginInvoke(() =>
                    {
                        if (!_disposed) ShowMissionAgent(card);
                    });
                    menu.Items.Add(openAgent);

                    var deleteCard = new ToolStripMenuItem("Delete agent card");
                    deleteCard.Click += (_, _) => pass.BeginInvoke(() =>
                    {
                        if (!_disposed)
                        {
                            _annotationDocument.Delete(note.DesktopId, note.Id);
                            _annotationStore?.Save();
                            Selection.Clear();
                            ReconcileVisuals();
                        }
                    });
                    menu.Items.Add(deleteCard);
                }
                else
                {
                    var editNote = new ToolStripMenuItem("Edit note…");
                    editNote.Click += (_, _) => pass.BeginInvoke(() =>
                    {
                        if (!_disposed)
                        {
                            _annotationTool.Select(note.Id);
                            QueueAnnotationTextEdit(note, new Point(location.X + pass.OriginX, location.Y + pass.OriginY));
                        }
                    });
                    menu.Items.Add(editNote);

                    var cutNote = new ToolStripMenuItem("Cut note");
                    cutNote.Click += (_, _) => pass.BeginInvoke(() =>
                    {
                        if (!_disposed)
                        {
                            _annotationTool.Select(note.Id);
                            CutSelectedAnnotations();
                        }
                    });
                    menu.Items.Add(cutNote);

                    var copyNote = new ToolStripMenuItem("Copy note");
                    copyNote.Click += (_, _) => pass.BeginInvoke(() =>
                    {
                        if (!_disposed)
                        {
                            _annotationTool.Select(note.Id);
                            CopySelectedAnnotations();
                        }
                    });
                    menu.Items.Add(copyNote);

                    var deleteNote = new ToolStripMenuItem("Delete note");
                    deleteNote.Click += (_, _) => pass.BeginInvoke(() =>
                    {
                        if (!_disposed)
                        {
                            _annotationDocument.Delete(note.DesktopId, note.Id);
                            _annotationStore?.Save();
                            Selection.Clear();
                            ReconcileVisuals();
                        }
                    });
                    menu.Items.Add(deleteNote);
                }

                PInvoke.SetForegroundWindow((HWND)pass.Handle);
                menu.Show(pass, location);
                return true;
            }
        }

        if (_zones != null)
        {
            var zone = _zones.Items.LastOrDefault(z => !z.IsTemporaryArea && !z.IsMission
                && ZoneLabelLayout.Get(z.Bounds, pointer.View) is { } label
                && label.Contains(pointer.Position.X, pointer.Position.Y));
            if (zone != null)
            {
                _annotationContextMenu?.Dispose();
                var menu = _annotationContextMenu = new ContextMenuStrip();
                var chooseFolder = new ToolStripMenuItem("Choose project folder…");
                chooseFolder.Click += (_, _) => pass.BeginInvoke(() =>
                {
                    if (!_disposed) ChooseProjectFolder(pass, zone.Id);
                });
                menu.Items.Add(chooseFolder);
                menu.Items.Add(new ToolStripSeparator());

                var putProjectInBackpack = new ToolStripMenuItem("Put in backpack");
                putProjectInBackpack.Click += (_, _) => pass.BeginInvoke(() =>
                {
                    if (!_disposed) CollectProjectInInventory(zone.Id);
                });
                menu.Items.Add(putProjectInBackpack);

                PInvoke.SetForegroundWindow((HWND)pass.Handle);
                menu.Show(pass, location);
                return true;
            }
        }

        return false;
    }

    internal Func<string?, string?>? ProjectFolderPickerOverride { get; set; }

    private void ChooseProjectFolder(OverviewOverlay pass, Guid zoneId)
    {
        if (_placeholderWorkspace == null) return;
        WorkspaceDialogs.ChooseProjectFolder(pass, _placeholderWorkspace, zoneId,
            report: msg => _zoneWindow?.Report(msg),
            folderPicker: ProjectFolderPickerOverride);
    }
}
