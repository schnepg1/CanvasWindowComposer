using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace CanvasDesktop;

internal sealed class PlaceholderForm : Form, IWorkspacePlaceholder
{
    private WorkspaceItemView _item;
    private readonly Label _title, _details, _hint;
    private readonly Button _open;
    private readonly ToolStripMenuItem _auto;
    private readonly ContextMenuStrip _menu;
    public event Action? PlacementChanged;
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80; return cp; }
    }

    public PlaceholderForm(IWorkspaceCommands workspace, WorkspaceItemView item)
    {
        _item = item;
        Text = "Saved: " + item.Title;
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        ShowInTaskbar = false;
        MinimumSize = new Size(220, 150);
        BackColor = Color.FromArgb(28, 34, 46);
        ForeColor = Color.WhiteSmoke;
        Font = new Font("Segoe UI", 11);
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), RowCount = 4, ColumnCount = 1 };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _title = new Label { AutoSize = true, Dock = DockStyle.Top, Font = new Font(Font, FontStyle.Bold) };
        _details = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, Padding = new Padding(0, 12, 0, 8) };
        _open = new Button { Text = "Open", AutoSize = true, FlatStyle = FlatStyle.System, Anchor = AnchorStyles.Left };
        _open.Click += (_, _) =>
        {
            if (_item.State == "Needs linking") WorkspaceDialogs.LinkWindow(this, workspace, _item.Id);
            else workspace.Open(_item.Id);
        };
        _hint = new Label { Text = "Saved place • application is closed\nRight-click for options", AutoSize = true,
            Dock = DockStyle.Top, ForeColor = Color.LightSteelBlue, Padding = new Padding(0, 12, 0, 0) };
        panel.Controls.Add(_title); panel.Controls.Add(_details); panel.Controls.Add(_open); panel.Controls.Add(_hint);
        Controls.Add(panel);
        var menu = new ContextMenuStrip();
        _menu = menu;
        menu.Items.Add("Open", null, (_, _) => workspace.Open(item.Id));
        menu.Items.Add("Link open window…", null, (_, _) => WorkspaceDialogs.LinkWindow(this, workspace, item.Id));
        menu.Items.Add("Name this saved place…", null, (_, _) => WorkspaceDialogs.NamePlace(this, workspace, item.Id));
        _auto = new ToolStripMenuItem("Open automatically when Canvas starts") { CheckOnClick = true, Checked = item.OpenOnStartup };
        _auto.Click += (_, _) => workspace.SetAutoOpen(item.Id, _auto.Checked);
        menu.Items.Add(_auto);
        menu.Items.Add("Choose file to reopen…", null, (_, _) => WorkspaceDialogs.ChooseTarget(this, workspace, item.Id, false));
        menu.Items.Add("Choose folder to reopen…", null, (_, _) => WorkspaceDialogs.ChooseTarget(this, workspace, item.Id, true));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Forget this saved place", null, (_, _) => workspace.Forget(item.Id));
        ContextMenuStrip = menu;
        panel.ContextMenuStrip = menu; _title.ContextMenuStrip = menu; _details.ContextMenuStrip = menu;
        DoubleClick += (_, _) => workspace.Open(item.Id);
        _title.DoubleClick += (_, _) => workspace.Open(item.Id);
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) e.Cancel = true; };
        Move += (_, _) => PlacementChanged?.Invoke();
        ResizeEnd += (_, _) => PlacementChanged?.Invoke();
        UpdateItem(item);
    }

    public void ShowAt(WindowRect rect)
    {
        SetBounds(rect.X, rect.Y, rect.W, rect.H);
        Show();
    }

    public void UpdateItem(WorkspaceItemView item)
    {
        _item = item;
        Text = "Saved: " + _item.Title;
        _title.Text = _item.Title;
        _details.Text = (_item.Target.Length > 0 ? _item.Target : System.IO.Path.GetFileName(_item.ExecutablePath))
            + "\n\n" + (_item.OpenOnStartup ? "Opens when Canvas starts" : "Opens only when you choose");
        _open.Text = _item.State == "Opening…" ? "Opening…" : "Open";
        if (_item.State == "Needs linking") _open.Text = "Link open window…";
        _open.Enabled = _item.State != "Opening…";
        _hint.Text = _item.State == "Needs linking"
            ? "Several matches are possible. Choose the correct open window.\nRight-click for options"
            : "Saved place • application is closed\nRight-click for options";
        _auto.Checked = _item.OpenOnStartup;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { PlacementChanged = null; _menu.Dispose(); }
        base.Dispose(disposing);
    }
}

