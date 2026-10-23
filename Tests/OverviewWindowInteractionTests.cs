using CanvasDesktop;
using System.Drawing;
using System.Windows.Forms;

namespace CanvasDesktop.Tests;

public class OverviewWindowInteractionTests
{
    private static readonly IntPtr Root = (IntPtr)10;
    private static readonly IntPtr Child = (IntPtr)11;
    private static readonly WindowInteractionGeometry Geometry = new(new(-500, 100, 400, 300), new(1000, -200, 800, 600));

    [Theory]
    [InlineData(-500, 100, 1000, -200)]
    [InlineData(-300, 250, 1400, 100)]
    [InlineData(-101, 399, 1798, 398)]
    public void MapsZoomedThumbnailWithNegativeMonitorAndSourceOrigins(int x, int y, int sourceX, int sourceY)
    {
        Assert.True(Geometry.TryMap(new(x, y), false, out var mapped));
        Assert.Equal(new Point(sourceX, sourceY), mapped);
    }

    [Theory]
    [InlineData(-501, 100)]
    [InlineData(-100, 100)]
    [InlineData(-300, 400)]
    public void OutsidePointsAreRejectedUnlessDragging(int x, int y)
    {
        Assert.False(Geometry.TryMap(new(x, y), false, out _));
        Assert.True(Geometry.TryMap(new(x, y), true, out _));
    }

    [Fact]
    public void ClickAndDragStayWithOriginalChildOutsideThumbnail()
    {
        var api = new PointerApi(); var bridge = new OverviewWindowInteraction(api);
        Assert.True(bridge.Begin(Root));
        Assert.True(bridge.Pointer(Geometry, new(-300, 250), MouseButtons.Left, down: true));
        Assert.True(bridge.Captured);
        api.Child = (IntPtr)12;
        Assert.True(bridge.Pointer(Geometry, new(50, 450), MouseButtons.None));
        Assert.True(bridge.Pointer(Geometry, new(50, 450), MouseButtons.Left, up: true));
        Assert.False(bridge.Captured);
        Assert.Collection(api.Messages,
            down => { Assert.Equal(Child, down.Window); Assert.Equal(0x201u, down.Id); Assert.Equal(1u, down.Flags); },
            move => { Assert.Equal(Child, move.Window); Assert.Equal(0x200u, move.Id); Assert.Equal(new(2000, 450), move.Point); },
            up => { Assert.Equal(Child, up.Window); Assert.Equal(0x202u, up.Id); Assert.Equal(0u, up.Flags); });
    }

    [Fact]
    public void CancellationReleasesButtonsAndClearsHoverExactlyOnce()
    {
        var api = new PointerApi(); var bridge = new OverviewWindowInteraction(api); bridge.Begin(Root);
        bridge.Pointer(Geometry, new(-300, 250), MouseButtons.Left, down: true);
        bridge.Pointer(Geometry, new(-300, 250), MouseButtons.Right, down: true);
        Assert.True(bridge.End()); Assert.False(bridge.End());
        Assert.False(bridge.Active); Assert.False(bridge.Captured);
        Assert.Equal(new uint[] { 0x201, 0x204, 0x202, 0x205, 0x2A3 }, api.Messages.Select(m => m.Id));
        Assert.Equal(new uint[] { 1, 3, 2, 0, 0 }, api.Messages.Select(m => m.Flags));
    }

    [Fact]
    public void WheelUsesMappedScreenCoordinatesAndSignedDelta()
    {
        var api = new PointerApi(); var bridge = new OverviewWindowInteraction(api); bridge.Begin(Root);
        Assert.True(bridge.Wheel(Geometry, new(-300, 250), -120, false, 4));
        Assert.True(bridge.Wheel(Geometry, new(-300, 250), 120, true, 0));
        Assert.Equal(0x20Au, api.Messages[0].Id);
        Assert.Equal(unchecked((uint)(ushort)-120) << 16 | 4u, api.Messages[0].Flags);
        Assert.Equal(new Point(1400, 100), api.Messages[0].Point);
        Assert.Equal(0x20Eu, api.Messages[1].Id);
    }

