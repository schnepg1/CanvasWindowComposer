using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class AltTabSelectionIntentTests
{
    [Fact]
    public void QuickSwitchNeedsNoNativeSwitchCompletionEvent()
    {
        var intent = new AltTabSelectionIntent();
        Assert.True(intent.Begin());
        var generation = intent.Release();
        Assert.NotNull(generation);
        Assert.True(intent.TryComplete(generation.Value, out bool selected));
        Assert.True(selected);
        Assert.False(intent.TryComplete(generation.Value, out _));
    }

    [Fact]
    public void CyclingOnlyCompletesAfterAltReleaseAndEscapeCancelsNavigation()
    {
        var intent = new AltTabSelectionIntent();
        Assert.True(intent.Begin());
        Assert.False(intent.Begin());
        Assert.False(intent.TryComplete(1, out _));
        intent.Cancel();
        Assert.True(intent.TryComplete(intent.Release()!.Value, out bool selected));
        Assert.False(selected);
    }

    [Fact]
    public void NativeCompletionAndNewerGestureRejectOldReleaseCallbacks()
    {
        var intent = new AltTabSelectionIntent();
        intent.Begin(); var first = intent.Release()!.Value;
        intent.CompleteNative();
        Assert.False(intent.TryComplete(first, out _));
        intent.Begin(); var second = intent.Release()!.Value;
        Assert.False(intent.TryComplete(first, out _));
        Assert.True(intent.TryComplete(second, out bool selected));
        Assert.True(selected);
    }
}