internal sealed class WorkspaceForm : Form
{
    private readonly IWorkspaceCommands _workspace;
    private readonly ListView _items;
    private readonly Label _status;
    private bool _refreshing;

    public WorkspaceForm(IWorkspaceCommands workspace)
    {
        _workspace = workspace;
        Text = "Saved workspace"; Size = new Size(940, 520); MinimumSize = new Size(680, 360);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10);
        _items = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
            MultiSelect = false, HideSelection = false, CheckBoxes = true };
        _items.Columns.Add("Open on startup / Window", 270);
        _items.Columns.Add("State", 110);
        _items.Columns.Add("Folder / file / application", 350);
        _items.Columns.Add("Canvas position", 140);
        _items.ItemChecked += (_, e) =>
        { if (!_refreshing && e.Item.Tag is WorkspaceItemView item) workspace.SetAutoOpen(item.Id, e.Item.Checked); };
        _items.DoubleClick += (_, _) => WithSelected(workspace.FocusItem);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8) };
        AddButton(actions, "Go to", workspace.FocusItem);
        AddButton(actions, "Open", workspace.Open);
        AddButton(actions, "Link window…", id => WorkspaceDialogs.LinkWindow(this, workspace, id));
        AddButton(actions, "Name…", id => WorkspaceDialogs.NamePlace(this, workspace, id));
        AddButton(actions, "Close to placeholder", workspace.CloseToPlaceholder);
        AddButton(actions, "Choose file…", id => WorkspaceDialogs.ChooseTarget(this, workspace, id, false));
        AddButton(actions, "Choose folder…", id => WorkspaceDialogs.ChooseTarget(this, workspace, id, true));
        AddButton(actions, "Forget", workspace.Forget);
        _status = new Label { Dock = DockStyle.Bottom, Height = 54, Padding = new Padding(10), AutoEllipsis = true };
        Controls.Add(_items); Controls.Add(_status); Controls.Add(actions);
        workspace.Changed += OnWorkspaceChanged;
        RefreshItems();
    }

    private void OnWorkspaceChanged()
    {
        // A checkbox changes the model inside ListView's own event dispatch.
        // Rebuild after that dispatch completes, not while its row is in use.
        if (IsHandleCreated && !IsDisposed) BeginInvoke(RefreshItems);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _workspace.Changed -= OnWorkspaceChanged;
        base.Dispose(disposing);
    }

    private void AddButton(FlowLayoutPanel panel, string label, Action<Guid> action)
    {
        var button = new Button { Text = label, AutoSize = true, Padding = new Padding(4) };
        button.Click += (_, _) => WithSelected(action);
        panel.Controls.Add(button);
    }

    private void WithSelected(Action<Guid> action)
    {
        if (_items.SelectedItems.Count > 0 && _items.SelectedItems[0].Tag is WorkspaceItemView item) action(item.Id);
    }

    private void RefreshItems()
    {
        if (IsDisposed) return;
        Guid? selected = _items.SelectedItems.Count > 0 ? (_items.SelectedItems[0].Tag as WorkspaceItemView)?.Id : null;
        _refreshing = true;
        _items.BeginUpdate();
        try
        {
            _items.Items.Clear();
            foreach (var item in _workspace.Items.OrderBy(i => i.Title))
            {
                var row = new ListViewItem(item.Title) { Tag = item, Checked = item.OpenOnStartup, Selected = item.Id == selected };
                row.SubItems.Add(item.State);
                row.SubItems.Add(item.Target.Length > 0 ? item.Target : item.ExecutablePath);
                row.SubItems.Add($"{item.Bounds.X:0}, {item.Bounds.Y:0}");
                _items.Items.Add(row);
            }
            _status.Text = _workspace.Status ?? "Checked items open when Canvas starts. Other saved places remain placeholders. Double-click a row to find it.";
        }
        finally { _items.EndUpdate(); _refreshing = false; }
    }
}

