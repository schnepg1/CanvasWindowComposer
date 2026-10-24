using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CanvasDesktop;

internal sealed class PlaceholderForm : Form, IWorkspacePlaceholder
{
    private WorkspaceItemView _item;
    private readonly PlaceholderArtwork _title;
    private readonly Label _pathHeader, _details, _hint;
    private readonly Bitmap? _openIcon;
    private readonly PlaceholderOpenButton _open;
    private readonly WorkspaceContextMenu _menu;
    private System.Windows.Media.Imaging.BitmapSource? _overviewImage;
    private Size _overviewSize;
    private int _overviewDpi;
    private bool _itemApplied;
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
        _title = new PlaceholderArtwork { Dock = DockStyle.Fill, MinimumSize = new Size(0, 40),
            BackColor = Color.Transparent, DrawSymbolBackground = false };
        var panel = new PlaceholderCardPanel(_title) { Dock = DockStyle.Fill, Padding = new Padding(18), RowCount = 5, ColumnCount = 1 };
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _pathHeader = new Label
        {
            Text = "Path:",
            Dock = DockStyle.Top,
            Height = (int)(22 * DeviceDpi / 96f),
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(160, 181, 208),
            BackColor = Color.FromArgb(10, 16, 25),
            Padding = new Padding(0)
        };
        _details = new Label
        {
            Dock = DockStyle.Top,
            Height = (int)(32 * DeviceDpi / 96f),
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 12.5f),
            ForeColor = Color.LightSteelBlue,
            BackColor = Color.FromArgb(10, 16, 25),
            Padding = new Padding(0, 2, 0, 4)
        };
        int iconSize = Math.Max(14, (int)(15 * DeviceDpi / 96f));
        _openIcon = CreateOpenSymbol(iconSize, Color.White);
        _open = new PlaceholderOpenButton
        {
            Text = "Open",
            AutoSize = true,
            Anchor = AnchorStyles.None,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(37, 99, 235),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            Padding = new Padding(16, 6, 16, 6),
            Margin = new Padding(0, 16, 0, 6),
            Cursor = Cursors.Hand,
            Image = _openIcon,
            TextImageRelation = TextImageRelation.ImageBeforeText
        };
        _open.FlatAppearance.BorderSize = 0;
        _open.FlatAppearance.MouseOverBackColor = Color.FromArgb(59, 130, 246);
        _open.FlatAppearance.MouseDownBackColor = Color.FromArgb(29, 78, 216);
        _open.Click += (_, _) =>
        {
            _open.Text = "Opening…";
            _open.Enabled = false;
            if (_item.State == "Needs linking") WorkspaceDialogs.LinkWindow(this, workspace, _item.Id);
            else workspace.Open(_item.Id);
        };
        _hint = new Label
        {
            Text = "Application is closed · Right-click for options",
            AutoSize = true,
            TextAlign = ContentAlignment.TopCenter,
            Dock = DockStyle.Top,
            ForeColor = Color.LightSteelBlue,
            BackColor = Color.FromArgb(10, 16, 25),
            Padding = new Padding(0, 12, 0, 0)
        };
        panel.Controls.Add(_title);
        panel.Controls.Add(_pathHeader);
        panel.Controls.Add(_details);
        panel.Controls.Add(_open);
        panel.Controls.Add(_hint);
        Controls.Add(panel);
        var menu = new WorkspaceContextMenu(workspace, item.Id);
        _menu = menu;
        ContextMenuStrip = menu;
        panel.ContextMenuStrip = menu; _title.ContextMenuStrip = menu;
        _pathHeader.ContextMenuStrip = menu; _details.ContextMenuStrip = menu;
        DoubleClick += (_, _) => workspace.Open(item.Id);
        _title.DoubleClick += (_, _) => workspace.Open(item.Id);
        _pathHeader.DoubleClick += (_, _) => workspace.Open(item.Id);
        _details.DoubleClick += (_, _) => workspace.Open(item.Id);
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) e.Cancel = true; };
        Move += (_, _) => PlacementChanged?.Invoke();
        ResizeEnd += (_, _) => PlacementChanged?.Invoke();
        UpdateItem(item);
    }

    private static Bitmap CreateOpenSymbol(int size, Color color)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = size;
        float stroke = Math.Max(1.6f, s / 9.5f);
        using var pen = new Pen(color, stroke)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        float left = s * 0.16f;
        float right = s * 0.84f;
        float top = s * 0.16f;
        float bottom = s * 0.84f;
        float midX = s * 0.50f;
        float midY = s * 0.50f;

        var boxPoints = new[]
        {
            new PointF(midX, top),
            new PointF(left, top),
            new PointF(left, bottom),
            new PointF(right, bottom),
            new PointF(right, midY)
        };
        g.DrawLines(pen, boxPoints);

        var arrowStart = new PointF(s * 0.42f, s * 0.58f);
        var arrowEnd = new PointF(right, top);
        g.DrawLine(pen, arrowStart, arrowEnd);

        float head = s * 0.32f;
        g.DrawLine(pen, arrowEnd, new PointF(arrowEnd.X - head, arrowEnd.Y));
        g.DrawLine(pen, arrowEnd, new PointF(arrowEnd.X, arrowEnd.Y + head));

        return bmp;
    }

    public void ShowAt(WindowRect rect)
    {
        SetBounds(rect.X, rect.Y, rect.W, rect.H);
        Show();
    }

    public void UpdateItem(WorkspaceItemView item)
    {
        bool appearanceChanged = !_itemApplied || _item.Title != item.Title || _item.Target != item.Target
            || _item.ExecutablePath != item.ExecutablePath || _item.IsFolder != item.IsFolder
            || _item.State != item.State || _item.OpenOnStartup != item.OpenOnStartup;
        _item = item;
        // World position, saved bounds and nonvisual metadata do not repaint a
        // card or invalidate its snapshot. Native size/DPI are checked separately.
        if (!appearanceChanged) return;
        _itemApplied = true;
        _overviewImage = null;
        Text = "Saved: " + _item.Title;
        _title.UpdateItem(item);
        _title.Parent?.Invalidate(true);
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
        _menu.UpdateItem(item);
    }

    // WM_PRINT used by DrawToBitmap renders controls even when their native
    // window is entirely off-screen. A DWM thumbnail cannot guarantee that.
    internal System.Windows.Media.Imaging.BitmapSource GetOverviewImage()
    {
        if (_overviewImage != null && _overviewSize == Size && _overviewDpi == DeviceDpi) return _overviewImage;
        PerformLayout();
        using var bitmap = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size));
        var bits = bitmap.LockBits(new Rectangle(Point.Empty, Size), System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            // BitmapSource.Create copies these premultiplied pixels into WPF's
            // owned bitmap. No PNG encoder, stream or decoder is needed.
            var image = System.Windows.Media.Imaging.BitmapSource.Create(Width, Height, 96, 96,
                System.Windows.Media.PixelFormats.Pbgra32, null, bits.Scan0, checked(bits.Stride * Height), bits.Stride);
            image.Freeze();
            _overviewSize = Size;
            _overviewDpi = DeviceDpi;
            return _overviewImage = image;
        }
        finally { bitmap.UnlockBits(bits); }
    }

    // Gesture rendering may borrow this immutable image, but must never trigger
    // DrawToBitmap/pixel conversion in response to a pointer or wheel event.
    internal System.Windows.Media.ImageSource? CachedOverviewImage => _overviewImage;

    internal System.Windows.Media.ImageSource? OverviewLogo => _title.OverviewLogo;

    internal void ReleaseOverviewImage() => _overviewImage = null;

    internal bool HitTestOpenButton(int clientX, int clientY)
    {
        if (_open == null || !_open.Visible || !_open.Enabled) return false;
        var ptInOpen = _open.PointToClient(PointToScreen(new Point(clientX, clientY)));
        return _open.ClientRectangle.Contains(ptInOpen);
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_MOUSEACTIVATE = 0x0021;
        if (m.Msg == WM_MOUSEACTIVATE)
        {
            m.Result = (IntPtr)1; // MA_ACTIVATE: click activates window and delivers mouse click
            return;
        }
        base.WndProc(ref m);
    }

    internal void ShowContextMenu(Control owner, Point location) => _menu.Show(owner, location);

    protected override void Dispose(bool disposing)
    {
        if (disposing) { PlacementChanged = null; _overviewImage = null; _menu.Dispose(); _openIcon?.Dispose(); }
        base.Dispose(disposing);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // Small saved places keep their original geometry; secondary copy yields
        // space to the title and Open action instead of forcing a larger window.
        if (_hint != null) _hint.Visible = ClientSize.Height >= 260 * DeviceDpi / 96;
        if (_details != null) _details.Visible = ClientSize.Height >= 190 * DeviceDpi / 96;
        if (_pathHeader != null) _pathHeader.Visible = ClientSize.Height >= 190 * DeviceDpi / 96;
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

internal sealed class PlaceholderOpenButton : Button
{
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private bool _clickedInCurrentDown;

    public PlaceholderOpenButton()
    {
        SetStyle(ControlStyles.Selectable, true);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_MOUSEACTIVATE)
        {
            m.Result = (IntPtr)1; // MA_ACTIVATE
            return;
        }
        if (m.Msg == WM_LBUTTONDOWN)
        {
            _clickedInCurrentDown = false;
        }
        else if (m.Msg == WM_LBUTTONUP)
        {
            base.WndProc(ref m);
            if (!_clickedInCurrentDown)
            {
                _clickedInCurrentDown = true;
                OnClick(EventArgs.Empty);
            }
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnClick(EventArgs e)
    {
        _clickedInCurrentDown = true;
        base.OnClick(e);
    }
}
