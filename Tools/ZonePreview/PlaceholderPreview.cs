using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Media.Imaging;
using CanvasDesktop;

// Renders production card artwork and annotation drawing without showing a window,
// installing hooks, changing the desktop, or touching workspace persistence.
internal static class PlaceholderPreview
{
    public static void Render(string output, bool optimized = false, string state = "Placeholder")
    {
        using var sheet = new Bitmap(1560, 1220);
        using var g = Graphics.FromImage(sheet);
        g.Clear(Color.FromArgb(12, 18, 29));
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        using var heading = new Font("Segoe UI", 21, FontStyle.Bold);
        using var small = new Font("Segoe UI", 12);
        g.DrawString("Placeholder cards · full-color icon backgrounds", heading, Brushes.WhiteSmoke, 32, 24);
        var folder = new WorkspaceItemView(Guid.NewGuid(), "Canvas project", @"C:\Windows\explorer.exe",
            @"G:\Coding\Projects\Canvas", false, new WorldRect { W = 800, H = 550 }, state, IsFolder: true);
        string editor = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "notepad.exe");
        var app = folder with { Id = Guid.NewGuid(), Title = "Research notes", ExecutablePath = editor,
            Target = @"C:\Projects\research-notes.txt", IsFolder = false };
        var commands = new PreviewCommands(new[] { folder, app });
        using var folderArtwork = new PlaceholderArtwork(); folderArtwork.UpdateItem(folder);
        using var appArtwork = new PlaceholderArtwork(); appArtwork.UpdateItem(app);
        using var folderBitmap = RenderCard(commands, folder, 720, 430, optimized);
        using var appBitmap = RenderCard(commands, app, 720, 430, optimized);
        g.DrawImageUnscaled(folderBitmap, 32, 84);
        g.DrawImageUnscaled(appBitmap, 800, 84);
        g.DrawString(state == "Opening…" ? "Icon cards · 20% zoom · temporary launch feedback stays visible"
            : "Icon cards · 20% zoom · no text when an icon exists; title fallback at right", small, Brushes.LightSteelBlue, 32, 542);
        DrawOverview(g, folderArtwork.OverviewLogo, appArtwork.OverviewLogo, .2, 32, 578, 1496, 270,
            PlaceholderLaunchPresentation.For(folder).Text);
        g.DrawString("Icon cards · 5% zoom · icons scale with the card and stay inside it", small, Brushes.LightSteelBlue, 32, 878);
        DrawOverview(g, folderArtwork.OverviewLogo, appArtwork.OverviewLogo, .05, 32, 914, 1496, 270,
            PlaceholderLaunchPresentation.For(folder).Text);
        sheet.Save(output, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static Bitmap RenderCard(IWorkspaceCommands commands, WorkspaceItemView item, int width, int height, bool optimized)
    {
        using var form = new PlaceholderForm(commands, item) { ClientSize = new Size(width, height) };
        _ = form.Handle;
        form.PerformLayout();
        var bitmap = new Bitmap(width, height);
        if (optimized)
        {
            form.ShowAt(new WindowRect(-10000, -10000, width, height));
            var image = form.GetOverviewImage(OverviewCardImageQueue.ImageMaxDimension);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = new MemoryStream(); encoder.Save(stream); stream.Position = 0;
            using var rendered = Image.FromStream(stream);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.DrawImage(rendered, new Rectangle(0, 0, width, height));
            return bitmap;
        }
        // Client content only, so the preview is independent of the machine's title bar theme.
        // A standalone visible control tree is renderable without showing a Form;
        // descendants of an unshown Form otherwise paint as empty backgrounds.
        using var host = new System.Windows.Forms.Panel { Size = new Size(width, height),
            BackColor = form.BackColor, ForeColor = form.ForeColor, Font = form.Font };
        host.Controls.Add(form.Controls[0]);
        host.CreateControl(); host.PerformLayout();
        host.DrawToBitmap(bitmap, new Rectangle(0, 0, width, height));
        return bitmap;
    }

    public static void Benchmark(string output)
    {
        var item = new WorkspaceItemView(Guid.NewGuid(), "Research notes", Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "notepad.exe"),
            @"C:\Projects\research-notes.txt", false, new WorldRect { W = 1920, H = 1080 }, "Placeholder");
        var commands = new PreviewCommands(new[] { item });
        using var form = new PlaceholderForm(commands, item);
        form.ShowAt(new WindowRect(-10000, -10000, 1920, 1080));
        object Measure(int limit)
        {
            form.ReleaseOverviewImage(); form.GetOverviewImage(limit);
            var samples = new List<double>();
            BitmapSource? image = null;
            for (int i = 0; i < 16; i++)
            {
                form.ReleaseOverviewImage();
                var clock = System.Diagnostics.Stopwatch.StartNew();
                image = form.GetOverviewImage(limit); clock.Stop(); samples.Add(clock.Elapsed.TotalMilliseconds);
            }
            samples.Sort();
            var warm = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 1000; i++) form.GetOverviewImage(limit);
            warm.Stop();
            return new { width = image!.PixelWidth, height = image.PixelHeight,
                bytes = image.PixelWidth * image.PixelHeight * 4L, medianMs = samples[8], p95Ms = samples[15],
                warm1000CallsMs = warm.Elapsed.TotalMilliseconds };
        }
        var result = new { fullWindowCapture = Measure(0), overviewImage = Measure(OverviewCardImageQueue.ImageMaxDimension),
            mediumZoomImage = Measure(OverviewCardImageQueue.ImageDimension(1920 * .26)) };
        File.WriteAllText(output, System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private static void DrawOverview(Graphics g, System.Windows.Media.ImageSource? folder,
        System.Windows.Media.ImageSource? app, double zoom, int x, int y, int width, int height, string? launchStatus)
    {
        using var backdrop = new SolidBrush(Color.FromArgb(20, 28, 43));
        g.FillRectangle(backdrop, x, y, width, height);
        var cards = new[]
        {
            new PlaceholderAnnotation((IntPtr)1, "Canvas project", new WorldRect { X = 220 / zoom, Y = 90 / zoom, W = 720, H = 430 },
                ImageOpacity: 0, LabelOpacity: 1, Logo: folder, LaunchStatus: launchStatus),
            new PlaceholderAnnotation((IntPtr)2, "Research notes", new WorldRect { X = 720 / zoom, Y = 90 / zoom, W = 720, H = 430 },
                ImageOpacity: 0, LabelOpacity: 1, Logo: app, LaunchStatus: launchStatus),
            new PlaceholderAnnotation((IntPtr)3, "Missing icon: show title", new WorldRect { X = 1140 / zoom, Y = 90 / zoom, W = 720, H = 430 },
                ImageOpacity: 0, LabelOpacity: 1, LaunchStatus: launchStatus)
        };
        var drawing = new OverviewAnnotationSurface();
        drawing.Update(OverviewAnnotationSnapshot.Create(Array.Empty<ZoneView>(), 0, 0, zoom,
            new Rectangle(0, 0, width, height), null, false, null, cards, showHints: false));
        drawing.Measure(new System.Windows.Size(width, height));
        drawing.Arrange(new System.Windows.Rect(0, 0, width, height));
        drawing.UpdateLayout();
        var rendered = new RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rendered.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rendered));
        using var stream = new MemoryStream(); encoder.Save(stream); stream.Position = 0;
        using var overlay = Image.FromStream(stream);
        g.DrawImageUnscaled(overlay, x, y);
    }

    private sealed class PreviewCommands : IWorkspaceCommands
    {
        public PreviewCommands(IReadOnlyList<WorkspaceItemView> items) { Items = items; }
        public IReadOnlyList<WorkspaceItemView> Items { get; }
        public string? Status => null;
        public event Action? Changed { add { } remove { } }
        public void Open(Guid id) { }
        public void FocusItem(Guid id) { }
        public void CloseToPlaceholder(Guid id) { }
        public void Forget(Guid id) { }
        public void SetAutoOpen(Guid id, bool value) { }
        public void SetTarget(Guid id, string selected, bool folder) { }
        public void SetName(Guid id, string name) { }
        public bool SetChromeProfile(Guid id, string profileDirectory) => false;
        public IReadOnlyList<WorkspaceLinkCandidate> LinkCandidates(Guid id) => Array.Empty<WorkspaceLinkCandidate>();
        public bool LinkWindow(Guid id, Guid token) => false;
    }
}
