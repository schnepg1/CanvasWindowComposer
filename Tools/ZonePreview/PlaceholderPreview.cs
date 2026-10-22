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
    public static void Render(string output)
    {
        using var sheet = new Bitmap(1560, 1220);
        using var g = Graphics.FromImage(sheet);
        g.Clear(Color.FromArgb(12, 18, 29));
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        using var heading = new Font("Segoe UI", 21, FontStyle.Bold);
        using var small = new Font("Segoe UI", 12);
        g.DrawString("Placeholder cards · large titles and app symbols", heading, Brushes.WhiteSmoke, 32, 24);
        var folder = new WorkspaceItemView(Guid.NewGuid(), "Canvas project", @"C:\Windows\explorer.exe",
            @"G:\Coding\Projects\Canvas", false, new WorldRect { W = 800, H = 550 }, "Placeholder", IsFolder: true);
        string editor = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "notepad.exe");
        var app = folder with { Id = Guid.NewGuid(), Title = "Research notes", ExecutablePath = editor,
            Target = @"C:\Projects\research-notes.txt", IsFolder = false };
        var commands = new PreviewCommands(new[] { folder, app });
        using var folderBitmap = RenderCard(commands, folder, 720, 430);
        using var appBitmap = RenderCard(commands, app, 720, 430);
        g.DrawImageUnscaled(folderBitmap, 32, 84);
        g.DrawImageUnscaled(appBitmap, 800, 84);
        g.DrawString("Overview · 25% zoom · crisp labels drawn above the thumbnails", small, Brushes.LightSteelBlue, 32, 542);
        DrawOverview(g, folderBitmap, appBitmap, .25, 32, 578, 1496, 270);
        g.DrawString("Overview · 10% zoom · titles keep their minimum readable size", small, Brushes.LightSteelBlue, 32, 878);
        DrawOverview(g, folderBitmap, appBitmap, .1, 32, 914, 1496, 270);
        sheet.Save(output, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static Bitmap RenderCard(IWorkspaceCommands commands, WorkspaceItemView item, int width, int height)
    {
        using var form = new PlaceholderForm(commands, item) { ClientSize = new Size(width, height) };
        _ = form.Handle;
        form.PerformLayout();
        var bitmap = new Bitmap(width, height);
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

    private static void DrawOverview(Graphics g, Bitmap folder, Bitmap app, double zoom, int x, int y, int width, int height)
    {
        using var backdrop = new SolidBrush(Color.FromArgb(20, 28, 43));
        g.FillRectangle(backdrop, x, y, width, height);
        var cards = new[]
        {
            new PlaceholderAnnotation((IntPtr)1, "Canvas project", new WorldRect { X = 220 / zoom, Y = 90 / zoom, W = 720, H = 430 }),
            new PlaceholderAnnotation((IntPtr)2, "Research notes", new WorldRect { X = 720 / zoom, Y = 90 / zoom, W = 720, H = 430 }, true)
        };
        for (int i = 0; i < cards.Length; i++)
        {
            var b = cards[i].Bounds;
            g.DrawImage(i == 0 ? folder : app, new Rectangle(x + (int)(b.X * zoom), y + (int)(b.Y * zoom),
                (int)(b.W * zoom), (int)(b.H * zoom)));
        }
        var drawing = new OverviewAnnotationSurface();
        drawing.Update(OverviewAnnotationSnapshot.Create(Array.Empty<ZoneView>(), 0, 0, zoom,
            new Rectangle(0, 0, width, height), null, false, null, cards));
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
        public bool SetChromeProfile(Guid id, string profilePath) => true;
        public IReadOnlyList<WorkspaceLinkCandidate> LinkCandidates(Guid id) => Array.Empty<WorkspaceLinkCandidate>();
        public bool LinkWindow(Guid id, Guid token) => false;
    }
}
