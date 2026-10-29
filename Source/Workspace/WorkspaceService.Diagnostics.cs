using System.IO;
using System.Linq;

namespace CanvasDesktop;

internal sealed partial class WorkspaceService
{
    // IDs and application names connect native evidence to session bindings.
    // Titles, URLs, targets, profile directories and full executable paths are omitted.
    internal object WindowDiagnostics() => new
    {
        Desktop = _desktop,
        SavedPlaces = Document.Items.Count,
        Bindings = _bindings.Windows.Select(live => new
        {
            Handle = live.Identity.Handle.ToInt64(), live.Identity.ProcessId,
            live.Identity.ProcessStartedAtUtcTicks, live.ItemId, live.Forgotten,
            CurrentIdentity = StillCurrent(live), Visible = _api.IsWindowVisible(live.Identity.Handle),
            CurrentDesktop = _desktops.IsOnCurrentDesktop(live.Identity.Handle),
            CanvasTracked = _canvas.HasWindow(live.Identity.Handle),
            Placement = live.ItemId is { } id ? Find(id)?.Placement.ToString() : null
        }).ToArray(),
        Places = Document.Items.Where(i => i.DesktopId == _desktop).Select(i => new
        {
            i.Id, Application = Path.GetFileName(i.ExecutablePath), Open = IsOpen(i),
            Placement = i.Placement.ToString(), HasProfile = (i.WebResource?.ProfilePath ?? i.ChromeProfilePath).Length > 0,
            HasTarget = i.Target.Length > 0 || i.WebResource != null
        }).ToArray()
    };
}
