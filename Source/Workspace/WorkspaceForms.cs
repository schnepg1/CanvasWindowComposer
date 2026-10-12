using System;
using System.Drawing;
using System.Windows.Forms;

namespace CanvasDesktop;

internal sealed class PlaceholderForm : Form, IWorkspacePlaceholder
{
    private WorkspaceItemView _item;
    private readonly PlaceholderArtwork _title;
    private readonly Label _details, _hint;
    private readonly Button _open;
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
        _details = new Label { Dock = DockStyle.Top, Height = 28, AutoEllipsis = true,
            ForeColor = Color.LightSteelBlue, Padding = new Padding(0, 4, 0, 4) };
        _open = new Button { Text = "Open", AutoSize = true, FlatStyle = FlatStyle.System, Anchor = AnchorStyles.Left };
        _open.Click += (_, _) =>
        {
            if (_item.State == "Needs linking") WorkspaceDialogs.LinkWindow(this, workspace, _item.Id);
            else workspace.Open(_item.Id);
        };
        _hint = new Label { Text = "Application is closed · Right-click for options", AutoSize = true,
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
        if (_item != item) _overviewImage = null;
        _item = item;
        Text = "Saved: " + _item.Title;
        _title.UpdateItem(item);
        _details.Text = _item.Target.Length > 0 ? _item.Target : System.IO.Path.GetFileName(_item.ExecutablePath);
        _open.Text = _item.State == "Opening…" ? "Opening…" : "Open";
        if (_item.State is "Minimized" or "Sleeping") _open.Text = "Restore";
        else if (_item.State == "Open") _open.Text = "Focus";
        if (_item.State == "Needs linking") _open.Text = "Link open window…";
        _open.Enabled = _item.State != "Opening…";
        _hint.Text = _item.State == "Needs linking"
            ? "Several matches are possible. Choose the correct open window.\nRight-click for options"
            : (_item.State is "Minimized" or "Sleeping" ? "Application is minimized · Restore to use it"
                : _item.State == "Open" ? "Overview preview · Double-click to focus"
                : _item.OpenOnStartup ? "Opens when Canvas starts" : "Application is closed") + " · Right-click for options";
        _auto.Checked = _item.OpenOnStartup;
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

    protected override void Dispose(bool disposing)
    {
        if (disposing) { PlacementChanged = null; _overviewImage = null; _menu.Dispose(); }
        base.Dispose(disposing);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // Small saved places keep their original geometry; secondary copy yields
        // space to the title and Open action instead of forcing a larger window.
        if (_hint != null) _hint.Visible = ClientSize.Height >= 260 * DeviceDpi / 96;
        if (_details != null) _details.Visible = ClientSize.Height >= 190 * DeviceDpi / 96;
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
