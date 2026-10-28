using System;
using System.IO;

namespace CanvasDesktop;

internal sealed partial class WorkspaceService
{
    public bool SaveWebResource(Guid? id, string executable, string name, WebResource resource, WorldRect? bounds = null)
    {
        if (_disposed || !BrowserSupport.ValidResource(executable, resource) || resource == null
            || !Path.IsPathFullyQualified(executable) || !File.Exists(executable) || name == null || name.Length > 200
            || (resource.ProfilePath.Length > 0 && !BrowserSupport.ProfileExists(executable, resource.ProfilePath)))
        { _status = "Choose an installed supported browser, an HTTP or HTTPS URL, and an existing profile."; Changed?.Invoke(); return false; }
        var old = id.HasValue ? Find(id.Value) : null;
        if (id.HasValue && (old == null || old.IsFolder || !BrowserSupport.Supports(old.ExecutablePath)
            || _launching.ContainsKey(id.Value) || _queued.Contains(id.Value))) return false;
        if (old == null && Document.Items.Count >= 2000) return false;
        var item = new WorkspaceItem
        {
            Id = old?.Id ?? Guid.NewGuid(), DesktopId = old?.DesktopId ?? _desktop,
            Title = old?.Title ?? new Uri(resource.Url).Host, Name = name.Trim(), ExecutablePath = Path.GetFullPath(executable),
            WebResource = resource, CaptureChromeProfile = false, OpenOnStartup = old?.OpenOnStartup ?? false,
            Bounds = old?.Bounds ?? bounds ?? new WorldRect { X = _canvas.CamX + 80, Y = _canvas.CamY + 80, W = 1000, H = 700 }
        };
        if (!WorkspaceGeometry.ValidBounds(item.Bounds)) return false;
        int index = old == null ? Document.Items.Count : Document.Items.IndexOf(old);
        if (old == null) Document.Items.Add(item); else Document.Items[index] = item;
        if (!Save())
        {
            if (old == null) Document.Items.Remove(item); else Document.Items[index] = old;
            Changed?.Invoke(); return false;
        }
        RemoveTemporaryMembership(item.Id);
        // The new launch settings apply on the next open. Never navigate or close
        // the user's current window as a side effect of saving a URL/browser.
        if (old != null && (!WorkspaceAppIdentity.Same(old.ExecutablePath, item.ExecutablePath) || old.WebResource != resource)
            && BoundWindow(item.Id) is { } live)
        { live.Forgotten = true; _bindings.Unbind(live); }
        _ambiguous.Remove(item.Id); _launchMessages.Remove(item.Id); _status = null;
        EnsureCards(); PublishTemporaryAreaChange(); Changed?.Invoke(); return true;
    }
}
