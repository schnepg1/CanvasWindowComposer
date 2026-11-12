namespace CanvasDesktop;

internal enum DesktopHandoffCloseDecision
{
    KeepCovered,
    WaitForButtonUp,
    ExposeDesktop
}

/// <summary>
/// UI-owned authorization to remove the desktop pan cover after native placement.
/// Invalidating a close leaves the pending placement reserved until its result arrives.
/// </summary>
internal sealed class DesktopHandoffClose
{
    private long _generation;
    private long? _pendingGeneration;
    private bool _placementVerified;
    private bool _exposed;

    public bool StoppingClickHeld { get; private set; }
    public bool ShutdownRequested { get; private set; }
    public bool CanRunShutdownRecovery => ShutdownRequested && !_pendingGeneration.HasValue;

    public long? TryBeginPlacement()
    {
        if (_pendingGeneration.HasValue || _placementVerified || _exposed || ShutdownRequested) return null;
        _pendingGeneration = ++_generation;
        return _pendingGeneration;
    }

    public void Invalidate()
    {
        _generation++;
        _placementVerified = false;
    }

    // A timeout or a user cancel can revoke the close without cancelling a USER32 call.
    public bool TryAbandonPendingClose(long generation)
    {
        if (_pendingGeneration != generation || _generation != generation) return false;
        Invalidate();
        return true;
    }

    // The session may end before placement drains. Never begin competing recovery writes.
    public void RequestShutdown()
    {
        if (ShutdownRequested) return;
        ShutdownRequested = true;
        Invalidate();
    }

    public DesktopHandoffCloseDecision CompletePlacement(long generation, bool placementVerified)
    {
        if (_pendingGeneration != generation) return DesktopHandoffCloseDecision.KeepCovered;
        _pendingGeneration = null;

        if (generation != _generation || !placementVerified)
            return DesktopHandoffCloseDecision.KeepCovered;

        _placementVerified = true;
        return StoppingClickHeld ? DesktopHandoffCloseDecision.WaitForButtonUp : ExposeDesktop();
    }

    public bool HoldStoppingClick()
    {
        if (StoppingClickHeld || _exposed || ShutdownRequested) return false;
        StoppingClickHeld = true;
        return true;
    }

    public DesktopHandoffCloseDecision ReleaseStoppingClick()
    {
        if (!StoppingClickHeld) return DesktopHandoffCloseDecision.KeepCovered;
        StoppingClickHeld = false;
        return _placementVerified ? ExposeDesktop() : DesktopHandoffCloseDecision.KeepCovered;
    }

    public void LoseStoppingClickCapture()
    {
        if (!StoppingClickHeld) return;
        StoppingClickHeld = false;
        Invalidate();
    }

    private DesktopHandoffCloseDecision ExposeDesktop()
    {
        _exposed = true;
        return DesktopHandoffCloseDecision.ExposeDesktop;
    }
}
