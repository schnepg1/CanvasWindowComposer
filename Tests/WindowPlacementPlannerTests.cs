using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class WindowPlacementPlannerTests
{
    private static readonly (int x, int y, int w, int h)[] Screens = [(0, 0, 1920, 1080)];
    private static readonly CanvasCamera Camera = new(0, 0, 1);
    private static WorldRect World(double x, double y, double w = 800, double h = 600)
        => new() { X = x, Y = y, W = w, H = h };

    [Fact]
    public void Camera_PreservesFractionalRoundingAndMinimumNativeSize()
    {
        var camera = new CanvasCamera(100.5, -20.5, 0.5);
        Assert.Equal(new WindowRect(-50, 10, 401, 301), camera.Project(World(0, 0, 800.3, 600.1)));
        Assert.Equal(new WindowRect(0, 0, 200, 100), camera.Project(World(100.5, -20.5, 1, 1)));
    }

    [Theory]
    [InlineData(1919, 100, true)]
    [InlineData(1920, 100, false)]
    [InlineData(-800, 100, false)]
    [InlineData(-799, 100, true)]
    [InlineData(100, 1079, true)]
    [InlineData(100, 1080, false)]
    [InlineData(100, -600, false)]
    [InlineData(100, -599, true)]
    public void ViewportIntersection_RequiresPositiveOverlap(int x, int y, bool visible)
    {
        var plan = WindowPlacementPlanner.Plan(World(x, y), Camera, Screens, true, false);
        Assert.Equal(visible, plan.IntersectsViewport);
        Assert.Equal(visible ? WindowRegionChange.None : WindowRegionChange.Clip, plan.RegionChange);
    }

    [Theory]
    [InlineData(-5000, 100, -799, 100)]
    [InlineData(5000, 100, 1919, 100)]
    [InlineData(100, -5000, 100, -599)]
    [InlineData(100, 5000, 100, 1079)]
    [InlineData(5000, 5000, 1919, 1079)]
    public void FirstOffScreenPlacement_RetainsOnePixelCompatibilityOverlap(int x, int y, int parkedX, int parkedY)
    {
        var plan = WindowPlacementPlanner.Plan(World(x, y), Camera, Screens, true, false);
        Assert.True(plan.ShouldMove);
        Assert.Equal(new WindowRect(parkedX, parkedY, 800, 600), plan.TargetRect);
        Assert.Equal(new WindowRect(x, y, 800, 600), plan.ProjectedRect);
        Assert.False(plan.IntersectsViewport); // Native parking must not change logical visibility.
    }

    [Fact]
    public void ClippedWindow_StaysParkedUntilTopologyRequiresRepositioning()
    {
        var world = World(5000, 100);
        var unchanged = WindowPlacementPlanner.Plan(world, Camera, Screens, true, true);
        Assert.False(unchanged.ShouldMove);
        Assert.Equal(WindowRegionChange.None, unchanged.RegionChange);

        (int, int, int, int)[] newScreens = [(-1280, 0, 1280, 1024)];
        var changed = WindowPlacementPlanner.Plan(world, Camera, newScreens, true, true, repositionClipped: true);
        Assert.True(changed.ShouldMove);
        Assert.Equal(new WindowRect(-1, 100, 800, 600), changed.TargetRect);
        Assert.Equal(WindowRegionChange.None, changed.RegionChange);
    }

    [Fact]
    public void ClippedWindow_ReenteringViewport_UnclipsAtItsWorldPosition()
    {
        var plan = WindowPlacementPlanner.Plan(World(5000, 200), new(4900, 0, 1), Screens, true, true);
        Assert.True(plan.IntersectsViewport);
        Assert.True(plan.ShouldMove);
        Assert.Equal(WindowRegionChange.Unclip, plan.RegionChange);
        Assert.Equal(new WindowRect(100, 200, 800, 600), plan.TargetRect);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisabledClipping_ProjectsOffScreenAndClearsAnyPriorClip(bool wasClipped)
    {
        var plan = WindowPlacementPlanner.Plan(World(5000, 5000), Camera, Screens, false, wasClipped);
        Assert.True(plan.ShouldMove);
        Assert.False(plan.IntersectsViewport);
        Assert.Equal(new WindowRect(5000, 5000, 800, 600), plan.TargetRect);
        Assert.Equal(wasClipped ? WindowRegionChange.Unclip : WindowRegionChange.None, plan.RegionChange);
    }

    [Theory]
    [InlineData((int)WindowState.Minimized, false)]
    [InlineData((int)WindowState.Maximized, false)]
    [InlineData((int)WindowState.Normal, true)]
    public void Projection_DoesNotMutateNativeStateOrScreenFixedWindows(int state, bool pinned)
    {
        var world = World(5000, 5000);
        world.State = (WindowState)state;
        world.PinnedToScreen = pinned;
        var plan = WindowPlacementPlanner.Plan(world, Camera, Screens, true, true);
        Assert.False(plan.ShouldMove);
        Assert.Equal(WindowRegionChange.None, plan.RegionChange);
    }

    [Fact]
    public void SavedPinnedLayout_CanBeExplicitlyPlacedWithoutClipping()
    {
        var world = World(5000, 100);
        world.PinnedToScreen = true;
        var plan = WindowPlacementPlanner.Plan(world, Camera, Screens, true, true, PlacementIntent.RestoreLayout);
        Assert.True(plan.ShouldMove);
        Assert.Equal(WindowRegionChange.Unclip, plan.RegionChange);
        Assert.Equal(new WindowRect(5000, 100, 800, 600), plan.TargetRect);
    }

    [Fact]
    public void MultipleMonitors_UsesTheirUnionWithoutFillingTheGap()
    {
        (int, int, int, int)[] screens = [(-1920, -200, 1920, 1080), (1000, 0, 1920, 1080)];
        Assert.True(WindowPlacementPlanner.IntersectsAnyScreen(new(-1800, -100, 800, 600), screens));
        Assert.False(WindowPlacementPlanner.IntersectsAnyScreen(new(100, 100, 800, 600), screens));
        var plan = WindowPlacementPlanner.Plan(World(-5000, -100), Camera, screens, true, false);
        Assert.Equal(new WindowRect(-2719, -100, 800, 600), plan.TargetRect);
    }

    [Fact]
    public void InvalidOrEmptyMonitorList_UsesFallbackOnlyForParkingAndRecovery()
    {
        (int, int, int, int)[] invalid = [(0, 0, 0, 1080), (0, 0, 1920, -1)];
        foreach (var screens in new[] { Array.Empty<(int, int, int, int)>(), invalid })
        {
            var plan = WindowPlacementPlanner.Plan(World(5000, 5000), Camera, screens, true, false);
            Assert.False(plan.IntersectsViewport);
            Assert.Equal(new WindowRect(1919, 1079, 800, 600), plan.TargetRect);
            Assert.Equal(new WindowRect(1120, 480, 800, 600),
                WindowPlacementPlanner.FitDesktop(new(5000, 5000, 800, 600), screens));
        }
    }

    [Fact]
    public void Intersection_DoesNotOverflowAtLargeCanvasCoordinates()
    {
        (int, int, int, int)[] screens = [(int.MaxValue - 1000, 0, 1000, 1080)];
        Assert.True(WindowPlacementPlanner.IntersectsAnyScreen(new(int.MaxValue - 10, 10, 800, 600), screens));
        Assert.False(WindowPlacementPlanner.IntersectsAnyScreen(new(int.MinValue, 10, 800, 600), screens));
    }

    [Fact]
    public void Pinning_KeepsVisibleActualPlacementUnlessItWasClipped()
    {
        var world = World(500, 200);
        var actual = new WindowRect(100, 100, 800, 600);
        Assert.Equal(actual, WindowPlacementPlanner.ResolvePinnedPlacement(world, Camera, actual, false, Screens));
        Assert.Equal(new WindowRect(500, 200, 800, 600),
            WindowPlacementPlanner.ResolvePinnedPlacement(world, Camera, actual, true, Screens));
    }

    [Fact]
    public void Pinning_PreservesSizeWhileDesktopRecoveryFitsOversizedWindows()
    {
        var world = World(-5000, -5000, 3000, 2000);
        var pinned = WindowPlacementPlanner.ResolvePinnedPlacement(world, Camera, new(-5000, -5000, 3000, 2000), false, Screens);
        Assert.Equal(new WindowRect(0, 0, 3000, 2000), pinned);
        Assert.Equal(new WindowRect(0, 0, 1920, 1080), WindowPlacementPlanner.FitDesktop(pinned, Screens));
    }
}
