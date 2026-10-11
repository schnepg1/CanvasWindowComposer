using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using CanvasDesktop;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CanvasDesktop.Tests;

public class AutomaticParkingNativeTests
{
    [Fact]
    [Trait("Category", "NativeParking")]
    public void InvalidHandleFailsClosed()
    {
        var api = new Win32WindowApi(diagnostics: new PerformanceDiagnostics());
        Assert.False(api.CanAutomaticallyMinimize(new(-1)));
        Assert.False(api.MinimizeForParking(new(-1)).Accepted);
        Assert.False(api.RestoreFromParking(new(-1), new(0, 0, 400, 300)).Accepted);
    }

    [Fact]
    [Trait("Category", "NativeParking")]
    public void OwnedWindowMinimizesAndRestoresWithoutChangingForeground()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var window = new NonactivatingWindow();
                var api = new Win32WindowApi(diagnostics: new PerformanceDiagnostics());
                window.Show();
                var foreground = api.GetForegroundWindow();
                var rect = api.GetWindowRect(window.Handle);
                Assert.False(api.CanAutomaticallyMinimize(window.Handle)); // Own-process discovery is excluded.
                Assert.True(api.MinimizeForParking(window.Handle).Accepted);
                Assert.NotEqual(0, api.GetWindowStyle(window.Handle) & (int)WINDOW_STYLE.WS_MINIMIZE);
                Assert.Equal(foreground, api.GetForegroundWindow());
                Assert.True(api.RestoreFromParking(window.Handle, new(rect.x, rect.y, rect.w, rect.h)).Accepted);
                Assert.Equal(0, api.GetWindowStyle(window.Handle) & (int)(WINDOW_STYLE.WS_MINIMIZE | WINDOW_STYLE.WS_MAXIMIZE));
                Assert.Equal(rect, api.GetWindowRect(window.Handle));
                Assert.Equal(foreground, api.GetForegroundWindow());
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(10000), "Native parking smoke test timed out.");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class NonactivatingWindow : Form
    {
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x08000080; return cp; }
        }
        public NonactivatingWindow()
        {
            FormBorderStyle = FormBorderStyle.None; AutoScaleMode = AutoScaleMode.None;
            ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            var desktop = SystemInformation.VirtualScreen;
            SetBounds(desktop.Left - 600, desktop.Top - 400, 400, 300);
        }
    }
}
