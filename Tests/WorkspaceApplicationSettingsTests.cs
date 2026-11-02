using System.Windows;
using System.Windows.Controls;
using CanvasDesktop;
using Forms = System.Windows.Forms;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class WorkspaceApplicationSettingsTests
{
    [Theory]
    [InlineData("chrome.exe", false, false, true, false, false, false)]
    [InlineData("msedge.exe", false, false, true, false, false, false)]
    [InlineData("firefox.exe", false, true, true, false, false, false)]
    [InlineData("explorer.exe", false, false, false, false, true, false)]
    [InlineData("explorer.exe", true, false, false, false, true, false)]
    [InlineData("Code.exe", false, false, false, true, true, false)]
    [InlineData("Code - Insiders.exe", false, false, false, true, true, false)]
    [InlineData("editor.exe", false, false, false, true, true, true)]
    public void CardAndWorkspaceSettingsUseTheSameApplicationCapabilities(string executable, bool folder, bool resource,
        bool website, bool file, bool directory, bool advanced)
        => WorkspaceViewTests.OnSta(() =>
        {
            var item = new WorkspaceItem { Title = "Saved window", ExecutablePath = @"C:\Apps\" + executable,
                IsFolder = folder, Target = folder ? @"C:\Work" : "", Bounds = new() { W = 800, H = 550 },
                WebResource = resource ? new("https://example.com/") : null, CaptureChromeProfile = !resource };
            using var rig = new WorkspaceHarness(new() { Items = [item] }); rig.Workspace.Start();
            using var menu = new WorkspaceContextMenu(rig.Workspace, item.Id);
            var actions = menu.Items.OfType<Forms.ToolStripMenuItem>().ToArray();
            Assert.Equal(website, actions.Single(a => a.Text is "Set website URL…" or "Web resource settings…").Available);
            var advancedMenu = actions.Single(a => a.Text == "Advanced launch settings");
            Assert.Equal(advanced, advancedMenu.Available);
            var targetActions = advanced ? advancedMenu.DropDownItems.OfType<Forms.ToolStripMenuItem>() : actions;
            Assert.Equal(file, targetActions.Single(a => a.Text == "Choose file to reopen…").Available);
            Assert.Equal(directory, targetActions.Single(a => a.Text == "Choose folder to reopen…").Available);
            var window = new WorkspaceWindow(rig.Workspace);
            try
            {
                WorkspaceViewTests.Descendants(window).OfType<ListBox>().Single().SelectedIndex = 0;
                var buttons = WorkspaceViewTests.Descendants(window).OfType<Button>().ToArray();
                Assert.Equal(website, buttons.Single(b => b.Content is "Set website URL…" or "Web resource settings…").Visibility == Visibility.Visible);
                Assert.Equal(file, buttons.Single(b => Equals(b.Content, "Choose file…")).Visibility == Visibility.Visible);
                Assert.Equal(directory, buttons.Single(b => Equals(b.Content, "Choose folder…")).Visibility == Visibility.Visible);
                var expander = WorkspaceViewTests.Descendants(window).OfType<Expander>().Single();
                Assert.Equal(advanced, expander.Visibility == Visibility.Visible);
                Assert.False(expander.IsExpanded);
            }
            finally { window.Close(); }
        });

    [Fact]
    public void MenuUpdatesMoveGenericTargetsIntoAdvancedWithoutLosingOrDuplicatingActions()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var rig = new WorkspaceHarness();
            var item = new WorkspaceItemView(Guid.NewGuid(), "Project", @"C:\Apps\Code.exe", @"C:\Work", false,
                new() { W = 800, H = 550 }, "Placeholder");
            using var menu = new WorkspaceContextMenu(rig.Workspace, item.Id);
            foreach (var executable in new[] { "Code.exe", "editor.exe", "editor.exe", "chrome.exe", "Code.exe" })
            {
                menu.UpdateItem(item with { ExecutablePath = @"C:\Apps\" + executable });
                var advanced = menu.Items.OfType<Forms.ToolStripMenuItem>().Single(a => a.Text == "Advanced launch settings");
                var direct = menu.Items.OfType<Forms.ToolStripMenuItem>().Count(a => a.Text!.StartsWith("Choose "));
                Assert.Equal(executable == "editor.exe" ? 0 : 2, direct);
                Assert.Equal(executable == "editor.exe" ? 2 : 0, advanced.DropDownItems.Count);
            }
        });

    [Fact]
    public void OldBrowserTitlesDoNotPretendToContainASavedWebsite()
        => WorkspaceViewTests.OnSta(() =>
        {
            var item = new WorkspaceItem { Title = "https://example.com/ - Google Chrome", ExecutablePath = @"C:\Apps\chrome.exe",
                Bounds = new() { W = 800, H = 550 } };
            using var rig = new WorkspaceHarness(new() { Items = [item] }); rig.Workspace.Start();
            var view = Assert.Single(rig.Workspace.Items);
            Assert.True(view.MissingWebsiteUrl);
            using var card = new PlaceholderForm(rig.Workspace, view);
            var labels = card.Controls.OfType<Forms.TableLayoutPanel>().Single().Controls.OfType<Forms.Label>();
            Assert.Contains(labels, label => label.Text.Contains("No website URL saved"));
            var dialog = WorkspaceInteractionDialogs.CreateWebResource(IntPtr.Zero, rig.Workspace, item.Id,
                installed: [new(item.ExecutablePath, "Chrome")], readProfiles: _ => []);
            try
            {
                Assert.Empty(WorkspaceViewTests.Descendants(dialog).OfType<TextBox>()
                    .Single(t => System.Windows.Automation.AutomationProperties.GetName(t) == "Website URL").Text);
                Assert.Contains(WorkspaceViewTests.Descendants(dialog).OfType<TextBlock>(), t => t.Text.Contains("No website URL is saved"));
                WorkspaceViewTests.Render(dialog, "browser-without-url-settings.png", 1.25);
            }
            finally { dialog.Close(); }
            Assert.Null(item.WebResource); Assert.Empty(item.Target);
        });
}
