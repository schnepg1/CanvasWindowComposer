using CanvasDesktop;
using Forms = System.Windows.Forms;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class PassiveShellObservationTests
{
    [Fact]
    public void GeometryRespectsPreviewHolesAndWindowsAboveTheTaskbar()
    {
        var surfaces = new[]
        {
            new WindowStateSnapshot.HitSurface { Handle = 1, ClassName = "Overlay", Bounds = new() { Left = -1000, Top = 0, Right = 0, Bottom = 900 },
                HasRegion = true, Region = [new() { Left = 0, Top = 0, Right = 1000, Bottom = 700 }] },
            new WindowStateSnapshot.HitSurface { Handle = 2, ClassName = "TaskListThumbnailWnd", Bounds = new() { Left = -900, Top = 700, Right = -600, Bottom = 850 } },
            new WindowStateSnapshot.HitSurface { Handle = 3, ClassName = "Shell_TrayWnd", Bounds = new() { Left = -1000, Top = 850, Right = 0, Bottom = 900 } }
        };
        Assert.Equal(0, WindowStateSnapshot.HitSurfaceIndex(surfaces, new() { X = -800, Y = 100 }));
        Assert.Equal(1, WindowStateSnapshot.HitSurfaceIndex(surfaces, new() { X = -800, Y = 750 }));
        Assert.Equal(2, WindowStateSnapshot.HitSurfaceIndex(surfaces, new() { X = -800, Y = 870 }));
        Assert.Equal(-1, WindowStateSnapshot.HitSurfaceIndex(surfaces, new() { X = 0, Y = 870 }));
        surfaces[0].Region = [new() { Left = 0, Top = 0, Right = 1000, Bottom = 900 }];
        Assert.Equal(0, WindowStateSnapshot.HitSurfaceIndex(surfaces, new() { X = -800, Y = 870 }));
    }

    [Fact]
    [Trait("Category", "NativePreview")]
    public void NativeGeometryAndDiagnosticCaptureDoNotSendHitTestMessagesToAnObservedWindow()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var window = new HitTestObserver { ShowInTaskbar = false,
                StartPosition = Forms.FormStartPosition.Manual, Location = new(-30000, -30000), Size = new(400, 300) };
            window.Show(); Forms.Application.DoEvents();
            int before = window.HitTests;
            var surfaces = WindowStateSnapshot.CaptureHitSurfaces();
            int index = WindowStateSnapshot.HitSurfaceIndex(surfaces, new() { X = -29900, Y = -29900 });
            Assert.InRange(index, 0, surfaces.Length - 1);
            Assert.Equal(window.Handle.ToInt64(), surfaces[index].Handle);
            Assert.Equal(before, window.HitTests);
            var report = WindowStateSnapshot.Capture();
            Assert.Equal("PassiveRootGeometry", report.HitTestMethod);
            Assert.Equal(before, window.HitTests);
        });

    private sealed class HitTestObserver : Forms.Form
    {
        public int HitTests;
        protected override void WndProc(ref Forms.Message message)
        {
            if (message.Msg == 0x84) HitTests++;
            base.WndProc(ref message);
        }
    }
}
