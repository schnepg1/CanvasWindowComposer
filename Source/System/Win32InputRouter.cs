using System;
using System.Collections.Generic;
using System.Threading;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CanvasDesktop;

/// <summary>
/// Production <see cref="IInputRouter"/> wiring the raw-mouse polling thread,
/// hidden message window (hotkeys), and Win32 event hooks into one composed
/// source. Owns lifetime of each underlying component.
///
/// Mouse path: <see cref="RawMouseInput"/> drains WM_INPUT on a dedicated
/// vsync-paced thread, parses to <see cref="MouseEvent"/>s in a ring buffer,
/// and posts a per-frame callback to the UI sync context. The UI-thread
/// callback drains the ring, fires <see cref="DragStarted"/> / <see cref="ButtonDown"/>
/// directly, accumulates pan deltas + drag-end + zoom flags, then fires
/// <see cref="InputAvailable"/> so subscribers can drain via TryDrain*.
/// </summary>
internal sealed class Win32InputRouter : IInputRouter, IDisposable
{
    internal object Diagnostics => new
    {
        Enabled, Windows = _winEvents.Diagnostics, Shell = _shellSelection?.Diagnostics,
        MiddleButtonBlocked = _middleBlockHandle is { IsInvalid: false, IsClosed: false }
    };
    private readonly RawMouseInput _mouse;
    private readonly MessageWindow _msgWindow;
    private readonly Win32EventRouter _winEvents;
    private bool _disposed;
    private readonly TaskbarSelectionIntent _taskbarSelection = new() { Enabled = true };
    private readonly AltTabSelectionIntent _altTabSelection = new();
    private ShellSelectionInputHook? _shellSelection;
    private System.Windows.Forms.Timer? _switchSettleTimer;
    private long _switchGeneration;
    private long _switchWaitStarted;
    private Func<IntPtr, bool>? _canSelectWindow;
    public void SetWindowSelectionTarget(Func<IntPtr, bool>? canSelect) => _canSelectWindow = canSelect;

    // Drainable mouse state, populated on the UI thread by DrainRing.
    private int _pendingPanDx;
    private int _pendingPanDy;
    private bool _hasPendingPan;
    private bool _dragJustEnded;
    private bool _zoomPending;
    private readonly List<PanSample> _panSamples = new();
    public long? PanReleaseTimestamp { get; private set; }
    public long? PanStartTimestamp { get; private set; }
    public IReadOnlyList<PanSample> DrainPanSamples()
    {
        var samples = _panSamples.ToArray();
        _panSamples.Clear();
        return samples;
    }

    private static long EventMilliseconds(long timestamp) => Environment.TickCount64
        - (long)System.Diagnostics.Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;

    // WH_MOUSE_LL hook state (EnableMiddleButtonBlock). The delegate is held
    // in a field so it stays GC-rooted while the native hook references it.
    private HOOKPROC? _middleBlockProc;
    private UnhookWindowsHookExSafeHandle? _middleBlockHandle;

    public event Action? InputAvailable;
    public event Action? DragStarted;
    public event Action? ButtonDown;
    public event Action? SearchHotkey;
    public event Action? OverviewHotkey;
    public event Action? EscPressed;

    public void EnableEscHotkey()
    {
        _msgWindow.EnableEscHotkey(() => EscPressed?.Invoke());
    }

    public void DisableEscHotkey()
    {
        _msgWindow.DisableEscHotkey();
    }
    public event Action<IntPtr>? WindowMinimized;
    public event Action<IntPtr>? WindowDestroyed;
    public event Action<IntPtr>? WindowShown;
    public event Action<IntPtr>? WindowRestored;
    public event Action<IntPtr>? WindowFocused;
    public event Action<IntPtr>? WindowMoved;
    public event Action<IntPtr>? WindowMoveEnded;
    public event Action? AltTabStarted;
    public event Action? AltTabEnded;
    public event Action<IntPtr>? WindowSelected;

