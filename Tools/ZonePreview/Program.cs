using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using CanvasDesktop;

// Isolated UI/composition preview: no hooks, persistence, window management or desktop recovery.
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        if (args.Length == 2 && args[0] == "--render-launching-placeholders")
        {
            PlaceholderPreview.Render(args[1], optimized: true, state: "Opening…");
            return;
        }
        if (args.Length == 2 && args[0] == "--render-controls")
        {
            using var popup = new CanvasControlsPopup();
            using var controlsHost = new Panel { Size = popup.ClientSize, BackColor = popup.BackColor,
                ForeColor = popup.ForeColor, Font = popup.Font, Padding = popup.Padding };
            foreach (Control child in popup.Controls.Cast<Control>().ToArray()) controlsHost.Controls.Add(child);
            controlsHost.CreateControl(); controlsHost.PerformLayout();
            using (var bitmap = new Bitmap(controlsHost.Width, controlsHost.Height))
            {
                controlsHost.DrawToBitmap(bitmap, new Rectangle(Point.Empty, controlsHost.Size));
                bitmap.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
            }
            // Expand the same content for an inspectable preview of every row.
            controlsHost.Height = 1600; controlsHost.PerformLayout();
            using var expanded = new Bitmap(controlsHost.Width, controlsHost.Height);
            controlsHost.DrawToBitmap(expanded, new Rectangle(Point.Empty, controlsHost.Size));
            expanded.Save(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(args[1])!,
                System.IO.Path.GetFileNameWithoutExtension(args[1]) + "-all.png"), System.Drawing.Imaging.ImageFormat.Png);
            return;
        }
        if (args.Length == 2 && args[0] == "--benchmark-placeholders")
        {
            PlaceholderPreview.Benchmark(args[1]);
            return;
        }
        if (args.Length == 2 && args[0] == "--render-overview-cards")
        {
            PlaceholderPreview.Render(args[1], optimized: true);
            return;
        }
        if (args.Length == 2 && args[0] == "--render-placeholders")
        {
            PlaceholderPreview.Render(args[1]);
            return;
        }
        var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        var document = new WorkspaceDocument();
        document.Items.Add(new WorkspaceItem { Title = "Browser · Design references", Bounds = new WorldRect { X = 180, Y = 210, W = 400, H = 260 } });
        document.Items.Add(new WorkspaceItem { Title = "Editor · Website project", Bounds = new WorldRect { X = 620, Y = 210, W = 400, H = 260 } });
        document.Items.Add(new WorkspaceItem { Title = "Music player", Bounds = new WorldRect { X = 1100, Y = 210, W = 400, H = 260, PinnedToScreen = true } });
        var zones = new ZoneService(document, () => Guid.Empty, () => true);
        zones.Add(new WorldRect { X = 80, Y = 120, W = 560, H = 390 }, out var id);
        zones.Update(id, "Website redesign", "#60A5FA");
        zones.SetMembership(id, document.Items.Take(2).Select(i => i.Id).ToArray());
        using var layout = new PreviewLayoutCommands(zones);
        void Replay(bool redo) { if (redo) layout.RedoZoneLayout(); else layout.UndoZoneLayout(); }
        var navigationWorkspace = new PreviewNavigationWorkspace(document);
        ZoneNavigatorWindow? navigator = null;
        Action<ZoneView> jump = _ => { };
        Action<Guid?> manage = _ => { };
        using var navigation = new ZoneNavigation(zones, navigationWorkspace, area => jump(area), () => { });
        void ShowNavigator()
        {
            if (navigator == null)
            {
                navigator = new ZoneNavigatorWindow(navigation, areaId => manage(areaId)) { Topmost = true };
                navigator.Closed += (_, _) => navigator = null;
                System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(navigator);
            }
            navigator.Show(); navigator.Activate(); navigator.FocusSearch();
        }
        if (args.Length == 2 && (args[0] == "--render" || args[0] == "--render-members" || args[0] == "--render-navigator"))
        {
            System.Windows.Window window = args[0] switch
            {
                "--render-members" => new ZoneMembersWindow(zones, zones.Items[0], () => null),
                "--render-navigator" => new ZoneNavigatorWindow(navigation, _ => { }),
                _ => new ZoneWindow(zones, () => { }, _ => { }, () => null, layout, Replay, ShowNavigator)
            };
            if (window is ZoneNavigatorWindow)
            {
                var grid = (System.Windows.Controls.Grid)window.Content;
                var body = grid.Children.OfType<System.Windows.Controls.Grid>().Single();
                var tree = body.Children.OfType<System.Windows.Controls.TreeView>().Single();
                var area = (System.Windows.Controls.TreeViewItem)tree.Items[0];
                area.IsExpanded = true;
                if (area.Items.Count > 0) ((System.Windows.Controls.TreeViewItem)area.Items[0]).IsSelected = true;
            }
            double width = window.Width, height = window.Height - 40;
            double contentWidth = width - 48, contentHeight = height - 48;
            var content = (System.Windows.FrameworkElement)window.Content;
            content.Measure(new System.Windows.Size(contentWidth, contentHeight));
            content.Arrange(new System.Windows.Rect(0, 0, contentWidth, contentHeight));
            content.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)width, (int)height, 96, 96,
                System.Windows.Media.PixelFormats.Pbgra32);
            var visual = new System.Windows.Media.DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(window.Background, null, new System.Windows.Rect(0, 0, width, height));
                dc.DrawRectangle(new System.Windows.Media.VisualBrush(content), null, new System.Windows.Rect(24, 24, contentWidth, contentHeight));
            }
            bitmap.Render(visual);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (var stream = System.IO.File.Create(args[1])) encoder.Save(stream);
            window.Close(); app.Shutdown(); return;
        }
        using var host = new Form { Text = "Canvas project area preview", BackColor = Color.FromArgb(17, 24, 39),
            StartPosition = FormStartPosition.Manual, Location = new Point(60, 80), ClientSize = new Size(900, 650) };
        using var source = new Form { Text = "Live preview source", BackColor = Color.FromArgb(38, 47, 66),
            ClientSize = new Size(400, 240), ShowInTaskbar = false };
        source.Controls.Add(new Label { Text = "LIVE APPLICATION CONTENT\n\nZone borders and names should remain visible above this thumbnail.",
            Dock = DockStyle.Fill, ForeColor = Color.White, Padding = new Padding(24), Font = new Font("Segoe UI", 14) });
        var camera = new OverviewCamera(WinFormsScreens.Instance);
        IntPtr thumbnail = IntPtr.Zero;
        var dwm = new DwmThumbnailApi();
        OverviewAnnotationWindow? annotation = null;
        ZoneWindow? manager = null;
        var gesture = new OverviewZoneInteraction();
        void Refresh()
        {
            if (!host.Visible || annotation == null) return;
            var screen = host.RectangleToScreen(host.ClientRectangle);
            var views = zones.Items.Select(z => z.Id == gesture.MovingId && gesture.Preview is { } moved
                ? z with { Bounds = moved } : z).ToArray();
            annotation.Update(OverviewAnnotationSnapshot.Create(views, 0, 0, 1, screen,
                gesture.Kind == ZoneGestureKind.Draw ? gesture.Preview : null,
                gesture.Armed || gesture.Kind == ZoneGestureKind.Draw, gesture.Ghosts));
            if (thumbnail != IntPtr.Zero)
            {
                var b = document.Items[0].Bounds;
                dwm.Update(thumbnail, new Windows.Win32.Graphics.Dwm.DWM_THUMBNAIL_PROPERTIES
                {
                    dwFlags = 1 | 4 | 8 | 16,
                    rcDestination = new Windows.Win32.Foundation.RECT((int)b.X - screen.X, (int)b.Y - screen.Y,
                        (int)(b.X + b.W) - screen.X, (int)(b.Y + b.H) - screen.Y),
                    opacity = 255, fVisible = true, fSourceClientAreaOnly = true
                });
            }
            host.Invalidate();
        }
        void ShowManager()
        {
            manager!.Show(); manager.Activate();
        }
        manage = areaId => { ShowManager(); if (areaId is { } selected) manager!.Select(selected); };
        jump = area =>
        {
            // The preview has no camera handoff: report selection and keep the navigator visible.
            host.Text = "Canvas project area preview · " + area.Name;
        };
        host.Shown += (_, _) =>
        {
            source.Show();
            dwm.Register((Windows.Win32.Foundation.HWND)host.Handle, (Windows.Win32.Foundation.HWND)source.Handle, out thumbnail);
            dwm.Update(thumbnail, new Windows.Win32.Graphics.Dwm.DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = 1 | 4 | 8 | 16,
                rcDestination = new Windows.Win32.Foundation.RECT(100, 160, 590, 440),
                opacity = 255, fVisible = true, fSourceClientAreaOnly = true
            });
            annotation = new OverviewAnnotationWindow(host.Handle);
            manager = new ZoneWindow(zones, () => { gesture.Arm(); Refresh(); host.Activate(); },
                _ => ShowManager(), () => null, layout, Replay, ShowNavigator) { Topmost = true };
            System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(manager);
            manager.Show();
            Refresh();
        };
        host.Move += (_, _) => Refresh();
        host.Resize += (_, _) => Refresh();
        zones.Changed += Refresh;
        host.Paint += (_, e) =>
        {
            var screen = host.RectangleToScreen(host.ClientRectangle);
            foreach (var item in document.Items.Skip(1))
            {
                var b = item.Bounds;
                var rect = new Rectangle((int)b.X - screen.X, (int)b.Y - screen.Y, (int)b.W, (int)b.H);
                using var brush = new SolidBrush(Color.FromArgb(38, 47, 66));
                e.Graphics.FillRectangle(brush, rect);
                e.Graphics.DrawString(item.Title, host.Font, Brushes.White, rect.X + 20, rect.Y + 20);
            }
        };
        host.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            var point = host.PointToScreen(e.Location);
            if (gesture.Armed || (Control.ModifierKeys & Keys.Shift) != 0)
                gesture.BeginDraw(point.X, point.Y);
            else
            {
                var screen = host.RectangleToScreen(host.ClientRectangle);
                var monitor = new ScreenRect(screen.X, screen.Y, screen.Width, screen.Height);
                double dpi = host.DeviceDpi / 96.0;
                var resize = zones.Items.Reverse().SelectMany(z => ZoneResizeLayout.Get(z.Bounds, 0, 0, 1,
                    monitor, dpi, dpi, true).Select(h => (Zone: z, Handle: h)))
                    .FirstOrDefault(h => h.Handle.Rect.Contains(e.X, e.Y));
                if (resize.Zone != null) gesture.BeginResize(resize.Zone, resize.Handle.Corner, point.X, point.Y);
                else
                {
                    var zone = zones.Items.Reverse().FirstOrDefault(z => ZoneLabelLayout.Get(z.Bounds, 0, 0, 1, monitor, dpi, dpi)
                        is { } label && label.Contains(e.X, e.Y));
                    if (zone == null) return;
                    gesture.BeginMove(zone, point.X, point.Y,
                        zones.Members(zone.Id).Where(m => m.Assigned && !m.Bounds.PinnedToScreen).Select(m => m.Bounds).ToArray());
                }
            }
            host.Capture = true; Refresh();
        };
        host.MouseMove += (_, e) =>
        {
            if (!gesture.Active) return;
            var point = host.PointToScreen(e.Location);
            gesture.Update(point.X, point.Y); Refresh();
        };
        host.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || !gesture.Active) return;
            var point = host.PointToScreen(e.Location);
            var result = gesture.Complete(point.X, point.Y, 1);
            host.Capture = false;
            if (result?.Kind == ZoneGestureKind.Draw)
            {
                zones.Add(result.Bounds, out var created); ShowManager(); manager!.Select(created);
            }
            else if (result?.Kind == ZoneGestureKind.Move) layout.Move(result.ZoneId, result.Dx, result.Dy);
            else if (result?.Kind == ZoneGestureKind.Resize) layout.Resize(result.ZoneId, result.Bounds);
            Refresh();
        };
        host.MouseCaptureChanged += (_, _) => { if (!host.Capture) { gesture.Cancel(); Refresh(); } };
        host.KeyPreview = true;
        host.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { gesture.Cancel(); host.Capture = false; Refresh(); }
            else if (e.KeyCode == Keys.P && !e.Control && !e.Alt) ShowNavigator();
            else if (e.Control && (e.KeyCode == Keys.Z || e.KeyCode == Keys.Y))
            {
                gesture.Cancel(); host.Capture = false;
                Replay(e.KeyCode == Keys.Y || e.Shift); Refresh(); e.SuppressKeyPress = true;
            }
        };
        host.FormClosed += (_, _) =>
        {
            annotation?.Close(); manager?.Close(); navigator?.Close();
            if (thumbnail != IntPtr.Zero) dwm.Unregister(thumbnail);
            source.Close(); app.Shutdown();
        };
        Application.Run(host);
    }
}
