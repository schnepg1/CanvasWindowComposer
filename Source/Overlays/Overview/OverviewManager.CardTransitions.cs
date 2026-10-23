using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace CanvasDesktop;

internal sealed partial class OverviewManager
{
    private readonly OverviewCardTransitions _cardTransitions = new();
    private IReadOnlyDictionary<IntPtr, Guid> _overviewItemIds = new Dictionary<IntPtr, Guid>();
    private IReadOnlyDictionary<IntPtr, IntPtr> _cardSources = WorkspaceOverviewSnapshot.Empty.CardSources;
    private Timer? _cardAnimationTimer;

    private void RefreshCardIdentities()
    {
        var snapshot = _placeholderWorkspace?.GetOverviewSnapshot() ?? WorkspaceOverviewSnapshot.Empty;
        _overviewItemIds = snapshot.ItemIds;
        _cardSources = snapshot.CardSources;
    }

    private bool CanShowUnderlay(IntPtr source)
        => _wm.UsesCachedPreview(source)
            || (_win32.GetWindowStyle(source) & (int)WINDOW_STYLE.WS_MINIMIZE) == 0;

    private IntPtr GetCardTransitionSource(IntPtr card)
    {
        if (!_cardSources.TryGetValue(card, out var source)) return IntPtr.Zero;
        if (GesturePreviewsActive) return _wm.HasCachedPreview(source) ? source : IntPtr.Zero;
        return _overviewItemIds.TryGetValue(card, out var id) && _cardTransitions.NeedsLiveUnderlay(id)
            && CanShowUnderlay(source) ? source : IntPtr.Zero;
    }

    private void UpdateCardAnimationTimer()
    {
        if (_cardTransitions.IsAnimating || _cardTransitions.HasPendingLiveSources)
        {
            if (_cardAnimationTimer == null)
            {
                _cardAnimationTimer = new Timer { Interval = 16 };
                _cardAnimationTimer.Tick += (_, _) =>
                {
                    if (_disposed || CurrentMode == OverviewMode.Hidden) { ClearCardTransitions(); return; }
                    RefreshCardIdentities();
                    _thumbnails.Reconcile();
                    RefreshZoneAnnotations();
                    // Remove any outgoing native thumbnail immediately after its cover becomes opaque.
                    if (!_cardTransitions.IsAnimating && !_cardTransitions.HasPendingLiveSources) _thumbnails.Reconcile();
                };
            }
            // Readiness failures need a retry even before a fade can start.
            // Waiting sources poll slowly; normal idle overview has no timer.
            _cardAnimationTimer.Interval = _cardTransitions.IsAnimating ? 16 : 250;
            _cardAnimationTimer.Start();
        }
        else _cardAnimationTimer?.Stop();
    }

    private void ClearCardTransitions()
    {
        _cardAnimationTimer?.Stop();
        _cardTransitions.Clear();
        _cardSources = WorkspaceOverviewSnapshot.Empty.CardSources;
        _overviewItemIds = WorkspaceOverviewSnapshot.Empty.ItemIds;
    }
}
