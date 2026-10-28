using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using CanvasDesktop;
using System.Windows;
using Wpf = System.Windows.Controls;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class WorkspaceViewTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    public void StartupTitleRendersInScreenSpaceAndClearsWhenTheIntroEnds(double scale)
        => OnSta(() =>
        {
            var drawing = new OverviewAnnotationSurface();
            drawing.Update(OverviewAnnotationSnapshot.Create([], 0, 0, .4, new System.Drawing.Rectangle(0, 0, 800, 500), null, false, null, [], false,
                new StartupIntroFrame(0, 0, .4, 1, 2, 1, false), true));
            drawing.Measure(new Size(800, 500)); drawing.Arrange(new Rect(0, 0, 800, 500));
            byte[] Pixels(bool saveFrame = false)
            {
                drawing.UpdateLayout();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(800 * scale), (int)(500 * scale),
                    96 * scale, 96 * scale, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(drawing);
                if (saveFrame)
                {
                    string directory = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TestResults");
                    Directory.CreateDirectory(directory);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(directory, $"startup-title-{(int)(scale * 100)}.png"));
                    encoder.Save(stream);
                }
                var pixels = new byte[(int)(800 * scale) * (int)(500 * scale) * 4];
                bitmap.CopyPixels(pixels, (int)(800 * scale) * 4, 0);
                return pixels;
            }
            Assert.Contains(Pixels(saveFrame: true), value => value > 0);
            drawing.Update(OverviewAnnotationSnapshot.Create([], 0, 0, .4, new System.Drawing.Rectangle(0, 0, 800, 500), null, false, null, [], false,
                new StartupIntroFrame(0, 0, .4, 0, 4, 1, false), true));
            Assert.Contains(Pixels(), value => value > 0); // Landing hint survives the title fade during a long tour.
            drawing.Update(OverviewAnnotationSnapshot.Create([], 0, 0, .4, new System.Drawing.Rectangle(0, 0, 800, 500), null, false, null, [], false));
            Assert.False(Pixels().Any(value => value != 0), "Title pixels remained after processing the cleared layout.");
        });

    [Fact]
    public void FadingCardUsesCurrentGeometryClippingAndOneTitleAboveTheOldThreshold()
        => OnSta(() =>
        {
            var drawing = new OverviewAnnotationSurface();
            drawing.Update(OverviewAnnotationSnapshot.Create([], 0, 0, 1, new System.Drawing.Rectangle(0, 0, 400, 300), null, false, null,
                [new PlaceholderAnnotation((IntPtr)42, "Project", new WorldRect { X = 100, Y = 100, W = 200, H = 150 },
                    Occluders: [new WorldRect { X = 250, Y = 100, W = 50, H = 150 }],
                    Opacity: .5, ImageOpacity: 0, LabelOpacity: 1)], false));
            drawing.Measure(new Size(400, 300)); drawing.Arrange(new Rect(0, 0, 400, 300));
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(400, 300, 96, 96,
                System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(drawing);
            var pixels = new byte[400 * 300 * 4]; bitmap.CopyPixels(pixels, 1600, 0);
            Assert.InRange(pixels[(110 * 400 + 110) * 4 + 3], (byte)126, (byte)129);
            Assert.Equal(0, pixels[(110 * 400 + 270) * 4 + 3]);
            Assert.Equal((IntPtr)42, drawing.HitPlaceholderLabel(200, 175));
            for (int y = 100; y < 250; y++)
                for (int x = 250; x < 300; x++)
                    Assert.Equal(0, pixels[(y * 400 + x) * 4 + 3]);
            Assert.Equal(IntPtr.Zero, drawing.HitPlaceholderLabel(270, 175));
        });

    [Fact]
    public void DistantCardRendersRectangleAndTitleWithoutPixelsOrHitsOutsideItsBounds()
    {
        OnSta(() =>
        {
            var drawing = new OverviewAnnotationSurface();
            drawing.Update(OverviewAnnotationSnapshot.Create([], 0, 0, .25, new System.Drawing.Rectangle(0, 0, 400, 300), null, false, null,
                [new PlaceholderAnnotation((IntPtr)42, "Project", new WorldRect { X = 400, Y = 400, W = 800, H = 600 })], false));
            drawing.Measure(new Size(400, 300)); drawing.Arrange(new Rect(0, 0, 400, 300));
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(400, 300, 96, 96,
                System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(drawing);
            var pixels = new byte[400 * 300 * 4]; bitmap.CopyPixels(pixels, 1600, 0);
            Assert.True(pixels[(110 * 400 + 110) * 4 + 3] > 0); // Card body exists without any native pixels.
            Assert.NotEqual(IntPtr.Zero, drawing.HitPlaceholderLabel(200, 175));
            Assert.Equal(IntPtr.Zero, drawing.HitPlaceholderLabel(90, 175));
            for (int y = 102; y < 248; y++)
                for (int x = 0; x < 98; x++)
                    Assert.Equal(0, pixels[(y * 400 + x) * 4 + 3]);
        });
    }

    [Theory]
    [InlineData("Minimized")]
    [InlineData("Sleeping")]
    public void MinimizedCardRestoresByDoubleClickWithoutAButtonOrStatusText(string state)
    {
        OnSta(() =>
        {
            var commands = new TestCommands();
            using var form = new PlaceholderForm(commands, commands.Items[0] with { State = state });
            form.ShowAt(new WindowRect(-10000, -10000, 800, 550));
            var panel = form.Controls.OfType<TableLayoutPanel>().Single();
            Assert.Empty(panel.Controls.OfType<Button>());
            Assert.DoesNotContain(panel.Controls.OfType<Label>(), label => label.Text.Contains("closed"));
            DoubleClick(panel.Controls.OfType<PlaceholderArtwork>().Single());
            Assert.Equal(commands.Items[0].Id, commands.Opened);
        });
    }

    [Fact]
    public void ClosingUnshownWorkspaceWindowUnsubscribesFromCommands()
    {
        OnSta(() =>
        {
            var commands = new TestCommands();
            var form = new WorkspaceWindow(commands);
            Assert.Equal(1, commands.Subscribers);
            form.Close(); form.Close();
            Assert.Equal(0, commands.Subscribers);
            commands.RaiseChanged(); // No callback into a disposed form.
        });
    }

    [Fact]
    public void OffscreenPlaceholderSnapshotRendersWithoutFocusOrApplicationLaunch()
    {
        OnSta(() =>
        {
            var commands = new TestCommands();
            using var form = new PlaceholderForm(commands, commands.Items[0]);
            var desktop = SystemInformation.VirtualScreen;
            form.ShowAt(new WindowRect(desktop.Left - 1000, desktop.Top - 700, 800, 550));
            var image = form.GetOverviewImage();
            Assert.True(image.IsFrozen);
            Assert.Same(image, form.GetOverviewImage());
            var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
            new System.Windows.Media.Imaging.FormatConvertedBitmap(image,
                System.Windows.Media.PixelFormats.Bgra32, null, 0).CopyPixels(pixels, image.PixelWidth * 4, 0);
            Assert.Contains(Enumerable.Range(0, pixels.Length / 4), i =>
                pixels[i * 4] < 80 && pixels[i * 4 + 1] < 80 && pixels[i * 4 + 2] < 80);
            form.UpdateItem(commands.Items[0] with { Title = "Changed while off-screen" });
            Assert.NotSame(image, form.GetOverviewImage());
            Assert.Null(commands.Opened);
            Assert.NotSame(form, Form.ActiveForm);
        });
    }

    [Fact]
    public void MovingSavedGeometryReusesTheCardPixelsButResizingAndReleasingDoNot()
        => OnSta(() =>
        {
            var commands = new TestCommands(); var item = commands.Items[0];
            using var form = new PlaceholderForm(commands, item);
            form.ShowAt(new WindowRect(-10000, -10000, 380, 260));
            var image = form.GetOverviewImage();
            var moved = item.Bounds; moved.X += 500; moved.Y -= 300;
            form.UpdateItem(item with { Bounds = moved });
            form.Left += 100;
            Assert.Same(image, form.GetOverviewImage());
            form.Width += 40;
            var resized = form.GetOverviewImage();
            Assert.NotSame(image, resized);
            Assert.Equal(form.Width, resized.PixelWidth);
            form.ReleaseOverviewImage();
            Assert.Null(form.CachedOverviewImage);
            Assert.NotSame(resized, form.GetOverviewImage());
        });

    [Theory]
    [InlineData("title")]
    [InlineData("target")]
    [InlineData("executable")]
    [InlineData("folder")]
    public void VisualCardChangesInvalidateTheSnapshot(string change)
        => OnSta(() =>
        {
            var commands = new TestCommands(); var item = commands.Items[0];
            using var form = new PlaceholderForm(commands, item);
            form.ShowAt(new WindowRect(-10000, -10000, 380, 260));
            var image = form.GetOverviewImage();
            var changed = change switch
            {
                "title" => item with { Title = "Renamed" },
                "target" => item with { Target = @"C:\Other\file.txt" },
                "executable" => item with { ExecutablePath = @"C:\Other\Other.exe" },
                _ => item with { IsFolder = true }
            };
            form.UpdateItem(changed);
            Assert.Null(form.CachedOverviewImage);
            Assert.NotSame(image, form.GetOverviewImage());
        });

    [Fact]
    public void RawCardSnapshotMatchesEncodedPixelsAndOwnsItsDataAfterFormDisposal()
        => OnSta(() =>
        {
            var commands = new TestCommands();
            using var form = new PlaceholderForm(commands, commands.Items[0]);
            form.ShowAt(new WindowRect(-10000, -10000, 380, 260));
            var actual = form.GetOverviewImage();
            Assert.True(actual.IsFrozen);
            using var bitmap = new System.Drawing.Bitmap(form.Width, form.Height,
                System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(System.Drawing.Point.Empty, form.Size));
            using var encoded = new MemoryStream(); bitmap.Save(encoded, System.Drawing.Imaging.ImageFormat.Png);
            encoded.Position = 0;
            var reference = new System.Windows.Media.Imaging.BitmapImage(); reference.BeginInit();
            reference.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            reference.StreamSource = encoded; reference.EndInit();
            byte[] Pixels(System.Windows.Media.Imaging.BitmapSource image)
            {
                var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(image,
                    System.Windows.Media.PixelFormats.Pbgra32, null, 0);
                var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
                converted.CopyPixels(pixels, image.PixelWidth * 4, 0); return pixels;
            }
            Assert.Equal(Pixels(reference), Pixels(actual));
            var before = Pixels(actual); form.Dispose();
            Assert.Equal(before, Pixels(actual));
        });

    [Fact]
    public void OverviewCardSnapshotYieldsToHigherWindows()
    {
        OnSta(() =>
        {
            var commands = new TestCommands();
            using var form = new PlaceholderForm(commands, commands.Items[0]);
            form.ShowAt(new WindowRect(-10000, -10000, 800, 550));
            var drawing = new OverviewAnnotationSurface();
            drawing.Update(OverviewAnnotationSnapshot.Create([], 0, 0, 1, new System.Drawing.Rectangle(0, 0, 300, 300), null, false, null,
                [new PlaceholderAnnotation(form.Handle, "Notes", new WorldRect { X = 100, Y = 100, W = 100, H = 100 },
                    Image: form.GetOverviewImage(), Occluders: [new WorldRect { X = 150, Y = 100, W = 50, H = 100 }])], false));
            drawing.Measure(new Size(300, 300)); drawing.Arrange(new Rect(0, 0, 300, 300));
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(300, 300, 96, 96,
                System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(drawing);
            var pixels = new byte[300 * 300 * 4]; bitmap.CopyPixels(pixels, 1200, 0);
            Assert.True(pixels[(120 * 300 + 120) * 4 + 3] > 0);
            Assert.Equal(0, pixels[(120 * 300 + 170) * 4 + 3]);
        });
    }

    [Fact]
    public void PlaceholderUsesSnapshotUpdatesAndSendsOnlyItemId()
    {
        OnSta(() =>
        {
            var commands = new TestCommands();
            var item = commands.Items[0];
            using var form = new PlaceholderForm(commands, item);
            form.ShowAt(new WindowRect(10, 20, 800, 550));
            var title = form.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<PlaceholderArtwork>().Single();
            DoubleClick(title);
            Assert.Equal(item.Id, commands.Opened);
            form.UpdateItem(item with { Title = "Changed", State = "Opening…", OpenOnStartup = true });
            Assert.Equal("Saved: Changed", form.Text);
            Assert.Equal("Changed", title.Text);
            var image = form.GetOverviewImage(512);
            form.UpdateItem(item with { Title = "Changed", State = "Open", OpenOnStartup = false });
            var opened = form.GetOverviewImage(512);
            Assert.NotSame(image, opened); // Launch feedback has disappeared.
            form.UpdateItem(item with { Title = "Changed", State = "Open", OpenOnStartup = true });
            Assert.Same(opened, form.GetOverviewImage(512));
        });
    }

    [Fact]
    public void WorkspaceCheckboxAndButtonsUseCommandContract()
    {
        OnSta(() =>
        {
            var commands = new TestCommands();
            var window = new WorkspaceWindow(commands);
            try
            {
                var list = Descendants(window).OfType<Wpf.ListBox>().Single();
                list.SelectedIndex = 0;
                var row = Assert.IsType<WorkspaceItemView>(list.SelectedItem);
                var check = new Wpf.CheckBox { DataContext = row, IsChecked = true };
                list.RaiseEvent(new RoutedEventArgs(Wpf.CheckBox.ClickEvent, check));
                Assert.Equal((row.Id, true), commands.AutoOpen);
                Descendants(window).OfType<Wpf.Button>().Single(b => Equals(b.Content, "Open"))
                    .RaiseEvent(new RoutedEventArgs(Wpf.Button.ClickEvent));
                Assert.Equal(row.Id, commands.Opened);
                commands.RaiseChanged();
                window.Close(); // A queued refresh must not access a closed view.
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LinkDialogSendsOnlyOpaqueCandidateAndSlotIdsAndHandlesStaleSelection(bool succeeds)
    {
        OnSta(() =>
        {
            var commands = new TestCommands { LinkSucceeds = succeeds };
            var candidate = new WorkspaceLinkCandidate(Guid.NewGuid(), "Project ? Editor [PID 42, window 0x123]");
            commands.Candidates = new[] { candidate };
            var window = WorkspaceInteractionDialogs.CreateLinkWindow(IntPtr.Zero, commands, commands.Items[0].Id);
            Exception? callbackFailure = null;
            window.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    var link = Descendants(window).OfType<Wpf.Button>().Single(b => Equals(b.Content, "Link selected window"));
                    Assert.False(link.IsEnabled);
                    Descendants(window).OfType<Wpf.ListBox>().Single().SelectedIndex = 0;
                    Assert.True(link.IsEnabled);
                    link.RaiseEvent(new RoutedEventArgs(Wpf.Button.ClickEvent));
                    Assert.Equal((commands.Items[0].Id, candidate.Token), commands.Linked);
                    if (!succeeds)
                    {
                        Assert.False(link.IsEnabled);
                        Assert.Contains(Descendants(window).OfType<Wpf.TextBlock>(), l => l.Text.Contains("no longer available"));
                    }
                }
                catch (Exception ex) { callbackFailure = ex; }
                finally { window.Close(); }
            });
            bool? result = window.ShowDialog();
            if (callbackFailure != null) ExceptionDispatchInfo.Capture(callbackFailure).Throw();
            Assert.Equal(succeeds, result == true);
        });
    }

    [Fact]
    public void LinkDialogShowsEmptyStateAndRendersWithoutEligibleWindows()
    {
        OnSta(() =>
        {
            var commands = new TestCommands();
            var window = WorkspaceInteractionDialogs.CreateLinkWindow(IntPtr.Zero, commands, commands.Items[0].Id);
            try
            {
                Assert.Empty(Descendants(window).OfType<Wpf.ListBox>().Single().Items);
                Assert.Contains(Descendants(window).OfType<Wpf.TextBlock>(), l => l.Text.Contains("No eligible windows"));
                Render(window, "workspace-link-preview.png");
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void WorkspaceSearchRetainsSelectionByIdAndShowsAnEmptyState()
    {
        OnSta(() =>
        {
            var commands = new TestCommands();
            var window = new WorkspaceWindow(commands);
            try
            {
                var list = Descendants(window).OfType<Wpf.ListBox>().Single();
                var filter = Descendants(window).OfType<Wpf.TextBox>().Single();
                list.SelectedIndex = 0;
                filter.Text = "notes.txt";
                Assert.Equal(commands.Items[0].Id, Assert.IsType<WorkspaceItemView>(list.SelectedItem).Id);
                filter.Text = "no matches";
                Assert.Empty(list.Items);
                Assert.Contains(Descendants(window).OfType<Wpf.TextBlock>(), l => l.Text.Contains("No saved places"));
                filter.Clear();
                Render(window, "workspace-shell-preview.png");
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void AmbiguousPlaceholderKeepsLinkingInItsContextMenu()
    {
        OnSta(() =>
        {
            var commands = new TestCommands();
            using var form = new PlaceholderForm(commands, commands.Items[0] with { State = "Needs linking" });
            form.ShowAt(new WindowRect(10, 20, 800, 550));
            var panel = form.Controls.OfType<TableLayoutPanel>().Single();
            Assert.Empty(panel.Controls.OfType<Button>());
            Assert.Contains(form.ContextMenuStrip!.Items.OfType<ToolStripMenuItem>(), menu => menu.Text == "Link open window…");
            Assert.Contains(form.ContextMenuStrip!.Items.OfType<ToolStripMenuItem>(), menu => menu.Text == "Rename…");
            using var bitmap = new System.Drawing.Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
            bitmap.Save(Path.Combine(AppContext.BaseDirectory, "workspace-ambiguous-preview.png"));
        });
    }

    internal static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }

    [Fact]
    public void RenameAndProfileDialogsSaveTheExplicitChoiceAndCancelLeavesItUnchanged()
        => OnSta(() =>
        {
            var commands = new TestCommands(); var item = commands.Items[0];
            var rename = WorkspaceInteractionDialogs.CreateNamePlace(IntPtr.Zero, commands, item.Id);
            rename.Loaded += (_, _) =>
            {
                Descendants(rename).OfType<Wpf.TextBox>().Single().Text = "Work browser";
                Descendants(rename).OfType<Wpf.Button>().Single(b => Equals(b.Content, "Save"))
                    .RaiseEvent(new RoutedEventArgs(Wpf.Button.ClickEvent));
            };
            Assert.True(rename.ShowDialog()); Assert.Equal((item.Id, "Work browser"), commands.Renamed);
            var cancel = WorkspaceInteractionDialogs.CreateNamePlace(IntPtr.Zero, commands, item.Id);
            cancel.Loaded += (_, _) =>
            {
                Descendants(cancel).OfType<Wpf.TextBox>().Single().Text = "Discard";
                cancel.DialogResult = false;
            };
            Assert.False(cancel.ShowDialog()); Assert.Equal((item.Id, "Work browser"), commands.Renamed);
            var choices = new[] { new ChromeProfileChoice(@"C:\Profiles\Profile 1", "Work"), new ChromeProfileChoice(@"C:\Profiles\Default", "Personal") };
            var profile = WorkspaceInteractionDialogs.CreateChromeProfile(IntPtr.Zero, commands, item.Id, choices);
            profile.Loaded += (_, _) =>
            {
                Descendants(profile).OfType<Wpf.ComboBox>().Single().SelectedItem = choices[0];
                Descendants(profile).OfType<Wpf.Button>().Single(b => Equals(b.Content, "Save"))
                    .RaiseEvent(new RoutedEventArgs(Wpf.Button.ClickEvent));
            };
            Assert.True(profile.ShowDialog()); Assert.Equal((item.Id, choices[0].Path), commands.Profile);
        });

    internal static void Render(Window window, string fileName, double scale = 1)
    {
        window.ShowActivated = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = System.Windows.Forms.SystemInformation.VirtualScreen.Right + 500;
        window.Top = System.Windows.Forms.SystemInformation.VirtualScreen.Bottom + 500;
        window.Show(); window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        double width = content.ActualWidth + 48, height = content.ActualHeight + 48;
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(width * scale),
            (int)(height * scale), 96 * scale, 96 * scale, System.Windows.Media.PixelFormats.Pbgra32);
        var visual = new System.Windows.Media.DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
            drawing.DrawRectangle(new System.Windows.Media.VisualBrush(content), null,
                new Rect(24, 24, width - 48, height - 48));
        }
        bitmap.Render(visual);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(AppContext.BaseDirectory, fileName)); encoder.Save(stream);
    }

    internal static void DoubleClick(Control control)
        => typeof(Control).GetMethod("OnDoubleClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(control, [EventArgs.Empty]);

    internal static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class TestCommands : IWorkspaceCommands
    {
        private Action? _changed;
        public int Subscribers => _changed?.GetInvocationList().Length ?? 0;
        public Guid? Opened;
        public (Guid Id, bool Value)? AutoOpen;
        public (Guid Id, Guid Candidate)? Linked;
        public (Guid Id, string Name)? Renamed;
        public (Guid Id, string Path)? Profile;
        public bool LinkSucceeds = true;
        public IReadOnlyList<WorkspaceLinkCandidate> Candidates = Array.Empty<WorkspaceLinkCandidate>();
        public IReadOnlyList<WorkspaceItemView> Items { get; } = new[]
        {
            new WorkspaceItemView(Guid.NewGuid(), "Notes", @"C:\Apps\Editor.exe", @"C:\Work\notes.txt",
                false, new WorldRect { X = 100, Y = 200, W = 800, H = 550 }, "Placeholder")
        };
        public string? Status => null;
        public event Action? Changed { add => _changed += value; remove => _changed -= value; }
        public void RaiseChanged() => _changed?.Invoke();
        public void Open(Guid id) => Opened = id;
        public void SetAutomaticMinimizationExcluded(Guid id, bool value) { }
        public void SetAutoOpen(Guid id, bool value) => AutoOpen = (id, value);
        public void FocusItem(Guid id) { }
        public void CloseToPlaceholder(Guid id) { }
        public void Forget(Guid id) { }
        public void SetTarget(Guid id, string selected, bool folder) { }
        public void SetName(Guid id, string name) => Renamed = (id, name);
        public bool SetChromeProfile(Guid id, string profilePath) { Profile = (id, profilePath); return true; }
        public bool SaveWebResource(Guid? id, string executable, string name, WebResource resource, WorldRect? bounds = null) => false;
        public IReadOnlyList<WorkspaceLinkCandidate> LinkCandidates(Guid id) => Candidates;
        public bool LinkWindow(Guid id, Guid candidateToken) { Linked = (id, candidateToken); return LinkSucceeds; }
    }
}