    [Fact]
    public void SecondDownBecomesDoubleClickWithoutExtraDown()
    {
        var api = new PointerApi(); var bridge = new OverviewWindowInteraction(api); bridge.Begin(Root);
        bridge.Pointer(Geometry, new(-300, 250), MouseButtons.Left, down: true, doubleClick: true);
        Assert.Equal(0x203u, Assert.Single(api.Messages).Id);
    }

    [Fact]
    public void DuplicateHoverIsNotPostedAndModifiersArePreserved()
    {
        var api = new PointerApi(); var bridge = new OverviewWindowInteraction(api); bridge.Begin(Root);
        bridge.Pointer(Geometry, new(-300, 250), MouseButtons.None, modifiers: 12);
        bridge.Pointer(Geometry, new(-300, 250), MouseButtons.None, modifiers: 12);
        Assert.Equal(12u, Assert.Single(api.Messages).Flags);
    }

    [Fact]
    public void FailedPostEndsSessionAndAttemptsToReleaseHeldButton()
    {
        var api = new PointerApi(); var bridge = new OverviewWindowInteraction(api); bridge.Begin(Root);
        bridge.Pointer(Geometry, new(-300, 250), MouseButtons.Left, down: true);
        api.Accept = false;
        Assert.False(bridge.Pointer(Geometry, new(-290, 250), MouseButtons.None));
        Assert.False(bridge.Active); Assert.False(bridge.Captured);
        Assert.Contains(api.Messages, m => m.Id == 0x202);
    }

    [Fact]
    public void MissingSourceOrFailedActivationDoesNotStartSession()
    {
        var api = new PointerApi { Available = false }; var bridge = new OverviewWindowInteraction(api);
        Assert.False(bridge.Begin(Root));
        api.Available = true; api.Activated = false;
        Assert.False(bridge.Begin(Root)); Assert.False(bridge.Active); Assert.Empty(api.Messages);
    }

    [Fact]
    public void DestroyedChildCancelsInsteadOfRetargetingDrag()
    {
        var api = new PointerApi(); var bridge = new OverviewWindowInteraction(api); bridge.Begin(Root);
        bridge.Pointer(Geometry, new(-300, 250), MouseButtons.Left, down: true);
        api.Related = false;
        Assert.False(bridge.Pointer(Geometry, new(-290, 250), MouseButtons.None));
        Assert.False(bridge.Active); Assert.Single(api.Messages);
    }

    [Fact]
    public void NonclientAreaDoesNotReceiveClicks()
    {
        var api = new PointerApi { Client = false }; var bridge = new OverviewWindowInteraction(api); bridge.Begin(Root);
        Assert.False(bridge.Pointer(Geometry, new(-300, 250), MouseButtons.Left, down: true));
        Assert.Empty(api.Messages); Assert.False(bridge.Captured);
    }

    internal sealed class PointerApi : IWindowInteractionApi
    {
        public bool Available = true, Activated = true, Related = true, Client = true, Accept = true;
        public IntPtr Child = (IntPtr)11;
        public List<(IntPtr Window, uint Id, uint Flags, Point Point)> Messages = [];
        public bool IsAvailable(IntPtr window) => Available;
        public bool Activate(IntPtr window) => Activated;
        public bool TryResolveClient(IntPtr window, Point screen, out IntPtr child, out Point client)
        { child = Child; client = new(screen.X - 100, screen.Y - 50); return Client; }
        public bool TryToClient(IntPtr window, Point screen, out Point client)
        { client = new(screen.X - 100, screen.Y - 50); return true; }
        public bool IsChildOf(IntPtr window, IntPtr child) => child != IntPtr.Zero && Related;
        public bool Post(IntPtr window, uint message, uint flags, Point point)
        { Messages.Add((window, message, flags, point)); return Accept; }
    }
}
