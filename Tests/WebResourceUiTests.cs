using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class WebResourceUiTests
{
    private static T Field<T>(Window window, string name) where T : DependencyObject
        => WorkspaceViewTests.Descendants(window).OfType<T>().Single(c => AutomationProperties.GetName(c) == name);
    private static void Save(Window window) => WorkspaceViewTests.Descendants(window).OfType<Button>()
        .Single(b => Equals(b.Content, "Save")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [Fact]
    public void BrowserChangeClearsTheOldProfileAndSavePersistsTheNewSelection()
        => WorkspaceViewTests.OnSta(() =>
        {
            string root = Path.Combine(Path.GetTempPath(), "canvas-web-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try
            {
                string chrome = Path.Combine(root, "chrome.exe"), firefox = Path.Combine(root, "firefox.exe");
                File.WriteAllText(chrome, "test"); File.WriteAllText(firefox, "test");
                string chromeProfile = Path.Combine(root, "Chrome", "Profile 1"), firefoxProfile = Path.Combine(root, "Firefox", "work");
                Directory.CreateDirectory(chromeProfile); Directory.CreateDirectory(firefoxProfile);
                File.WriteAllText(Path.Combine(chromeProfile, "Preferences"), "{}"); File.WriteAllText(Path.Combine(firefoxProfile, "prefs.js"), "");
                var item = new WorkspaceItem { ExecutablePath = chrome, ChromeProfilePath = chromeProfile,
                    Bounds = new WorldRect { X = 100, Y = 100, W = 1000, H = 700 } };
                using var rig = new WorkspaceHarness(new() { Items = [item] }); rig.Workspace.Start();
                var browsers = new[] { new BrowserInstallation(chrome, "Chrome"), new BrowserInstallation(firefox, "Firefox") };
                IReadOnlyList<ChromeProfileChoice> Profiles(string exe) => [new(exe == chrome ? chromeProfile : firefoxProfile, "Work")];
                var window = WorkspaceInteractionDialogs.CreateWebResource(IntPtr.Zero, rig.Workspace, item.Id, installed: browsers, readProfiles: Profiles);
                window.Loaded += (_, _) =>
                {
                    Assert.Equal(chromeProfile, ((ChromeProfileChoice)Field<ComboBox>(window, "Browser profile").SelectedItem).Path);
                    Field<ComboBox>(window, "Browser").SelectedItem = browsers[1];
                    Assert.Equal("", ((ChromeProfileChoice)Field<ComboBox>(window, "Browser profile").SelectedItem).Path);
                    Field<ComboBox>(window, "Browser profile").SelectedIndex = 1;
                    Field<TextBox>(window, "Website URL").Text = "https://dash.cloudflare.com/";
                    Field<TextBox>(window, "Web resource name").Text = "Cloudflare — Work";
                    Save(window);
                };
                Assert.True(window.ShowDialog());
                var saved = Assert.Single(rig.Workspace.Document.Items);
                Assert.Equal(firefox, saved.ExecutablePath); Assert.Equal(firefoxProfile, saved.WebResource!.ProfilePath);
                Assert.Equal("Cloudflare — Work", saved.Name); Assert.Empty(saved.ChromeProfilePath);
            }
            finally { Directory.Delete(root, recursive: true); }
        });

    [Fact]
    public void InvalidUrlsAndCancelLeaveTheWorkspaceUnchanged()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var rig = new WorkspaceHarness(); rig.Workspace.Start();
            var browsers = new[] { new BrowserInstallation(@"C:\Apps\chrome.exe", "Chrome") };
            var window = WorkspaceInteractionDialogs.CreateWebResource(IntPtr.Zero, rig.Workspace, installed: browsers, readProfiles: _ => []);
            window.Loaded += (_, _) =>
            {
                Field<TextBox>(window, "Website URL").Text = "javascript:alert(1)";
                Save(window); Assert.True(window.IsVisible); Assert.Empty(rig.Workspace.Document.Items);
                window.DialogResult = false;
            };
            Assert.False(window.ShowDialog()); Assert.Empty(rig.Workspace.Document.Items);
        });

    [Fact]
    public void WebSettingsRenderAndBrowserMenusExposeOnlyRelevantActions()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var rig = new WorkspaceHarness();
            var browsers = new[] { new BrowserInstallation(@"C:\Apps\firefox.exe", "Firefox") };
            var window = WorkspaceInteractionDialogs.CreateWebResource(IntPtr.Zero, rig.Workspace, installed: browsers, readProfiles: _ => []);
            Field<TextBox>(window, "Website URL").Text = "https://dash.cloudflare.com/";
            Field<TextBox>(window, "Web resource name").Text = "Cloudflare dashboard";
            WorkspaceViewTests.Render(window, "web-resource-settings.png", 1.25); window.Close();
            var item = new WorkspaceItemView(Guid.NewGuid(), "Cloudflare", browsers[0].ExecutablePath, "", false,
                new WorldRect { W = 1000, H = 700 }, "Placeholder", WebResource: new("https://dash.cloudflare.com/"));
            using var menu = new WorkspaceContextMenu(rig.Workspace, item.Id); menu.UpdateItem(item);
            var actions = menu.Items.OfType<System.Windows.Forms.ToolStripMenuItem>().ToArray();
            Assert.True(actions.Single(a => a.Text == "Web resource settings…").Available);
            Assert.False(actions.Single(a => a.Text == "Chrome profile…").Available);
            Assert.False(actions.Single(a => a.Text == "Choose file to reopen…").Available);
            Assert.Equal("WEBSITE", PlaceholderPathDisplay.For(item).Kind);
            Assert.Equal("dash.cloudflare.com", PlaceholderPathDisplay.For(item).Leaf);
            Assert.Equal("https://dash.cloudflare.com/", item.DisplayTarget);
        });
}
