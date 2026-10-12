using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using CanvasDesktop;
using System.Windows;
using Wpf = System.Windows.Controls;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class WorkspaceViewTests
{
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
    public void OffscreenPlaceholderPaintsChildrenWithoutFocusOrApplicationLaunch()
    {
        OnSta(() =>
        {
            var commands = new TestCommands();
            using var form = new PlaceholderForm(commands, commands.Items[0]);
            var artwork = form.Controls.OfType<TableLayoutPanel>().Single()
                .Controls.OfType<PlaceholderArtwork>().Single();
            int paints = 0;
            artwork.Paint += (_, _) => paints++;
            var desktop = SystemInformation.VirtualScreen;
            form.ShowAt(new WindowRect(desktop.Left - 1000, desktop.Top - 700, 800, 550));
            Assert.True(paints > 0); // No message pump, focus, or DrawToBitmap to mask startup paint debt.
            int initialPaints = paints;
            form.UpdateItem(commands.Items[0] with { Title = "Changed while off-screen" });
            form.PrepareOverviewPreview();
            Assert.True(paints > initialPaints);
            Assert.Equal("Changed while off-screen", artwork.Text);
            Assert.Null(commands.Opened);
            Assert.NotSame(form, Form.ActiveForm);
            Assert.False(form.ShowInTaskbar);
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
            var open = form.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<Button>().Single();
            open.PerformClick();
            Assert.Equal(item.Id, commands.Opened);
            form.UpdateItem(item with { Title = "Changed", State = "Opening…", OpenOnStartup = true });
            Assert.Equal("Saved: Changed", form.Text);
            Assert.Equal("Opening…", open.Text);
            Assert.False(open.Enabled);
            form.UpdateItem(item);
            Assert.True(open.Enabled);
            Assert.Equal("Open", open.Text);
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
    public void AmbiguousPlaceholderOffersLinkingInsteadOfClaimingApplicationIsClosed()
    {
        OnSta(() =>
        {
            var commands = new TestCommands();
            using var form = new PlaceholderForm(commands, commands.Items[0] with { State = "Needs linking" });
            form.ShowAt(new WindowRect(10, 20, 800, 550));
            var panel = form.Controls.OfType<TableLayoutPanel>().Single();
            Assert.Equal("Link open window…", panel.Controls.OfType<Button>().Single().Text);
            Assert.Contains(panel.Controls.OfType<Label>(), label => label.Text.Contains("Several matches"));
            Assert.Contains(form.ContextMenuStrip!.Items.OfType<ToolStripMenuItem>(), menu => menu.Text == "Name this saved place…");
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
        public void SetAutoOpen(Guid id, bool value) => AutoOpen = (id, value);
        public void FocusItem(Guid id) { }
        public void CloseToPlaceholder(Guid id) { }
        public void Forget(Guid id) { }
        public void SetTarget(Guid id, string selected, bool folder) { }
        public void SetName(Guid id, string name) { }
        public IReadOnlyList<WorkspaceLinkCandidate> LinkCandidates(Guid id) => Candidates;
        public bool LinkWindow(Guid id, Guid candidateToken) { Linked = (id, candidateToken); return LinkSucceeds; }
    }
}
