using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace CanvasDesktop;

internal sealed partial class OverviewManager
{
    private readonly OverviewCardTransitions _cardTransitions = new();
    private IReadOnlyDictionary<IntPtr, Guid> _overviewItemIds = new Dictionary<IntPtr, Guid>();
    private readonly Dictionary<IntPtr, IntPtr> _cardSources = new();
    private Timer? _cardAnimationTimer;

    private void RefreshCardIdentities()
    {
        _overviewItemIds = _placeholderWorkspace?.GetOverviewItemIds() ?? new Dictionary<IntPtr, Guid>();
        _cardSources.Clear();
        if (_placeholderWorkspace == null) return;
        foreach (var pair in _placeholderWorkspace.GetRepresentedWindows()) _cardSources[pair.Value] = pair.Key;
    }

    private bool CanShowUnderlay(IntPtr source)
        => _wm.UsesCachedPreview(source)
            || (_win32.GetWindowStyle(source) & (int)WINDOW_STYLE.WS_MINIMIZE) == 0;

    private IntPtr GetCardTransitionSource(IntPtr card)
        => _overviewItemIds.TryGetValue(card, out var id) && _cardTransitions.NeedsLiveUnderlay(id)
            && _cardSources.TryGetValue(card, out var source) && CanShowUnderlay(source)
                ? source : IntPtr.Zero;

    private void UpdateCardAnimationTimer()
    {
        if (_cardTransitions.IsAnimating)
        {
            if (_cardAnimationTimer == null)
            {
                _cardAnimationTimer = new Timer { Interval = 16 };
                _cardAnimationTimer.Tick += (_, _) =>
                {
                    if (_disposed || CurrentMode == OverviewMode.Hidden) { ClearCardTransitions(); return; }
                    _thumbnails.Reconcile();
                    RefreshZoneAnnotations();
                    // Remove any outgoing native thumbnail immediately after its cover becomes opaque.
                    if (!_cardTransitions.IsAnimating) _thumbnails.Reconcile();
                };
            }
            _cardAnimationTimer.Start();
        }
        else _cardAnimationTimer?.Stop();
    }

    private void ClearCardTransitions()
    {
        _cardAnimationTimer?.Stop();
        _cardTransitions.Clear();
        _cardSources.Clear();
        _overviewItemIds = new Dictionary<IntPtr, Guid>();
    }
}
