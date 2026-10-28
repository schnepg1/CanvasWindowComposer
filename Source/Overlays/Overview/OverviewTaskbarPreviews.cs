using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace CanvasDesktop;

// Explorer's grouped-window chooser can sit below our topmost surfaces. Expose
// its native rectangle rather than changing Explorer's z-order or stealing focus.
internal sealed class OverviewTaskbarPreviews : IDisposable
{
    private readonly Action<Rectangle[]> _changed;
    private readonly Dictionary<IntPtr, Rectangle> _previews = new();
    private readonly WINEVENTPROC _callback;
    private UnhookWinEventSafeHandle? _lifetimeHook, _locationHook;
    private bool _enabled;

    public OverviewTaskbarPreviews(Action<Rectangle[]> changed)
    {
        _changed = changed;
        _callback = OnWinEvent;
    }

    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled) return;
        _enabled = enabled;
        if (enabled)
        {
            uint flags = PInvoke.WINEVENT_OUTOFCONTEXT | PInvoke.WINEVENT_SKIPOWNPROCESS;
            _lifetimeHook = PInvoke.SetWinEventHook(PInvoke.EVENT_OBJECT_DESTROY,
                PInvoke.EVENT_OBJECT_HIDE, null, _callback, 0, 0, flags);
            _locationHook = PInvoke.SetWinEventHook(PInvoke.EVENT_OBJECT_LOCATIONCHANGE,
                PInvoke.EVENT_OBJECT_LOCATIONCHANGE, null, _callback, 0, 0, flags);
            // Also cover a chooser which was already open when Canvas appeared.
            PInvoke.EnumWindows((window, _) => { Update(window); return true; }, default);
        }
        else
        {
            _lifetimeHook?.Dispose(); _lifetimeHook = null;
            _locationHook?.Dispose(); _locationHook = null;
            _previews.Clear();
        }
        Publish();
    }

    private void OnWinEvent(HWINEVENTHOOK hook, uint type, HWND window,
        int objectId, int childId, uint thread, uint timestamp)
    {
        if (!_enabled || objectId != (int)OBJECT_IDENTIFIER.OBJID_WINDOW || childId != 0) return;
        bool changed = type is PInvoke.EVENT_OBJECT_HIDE or PInvoke.EVENT_OBJECT_DESTROY
            ? _previews.Remove(window) : Update(window);
        if (changed) Publish();
    }

    private bool Update(HWND window)
    {
        Span<char> name = stackalloc char[128];
        int length = PInvoke.GetClassName(window, name);
        if (length == 0 || !name[..length].SequenceEqual("TaskListThumbnailWnd")) return false;
        if (!PInvoke.IsWindowVisible(window) || !PInvoke.GetWindowRect(window, out var rect)
            || rect.right <= rect.left || rect.bottom <= rect.top)
            return _previews.Remove(window);
        var bounds = Rectangle.FromLTRB(rect.left, rect.top, rect.right, rect.bottom);
        if (_previews.TryGetValue(window, out var previous) && previous == bounds) return false;
        _previews[window] = bounds;
        return true;
    }

    private void Publish() => _changed(_previews.Values.ToArray());

    public void Dispose()
    {
        SetEnabled(false);
        GC.KeepAlive(_callback);
    }
}
