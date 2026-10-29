using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace CanvasDesktop;

internal sealed record TaskbarPreviewEvent(DateTime CapturedUtc, string Source, long Window, uint Event);
internal sealed record TaskbarPreviewSnapshot(bool Enabled, bool LifetimeHookAvailable, bool LocationHookAvailable,
    DateTime? LastScanUtc, int RecoveredUpdates, Rectangle[] Previews, TaskbarPreviewEvent[] RecentEvents);

// Explorer's grouped-window chooser can sit below our topmost surfaces. Expose
// its native rectangle rather than changing Explorer's z-order or stealing focus.
internal sealed class OverviewTaskbarPreviews : IDisposable
{
    private readonly Action<Rectangle[]> _changed;
    private readonly Dictionary<IntPtr, Rectangle> _previews = new();
    private readonly WINEVENTPROC _callback;
    private UnhookWinEventSafeHandle? _lifetimeHook, _locationHook;
    private bool _enabled;
    private readonly System.Windows.Forms.Timer _recoveryTimer = new() { Interval = 500 };
    private readonly Queue<TaskbarPreviewEvent> _events = new();
    private DateTime? _lastScanUtc;
    private int _recoveredUpdates;

    public OverviewTaskbarPreviews(Action<Rectangle[]> changed)
    {
        _changed = changed;
        _callback = OnWinEvent;
        _recoveryTimer.Tick += (_, _) => Refresh();
    }

    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled) { if (enabled) Refresh(); return; }
        _enabled = enabled;
        if (enabled)
        {
            uint flags = PInvoke.WINEVENT_OUTOFCONTEXT | PInvoke.WINEVENT_SKIPOWNPROCESS;
            _lifetimeHook = PInvoke.SetWinEventHook(PInvoke.EVENT_OBJECT_DESTROY,
                PInvoke.EVENT_OBJECT_HIDE, null, _callback, 0, 0, flags);
            _locationHook = PInvoke.SetWinEventHook(PInvoke.EVENT_OBJECT_LOCATIONCHANGE,
                PInvoke.EVENT_OBJECT_LOCATIONCHANGE, null, _callback, 0, 0, flags);
            // Also cover a chooser which was already open when Canvas appeared.
            Refresh(recovery: false);
            _recoveryTimer.Start();
        }
        else
        {
            _recoveryTimer.Stop();
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
        if (_previews.ContainsKey(window) || IsPreviewWindow(window)) Record("WinEvent", window, type);
        bool changed = type is PInvoke.EVENT_OBJECT_HIDE or PInvoke.EVENT_OBJECT_DESTROY
            ? _previews.Remove(window) : Update(window);
        if (changed) Publish();
    }

    private bool Update(HWND window)
    {
        if (!IsPreviewWindow(window)) return false;
        if (!PInvoke.IsWindowVisible(window) || !PInvoke.GetWindowRect(window, out var rect)
            || rect.right <= rect.left || rect.bottom <= rect.top)
            return _previews.Remove(window);
        var bounds = Rectangle.FromLTRB(rect.left, rect.top, rect.right, rect.bottom);
        if (_previews.TryGetValue(window, out var previous) && previous == bounds) return false;
        _previews[window] = bounds;
        return true;
    }

    private static bool IsPreviewWindow(HWND window)
    {
        Span<char> name = stackalloc char[128];
        int length = PInvoke.GetClassName(window, name);
        return length > 0 && name[..length].SequenceEqual("TaskListThumbnailWnd");
    }

    // A delayed/lost shell event must not leave a chooser covered for the rest of
    // the session. Scanning also drops destroyed HWNDs after Explorer restarts.
    internal void Refresh(bool recovery = true)
    {
        if (!_enabled) return;
        var seen = new HashSet<IntPtr>();
        bool changed = false;
        bool enumerated = PInvoke.EnumWindows((window, _) =>
        {
            if (!IsPreviewWindow(window) || !PInvoke.IsWindowVisible(window)) return true;
            seen.Add(window);
            changed |= Update(window);
            return true;
        }, default);
        if (!enumerated) { Record("ScanFailed", IntPtr.Zero, 0); return; }
        foreach (var stale in _previews.Keys.Where(h => !seen.Contains(h)).ToArray())
            changed |= _previews.Remove(stale);
        _lastScanUtc = DateTime.UtcNow;
        if (changed)
        {
            if (recovery) _recoveredUpdates++;
            Record(recovery ? "ScanRecovered" : "ScanBaseline", IntPtr.Zero, 0); Publish();
        }
    }

    private void Record(string source, IntPtr window, uint type)
    {
        if (_events.Count == 128) _events.Dequeue();
        _events.Enqueue(new(DateTime.UtcNow, source, window.ToInt64(), type));
    }

    internal TaskbarPreviewSnapshot Snapshot() => new(_enabled,
        _lifetimeHook is { IsInvalid: false, IsClosed: false }, _locationHook is { IsInvalid: false, IsClosed: false },
        _lastScanUtc, _recoveredUpdates, _previews.Values.ToArray(), _events.ToArray());

    private void Publish() => _changed(_previews.Values.ToArray());

    public void Dispose()
    {
        SetEnabled(false);
        _recoveryTimer.Dispose();
        GC.KeepAlive(_callback);
    }
}
