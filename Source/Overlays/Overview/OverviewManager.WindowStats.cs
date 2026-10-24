using System.Linq;
using System.Windows.Forms;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CanvasDesktop;

internal sealed partial class OverviewManager
{
    private string WindowStats()
    {
        int active = 0, minimized = 0, automatic = 0;
        // Count native applications, including minimized ones omitted from the
        // overview list. Saved placeholders must never inflate these totals.
        foreach (var handle in _mainCanvas.Windows.Keys.ToArray())
        {
            if (Control.FromHandle(handle) is PlaceholderForm || !_win32.IsWindowVisible(handle)) continue;
            bool isMinimized = (_win32.GetWindowStyle(handle) & (int)WINDOW_STYLE.WS_MINIMIZE) != 0;
            if (isMinimized)
            {
                minimized++;
                if (_wm.GetRuntimeVisibility(handle)?.AutomaticallyParked == true) automatic++;
            }
            else active++;
        }
        return $"Active: {active}  ·  Minimized: {minimized} (auto: {automatic})";
    }
}
