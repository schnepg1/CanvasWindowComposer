using System;
using System.Windows.Forms;
using CanvasDesktop;
using CanvasDesktop.CanvasView;
using Xunit;

namespace CanvasDesktop.Tests;

public class WindowResizeLayoutTests
{
    private static readonly CanvasRect StandardLocalRect = new(100, 100, 400, 300);

    [Fact]
    public void HitTest_ReturnsNone_WhenFarOutsideWindow()
    {
        var edge = WindowResizeLayout.HitTest(StandardLocalRect, 50, 50);
        Assert.Equal(WindowResizeEdge.None, edge);
    }

    [Fact]
    public void HitTest_ReturnsNone_WhenInsideInterior()
    {
        var edge = WindowResizeLayout.HitTest(StandardLocalRect, 300, 250);
        Assert.Equal(WindowResizeEdge.None, edge);
    }

    [Fact]
    public void HitTest_IdentifiesEdgesCorrectly()
    {
        // Top edge (y ≈ 100, x centered at 300)
        Assert.Equal(WindowResizeEdge.Top, WindowResizeLayout.HitTest(StandardLocalRect, 300, 100));
        Assert.Equal(WindowResizeEdge.Top, WindowResizeLayout.HitTest(StandardLocalRect, 300, 95));
        Assert.Equal(WindowResizeEdge.Top, WindowResizeLayout.HitTest(StandardLocalRect, 300, 105));

        // Bottom edge (y ≈ 400, x centered at 300)
        Assert.Equal(WindowResizeEdge.Bottom, WindowResizeLayout.HitTest(StandardLocalRect, 300, 400));
        Assert.Equal(WindowResizeEdge.Bottom, WindowResizeLayout.HitTest(StandardLocalRect, 300, 405));
        Assert.Equal(WindowResizeEdge.Bottom, WindowResizeLayout.HitTest(StandardLocalRect, 300, 395));

        // Left edge (x ≈ 100, y centered at 250)
        Assert.Equal(WindowResizeEdge.Left, WindowResizeLayout.HitTest(StandardLocalRect, 100, 250));
        Assert.Equal(WindowResizeEdge.Left, WindowResizeLayout.HitTest(StandardLocalRect, 95, 250));
        Assert.Equal(WindowResizeEdge.Left, WindowResizeLayout.HitTest(StandardLocalRect, 105, 250));

        // Right edge (x ≈ 500, y centered at 250)
        Assert.Equal(WindowResizeEdge.Right, WindowResizeLayout.HitTest(StandardLocalRect, 500, 250));
        Assert.Equal(WindowResizeEdge.Right, WindowResizeLayout.HitTest(StandardLocalRect, 505, 250));
        Assert.Equal(WindowResizeEdge.Right, WindowResizeLayout.HitTest(StandardLocalRect, 495, 250));
    }

    [Fact]
    public void HitTest_IdentifiesCornersCorrectly()
    {
        // TopLeft corner (100, 100)
        Assert.Equal(WindowResizeEdge.TopLeft, WindowResizeLayout.HitTest(StandardLocalRect, 100, 100));
        Assert.Equal(WindowResizeEdge.TopLeft, WindowResizeLayout.HitTest(StandardLocalRect, 98, 98));

        // TopRight corner (500, 100)
        Assert.Equal(WindowResizeEdge.TopRight, WindowResizeLayout.HitTest(StandardLocalRect, 500, 100));
        Assert.Equal(WindowResizeEdge.TopRight, WindowResizeLayout.HitTest(StandardLocalRect, 502, 98));

        // BottomLeft corner (100, 400)
        Assert.Equal(WindowResizeEdge.BottomLeft, WindowResizeLayout.HitTest(StandardLocalRect, 100, 400));
        Assert.Equal(WindowResizeEdge.BottomLeft, WindowResizeLayout.HitTest(StandardLocalRect, 98, 402));

        // BottomRight corner (500, 400)
        Assert.Equal(WindowResizeEdge.BottomRight, WindowResizeLayout.HitTest(StandardLocalRect, 500, 400));
        Assert.Equal(WindowResizeEdge.BottomRight, WindowResizeLayout.HitTest(StandardLocalRect, 502, 402));
    }

