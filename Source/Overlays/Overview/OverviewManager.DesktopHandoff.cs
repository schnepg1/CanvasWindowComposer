using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CanvasDesktop;

internal sealed partial class OverviewManager
{
    // The controlled integration supplies a placement that owns the native lease
    // and returns true only after verifying the final camera's window geometry.
    private readonly Func<Task<bool>?>? _requestDesktopPlacement;
    private DesktopHandoffClose? _desktopHandoff;
    private long _desktopHandoffGeneration;
    private bool _desktopPlacementFinished;
    private bool _desktopHandoffReturningToOverview;
    private bool _displayChangedDuringDesktopHandoff;
    private OverviewOverlay? _desktopHandoffRecoveryPass;
    private Guid _desktopHandoffDesktop;
    private Action? _navigationAfterDesktopHandoff;

    public bool DesktopHandoffPending => _desktopHandoff != null;
    internal event Action? DesktopHandoffRecovered;

    private void RequestDesktopPanClose()
    {
        if (DesktopHandoffPending) return;
        if (_requestDesktopPlacement == null || _passes.Count == 0)
        {
            TransitionTo(OverviewMode.Hidden);
            return;
        }

        var close = new DesktopHandoffClose();
        _desktopHandoff = close;
        _desktopHandoffGeneration = close.TryBeginPlacement()!.Value;
        _desktopPlacementFinished = false;
        _desktopHandoffReturningToOverview = false;
        _desktopHandoffDesktop = _wm.DesktopHandoffNavigationDesktop;
        CancelVisualFrame();

        Task<bool>? placement;
        try { placement = _requestDesktopPlacement(); }
        catch
        {
            ClearDesktopHandoff();
            throw;
        }
        if (placement == null)
        {
            // Preparation can pump input even when it ultimately declines the batch.
            if (_desktopHandoffReturningToOverview || _desktopStopSurface != null)
                CompleteDesktopPlacement(close, _desktopHandoffGeneration, verified: false);
            else
            {
                ClearDesktopHandoff();
                TransitionTo(OverviewMode.Hidden);
            }
            return;
        }

        if (!placement.IsCompleted && _desktopHandoffRecoveryPass == null)
            foreach (var pass in _passes)
                pass.ShowDesktopHandoffRecovery(() => OpenDesktopHandoffRecovery(pass));
        _ = ObserveDesktopPlacement(placement, close, _desktopHandoffGeneration, _passes[0]);
    }

    private async Task ObserveDesktopPlacement(Task<bool> placement, DesktopHandoffClose close, long generation, OverviewOverlay dispatcher)
    {
        bool verified;
        try { verified = await placement.ConfigureAwait(false); }
        catch (Exception) { verified = false; }

        try { dispatcher.BeginInvoke((Action)(() => CompleteDesktopPlacement(close, generation, verified))); }
        catch (InvalidOperationException) when (dispatcher.IsDisposed || !dispatcher.IsHandleCreated) { }
    }

    private void CompleteDesktopPlacement(DesktopHandoffClose close, long generation, bool verified)
    {
        if (_disposed || _desktopHandoff != close) return;
        _desktopPlacementFinished = true;
        if (_wm.DesktopPlacementShutdownRequested)
        {
            close.RequestShutdown();
            close.CompletePlacement(generation, verified);
            ClearDesktopHandoff();
            return;
        }
        if (!verified) _desktopHandoffReturningToOverview = true;
        ApplyDesktopHandoffDecision(close.CompletePlacement(generation, verified));
    }

    public bool TryRecoverDesktopHandoff()
    {
        if (_desktopHandoff is not { } close) return false;
        _desktopHandoffReturningToOverview = true;
        if (!close.TryAbandonPendingClose(_desktopHandoffGeneration)) close.Invalidate();
        ApplyDesktopHandoffDecision(DesktopHandoffCloseDecision.KeepCovered);
        return true;
    }

    public bool TryOpenDesktopHandoffRecovery()
        => OpenDesktopHandoffRecovery(_passes.Find(pass => pass.Screen.Primary));

    private bool OpenDesktopHandoffRecovery(OverviewOverlay? pass)
    {
        if (!TryRecoverDesktopHandoff()) return false;
        if (!DesktopHandoffPending || _wm.DesktopPlacementShutdownRequested || _desktopHandoffRecoveryPass != null)
            return true;
        pass ??= _passes[0];
        _desktopHandoffRecoveryPass = pass;
        foreach (var other in _passes)
            if (other != pass) other.HideDesktopHandoffRecovery();

        // Only Canvas model state and already cached labels are used while the
        // placement worker can be blocked in a foreign window procedure.
        var cards = _mainCanvas.Windows.Where(entry => !entry.Value.PinnedToScreen && !entry.Value.InInventory)
            .OrderBy(entry => entry.Value.ZOrder)
            .Select(entry => (entry.Value, _gestureTitles.GetValueOrDefault(entry.Key)
                ?? _placeholderTitles.GetValueOrDefault(entry.Key) ?? "Application window")).ToArray();
        pass.ShowDesktopHandoffRecoveryMap(() => OpenDesktopHandoffRecovery(pass),
            new(_screens, ViewCamera, _camera.MaxZoom, cards));
        return true;
    }

    private void ApplyDesktopHandoffDecision(DesktopHandoffCloseDecision decision)
    {
        if (decision == DesktopHandoffCloseDecision.ExposeDesktop && !_wm.DesktopHandoffGeometryStillVerified())
        {
            _desktopHandoffReturningToOverview = true;
            decision = DesktopHandoffCloseDecision.KeepCovered;
        }

        if (_wm.DesktopPlacementShutdownRequested)
        {
            if (_desktopPlacementFinished) ClearDesktopHandoff();
            return;
        }

        if (decision == DesktopHandoffCloseDecision.ExposeDesktop)
        {
            ClearDesktopHandoff(releasePlacement: false);
            try
            {
                TransitionToCore(OverviewMode.Hidden, syncCameraOnClose: false, zoomOutOnOpen: false,
                    desktopPlacementVerified: true);
            }
            finally { _wm.ReleaseVerifiedDesktopHandoffPlacement(); }
        }
        else if (_desktopPlacementFinished && _desktopHandoffReturningToOverview && _desktopStopSurface == null)
        {
            var navigate = _navigationAfterDesktopHandoff;
            var recoveryCamera = !_displayChangedDuringDesktopHandoff
                && _desktopHandoffDesktop == _wm.DesktopHandoffNavigationDesktop
                ? _desktopHandoffRecoveryPass?.DesktopHandoffRecoveryCamera : null;
            ClearDesktopHandoff();
            if (recoveryCamera is { } camera) _camera.SetTo(camera.X, camera.Y, camera.Zoom);
            TransitionTo(OverviewMode.Zooming);
            if (_displayChangedDuringDesktopHandoff)
            {
                _displayChangedDuringDesktopHandoff = false;
                OnDisplaySettingsChanged(this, EventArgs.Empty);
            }
            DesktopHandoffRecovered?.Invoke();
            navigate?.Invoke();
        }
    }

    private void ClearDesktopHandoff(bool releasePlacement = true)
    {
        _desktopHandoff = null;
        _desktopHandoffRecoveryPass = null;
        _navigationAfterDesktopHandoff = null;
        foreach (var pass in _passes) pass.HideDesktopHandoffRecovery();
        if (releasePlacement) _wm.ReleaseVerifiedDesktopHandoffPlacement();
    }
}
