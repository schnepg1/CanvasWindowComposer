using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class GesturePreviewPolicyTests
{
    [Fact]
    public void WheelBurstsExtendTheSettleDeadlineAndRestoreAtTheBoundary()
    {
        var policy = new GesturePreviewPolicy();
        Assert.False(policy.Update(true, true, false, false, 0));
        policy.Motion(0);
        Assert.True(policy.Update(true, true, false, false, 179));
        policy.Motion(150);
        Assert.True(policy.Update(true, true, false, false, 329));
        Assert.Equal(1, policy.RemainingMs(329));
        Assert.False(policy.Update(true, true, false, false, 330));
        Assert.False(policy.Update(true, true, false, true, 331)); // A click without motion stays live.
    }

    [Fact]
    public void HeldDragAndDesktopPanDoNotRestoreMidGesture()
    {
        var policy = new GesturePreviewPolicy();
        policy.Motion(10);
        Assert.True(policy.Update(true, true, false, true, 5000));
        Assert.False(policy.Update(true, true, false, false, 5000));
        Assert.True(policy.Update(true, true, true, false, 10000));
        Assert.False(policy.Update(true, true, false, false, 10001));
    }

    [Fact]
    public void DisablingQualityClosingAndResetDiscardOldMotion()
    {
        var policy = new GesturePreviewPolicy();
        policy.Motion(0);
        Assert.False(policy.Update(false, true, true, true, 1));
        Assert.False(policy.Update(true, true, false, false, 2));
        policy.Motion(10);
        Assert.False(policy.Update(true, false, false, true, 11));
        Assert.False(policy.Update(true, true, false, false, 12));
        policy.Motion(20); Assert.True(policy.Update(true, true, false, false, 21));
        policy.Reset(); Assert.False(policy.Active);
        Assert.False(policy.Update(true, true, false, true, 22));
    }
}
