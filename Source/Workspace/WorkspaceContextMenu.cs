using System;
using System.Linq;
using System.Windows.Forms;

namespace CanvasDesktop;

/// <summary>One menu for native previews, detailed cards and distant title rectangles.</summary>
internal sealed class WorkspaceContextMenu : ContextMenuStrip
{
    private readonly ToolStripMenuItem _auto, _excludeMinimize, _profile, _web, _file, _folder, _keep, _forget;

    public WorkspaceContextMenu(IWorkspaceCommands workspace, Guid id)
    {
        IWin32Window Owner() => (IWin32Window?)SourceControl?.FindForm() ?? (IWin32Window?)SourceControl ?? new NativeDialogOwner(IntPtr.Zero);
        Items.Add("Open", null, (_, _) => workspace.Open(id));
        _keep = new ToolStripMenuItem("Keep this place", null, (_, _) => workspace.KeepPlace(id)); Items.Add(_keep);
        Items.Add("Link open window…", null, (_, _) => WorkspaceDialogs.LinkWindow(Owner(), workspace, id));
        Items.Add("Rename…", null, (_, _) => WorkspaceDialogs.NamePlace(Owner(), workspace, id));
        _profile = new ToolStripMenuItem("Chrome profile…", null, (_, _) => WorkspaceInteractionDialogs.ChooseChromeProfile(Owner().Handle, workspace, id));
        Items.Add(_profile);
        _web = new ToolStripMenuItem("Web resource settings…", null, (_, _) => WorkspaceInteractionDialogs.WebResourceSettings(Owner().Handle, workspace, id));
        Items.Add(_web);
        _auto = new ToolStripMenuItem("Open automatically when Canvas starts") { CheckOnClick = true };
        _auto.Click += (_, _) => workspace.SetAutoOpen(id, _auto.Checked); Items.Add(_auto);
        _excludeMinimize = new ToolStripMenuItem("Exclude from auto-minimize") { CheckOnClick = true };
        _excludeMinimize.Click += (_, _) => workspace.SetAutomaticMinimizationExcluded(id, _excludeMinimize.Checked); Items.Add(_excludeMinimize);
        _file = new ToolStripMenuItem("Choose file to reopen…", null, (_, _) => WorkspaceDialogs.ChooseTarget(Owner(), workspace, id, false)); Items.Add(_file);
        _folder = new ToolStripMenuItem("Choose folder to reopen…", null, (_, _) => WorkspaceDialogs.ChooseTarget(Owner(), workspace, id, true)); Items.Add(_folder);
        Items.Add(new ToolStripSeparator());
        _forget = new ToolStripMenuItem("Forget this saved place", null, (_, _) => workspace.Forget(id)); Items.Add(_forget);
        Opening += (_, e) =>
        {
            if (workspace.Items.FirstOrDefault(i => i.Id == id) is not { } item) { e.Cancel = true; return; }
            UpdateItem(item);
        };
        if (workspace.Items.FirstOrDefault(i => i.Id == id) is { } initial) UpdateItem(initial);
    }

    public void UpdateItem(WorkspaceItemView item)
    {
        _keep.Visible = item.Temporary;
        _forget.Visible = !item.Temporary;
        _auto.Visible = !item.Temporary;
        _auto.Checked = item.OpenOnStartup;
        _excludeMinimize.Checked = item.ExcludeFromAutomaticMinimization;
        _profile.Visible = ChromeProfiles.Supports(item.ExecutablePath) && !item.IsFolder && item.WebResource == null;
        _profile.Enabled = item.State is not "Opening…" and not "Queued";
        _web.Visible = BrowserSupport.Supports(item.ExecutablePath) && !item.IsFolder;
        _web.Enabled = item.State is not "Opening…" and not "Queued";
        _file.Visible = _folder.Visible = item.WebResource == null;
    }
}
