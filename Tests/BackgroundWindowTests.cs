using System.Threading;
using System.Windows;
using System.Windows.Controls;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class BackgroundWindowTests
{
    [Fact]
    public void EditorRendersAnInspectableFrameAtItsDefaultSize()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var rig = new Rig();
            rig.Add();
            var window = rig.Window();
            try
            {
                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(992, 808)); content.Arrange(new Rect(0, 0, 992, 808)); content.UpdateLayout();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(992, 808, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(content);
                string directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TestResults"));
                Directory.CreateDirectory(directory);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(directory, "background-editor.png")); encoder.Save(stream);
                Assert.True(stream.Length > 1000);
            }
            finally { window.Close(); }
        });

    [Fact]
    public void DraftSurvivesUnrelatedRefreshAndCannotOverwriteNewCanvasGeometry()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var rig = new Rig();
            var original = rig.Add();
            var window = rig.Window();
            try
            {
                window.Select(original.Id);
                Assert.False(window.HasPendingChanges);
                Control<TextBox>(window, "BackgroundName").Text = "My draft";
                Assert.True(window.HasPendingChanges);
                rig.Service.Save();
                Assert.Equal("My draft", Control<TextBox>(window, "BackgroundName").Text);
                var moved = original.Bounds; moved.X = 420;
                rig.Service.Update(original with { Bounds = moved });
                Click(window, "Save changes");
                Assert.Equal(original.Name, rig.Service.Items.Single().Name);
                Assert.Equal(420, rig.Service.Items.Single().Bounds.X);
                Click(window, "Reload saved values");
                Assert.Equal("420", Control<TextBox>(window, "BackgroundX").Text);
                Control<TextBox>(window, "BackgroundName").Text = "Saved study";
                Click(window, "Save changes");
                Assert.Equal("Saved study", rig.Service.Items.Single().Name);
                Assert.False(window.HasPendingChanges);
            }
            finally { window.Close(); }
        });

    [Fact]
    public void DesktopChangeClearsDraftAndUpdatesHistoryWithoutLeakingOldSelection()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var rig = new Rig();
            var original = rig.Add();
            var window = rig.Window();
            try
            {
                window.Select(original.Id);
                Control<TextBox>(window, "BackgroundName").Text = "Old desktop draft";
                rig.Desktop = Guid.NewGuid(); rig.Service.NotifyDesktopChanged();
                Assert.Empty(Control<ListBox>(window, "BackgroundList").Items);
                Assert.Equal("", Control<TextBox>(window, "BackgroundName").Text);
                Assert.False(Descendants(window).OfType<Button>().Single(button => Equals(button.Content, "Undo")).IsEnabled);
                Click(window, "Save changes");
                Assert.Empty(rig.Service.Items);
                rig.Desktop = original.DesktopId; rig.Service.NotifyDesktopChanged();
                Assert.Equal(original.Name, Control<TextBox>(window, "BackgroundName").Text);
            }
            finally { window.Close(); }
        });

    [Fact]
    public void ProjectAttachmentUsesAreaBoundsAndDetachmentRetainsThem()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var rig = new Rig();
            var zone = new ZoneView(Guid.NewGuid(), "Reading", "#60A5FA", new() { X = 200, Y = 300, W = 8000, H = 4500 });
            rig.Zones.Add(zone);
            var original = rig.Add();
            var window = rig.Window();
            try
            {
                window.Select(original.Id);
                var project = Control<ComboBox>(window, "BackgroundProject");
                project.SelectedIndex = 1;
                Assert.True(Control<TextBox>(window, "BackgroundWidth").IsReadOnly);
                Assert.Equal("8000", Control<TextBox>(window, "BackgroundWidth").Text);
                Click(window, "Save changes");
                Assert.Equal(zone.Id, rig.Service.Items.Single().ZoneId);
                project.SelectedIndex = 0;
                Assert.False(Control<TextBox>(window, "BackgroundWidth").IsReadOnly);
                Click(window, "Save changes");
                Assert.Null(rig.Service.Items.Single().ZoneId);
                Assert.Equal(zone.Bounds, rig.Service.Items.Single().Bounds);
            }
            finally { window.Close(); }
        });

    [Fact]
    public void InvalidGeometryDoesNotSaveAndClosingUnsubscribesFromService()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var rig = new Rig();
            var original = rig.Add();
            var window = rig.Window();
            window.Select(original.Id);
            var name = Control<TextBox>(window, "BackgroundName");
            Control<TextBox>(window, "BackgroundWidth").Text = "NaN";
            Click(window, "Save changes");
            Assert.Equal(original, rig.Service.Items.Single());
            window.Close();
            rig.Service.Update(original with { Name = "After close" });
            Assert.Equal(original.Name, name.Text);
        });

    private static T Control<T>(DependencyObject root, string name) where T : FrameworkElement
        => Descendants(root).OfType<T>().Single(control => control.Name == name);
    private static void Click(DependencyObject root, string caption)
        => Descendants(root).OfType<Button>().Single(button => Equals(button.Content, caption))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in Descendants(child)) yield return descendant;
    }

    private sealed class Rig : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "CanvasBackgroundUi-" + Guid.NewGuid().ToString("N"));
        public Guid Desktop = Guid.NewGuid();
        public List<ZoneView> Zones { get; } = new();
        public BackgroundService Service { get; }
        public BackgroundAssets Assets { get; }
        public BackgroundImageCache Cache { get; }
        public Rig()
        {
            Service = new(new BackgroundPersistence(Path.Combine(_directory, "backgrounds.json")), () => Desktop, () => Zones);
            Assets = new(Path.Combine(_directory, "assets"));
            Cache = new(Assets, new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
        }
        public BackgroundSurface Add()
        {
            var item = new BackgroundSurface(Guid.NewGuid(), Desktop, "Study", "builtin:study", new() { W = 3840, H = 2160 });
            Assert.True(Service.Add(item)); return item;
        }
        public BackgroundWindow Window() => new(Service, Assets, Cache, () => Zones, () => new() { W = 3840, H = 2160 }, _ => { }, _ => { });
        public void Dispose()
        {
            Cache.Dispose();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }
}