    public void SetTaskbarNavigationEnabled(bool enabled)
    {
        if (_disposed) return;
        // Foreground taskbar choices navigate in both desktop and Canvas views.
        // Only Canvas view converts the current app's minimize toggle into navigation.
        _taskbarSelection.NavigateMinimize = enabled;
        _taskbarSelection.Cancel();
    }

    public Win32InputRouter(IAppConfig config)
    {
        try
        {
            _mouse = new RawMouseInput(config, OnInputFrame);

            _msgWindow = new MessageWindow();
            _msgWindow.RegisterHandlers(
                onSearchHotkey: config.DisableSearch ? null : () => SearchHotkey?.Invoke(),
                onOverviewHotkey: config.DisableZoomHotkey ? null : () => OverviewHotkey?.Invoke());

            _winEvents = new Win32EventRouter(_taskbarSelection, _altTabSelection,
                window => _canSelectWindow?.Invoke(window) == true);
            _winEvents.WindowMinimized += h => WindowMinimized?.Invoke(h);
            _winEvents.WindowDestroyed += h => WindowDestroyed?.Invoke(h);
            _winEvents.WindowShown += h => WindowShown?.Invoke(h);
            _winEvents.WindowRestored += h => WindowRestored?.Invoke(h);
            _winEvents.WindowFocused += h => WindowFocused?.Invoke(h);
            _winEvents.WindowMoved += h => WindowMoved?.Invoke(h);
            _winEvents.WindowMoveEnded += h => WindowMoveEnded?.Invoke(h);
            _winEvents.AltTabStarted += () => AltTabStarted?.Invoke();
            _winEvents.AltTabEnded += () => AltTabEnded?.Invoke();
            _winEvents.WindowSelected += h => WindowSelected?.Invoke(h);

            var context = SynchronizationContext.Current ?? new System.Windows.Forms.WindowsFormsSynchronizationContext();
            _shellSelection = new(_taskbarSelection, _altTabSelection,
                () => context.Post(_ => { if (!_disposed) _winEvents.BeginKeyboardSwitch(); }, null),
                generation => context.Post(_ => ScheduleSwitchCompletion(generation), null));

            _mouse.Install();
        }
        catch
        {
            try { Dispose(); }
            catch (Exception ex) { TrayApp.Log("Input startup rollback failed: " + ex); }
            throw;
        }
    }

    private void ScheduleSwitchCompletion(long generation)
    {
        if (_disposed) return;
        _switchGeneration = generation;
        _switchWaitStarted = Environment.TickCount64;
        if (_switchSettleTimer == null)
        {
            _switchSettleTimer = new() { Interval = 30 };
            _switchSettleTimer.Tick += (_, _) =>
            {
                if (_disposed) return;
                // Let Windows finish the release before reading its selected HWND.
                // This path also covers fast switches with no native SWITCHEND.
                var window = PInvoke.GetForegroundWindow();
                bool targetReady = _canSelectWindow?.Invoke(window) == true;
                // The shell's transient switcher can remain foreground for the
                // first frame after Alt is released. Wait for an enrolled app,
                // not a guessed shell class or the previously focused app.
                if (!targetReady && Environment.TickCount64 - _switchWaitStarted < 1000) return;
                _switchSettleTimer.Stop();
                if (_altTabSelection.TryComplete(_switchGeneration, out bool selected))
                    _winEvents.CompleteKeyboardSwitch(window, selected && targetReady);
            };
        }
        _switchSettleTimer.Stop();
        _switchSettleTimer.Start();
    }

