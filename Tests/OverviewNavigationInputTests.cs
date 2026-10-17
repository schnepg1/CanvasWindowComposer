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
    }

    [Theory]
    [InlineData(30, false, false, 0, 30, 0)]
    [InlineData(-30, false, false, 0, -30, 0)]
    [InlineData(20, true, false, -20, 0, 0)]
    [InlineData(-20, true, false, 20, 0, 0)]
    [InlineData(30, false, true, 0, 0, 0.25)]
    [InlineData(-60, false, true, 0, 0, -0.5)]
    [InlineData(30, true, true, 0, 0, 0)]
    public void WheelTranslationSupportsScrollAndPinch(int delta, bool horizontal, bool control,
        double expectedX, double expectedY, double expectedZoom)
    {
        var (x, y, zoom) = OverviewNavigationInput.TranslateWheel(delta, horizontal, control);
        Assert.Equal(expectedX, x);
        Assert.Equal(expectedY, y);
        Assert.Equal(expectedZoom, zoom);
    }
}
