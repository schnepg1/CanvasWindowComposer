using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class WorkspaceViewTests
{
    [Fact]
    public void DisposingUnshownWorkspaceFormUnsubscribesFromCommands()
    {
        OnSta(() =>
        {
            var commands = new TestCommands();
            var form = new WorkspaceForm(commands);
            Assert.Equal(1, commands.Subscribers);
            form.Dispose(); form.Dispose();
            Assert.Equal(0, commands.Subscribers);
            commands.RaiseChanged(); // No callback into a disposed form.
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
            using var form = new WorkspaceForm(commands);
            form.Show();
            var list = form.Controls.OfType<ListView>().Single();
            var row = Assert.Single(list.Items.Cast<ListViewItem>());
            Assert.IsType<WorkspaceItemView>(row.Tag);
            row.Selected = true; row.Checked = true;
            Assert.Equal((commands.Items[0].Id, true), commands.AutoOpen);
            var open = form.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Button>().Single(b => b.Text == "Open");
            open.PerformClick();
            Assert.Equal(commands.Items[0].Id, commands.Opened);
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
            var candidate = new WorkspaceLinkCandidate(Guid.NewGuid(), "Project — Editor [PID 42, window 0x123]");
            commands.Candidates = new[] { candidate };
            using var form = new WorkspaceLinkForm(commands, commands.Items[0].Id);
            form.Show();
            var link = form.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Button>()
                .Single(b => b.Text == "Link selected window");
            Assert.False(link.Enabled);
            form.Controls.OfType<ListBox>().Single().SelectedIndex = 0;
            Assert.True(link.Enabled);
            link.PerformClick();
            Assert.Equal((commands.Items[0].Id, candidate.Token), commands.Linked);
            if (succeeds) Assert.Equal(DialogResult.OK, form.DialogResult);
            else
            {
                Assert.False(link.Enabled);
                Assert.Contains(form.Controls.OfType<Label>(), l => l.Text.Contains("no longer available"));
            }
        });
    }

    [Fact]
    public void LinkDialogShowsEmptyStateAndRendersWithoutEligibleWindows()
    {
        OnSta(() =>
        {
            var commands = new TestCommands();
            using var form = new WorkspaceLinkForm(commands, commands.Items[0].Id);
            form.Show(); form.PerformLayout();
            Assert.Empty(form.Controls.OfType<ListBox>().Single().Items);
            Assert.Contains(form.Controls.OfType<Label>(), l => l.Text.Contains("No eligible windows"));
            using var bitmap = new System.Drawing.Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
            bitmap.Save(Path.Combine(AppContext.BaseDirectory, "workspace-link-preview.png"));
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

    private static void OnSta(Action action)
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
