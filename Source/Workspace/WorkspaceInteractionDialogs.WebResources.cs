using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace CanvasDesktop;

internal static partial class WorkspaceInteractionDialogs
{
    public static void WebResourceSettings(IntPtr owner, IWorkspaceCommands workspace, Guid? id = null, WorldRect? bounds = null)
        => CreateWebResource(owner, workspace, id, bounds).ShowDialog();

    internal static Window CreateWebResource(IntPtr owner, IWorkspaceCommands workspace, Guid? id = null, WorldRect? bounds = null,
        IReadOnlyList<BrowserInstallation>? installed = null, Func<string, IReadOnlyList<ChromeProfileChoice>>? readProfiles = null)
    {
        var item = workspace.Items.FirstOrDefault(i => i.Id == id);
        var dialog = Create(owner, id == null ? "Add web resource" : "Web resource settings", 680, 650);
        var root = new StackPanel { Margin = new Thickness(24) };
        dialog.Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        root.Children.Add(new TextBlock { Text = "Save a website in its own browser window at this place on the canvas.", TextWrapping = TextWrapping.Wrap });
        void Label(string label) => root.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 14, 0, 6) });
        Label("Name (optional)");
        var name = new TextBox { Text = item?.Name ?? "", MaxLength = 200 };
        AutomationProperties.SetName(name, "Web resource name"); root.Children.Add(name);
        Label("Website URL");
        var url = new TextBox { Text = item?.WebResource?.Url ?? "", MaxLength = 16384 };
        AutomationProperties.SetName(url, "Website URL"); root.Children.Add(url);
        Label("Browser");
        var browsers = (installed ?? BrowserSupport.Installed()).ToList();
        if (item != null && BrowserSupport.Supports(item.ExecutablePath) && !browsers.Any(b => b.ExecutablePath.Equals(item.ExecutablePath, StringComparison.OrdinalIgnoreCase)))
            browsers.Add(new(item.ExecutablePath, BrowserSupport.Name(item.ExecutablePath)!));
        var browser = new ComboBox { ItemsSource = browsers, DisplayMemberPath = nameof(BrowserInstallation.Description) };
        AutomationProperties.SetName(browser, "Browser"); root.Children.Add(browser);
        root.Children.Add(ZoneTheme.Button("Choose browser executable…", () =>
        {
            var picker = new Microsoft.Win32.OpenFileDialog { Title = "Choose Chrome, Edge or Firefox", Filter = "Browser executable|chrome.exe;msedge.exe;firefox.exe" };
            if (picker.ShowDialog(dialog) != true || !BrowserSupport.Supports(picker.FileName)) return;
            var choice = browsers.FirstOrDefault(b => b.ExecutablePath.Equals(picker.FileName, StringComparison.OrdinalIgnoreCase));
            if (choice == null) { choice = new(picker.FileName, BrowserSupport.Name(picker.FileName)!); browsers.Add(choice); browser.Items.Refresh(); }
            browser.SelectedItem = choice;
        }));
        Label("Profile (optional)");
        var profiles = new ComboBox { DisplayMemberPath = nameof(ChromeProfileChoice.Description) };
        AutomationProperties.SetName(profiles, "Browser profile"); root.Children.Add(profiles);
        var profilePath = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
        root.Children.Add(profilePath);
        profiles.SelectionChanged += (_, _) => profilePath.Text = (profiles.SelectedItem as ChromeProfileChoice)?.Path ?? "";
        void LoadProfiles()
        {
            var choices = new List<ChromeProfileChoice> { new("", "Use browser's default selection") };
            if (browser.SelectedItem is BrowserInstallation selected)
            {
                choices.AddRange((readProfiles ?? BrowserSupport.Profiles)(selected.ExecutablePath));
                string saved = item != null && selected.ExecutablePath.Equals(item.ExecutablePath, StringComparison.OrdinalIgnoreCase)
                    ? item.WebResource?.ProfilePath ?? item.ChromeProfilePath : "";
                if (saved.Length > 0 && !choices.Any(p => p.Path.Equals(saved, StringComparison.OrdinalIgnoreCase))) choices.Add(new(saved, "Saved profile"));
                profiles.ItemsSource = choices;
                profiles.SelectedItem = choices.FirstOrDefault(p => p.Path.Equals(saved, StringComparison.OrdinalIgnoreCase)) ?? choices[0];
            }
            else { profiles.ItemsSource = choices; profiles.SelectedIndex = 0; }
        }
        browser.SelectionChanged += (_, _) => LoadProfiles();
        browser.SelectedItem = browsers.FirstOrDefault(b => b.ExecutablePath.Equals(item?.ExecutablePath, StringComparison.OrdinalIgnoreCase)) ?? browsers.FirstOrDefault();
        LoadProfiles();
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
        root.Children.Add(ZoneTheme.Button("Choose profile folder…", () =>
        {
            if (browser.SelectedItem is not BrowserInstallation selected) { status.Text = "Choose a browser first."; return; }
            var picker = new Microsoft.Win32.OpenFolderDialog { Title = "Choose an existing " + selected.Name + " profile folder" };
            if (picker.ShowDialog(dialog) != true) return;
            if (!BrowserSupport.ProfileExists(selected.ExecutablePath, picker.FolderName))
            { status.Text = "Choose a profile folder containing " + (BrowserSupport.IsFirefox(selected.ExecutablePath) ? "prefs.js." : "Preferences."); return; }
            var choices = ((IEnumerable<ChromeProfileChoice>)profiles.ItemsSource).ToList();
            var choice = choices.FirstOrDefault(p => p.Path.Equals(picker.FolderName, StringComparison.OrdinalIgnoreCase));
            if (choice == null) { choice = new(picker.FolderName, Path.GetFileName(picker.FolderName)); choices.Add(choice); }
            profiles.ItemsSource = choices; profiles.SelectedItem = choice; status.Text = "";
        }));
        root.Children.Add(new TextBlock { Text = "Profiles and logins belong to the selected browser. Reopening uses the saved URL; browsing does not change it. Saving leaves the current window open. Existing windows may need ‘Link open window…’ after a restart.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0) });
        root.Children.Add(status);
        var actions = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Margin = new Thickness(0, 16, 0, 0) };
        var save = ZoneTheme.Button("Save", () =>
        {
            if (browser.SelectedItem is not BrowserInstallation selected) { status.Text = "Choose an installed Chrome, Edge or Firefox browser."; return; }
            string address = url.Text.Trim();
            if (!BrowserSupport.ValidUrl(address)) { status.Text = "Enter a full HTTP or HTTPS website URL, such as https://dash.cloudflare.com/."; url.Focus(); return; }
            var resource = new WebResource(address, (profiles.SelectedItem as ChromeProfileChoice)?.Path ?? "");
            if (workspace.SaveWebResource(id, selected.ExecutablePath, name.Text, resource, bounds)) dialog.DialogResult = true;
            else status.Text = workspace.Status ?? "Could not save. Check the browser/profile and wait for pending launches to finish.";
        }); save.IsDefault = true;
        var cancel = ZoneTheme.Button("Cancel", () => dialog.DialogResult = false); cancel.IsCancel = true;
        save.Margin = new Thickness(0, 0, 6, 0); cancel.Margin = new Thickness(6, 0, 0, 0);
        actions.Children.Add(save); actions.Children.Add(cancel); root.Children.Add(actions);
        return dialog;
    }
}
