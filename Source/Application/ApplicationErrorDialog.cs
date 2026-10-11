using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CanvasDesktop;

/// <summary>Standalone error UI that remains usable after the application has shut down.</summary>
internal sealed class ApplicationErrorDialog : Form
{
    internal ApplicationErrorDialog(string summary, string report, Action<string>? copy = null)
    {
        Text = "Canvas Desktop — Error details";
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(820, 540);
        MinimumSize = new Size(560, 360);
        MaximizeBox = true;
        MinimizeBox = false;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 4
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var heading = new Label
        {
            Text = summary, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 12)
        };
        var details = new RichTextBox
        {
            Name = "ErrorDetails", Text = report, ReadOnly = true, Dock = DockStyle.Fill,
            WordWrap = false, DetectUrls = false, ScrollBars = RichTextBoxScrollBars.Both,
            BackColor = SystemColors.Window, ForeColor = SystemColors.WindowText,
            AccessibleName = "Full error details", Margin = new Padding(0)
        };
        var status = new Label
        {
            Text = "Select text and press Ctrl+C, or copy all details below.",
            AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 8),
            AccessibleName = "Copy status"
        };
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false, Margin = new Padding(0)
        };
        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        var copyButton = new Button { Text = "Copy details", AutoSize = true };
        copyButton.Click += (_, _) =>
        {
            try
            {
                if (copy != null) copy(report);
                else Clipboard.SetDataObject(report, true, 5, 100);
                status.Text = "Error details copied to clipboard.";
            }
            catch (ExternalException)
            {
                status.Text = "Clipboard is busy. Try again, or select text and press Ctrl+C.";
            }
        };
        buttons.Controls.Add(close);
        buttons.Controls.Add(copyButton);
        layout.Controls.Add(heading, 0, 0);
        layout.Controls.Add(details, 0, 1);
        layout.Controls.Add(status, 0, 2);
        layout.Controls.Add(buttons, 0, 3);
        Controls.Add(layout);
        AcceptButton = close;
        CancelButton = close;
        Shown += (_, _) => { details.Select(0, 0); copyButton.Focus(); };
    }

    internal static string CreateReport(Exception error, string context, bool recoveryAttempted)
        => $"Canvas Desktop error{Environment.NewLine}" +
           $"Time: {DateTimeOffset.Now:O}{Environment.NewLine}" +
           $"Context: {context}{Environment.NewLine}" +
           $"Desktop recovery: {(recoveryAttempted ? "Attempted; check the log for any recovery failures." : "Not required; the desktop session had not started.")}{Environment.NewLine}" +
           $"Version: {typeof(Program).Assembly.GetName().Version}{Environment.NewLine}" +
           $"Runtime: {RuntimeInformation.FrameworkDescription}{Environment.NewLine}" +
           $"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture}){Environment.NewLine}" +
           $"Executable: {Environment.ProcessPath}{Environment.NewLine}" +
           $"Log file: {TrayApp.LogPath}{Environment.NewLine}{Environment.NewLine}" +
           error;

    internal static void ShowError(Exception error, string context, bool recoveryAttempted = false)
    {
        string report = CreateReport(error, context, recoveryAttempted);
        bool logged = TrayApp.TryLog(report);
        report += Environment.NewLine + Environment.NewLine +
            (logged ? "This error was saved to the log file." : "Could not write the log file. Copy these details before closing.");
        using var dialog = new ApplicationErrorDialog(
            recoveryAttempted ? "Canvas stopped after an error. Desktop recovery was attempted."
                : "Canvas could not complete startup.", report);
        dialog.ShowDialog();
    }
}
