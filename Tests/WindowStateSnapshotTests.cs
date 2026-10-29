using System.Runtime.InteropServices;
using System.Text.Json;
using CanvasDesktop;
using Forms = System.Windows.Forms;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class WindowStateSnapshotTests
{
    [Fact]
    [Trait("Category", "NativePreview")]
    public void TrayRecordingWritesNativeAndCanvasStateAndStopsAfterItsBoundedDuration()
        => WorkspaceViewTests.OnSta(() =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "CanvasWindowDiagnostics-" + Guid.NewGuid().ToString("N"));
            string? completed = null;
            try
            {
                using var dispatcher = new Forms.Control(); _ = dispatcher.Handle;
                using var recorder = new WindowDiagnosticsRecorder(() => new { TestState = "captured" }, path => completed = path,
                    durationMilliseconds: 1500);
                string path = recorder.Start(directory);
                Assert.Equal(path, recorder.Start(directory)); // A repeated click must not start an overlapping recording.
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                while (completed == null && elapsed.Elapsed.TotalSeconds < 7)
                {
                    Forms.Application.DoEvents();
                    Thread.Sleep(20);
                }
                Assert.Equal(path, completed);
                var lines = File.ReadAllLines(path);
                Assert.True(lines.Length >= 2);
                using var first = JsonDocument.Parse(lines[0]);
                Assert.Equal("captured", first.RootElement.GetProperty("Canvas").GetProperty("TestState").GetString());
                Assert.True(first.RootElement.GetProperty("Native").GetProperty("Windows").GetArrayLength() > 0);
                using var last = JsonDocument.Parse(lines[^1]);
                Assert.True(last.RootElement.GetProperty("Native").GetProperty("CapturedUtc").GetDateTime()
                    > first.RootElement.GetProperty("Native").GetProperty("CapturedUtc").GetDateTime());
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        });

    [Fact]
    [Trait("Category", "NativePreview")]
    public void CaptureReportsNativeGeometryAndCaptureWithoutRecordingWindowTitles()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var owned = new Forms.Form { Text = "private test document title", ShowInTaskbar = false,
                StartPosition = Forms.FormStartPosition.Manual, Location = new(-30000, -30000), Size = new(300, 200) };
            owned.Show();
            owned.Capture = true;
            try
            {
                var snapshot = WindowStateSnapshot.Capture();
                var window = Assert.Single(snapshot.Windows, w => w.Handle == owned.Handle.ToInt64());
                Assert.Equal(Environment.ProcessId, window.ProcessId);
                Assert.True(window.Visible);
                Assert.Equal(-30000, window.Bounds.Left);
                Assert.Equal(owned.Handle.ToInt64(), window.Gui.Capture);
                Assert.Empty(window.Title);
                var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { IncludeFields = true });
                Assert.DoesNotContain(owned.Text, json);
                Assert.Contains("Capture", json);
                Assert.Equal(owned.Handle, GetCapture()); // Inspection leaves capture intact.
            }
            finally { owned.Capture = false; }
        });

    [DllImport("user32.dll")] private static extern IntPtr GetCapture();
}
