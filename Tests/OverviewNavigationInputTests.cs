using CanvasDesktop;
using Xunit;

namespace CanvasDesktop.Tests;

public class OverviewNavigationInputTests
{
    [Fact]
    public void DecodeWheel_PreservesSignedDeltaAndNegativeMonitorCoordinates()
    {
        nint wParam = unchecked((nint)((-30 & 0xffff) << 16 | 0x0008));
        nint lParam = unchecked((nint)((220 & 0xffff) << 16 | (-400 & 0xffff)));
        var wheel = OverviewNavigationInput.DecodeWheel(
            OverviewNavigationInput.MouseWheelMessage, wParam, lParam);
        Assert.Equal(-400, wheel.ScreenX);
        Assert.Equal(220, wheel.ScreenY);
        Assert.Equal(-30, wheel.Delta);
        Assert.False(wheel.Horizontal);
        Assert.True(wheel.Control);
        Assert.False(wheel.Shift);
    }

    [Fact]
    public void DecodeWheel_ReportsShiftModifier()
    {
        nint wParam = unchecked((nint)((30 & 0xffff) << 16 | 0x0004));
        var wheel = OverviewNavigationInput.DecodeWheel(
            OverviewNavigationInput.MouseWheelMessage, wParam, 0);
        Assert.True(wheel.Shift);
    }

    [Theory]
    [InlineData(30, false, false, false, 0, 30, 0)]
    [InlineData(-30, false, false, false, 0, -30, 0)]
    [InlineData(20, true, false, false, 20, 0, 0)]
    [InlineData(-20, true, false, false, -20, 0, 0)]
    [InlineData(30, false, true, false, 0, 0, 0.25)]
    [InlineData(-60, false, true, false, 0, 0, -0.5)]
    [InlineData(30, true, true, false, 0, 0, 0)]
    [InlineData(30, false, false, true, 30, 0, 0)]
    [InlineData(-30, false, false, true, -30, 0, 0)]
    [InlineData(30, false, true, true, 0, 0, 0.25)]
    public void WheelTranslationSupportsScrollAndPinch(int delta, bool horizontal, bool control, bool shift,
        double expectedX, double expectedY, double expectedZoom)
    {
        var (x, y, zoom) = OverviewNavigationInput.TranslateWheel(delta, horizontal, control, shift);
        Assert.Equal(expectedX, x);
        Assert.Equal(expectedY, y);
        Assert.Equal(expectedZoom, zoom);
    }
}
