using CanvasDesktop.CanvasView;

namespace CanvasDesktop.Tests;

public class CanvasViewTransformTests
{
    [Theory]
    [InlineData(.05, 1, 1)]
    [InlineData(.4, 1.5, 2)]
    [InlineData(1, 2, 1.5)]
    public void WorldPixelAndDipRoundTripsPreserveFractionalGeometryAtNegativeMonitorOrigins(double zoom, double dpiX, double dpiY)
    {
        var view = new CanvasViewTransform(-230.25, 812.5, zoom, new(-1920, 100, 1920, 1080), dpiX, dpiY);
        var world = new CanvasPoint(-314.125, 946.75);
        var local = view.WorldToLocal(world);
        var back = view.LocalToWorld(view.DipToLocal(view.LocalToDip(local)));
        Assert.Equal(world.X, back.X, 8); Assert.Equal(world.Y, back.Y, 8);
        var rect = view.WorldToDip(new CanvasRect(world.X, world.Y, 2.5, 1.25));
        Assert.Equal(2.5 * zoom / dpiX, rect.Width, 8);
        Assert.Equal(1.25 * zoom / dpiY, rect.Height, 8); // No app-window minimum size.
    }

    [Fact]
    public void PointerGeometryStaysContinuousWhenItsCapturedLocalCoordinatesCrossASecondMonitor()
    {
        var left = new CanvasViewTransform(345.5, -200, .5, new(-1920, 0, 1920, 1080), 1.5, 1.5);
        var right = new CanvasViewTransform(345.5, -200, .5, new(0, 0, 2560, 1440), 2, 2);
        var onCapturedLeft = left.LocalToWorld(new(1940, 200));
        var onRight = right.LocalToWorld(new(20, 200));
        Assert.Equal(onRight, onCapturedLeft);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidZoomOrDpiCannotCreateAProjection(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CanvasViewTransform(0, 0, value, default));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CanvasViewTransform(0, 0, 1, default, value, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CanvasViewTransform(0, 0, 1, default, 1, value));
    }

    [Fact]
    public void ModuleHasNoDependencyOnTheApplicationOrWindowsUiAssemblies()
    {
        var assembly = typeof(CanvasToolController).Assembly;
        Assert.NotEqual(typeof(Canvas).Assembly, assembly);
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
            reference.Name == "CanvasDesktop" || reference.Name!.StartsWith("Presentation")
            || reference.Name.StartsWith("System.Windows") || reference.Name.StartsWith("Windows.Win32"));
    }
}
