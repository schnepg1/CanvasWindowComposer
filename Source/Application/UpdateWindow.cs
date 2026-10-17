using System;
using System.Drawing;
using System.Windows.Forms;

namespace CanvasDesktop;

internal sealed class UpdateWindow : Form
{
    private readonly AppUpdateController _updates;
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(520, 0) };
    private readonly TextBox _details = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill };
    private readonly ComboBox _channel = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
    private readonly CheckBox _automatic = new() { Text = "Check automatically every six hours", AutoSize = true };
    private readonly Button _check = new() { Text = "Check now", AutoSize = true };
    private readonly Button _download = new() { Text = "Download update", AutoSize = true };
    private readonly Button _restart = new() { Text = "Restart to update", AutoSize = true };
    private readonly Button _save = new() { Text = "Save preferences", AutoSize = true };

    internal UpdateWindow(AppUpdateController updates, AppConfig config, Action restart)
    {
        _updates = updates;
        Text = "Canvas — Updates"; StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi; ClientSize = new(600, 400); MinimumSize = new(560, 360);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(18), ColumnCount = 1, RowCount = 5 };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = $"Canvas {updates.Version}", AutoSize = true, Font = new Font("Segoe UI", 12, FontStyle.Bold) }, 0, 0);
        var preferences = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        _channel.Items.AddRange(["stable", "preview"]); _channel.SelectedItem = updates.Channel;
        _automatic.Checked = config.CheckForUpdates;
        preferences.Controls.AddRange([new Label { Text = "Channel", AutoSize = true, Padding = new(0, 6, 0, 0) }, _channel, _automatic, _save]);
        layout.Controls.Add(preferences, 0, 1); layout.Controls.Add(_status, 0, 2); layout.Controls.Add(_details, 0, 3);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        var copy = new Button { Text = "Copy details", AutoSize = true };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetDataObject($"Canvas {updates.Version}\r\n{updates.Message}\r\n{updates.ErrorDetails}", true, 5, 100); }
            catch (System.Runtime.InteropServices.ExternalException) { _status.Text = "Clipboard busy. Select the details and press Ctrl+C, or try again."; }
        };
        _check.Click += async (_, _) => await updates.CheckAsync();
        _download.Click += async (_, _) => await updates.DownloadAsync();
        _restart.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "Save your workspace and restart Canvas to install the downloaded update?", "Canvas update", MessageBoxButtons.OKCancel) == DialogResult.OK) restart();
        };
        _save.Click += (_, _) =>
        {
            try { updates.ChangePreferences(_automatic.Checked, (string)_channel.SelectedItem!); }
            catch (Exception ex) { _details.Text = ex.ToString(); _status.Text = "Update preferences could not be saved."; }
        };
        actions.Controls.AddRange([_check, _download, _restart, copy]); layout.Controls.Add(actions, 0, 4);
        Controls.Add(layout); updates.Changed += RefreshState; RefreshState();
    }

    private void RefreshState()
    {
        _status.Text = _updates.Message; _details.Text = _updates.ErrorDetails ?? "Updates never restart Canvas without your confirmation. Preview releases may contain unfinished features.";
        _check.Enabled = _updates.Installed && !_updates.Busy;
        _download.Enabled = _check.Enabled && _updates.Available != null && _updates.ApplyAfterExit == null;
        _restart.Enabled = !_updates.Busy && _updates.ApplyAfterExit != null;
        _save.Enabled = _channel.Enabled = _automatic.Enabled = !_updates.Busy;
    }
    protected override void Dispose(bool disposing) { if (disposing) _updates.Changed -= RefreshState; base.Dispose(disposing); }
}
