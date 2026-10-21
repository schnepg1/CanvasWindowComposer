using System;
using System.Drawing;
using System.Windows.Forms;

namespace CanvasDesktop;

internal sealed class PlaceholderForm : Form, IWorkspacePlaceholder
{
    private WorkspaceItemView _item;
    private readonly PlaceholderArtwork _title;
    private readonly TableLayoutPanel _pathPanel;
    private readonly Label _leaf, _parent, _state, _hint;
    private readonly Button _open;
    private readonly ToolTip _pathTip;
    private readonly ToolStripMenuItem _auto;
    private readonly ContextMenuStrip _menu;
    private System.Windows.Media.Imaging.BitmapSource? _overviewImage;
    private Size _overviewSize;
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
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _title = new PlaceholderArtwork { Dock = DockStyle.Fill, MinimumSize = new Size(0, 40) };
        // The leaf identifies the saved target; its parent stays secondary and the
        // full path remains available in a tooltip.
        _pathPanel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 2,
            Margin = new Padding(0), Padding = new Padding(0), BackColor = BackColor };
        _pathPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _pathPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _leaf = new Label { Dock = DockStyle.Fill, Height = 24, AutoSize = false, AutoEllipsis = true,
            Font = new Font("Segoe UI", 13f), ForeColor = Color.WhiteSmoke, Padding = new Padding(0, 2, 0, 0) };
        _parent = new Label { Dock = DockStyle.Fill, Height = 18, AutoSize = false, AutoEllipsis = true,
            Font = new Font("Segoe UI", 9.5f), ForeColor = Color.LightSteelBlue };
        _pathPanel.Controls.Add(_leaf); _pathPanel.Controls.Add(_parent);
        _pathTip = new ToolTip { AutoPopDelay = 12000, InitialDelay = 600, ReshowDelay = 100 };
        _state = new Label { AutoSize = true, Padding = new Padding(8, 3, 8, 3), Margin = new Padding(0, 4, 10, 0),
            Font = new Font("Segoe UI", 9f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter };
        _open = new Button { Text = "Open", AutoSize = true, FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(40, 51, 68), ForeColor = Color.WhiteSmoke, UseVisualStyleBackColor = false,
            Padding = new Padding(12, 3, 12, 3), Margin = new Padding(0, 4, 0, 0),
            FlatAppearance = { BorderColor = Color.FromArgb(80, 103, 137), BorderSize = 1,
                MouseOverBackColor = Color.FromArgb(55, 68, 88), MouseDownBackColor = Color.FromArgb(32, 41, 55) } };
        _open.Click += (_, _) =>
        {
            if (_item.State == "Needs linking") WorkspaceDialogs.LinkWindow(this, workspace, _item.Id);
            else workspace.Open(_item.Id);
        };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true,
            FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0), Padding = new Padding(0),
            BackColor = BackColor };
        actions.Controls.Add(_state); actions.Controls.Add(_open);
        _hint = new Label { Text = "Right-click for options", AutoSize = true,
            Dock = DockStyle.Top, ForeColor = Color.LightSteelBlue, Padding = new Padding(0, 12, 0, 0) };
        panel.Controls.Add(_title); panel.Controls.Add(_pathPanel); panel.Controls.Add(actions); panel.Controls.Add(_hint);
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
        panel.ContextMenuStrip = menu; _title.ContextMenuStrip = menu; _pathPanel.ContextMenuStrip = menu;
        _leaf.ContextMenuStrip = menu; _parent.ContextMenuStrip = menu;
        _state.ContextMenuStrip = menu; _open.ContextMenuStrip = menu;
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
        if (_item != item) _overviewImage = null;
        _item = item;
        Text = "Saved: " + _item.Title;
        _title.UpdateItem(item);
        var path = PlaceholderPathDisplay.For(_item);
        _leaf.Text = path.Leaf;
        _parent.Text = path.Parent;
        _parent.Visible = path.Parent.Length > 0;
        _leaf.AccessibleName = "Saved target: " + path.Full;
        _parent.AccessibleName = "Saved target folder: " + path.Parent;
        _pathTip.SetToolTip(_leaf, path.Full);
        _pathTip.SetToolTip(_parent, path.Full);
        ApplyState(_item.State);
        _open.Text = _item.State == "Opening…" ? "Opening…" : "Open";
        if (_item.State is "Minimized" or "Sleeping") _open.Text = "Restore";
        else if (_item.State == "Open") _open.Text = "Focus";
        if (_item.State == "Needs linking") _open.Text = "Link open window…";
        _open.Enabled = _item.State != "Opening…";
        _hint.Text = _item.State == "Needs linking"
            ? "Several matches are possible. Right-click for options"
            : _item.State == "Open" ? "Double-click to focus · Right-click for options"
            : _item.OpenOnStartup ? "Opens when Canvas starts · Right-click for options"
            : "Right-click for options";
        _auto.Checked = _item.OpenOnStartup;
    }

    private void ApplyState(string state)
    {
        (string text, Color back, Color fore) = state switch
        {
            "Open" => ("Open", Color.FromArgb(31, 77, 58), Color.FromArgb(134, 239, 172)),
            "Minimized" => ("Minimized", Color.FromArgb(74, 59, 30), Color.FromArgb(252, 211, 77)),
            "Sleeping" => ("Sleeping", Color.FromArgb(74, 59, 30), Color.FromArgb(252, 211, 77)),
            "Needs linking" => ("Needs linking", Color.FromArgb(76, 42, 50), Color.FromArgb(252, 165, 165)),
            "Opening…" => ("Opening…", Color.FromArgb(30, 58, 95), Color.FromArgb(147, 197, 253)),
            _ => ("Closed", Color.FromArgb(58, 69, 87), Color.FromArgb(203, 213, 225))
        };
        _state.Text = text;
        _state.BackColor = back;
        _state.ForeColor = fore;
        _state.AccessibleName = "State: " + text;
    }

    // WM_PRINT used by DrawToBitmap renders controls even when their native
    // window is entirely off-screen. A DWM thumbnail cannot guarantee that.
    internal System.Windows.Media.Imaging.BitmapSource GetOverviewImage()
    {
        if (_overviewImage != null && _overviewSize == Size) return _overviewImage;
        PerformLayout();
        using var bitmap = new Bitmap(Width, Height);
        DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size));
        using var stream = new System.IO.MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        stream.Position = 0;
        var image = new System.Windows.Media.Imaging.BitmapImage();
        image.BeginInit();
        image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        _overviewSize = Size;
        return _overviewImage = image;
    }

    internal void ReleaseOverviewImage() => _overviewImage = null;

    internal void ShowContextMenu(Control owner, Point location) => _menu.Show(owner, location);

    protected override void Dispose(bool disposing)
    {
        if (disposing) { PlacementChanged = null; _overviewImage = null; _menu.Dispose(); _pathTip.Dispose(); }
        base.Dispose(disposing);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // Small saved places keep their original geometry; secondary copy yields
        // space to the title and Open action instead of forcing a larger window.
        if (_hint != null) _hint.Visible = ClientSize.Height >= 260 * DeviceDpi / 96;
        if (_pathPanel != null) _pathPanel.Visible = ClientSize.Height >= 190 * DeviceDpi / 96;
    }
}

internal static class WorkspaceDialogs
{
    public static void NamePlace(IWin32Window owner, IWorkspaceCommands workspace, Guid id)
        => WorkspaceInteractionDialogs.NamePlace(owner.Handle, workspace, id);

    public static void LinkWindow(IWin32Window owner, IWorkspaceCommands workspace, Guid id)
        => WorkspaceInteractionDialogs.LinkWindow(owner.Handle, workspace, id);

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