internal static class WorkspaceDialogs
{
    public static void NamePlace(IWin32Window owner, IWorkspaceCommands workspace, Guid id)
    {
        using var dialog = new Form { Text = "Name this saved place", ClientSize = new Size(460, 140),
            StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false };
        var hint = new Label { Text = "Optional label, e.g. VS Code — Canvas project.\nLeave blank to follow the application's window title.",
            AutoSize = true, Location = new Point(12, 12) };
        var name = new TextBox { Location = new Point(12, 58), Width = 436, MaxLength = 200,
            Text = workspace.Items.FirstOrDefault(i => i.Id == id)?.Name ?? "" };
        var save = new Button { Text = "Save", DialogResult = DialogResult.OK, Location = new Point(282, 100) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(373, 100) };
        dialog.Controls.AddRange(new Control[] { hint, name, save, cancel });
        dialog.AcceptButton = save; dialog.CancelButton = cancel;
        if (dialog.ShowDialog(owner) == DialogResult.OK) workspace.SetName(id, name.Text);
    }

    public static void LinkWindow(IWin32Window owner, IWorkspaceCommands workspace, Guid id)
    {
        using var dialog = new WorkspaceLinkForm(workspace, id);
        dialog.ShowDialog(owner);
    }

    public static void ChooseTarget(IWin32Window owner, IWorkspaceCommands workspace, Guid id, bool folder)
    {
        if (folder)
        {
            using var dialog = new FolderBrowserDialog { Description = "Folder to reopen for this saved window" };
            if (dialog.ShowDialog(owner) == DialogResult.OK) workspace.SetTarget(id, dialog.SelectedPath, true);
        }
        else
        {
            using var dialog = new OpenFileDialog { Title = "File to open with this saved application" };
            if (dialog.ShowDialog(owner) == DialogResult.OK) workspace.SetTarget(id, dialog.FileName, false);
        }
    }
}

internal sealed class WorkspaceLinkForm : Form
{
    public WorkspaceLinkForm(IWorkspaceCommands workspace, Guid id)
    {
        Text = "Link an open window to this saved place";
        Size = new Size(760, 320); MinimumSize = new Size(480, 240);
        StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
        Font = new Font("Segoe UI", 10);
        var hint = new Label { Dock = DockStyle.Top, Height = 66, Padding = new Padding(10),
            Text = "Select the correct window. Linking moves it to this saved place.\nOnly unbound windows of the same application on this desktop are listed." };
        var list = new ListBox { Dock = DockStyle.Fill, DisplayMember = nameof(WorkspaceLinkCandidate.Description) };
        foreach (var candidate in workspace.LinkCandidates(id)) list.Items.Add(candidate);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8) };
        var link = new Button { Text = "Link selected window", AutoSize = true, Enabled = false };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var status = new Label { Dock = DockStyle.Bottom, Height = 38, Padding = new Padding(10),
            Text = list.Items.Count == 0 ? "No eligible windows. Open the application, then try again." : "" };
        list.SelectedIndexChanged += (_, _) => link.Enabled = list.SelectedItem != null;
        link.Click += (_, _) =>
        {
            if (list.SelectedItem is not WorkspaceLinkCandidate candidate) return;
            if (workspace.LinkWindow(id, candidate.Token)) { DialogResult = DialogResult.OK; Close(); }
            else { status.Text = "That window is no longer available. Close this dialog and try again."; link.Enabled = false; }
        };
        actions.Controls.Add(link); actions.Controls.Add(cancel);
        Controls.Add(list); Controls.Add(status); Controls.Add(actions); Controls.Add(hint);
        AcceptButton = link; CancelButton = cancel;
    }
}
