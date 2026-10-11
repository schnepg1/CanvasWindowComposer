using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace CanvasDesktop;

internal static class WorkspaceInteractionDialogs
{
    private static Window Create(IntPtr owner, string title, double width, double height)
    {
        var window = new Window { Title = title, Width = width, Height = height,
            MinWidth = 400, MinHeight = 240, ShowInTaskbar = false,
            WindowStartupLocation = owner == IntPtr.Zero ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner };
        ZoneTheme.Apply(window);
        new WindowInteropHelper(window).Owner = owner;
        return window;
    }

    public static void NamePlace(IntPtr owner, IWorkspaceCommands workspace, Guid id)
    {
        var dialog = Create(owner, "Name this saved place", 520, 280);
        var root = new StackPanel { Margin = new Thickness(24) }; dialog.Content = root;
        root.Children.Add(new TextBlock { Text = "Choose a name, or leave blank to follow the application's window title.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });
        var name = new TextBox { MaxLength = 200, Text = workspace.Items.FirstOrDefault(i => i.Id == id)?.Name ?? "" };
        System.Windows.Automation.AutomationProperties.SetName(name, "Saved place name");
        root.Children.Add(name);
        var actions = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Margin = new Thickness(0, 12, 0, 0) };
        var save = ZoneTheme.Button("Save", () => dialog.DialogResult = true); save.IsDefault = true;
        var cancel = ZoneTheme.Button("Cancel", () => dialog.DialogResult = false); cancel.IsCancel = true;
        save.Margin = new Thickness(0, 0, 6, 0); cancel.Margin = new Thickness(6, 0, 0, 0);
        actions.Children.Add(save); actions.Children.Add(cancel); root.Children.Add(actions);
        dialog.Loaded += (_, _) => { name.Focus(); name.SelectAll(); };
        if (dialog.ShowDialog() == true) workspace.SetName(id, name.Text);
    }

    public static void LinkWindow(IntPtr owner, IWorkspaceCommands workspace, Guid id)
        => CreateLinkWindow(owner, workspace, id).ShowDialog();

    internal static Window CreateLinkWindow(IntPtr owner, IWorkspaceCommands workspace, Guid id)
    {
        var dialog = Create(owner, "Link an open window", 700, 420);
        var root = new DockPanel { Margin = new Thickness(24) }; dialog.Content = root;
        var hint = new TextBlock { Text = "Select the window to move to this saved place. Only unbound windows of the same application on this desktop are listed.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
        DockPanel.SetDock(hint, Dock.Top); root.Children.Add(hint);
        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var candidates = workspace.LinkCandidates(id);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap,
            Text = candidates.Count == 0 ? "No eligible windows. Open the application, then try again." : "" };
        footer.Children.Add(status);
        var list = new ListBox { ItemsSource = candidates, DisplayMemberPath = nameof(WorkspaceLinkCandidate.Description) };
        System.Windows.Automation.AutomationProperties.SetName(list, "Eligible open windows");
        var actions = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
        var link = ZoneTheme.Button("Link selected window", () =>
        {
            if (list.SelectedItem is not WorkspaceLinkCandidate candidate) return;
            if (workspace.LinkWindow(id, candidate.Token)) dialog.DialogResult = true;
            else
            {
                status.Text = "That window is no longer available. Choose another window or try again.";
                list.ItemsSource = workspace.LinkCandidates(id);
                list.SelectedIndex = -1;
            }
        });
        link.IsDefault = true; link.IsEnabled = false;
        list.SelectionChanged += (_, _) => link.IsEnabled = list.SelectedItem != null;
        var cancel = ZoneTheme.Button("Cancel", () => dialog.DialogResult = false); cancel.IsCancel = true;
        link.Margin = new Thickness(0, 4, 6, 0); cancel.Margin = new Thickness(6, 4, 0, 0);
        actions.Children.Add(link); actions.Children.Add(cancel); footer.Children.Add(actions);
        root.Children.Add(list);
        return dialog;
    }
}
