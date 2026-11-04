namespace CanvasDesktop;

/// <summary>Pure timing policy; never captures, minimizes or changes window geometry.</summary>
internal sealed class GesturePreviewPolicy
{
    internal const int SettleMs = 180;
    private bool _hasMotion;
    private long _lastMotion;
    public bool Active { get; private set; }

    public void Motion(long now)
    {
        _hasMotion = true;
        _lastMotion = now;
    }

    public bool Update(bool lowQuality, bool visible, bool desktopPan, bool pointerHeld, long now)
    {
        Active = lowQuality && visible && (desktopPan || (_hasMotion
            && (pointerHeld || now - _lastMotion < SettleMs)));
        if (!Active) _hasMotion = false;
        return Active;
    }

    public int RemainingMs(long now)
        => (int)System.Math.Clamp(SettleMs - (now - _lastMotion), 1, SettleMs);

    public void Reset() { Active = _hasMotion = false; }
}
