using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CanvasDesktop;

internal sealed class PlaceholderForm : Form, IWorkspacePlaceholder
{
    private WorkspaceItemView _item;
    private readonly PlaceholderArtwork _title;
    private readonly Label _pathHeader, _details, _launchState;
    private readonly WorkspaceContextMenu _menu;
    private readonly ToolTip _resourceTip = new();
    private System.Windows.Media.Imaging.BitmapSource? _overviewImage;
    private Size _overviewSize;
    private int _overviewDpi;
    private int _overviewMaxDimension;
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
        var panel = new PlaceholderCardPanel(_title) { Dock = DockStyle.Fill, Padding = new Padding(18), RowCount = 4, ColumnCount = 1 };
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _pathHeader = new Label
        {
            Text = "Path:",
            AutoSize = true,
            Anchor = AnchorStyles.None,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(160, 181, 208),
            BackColor = Color.FromArgb(10, 16, 25),
            Padding = new Padding(4, 2, 4, 2)
        };
        _details = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.None,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 20f),
            ForeColor = Color.LightSteelBlue,
            BackColor = Color.FromArgb(10, 16, 25),
            Padding = new Padding(4, 2, 4, 2)
        };
        _launchState = new Label
        {
            AutoSize = true, Anchor = AnchorStyles.None, TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 14f, FontStyle.Bold), UseMnemonic = false,
            BackColor = Color.FromArgb(10, 16, 25), ForeColor = Color.LightSkyBlue,
            Padding = new Padding(6, 4, 6, 4), Margin = new Padding(3, 3, 3, 10),
            Visible = false, AccessibleName = "Application launch status"
        };
        panel.Controls.Add(_title);
        panel.Controls.Add(_launchState);
        panel.Controls.Add(_pathHeader);
        panel.Controls.Add(_details);
        Controls.Add(panel);
        var menu = new WorkspaceContextMenu(workspace, item.Id);
        _menu = menu;
        ContextMenuStrip = menu;
        panel.ContextMenuStrip = menu; _title.ContextMenuStrip = menu;
        _pathHeader.ContextMenuStrip = menu; _details.ContextMenuStrip = menu;
        _launchState.ContextMenuStrip = menu;
        DoubleClick += (_, _) => workspace.Open(item.Id);
        panel.DoubleClick += (_, _) => workspace.Open(item.Id);
        _title.DoubleClick += (_, _) => workspace.Open(item.Id);
        _pathHeader.DoubleClick += (_, _) => workspace.Open(item.Id);
        _details.DoubleClick += (_, _) => workspace.Open(item.Id);
        _launchState.DoubleClick += (_, _) => workspace.Open(item.Id);
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
        var launch = PlaceholderLaunchPresentation.For(item);
        bool launchChanged = _launchState.Text != launch.Text;
        bool appearanceChanged = !_itemApplied || _item.Title != item.Title || _item.Target != item.Target
            || _item.WebResource != item.WebResource
            || _item.ExecutablePath != item.ExecutablePath || _item.IsFolder != item.IsFolder || launchChanged;
        _item = item;
        _menu.UpdateItem(item);
        // World position, saved bounds and nonvisual metadata do not repaint a
        // card or invalidate its snapshot. Native size/DPI are checked separately.
        if (!appearanceChanged) return;
        _itemApplied = true;
        _overviewImage = null;
        Text = "Saved: " + _item.Title;
        _title.UpdateItem(item);
        _title.Parent?.Invalidate(true);
        _pathHeader.Text = item.WebResource == null ? "Path:" : "Website:";
        _details.Text = item.WebResource == null ? (_item.Target.Length > 0 ? _item.Target : System.IO.Path.GetFileName(_item.ExecutablePath))
            : new Uri(item.WebResource.Url).Host + "\n" + item.BrowserDetails;
        _resourceTip.SetToolTip(_details, item.WebResource == null ? "" : item.WebResource.Url + "\n" + item.BrowserDetails);
        _details.AccessibleDescription = item.WebResource?.Url;
        _launchState.Text = launch.Text;
        _launchState.Visible = launch.Text.Length > 0;
        _launchState.ForeColor = launch.Busy ? Color.LightSkyBlue : Color.Khaki;
        AccessibleDescription = launch.Text;
        // Show launch feedback before Process.Start can begin work. No animation
        // timer or repeated snapshot capture is needed while waiting for the app.
        if (launchChanged && launch.Busy && IsHandleCreated && Visible) Refresh();
    }

    // Bounded overview images paint the shared artwork/text directly at the
    // requested resolution. The default full native snapshot remains available
    // for diagnostics and pixel-fidelity checks without relying on DWM visibility.
    internal System.Windows.Media.Imaging.BitmapSource GetOverviewImage(int maxDimension = 0)
    {
        if (maxDimension < 0) throw new ArgumentOutOfRangeException(nameof(maxDimension));
        if (CachedOverviewImage != null && _overviewMaxDimension == maxDimension) return _overviewImage!;
        PerformLayout();
        double scale = maxDimension == 0 ? 1 : Math.Min(1, (double)maxDimension / Math.Max(Width, Height));
        var target = new Size(Math.Max(1, (int)Math.Ceiling(Width * scale)), Math.Max(1, (int)Math.Ceiling(Height * scale)));
        using var bitmap = new Bitmap(target.Width, target.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        if (maxDimension == 0) DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size));
        else
        {
            // Paint once at overview resolution. Walking the native transparent
            // control tree re-scaled the full icon for each child and allocated
            // a window-sized bitmap even when its card occupied few pixels.
            using var graphics = Graphics.FromImage(bitmap);
            graphics.ScaleTransform((float)scale, (float)scale);
            DrawOverview(graphics);
        }
        var bits = bitmap.LockBits(new Rectangle(Point.Empty, target), System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            // BitmapSource.Create copies these premultiplied pixels into WPF's
            // owned bitmap. No PNG encoder, stream or decoder is needed.
            var image = System.Windows.Media.Imaging.BitmapSource.Create(target.Width, target.Height, 96, 96,
                System.Windows.Media.PixelFormats.Pbgra32, null, bits.Scan0, checked(bits.Stride * target.Height), bits.Stride);
            image.Freeze();
            _overviewSize = Size;
            _overviewDpi = DeviceDpi;
            _overviewMaxDimension = maxDimension;
            return _overviewImage = image;
        }
        finally { bitmap.UnlockBits(bits); }
    }

    // Gesture rendering may borrow this immutable image, but must never trigger
    // DrawToBitmap/pixel conversion in response to a pointer or wheel event.
    internal System.Windows.Media.ImageSource? CachedOverviewImage
        => _overviewSize == Size && _overviewDpi == DeviceDpi ? _overviewImage : null;

    private void DrawOverview(Graphics graphics)
    {
        graphics.Clear(BackColor);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        _title.DrawIconBackground(graphics, new Rectangle(Point.Empty, Size));
        var panel = _title.Parent!;
        var state = graphics.Save();
        graphics.TranslateTransform(panel.Left + _title.Left, panel.Top + _title.Top);
        _title.DrawForeground(graphics);
        graphics.Restore(state);
        foreach (var label in new[] { _launchState, _pathHeader, _details })
        {
            if (!label.Visible || label.Text.Length == 0) continue;
            var area = new RectangleF(panel.Left + label.Left + label.Padding.Left,
                panel.Top + label.Top + label.Padding.Top,
                Math.Max(1, label.Width - label.Padding.Horizontal), Math.Max(1, label.Height - label.Padding.Vertical));
            using var brush = new SolidBrush(label.ForeColor);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.LineLimit };
            PlaceholderArtwork.DrawBackedText(graphics, label.Text, label.Font, brush, area, format, DeviceDpi / 96f);
        }
    }

    internal System.Windows.Media.ImageSource? OverviewLogo => _title.OverviewLogo;
    internal string? LaunchStatus => _launchState.Text.Length > 0 ? _launchState.Text : null;

    internal void ReleaseOverviewImage() => _overviewImage = null;

    protected override void OnLayout(LayoutEventArgs e)
    {
        if (_details != null && _title.Parent is { } panel)
        {
            int width = Math.Max(1, panel.ClientSize.Width - panel.Padding.Horizontal - 6);
            var maximum = new Size(width, 0);
            if (_pathHeader.MaximumSize != maximum) _pathHeader.MaximumSize = maximum;
            if (_launchState.MaximumSize != maximum) _launchState.MaximumSize = maximum;
            maximum = new Size(width, _details.Font.Height * 2 + _details.Padding.Vertical);
            if (_details.MaximumSize != maximum) _details.MaximumSize = maximum;
        }
        base.OnLayout(e);
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
        if (disposing) { PlacementChanged = null; _overviewImage = null; _menu.Dispose(); _resourceTip.Dispose(); }
        base.Dispose(disposing);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // Small saved places yield their limited height to the title.
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

