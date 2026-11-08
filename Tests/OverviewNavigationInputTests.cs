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
    [InlineData(30, false, false, false, 0, 0, 0.25)]
    [InlineData(-30, false, false, false, 0, 0, -0.25)]
    [InlineData(20, true, false, false, 20, 0, 0)]
    [InlineData(-20, true, false, false, -20, 0, 0)]
    [InlineData(30, false, true, false, 0, 0, 0.25)]
    [InlineData(-60, false, true, false, 0, 0, -0.50)]
    [InlineData(30, true, true, false, 30, 0, 0)]
    [InlineData(30, false, false, true, 30, 0, 0)]
    [InlineData(-30, false, false, true, -30, 0, 0)]
    [InlineData(30, false, true, true, 30, 0, 0)]
    public void WheelTranslationSupportsScrollAndPinch_Mouse(int delta, bool horizontal, bool control, bool shift,
        double expectedX, double expectedY, double expectedZoom)
    {
        var (x, y, zoom) = OverviewNavigationInput.TranslateWheel(delta, horizontal, control, shift, isTouchpad: false);
        Assert.Equal(expectedX, x);
        Assert.Equal(expectedY, y);
        Assert.Equal(expectedZoom, zoom);
    }

    [Fact]
    public void TranslateWheel_TouchpadPinch_ReturnsZoomNotches()
    {
        var (x, y, zoom) = OverviewNavigationInput.TranslateWheel(delta: 24, horizontal: false, control: true, shift: false, isTouchpad: true);
        Assert.Equal(0, x);
        Assert.Equal(0, y);
        Assert.Equal(0.20, zoom, 6);
    }

    [Fact]
    public void TranslateWheel_TouchpadScroll_ReturnsPanCoordinates()
    {
        // 2-finger vertical scroll
        var (vx, vy, vzoom) = OverviewNavigationInput.TranslateWheel(delta: 15, horizontal: false, control: false, shift: false, isTouchpad: true);
        Assert.Equal(0, vx);
        Assert.Equal(15, vy);
        Assert.Equal(0, vzoom);

        // 2-finger horizontal scroll
        var (hx, hy, hzoom) = OverviewNavigationInput.TranslateWheel(delta: 20, horizontal: true, control: false, shift: false, isTouchpad: true);
        Assert.Equal(20, hx);
        Assert.Equal(0, hy);
        Assert.Equal(0, hzoom);

        // Shift + vertical scroll
        var (sx, sy, szoom) = OverviewNavigationInput.TranslateWheel(delta: 25, horizontal: false, control: false, shift: true, isTouchpad: true);
        Assert.Equal(25, sx);
        Assert.Equal(0, sy);
        Assert.Equal(0, szoom);
    }

    [Fact]
    public void TranslateWheel_MouseWheel_ReturnsZoom()
    {
        var (x1, y1, zoom1) = OverviewNavigationInput.TranslateWheel(delta: 120, horizontal: false, control: false, shift: false, isTouchpad: false);
        Assert.Equal(0, x1);
        Assert.Equal(0, y1);
        Assert.Equal(1.0, zoom1);

        var (x2, y2, zoom2) = OverviewNavigationInput.TranslateWheel(delta: 120, horizontal: false, control: true, shift: false, isTouchpad: false);
        Assert.Equal(0, x2);
        Assert.Equal(0, y2);
        Assert.Equal(1.0, zoom2);
    }

    [Fact]
    public void TranslateWheel_ShiftWheel_ReturnsPanX()
    {
        var (x, y, zoom) = OverviewNavigationInput.TranslateWheel(delta: 120, horizontal: false, control: false, shift: true, isTouchpad: false);
        Assert.Equal(120, x);
        Assert.Equal(0, y);
        Assert.Equal(0, zoom);
    }
}
