using System;
using System.Linq;
using System.Windows;
using System.Windows.Interop;

namespace CanvasDesktop;

internal sealed partial class OverviewManager
{
    private SettingsWindow? _settingsWindow;
    private bool _settingsOverCanvas;
    internal SettingsWindow? SettingsPopup => _settingsWindow;
    public event Action? SettingsApplied;

    public void ShowSettings(bool zoomOnly = false, OverviewOverlay? pass = null)
    {
        if (_disposed || _appConfig is not AppConfig config) return;
        if (_settingsWindow != null)
        {
            if (_settingsWindow.ZoomOnly == zoomOnly) { _settingsWindow.Activate(); return; }
            CloseSettingsPopup();
        }
        CancelInertia();
        CancelInteraction();
        CloseActiveMenus();
        if (CurrentMode == OverviewMode.Zooming)
            pass ??= _passes.FirstOrDefault(p => p.Visible && p.Screen.Primary) ?? _passes.FirstOrDefault(p => p.Visible);
        var window = new SettingsWindow(config, () => { RefreshConfig(); SettingsApplied?.Invoke(); }, zoomOnly);
        _settingsWindow = window;
        _settingsOverCanvas = pass != null;
        window.Closed += (_, _) =>
        {
            if (_settingsWindow != window) return;
            _settingsWindow = null;
            bool focus = _settingsOverCanvas;
            _settingsOverCanvas = false;
            if (focus) FocusOverview();
        };
        System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(window);
        if (pass != null)
        {
            window.WindowStyle = WindowStyle.None;
            window.ResizeMode = ResizeMode.NoResize;
            window.ShowInTaskbar = false;
            window.Topmost = true;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.BorderBrush = ZoneTheme.Brush("#475569");
            window.BorderThickness = new Thickness(1);
            new WindowInteropHelper(window) { Owner = pass.Handle }.EnsureHandle();
            // Place in physical pixels after creating the HWND so mixed-DPI monitors work.
            var area = pass.Screen.WorkingArea;
            double scale = pass.DeviceDpi / 96.0;
            window.MinWidth = Math.Min(window.MinWidth, area.Width / scale);
            window.MinHeight = Math.Min(window.MinHeight, area.Height / scale);
            window.Width = Math.Min(window.Width, (area.Width - 24) / scale);
            window.Height = Math.Min(window.Height, (area.Height - 80) / scale);
            window.Show();
            int width = (int)Math.Ceiling(window.ActualWidth * scale);
            int height = (int)Math.Ceiling(window.ActualHeight * scale);
            int x = Math.Clamp(pass.OriginX + (int)(20 * scale), area.Left, Math.Max(area.Left, area.Right - width));
            int y = Math.Clamp(pass.OriginY + (int)(62 * scale), area.Top, Math.Max(area.Top, area.Bottom - height));
            PInvoke.SetWindowPos((HWND)new WindowInteropHelper(window).Handle, (HWND)new IntPtr(-1), x, y, width, height,
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
            void DismissOnDeactivate(object? sender, EventArgs args)
            {
                // Let a click-away keep focus on its destination.
                _settingsOverCanvas = false;
                window.Close();
            }
            window.Deactivated += DismissOnDeactivate;
            // WPF deactivates an active window during Close. Detach before that
            // native callback so Save, Cancel and Escape cannot re-enter Close.
            window.Closing += (_, _) => window.Deactivated -= DismissOnDeactivate;
        }
        else window.Show();
        window.Activate();
    }

    private bool CloseSettingsPopup(bool canvasOnly = false)
    {
        if (_settingsWindow == null || (canvasOnly && !_settingsOverCanvas)) return false;
        _settingsWindow.Close();
        return true;
    }
}
