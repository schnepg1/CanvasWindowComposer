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
    internal bool GesturePreviewsActive => _gesturePreviews.Active;
    internal IReadOnlyList<PlaceholderAnnotation> GesturePreviewCards => _annotationCards;

    private bool GesturePointerHeld => _tools.Kind is CanvasInteractionKind.Pan or CanvasInteractionKind.Window;

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
            }
            else
            {
                _restoringGesturePreviews = true;
                _gestureRestoreUntil = _gestureClock.TickCount64 + 1000;
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
        if (_disposed || CurrentMode == OverviewMode.Hidden) return;
        // Reconcile registers live sources before replacing the lightweight cards.
        ReconcileVisuals();
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
    }
}
