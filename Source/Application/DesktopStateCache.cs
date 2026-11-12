using System;
using System.Collections.Generic;

namespace CanvasDesktop;

/// <summary>
/// Saves the canvas state per virtual desktop and swaps it on
/// <see cref="IVirtualDesktops.DesktopChanged"/>. Cancels overview inertia
/// on switch and resets the WM so projected positions don't bleed across
/// desktops. Fires <see cref="AfterStateLoaded"/> once the new desktop's
/// canvas is in place — the minimap subscribes to that for its bring-up flash.
/// </summary>
internal sealed class DesktopStateCache : IDisposable
{
    private readonly Dictionary<Guid, CanvasState> _states = new();
    private readonly Canvas _canvas;
    private readonly WindowManager _wm;
    private readonly IOverviewController _overview;
    private readonly IVirtualDesktops _vds;
    private Guid _lastDesktopId;
    private bool _disposed;
    private bool _switchPending;
    private bool _applyingSwitch;

    public event Action? AfterStateLoaded;
    public event Action? BeforeStateSaved;
    internal Guid CurrentDesktopId => _lastDesktopId;
    internal bool HasPendingSwitch => _switchPending || _applyingSwitch || _vds.CurrentDesktopId != _lastDesktopId;

    public DesktopStateCache(Canvas canvas, WindowManager wm, IOverviewController overview, IVirtualDesktops vds)
    {
        _canvas = canvas;
        _wm = wm;
        _overview = overview;
        _vds = vds;
        _lastDesktopId = vds.CurrentDesktopId;

        vds.DesktopChanged += OnDesktopChanged;
    }

    private void OnDesktopChanged()
    {
        if (_disposed || _wm.DesktopPlacementShutdownRequested) return;
        if (!_switchPending)
        {
            _switchPending = true;
            // Recovery invalidates the old close immediately and keeps any held stopping click owned.
            _overview.TryRecoverDesktopHandoff();
        }
        ApplyPendingSwitch();
    }

    internal void ApplyPendingSwitch()
    {
        if (_disposed || !_switchPending || _applyingSwitch || _wm.DesktopPlacementShutdownRequested
            || _wm.HasDesktopPlacementReservation || _overview.DesktopHandoffPending) return;

        _switchPending = false;
        if (_lastDesktopId == _vds.CurrentDesktopId) return;

        _applyingSwitch = true;
        try { SwitchToCurrentDesktop(); }
        finally { _applyingSwitch = false; }
    }

    private void SwitchToCurrentDesktop()
    {
        _overview.CancelStartupIntro();
        _overview.CancelInertia();
        _overview.CancelInteraction();

        if (_lastDesktopId != Guid.Empty)
        {
            BeforeStateSaved?.Invoke();
            _states[_lastDesktopId] = _canvas.SaveState();
            _wm.Reset();
        }

        _lastDesktopId = _vds.CurrentDesktopId;

        if (_states.TryGetValue(_lastDesktopId, out var state))
            _canvas.LoadState(state);
        _canvas.Commit();

        AfterStateLoaded?.Invoke();
        _overview.SyncCamera();
    }

    public void Dispose()
    {
        _disposed = true;
        _switchPending = false;
        _vds.DesktopChanged -= OnDesktopChanged;
    }
}