    [Fact]
    public void TryResize_ExpandsBeyondPhysicalScreenDimensions()
    {
        var original = new WorldRect { X = 0, Y = 0, W = 1920, H = 1080 };

        // Right expansion
        bool success = WindowResizeLayout.TryResize(original, WindowResizeEdge.Right, 3000, 0, out var rightExpanded);
        Assert.True(success);
        Assert.Equal(0, rightExpanded.X);
        Assert.Equal(0, rightExpanded.Y);
        Assert.Equal(4920, rightExpanded.W);
        Assert.Equal(1080, rightExpanded.H);

        // Bottom expansion
        success = WindowResizeLayout.TryResize(original, WindowResizeEdge.Bottom, 0, 2000, out var bottomExpanded);
        Assert.True(success);
        Assert.Equal(1920, bottomExpanded.W);
        Assert.Equal(3080, bottomExpanded.H);

        // Left expansion (expanding leftwards increases width and shifts X)
        success = WindowResizeLayout.TryResize(original, WindowResizeEdge.Left, -1000, 0, out var leftExpanded);
        Assert.True(success);
        Assert.Equal(-1000, leftExpanded.X);
        Assert.Equal(2920, leftExpanded.W);
        Assert.Equal(1080, leftExpanded.H);

        // Top expansion (expanding upwards increases height and shifts Y)
        success = WindowResizeLayout.TryResize(original, WindowResizeEdge.Top, 0, -1000, out var topExpanded);
        Assert.True(success);
        Assert.Equal(-1000, topExpanded.Y);
        Assert.Equal(2080, topExpanded.H);
        Assert.Equal(1920, topExpanded.W);

        // Corner expansion (BottomRight)
        success = WindowResizeLayout.TryResize(original, WindowResizeEdge.BottomRight, 5000, 4000, out var cornerExpanded);
        Assert.True(success);
        Assert.Equal(6920, cornerExpanded.W);
        Assert.Equal(5080, cornerExpanded.H);
    }

    [Fact]
    public void TryResize_ClampsToMinimumDimensions()
    {
        var original = new WorldRect { X = 100, Y = 100, W = 800, H = 600 };

        // Shrinking right beyond minimum width
        bool success = WindowResizeLayout.TryResize(original, WindowResizeEdge.Right, -700, 0, out var minWidth);
        Assert.True(success);
        Assert.Equal(WindowResizeLayout.MinimumWidth, minWidth.W);
        Assert.Equal(100, minWidth.X);

        // Shrinking bottom beyond minimum height
        success = WindowResizeLayout.TryResize(original, WindowResizeEdge.Bottom, 0, -550, out var minHeight);
        Assert.True(success);
        Assert.Equal(WindowResizeLayout.MinimumHeight, minHeight.H);
        Assert.Equal(100, minHeight.Y);

        // Shrinking left beyond minimum width (anchor right edge)
        success = WindowResizeLayout.TryResize(original, WindowResizeEdge.Left, 750, 0, out var minWidthLeft);
        Assert.True(success);
        Assert.Equal(WindowResizeLayout.MinimumWidth, minWidthLeft.W);
        Assert.Equal(original.X + original.W - WindowResizeLayout.MinimumWidth, minWidthLeft.X);

        // Shrinking top beyond minimum height (anchor bottom edge)
        success = WindowResizeLayout.TryResize(original, WindowResizeEdge.Top, 0, 550, out var minHeightTop);
        Assert.True(success);
        Assert.Equal(WindowResizeLayout.MinimumHeight, minHeightTop.H);
        Assert.Equal(original.Y + original.H - WindowResizeLayout.MinimumHeight, minHeightTop.Y);
    }

    [Fact]
    public void CursorForEdge_MapsCorrectly()
    {
        Assert.Equal(Cursors.SizeNWSE, WindowResizeLayout.CursorForEdge(WindowResizeEdge.TopLeft));
        Assert.Equal(Cursors.SizeNWSE, WindowResizeLayout.CursorForEdge(WindowResizeEdge.BottomRight));
        Assert.Equal(Cursors.SizeNESW, WindowResizeLayout.CursorForEdge(WindowResizeEdge.TopRight));
        Assert.Equal(Cursors.SizeNESW, WindowResizeLayout.CursorForEdge(WindowResizeEdge.BottomLeft));
        Assert.Equal(Cursors.SizeNS, WindowResizeLayout.CursorForEdge(WindowResizeEdge.Top));
        Assert.Equal(Cursors.SizeNS, WindowResizeLayout.CursorForEdge(WindowResizeEdge.Bottom));
        Assert.Equal(Cursors.SizeWE, WindowResizeLayout.CursorForEdge(WindowResizeEdge.Left));
        Assert.Equal(Cursors.SizeWE, WindowResizeLayout.CursorForEdge(WindowResizeEdge.Right));
        Assert.Equal(Cursors.Default, WindowResizeLayout.CursorForEdge(WindowResizeEdge.None));
    }
}
