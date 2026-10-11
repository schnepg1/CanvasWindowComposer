using System.Runtime.InteropServices;
using System.Windows.Forms;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class ApplicationErrorDialogTests
{
    [Fact]
    public void ReportIncludesOriginalStackInnerExceptionsAndDiagnosticContext()
    {
        Exception error;
        try { throw new InvalidOperationException("Collection changed", new IOException("Inner failure")); }
        catch (Exception ex) { error = ex; }

        string report = ApplicationErrorDialog.CreateReport(error, "Application startup/run", true);
        Assert.Contains(error.ToString(), report);
        Assert.Contains(nameof(ReportIncludesOriginalStackInnerExceptionsAndDiagnosticContext), report);
        Assert.Contains("Inner failure", report);
        Assert.Contains("Context: Application startup/run", report);
        Assert.Contains("Desktop recovery: Attempted", report);
        Assert.Contains(TrayApp.LogPath, report);
        Assert.Contains("Runtime:", report);
        Assert.Contains("Version:", report);
        Assert.Contains("Time:", report);
        Assert.Contains("OS:", report);
        Assert.Contains("Desktop recovery: Not required", ApplicationErrorDialog.CreateReport(error, "Enable startup", false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DetailsAreSelectableAndCopyButtonCopiesEntireReportOrAllowsRetry(bool clipboardBusy)
        => WorkspaceViewTests.OnSta(() =>
        {
            string report = ApplicationErrorDialog.CreateReport(
                new InvalidOperationException("Collection was modified; enumeration operation may not execute."),
                "Application startup/run", true) + "\r\n" +
                string.Join("\r\n", Enumerable.Repeat("   at CanvasDesktop.WindowManager.UpdateRuntimeVisibility()", 100));
            string? copied = null;
            using var dialog = new ApplicationErrorDialog("Canvas stopped after an error.", report, text =>
            {
                if (clipboardBusy) throw new ExternalException("Clipboard is busy");
                copied = text;
            });
            dialog.Show();
            if (!clipboardBusy)
            {
                string directory = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TestResults");
                Directory.CreateDirectory(directory);
                using var preview = new System.Drawing.Bitmap(dialog.Width, dialog.Height);
                dialog.DrawToBitmap(preview, new System.Drawing.Rectangle(0, 0, preview.Width, preview.Height));
                preview.Save(Path.Combine(directory, "application-error-dialog-preview.png"));
            }
            var layout = Assert.IsType<TableLayoutPanel>(Assert.Single(dialog.Controls.Cast<Control>()));
            var details = Assert.Single(layout.Controls.OfType<RichTextBox>());
            Assert.True(details.ReadOnly);
            Assert.False(details.WordWrap);
            details.SelectAll();
            Assert.Equal(report, details.SelectedText.Replace("\n", "\r\n"));
            var buttons = Assert.Single(layout.Controls.OfType<FlowLayoutPanel>());
            var copy = buttons.Controls.OfType<Button>().Single(b => b.Text == "Copy details");
            copy.PerformClick();
            if (clipboardBusy)
            {
                Assert.Null(copied);
                Assert.Contains(layout.Controls.OfType<Label>(), label => label.Text.Contains("Clipboard is busy"));
                clipboardBusy = false;
                copy.PerformClick();
            }
            Assert.Equal(report, copied);
            Assert.Contains(layout.Controls.OfType<Label>(), label => label.Text.Contains("copied to clipboard"));
            dialog.ClientSize = new System.Drawing.Size(560, 360);
            Assert.True(details.Height > 100);
            Assert.True(copy.Enabled);
            dialog.Close();
        });
}
