using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace CanvasDesktop;

internal sealed partial class OverviewManager
{
    private readonly GesturePreviewPolicy _gesturePreviews = new();
    private readonly IClock _gestureClock;
    private readonly Dictionary<IntPtr, string> _gestureTitles = new();
    private Timer? _gestureSettleTimer;
    private bool _restoringGesturePreviews;
    private long _gestureRestoreUntil;
    private bool? _parkingBeforeGesture;

    private long _zoomSettleUntil;
    private Timer? _zoomSettleTimer;

    internal bool GesturePreviewsActive => _gesturePreviews.Active;
    internal IReadOnlyList<PlaceholderAnnotation> GesturePreviewCards => _annotationCards;

    internal bool IsZoomSettling => _gestureClock.TickCount64 < _zoomSettleUntil || _smoothZoom.IsActive;

    internal void MarkZoomActive()
    {
        bool wasSettling = _gestureClock.TickCount64 < _zoomSettleUntil;
        _zoomSettleUntil = _gestureClock.TickCount64 + GesturePreviewPolicy.SettleMs;
        if (!wasSettling)
        {
            _cardTransitions.Clear();
            _cardAnimationTimer?.Stop();
        }
        SuspendParkingForMotion();

        _zoomSettleTimer ??= new Timer();
        _zoomSettleTimer.Stop();
        _zoomSettleTimer.Interval = GesturePreviewPolicy.SettleMs;
        _zoomSettleTimer.Tick -= OnZoomSettleTick;
        _zoomSettleTimer.Tick += OnZoomSettleTick;
        _zoomSettleTimer.Start();
    }

    internal void SettleZoomMotion()
    {
        _zoomSettleTimer?.Stop();
        _zoomSettleUntil = 0;
        if (_disposed || CurrentMode == OverviewMode.Hidden) return;
        ResumeParkingAfterMotion();
        ReconcileVisuals();
    }

    private void OnZoomSettleTick(object? sender, EventArgs e) => SettleZoomMotion();

    internal void SuspendParkingForMotion()
    {
        _parkingBeforeGesture ??= _wm.SuspendAutomaticParking;
        _wm.SuspendAutomaticParking = true;
    }

    internal void ResumeParkingAfterMotion()
    {
        if (_parkingBeforeGesture.HasValue)
        {
            _wm.SuspendAutomaticParking = _parkingBeforeGesture.Value;
            _parkingBeforeGesture = null;
            _wm.RefreshAutomaticParking();
        }
    }

    private bool GesturePointerHeld => _tools.Kind is CanvasInteractionKind.Pan or CanvasInteractionKind.Window or CanvasInteractionKind.WindowResize;

    private bool UseCachedOverviewSource(IntPtr handle)
        => _wm.UsesCachedPreview(handle) || (GesturePreviewsActive
            && _mainCanvas.Windows.TryGetValue(handle, out var world)
            && !world.PinnedToScreen && world.State != WindowState.Maximized
            && _wm.HasCachedPreview(handle));

    private void ObserveGestureMotion() => _gesturePreviews.Motion(_gestureClock.TickCount64);

    private void UpdateGesturePreviews()
    {
        bool wasActive = GesturePreviewsActive;
        bool active = _gesturePreviews.Update(_appConfig.LowQualityMode,
            CurrentMode != OverviewMode.Hidden && !IsStartupIntroActive,
            CurrentMode == OverviewMode.Panning, GesturePointerHeld, _gestureClock.TickCount64);
        _thumbnails.SimplifyDuringMotion = active;
        if (wasActive != active)
        {
            if (active)
            {
                _gestureTitles.Clear();
                _restoringGesturePreviews = false;
                _cardAnimationTimer?.Stop();
                _cardTransitions.Clear();
                SuspendParkingForMotion();
            }
            else
            {
                _restoringGesturePreviews = true;
                _gestureRestoreUntil = _gestureClock.TickCount64 + 1000;
                ResumeParkingAfterMotion();
            }
        }

        if (!active || CurrentMode == OverviewMode.Panning || GesturePointerHeld)
        {
            _gestureSettleTimer?.Stop();
            return;
        }
        ScheduleGestureTick(_gesturePreviews.RemainingMs(_gestureClock.TickCount64));
    }

    private void ScheduleGestureTick(int delay)
    {
        if (_gestureSettleTimer == null)
        {
            _gestureSettleTimer = new Timer();
            _gestureSettleTimer.Tick += (_, _) => TickGesturePreviews();
        }
        _gestureSettleTimer.Stop();
        _gestureSettleTimer.Interval = delay;
        _gestureSettleTimer.Start();
    }

    private void RetryGestureRestoration(bool hasFallback)
    {
        if (!_restoringGesturePreviews) return;
        if (!hasFallback)
        {
            _restoringGesturePreviews = false;
            _gestureTitles.Clear();
        }
        else if (_gestureClock.TickCount64 < _gestureRestoreUntil)
            ScheduleGestureTick(250);
        // A persistently failing source retains its card but stops polling after
        // one second. Subsequent input/window changes can retry it normally.
    }

    internal void TickGesturePreviews()
    {
        _gestureSettleTimer?.Stop();
        if (!IsZoomSettling) SettleZoomMotion();
        else if (!_disposed && CurrentMode != OverviewMode.Hidden) ReconcileVisuals();
    }

    private string GestureTitle(IntPtr handle)
    {
        if (_gestureTitles.TryGetValue(handle, out var title)) return title;
        title = _placeholderTitles.TryGetValue(handle, out var saved) ? saved : _win32.GetWindowTitle(handle);
        if (string.IsNullOrWhiteSpace(title)) title = "Application";
        _gestureTitles[handle] = title;
        return title;
    }

    private void ResetGesturePreviews()
    {
        _gestureSettleTimer?.Stop();
        _gesturePreviews.Reset();
        _restoringGesturePreviews = false;
        _gestureTitles.Clear();
        _thumbnails.SimplifyDuringMotion = false;
        _thumbnails.CancelSettle();
        _zoomSettleTimer?.Stop();
        _zoomSettleUntil = 0;
        if (_parkingBeforeGesture.HasValue)
        {
            _wm.SuspendAutomaticParking = _parkingBeforeGesture.Value;
            _parkingBeforeGesture = null;
        }
    }
}
