using System;
using System.Linq;
using System.Windows.Forms;

namespace CanvasDesktop;

/// <summary>One menu for native previews, detailed cards and distant title rectangles.</summary>
internal sealed class WorkspaceContextMenu : ContextMenuStrip
{
    private readonly ToolStripMenuItem _auto, _profile;

    public WorkspaceContextMenu(IWorkspaceCommands workspace, Guid id)
    {
        IWin32Window Owner() => (IWin32Window?)SourceControl?.FindForm() ?? (IWin32Window?)SourceControl ?? new NativeDialogOwner(IntPtr.Zero);
        Items.Add("Open", null, (_, _) => workspace.Open(id));
        Items.Add("Link open window…", null, (_, _) => WorkspaceDialogs.LinkWindow(Owner(), workspace, id));
        Items.Add("Rename…", null, (_, _) => WorkspaceDialogs.NamePlace(Owner(), workspace, id));
        _profile = new ToolStripMenuItem("Chrome profile…", null, (_, _) => WorkspaceInteractionDialogs.ChooseChromeProfile(Owner().Handle, workspace, id));
        Items.Add(_profile);
        _auto = new ToolStripMenuItem("Open automatically when Canvas starts") { CheckOnClick = true };
        _auto.Click += (_, _) => workspace.SetAutoOpen(id, _auto.Checked); Items.Add(_auto);
        Items.Add("Choose file to reopen…", null, (_, _) => WorkspaceDialogs.ChooseTarget(Owner(), workspace, id, false));
        Items.Add("Choose folder to reopen…", null, (_, _) => WorkspaceDialogs.ChooseTarget(Owner(), workspace, id, true));
        Items.Add(new ToolStripSeparator());
        Items.Add("Forget this saved place", null, (_, _) => workspace.Forget(id));
        Opening += (_, e) =>
        {
            if (workspace.Items.FirstOrDefault(i => i.Id == id) is not { } item) { e.Cancel = true; return; }
            UpdateItem(item);
        };
        if (workspace.Items.FirstOrDefault(i => i.Id == id) is { } initial) UpdateItem(initial);
    }

    public void UpdateItem(WorkspaceItemView item)
    {
        _auto.Checked = item.OpenOnStartup;
        _profile.Visible = ChromeProfiles.Supports(item.ExecutablePath) && !item.IsFolder;
        _profile.Enabled = item.State is not "Opening…" and not "Queued";
    }
}