    /// <summary>
    /// Called on the UI thread once per vsync (when <see cref="RawMouseInput"/>
    /// has new events). Drains the ring buffer: button-down / drag-start fire
    /// events synchronously; pan / drag-end / zoom go into drainable state and
    /// callers consume via TryDrain* after <see cref="InputAvailable"/>.
    /// </summary>
    private void OnInputFrame()
    {
        if (_disposed) return;
        bool any = false;
        while (_mouse.Events.TryDequeue(out var evt))
        {
            any = true;
            switch (evt.Type)
            {
                case MouseEventType.DragStarted:
                    FlushPendingMouseFrame();
                    PanStartTimestamp = EventMilliseconds(evt.Timestamp);
                    DragStarted?.Invoke();
                    break;
                case MouseEventType.ButtonDown:
                    FlushPendingMouseFrame();
                    ButtonDown?.Invoke();
                    break;
                case MouseEventType.Pan:
                    _panSamples.Add(new(evt.Dx, evt.Dy, EventMilliseconds(evt.Timestamp)));
                    _pendingPanDx += evt.Dx;
                    _pendingPanDy += evt.Dy;
                    _hasPendingPan = true;
                    break;
                case MouseEventType.DragEnded:
                    _dragJustEnded = true;
                    PanReleaseTimestamp = EventMilliseconds(evt.Timestamp);
                    FlushPendingMouseFrame();
                    break;
                case MouseEventType.Zoom:
                    _zoomPending = true;
                    break;
            }
        }

        if (any)
            InputAvailable?.Invoke();
    }

    private void FlushPendingMouseFrame()
    {
        if (_hasPendingPan || _dragJustEnded || _zoomPending) InputAvailable?.Invoke();
    }

    public bool TryDrainPanDelta(out int dx, out int dy)
    {
        if (!_hasPendingPan)
        {
            dx = dy = 0;
            return false;
        }
        dx = _pendingPanDx;
        dy = _pendingPanDy;
        _pendingPanDx = 0;
        _pendingPanDy = 0;
        _hasPendingPan = false;
        return dx != 0 || dy != 0;
    }

    public bool TryDrainDragEnded()
    {
        if (!_dragJustEnded) return false;
        _dragJustEnded = false;
        return true;
    }

    public bool TryDrainZoom()
    {
        if (!_zoomPending) return false;
        _zoomPending = false;
        return true;
    }

    public bool Enabled
    {
        get { return _mouse.Enabled; }
        set { _mouse.Enabled = value; }
    }

    public void SetExtraPanSurfaces(IEnumerable<IntPtr> handles)
    {
        _mouse.SetExtraPanSurfaces(handles);
    }

    public void ClearExtraPanSurfaces()
    {
        _mouse.ClearExtraPanSurfaces();
    }

    /// <summary>
    /// Install WH_MOUSE_LL on the calling (UI) thread. Blocks WM_MBUTTONDOWN
    /// system-wide; matching WM_MBUTTONUP must pass through so any window
    /// that received the down before Install can complete its release.
    /// Idempotent.
    /// </summary>
    public void EnableMiddleButtonBlock()
    {
        if (_middleBlockHandle is { IsInvalid: false }) return;
        _middleBlockProc = MiddleButtonHookCallback;
        _middleBlockHandle = PInvoke.SetWindowsHookEx(
            WINDOWS_HOOK_ID.WH_MOUSE_LL,
            _middleBlockProc,
            PInvoke.GetModuleHandle((string?)null),
            0);
        if (_middleBlockHandle.IsInvalid)
        {
            _middleBlockHandle.Dispose();
            _middleBlockHandle = null;
            _middleBlockProc = null;
        }
    }

    public void DisableMiddleButtonBlock()
    {
        if (_middleBlockHandle == null) return;
        _middleBlockHandle.Dispose();
        _middleBlockHandle = null;
        _middleBlockProc = null;
    }

    private LRESULT MiddleButtonHookCallback(int nCode, WPARAM wParam, LPARAM lParam)
    {
        if (nCode >= 0 && (uint)wParam.Value == PInvoke.WM_MBUTTONDOWN)
            return (LRESULT)1;
        return PInvoke.CallNextHookEx(HHOOK.Null, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var failures = new List<Exception>();
        void Attempt(Action action) { try { action(); } catch (Exception ex) { failures.Add(ex); } }
        Attempt(DisableMiddleButtonBlock);
        Attempt(() => _switchSettleTimer?.Dispose());
        Attempt(() => _shellSelection?.Dispose());
        Attempt(() => _winEvents?.Dispose());
        Attempt(() => _mouse?.Dispose());
        Attempt(() => _msgWindow?.Dispose());
        if (failures.Count > 0) throw new AggregateException("Input cleanup failed.", failures);
    }
}
