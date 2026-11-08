using System;
using System.Collections.Generic;
using System.Linq;
using CanvasDesktop;
using CanvasDesktop.CanvasView;
using Xunit;

namespace CanvasDesktop.Tests;

public class ConnectionCanvasToolTests
{
    private static readonly Guid Desktop = Guid.NewGuid();

    private static CanvasPointer Pointer(double x, double y, CanvasPointerButton button = CanvasPointerButton.None)
        => new(0, new(x, y), button, CanvasModifiers.None, new CanvasViewTransform(0, 0, 1.0, new(0, 0, 1920, 1080)));

    [Fact]
    public void FindHandleAt_ReturnsCardinalPorts_WhenPointerNearHandle()
    {
        var doc = new AnnotationDocument();
        var selection = new CanvasSelection();
        var windowId = Guid.NewGuid();
        var window = new CanvasSelectionItem(
            new CanvasItemId(CanvasItemKind.Window, windowId),
            new CanvasRect(100, 100, 400, 300),
            "Test Window");

        var tool = new ConnectionCanvasTool(doc, () => Desktop, () => [window], selection);
        selection.Replace([window.Key]);

        // Top port: (300, 100)
        var top = tool.FindHandleAt(Pointer(302, 101));
        Assert.NotNull(top);
        Assert.Equal(ConnectionPort.Top, top.Port);
        Assert.Equal(window.Key, top.Item.Key);

        // Right port: (500, 250)
        var right = tool.FindHandleAt(Pointer(499, 252));
        Assert.NotNull(right);
        Assert.Equal(ConnectionPort.Right, right.Port);

        // Bottom port: (300, 400)
        var bottom = tool.FindHandleAt(Pointer(300, 398));
        Assert.NotNull(bottom);
        Assert.Equal(ConnectionPort.Bottom, bottom.Port);

        // Left port: (100, 250)
        var left = tool.FindHandleAt(Pointer(101, 250));
        Assert.NotNull(left);
        Assert.Equal(ConnectionPort.Left, left.Port);

        // Far away point
        var far = tool.FindHandleAt(Pointer(800, 800));
        Assert.Null(far);
    }

    [Fact]
    public void PointerDown_OnHandle_StartsConnectionGesture()
    {
        var doc = new AnnotationDocument();
        var selection = new CanvasSelection();
        var window = new CanvasSelectionItem(
            new CanvasItemId(CanvasItemKind.Window, Guid.NewGuid()),
            new CanvasRect(100, 100, 400, 300),
            "Source Window");

        var tool = new ConnectionCanvasTool(doc, () => Desktop, () => [window], selection);
        selection.Replace([window.Key]);

        // Pointer down on Right port (500, 250)
        bool handled = tool.PointerDown(Pointer(500, 250, CanvasPointerButton.Left));
        Assert.True(handled);
        Assert.True(tool.HasPendingInteraction);
        Assert.NotNull(tool.Preview);
        Assert.Equal(500, tool.Preview.A.X);
        Assert.Equal(250, tool.Preview.A.Y);
        Assert.Equal(ConnectionPort.Right, tool.Preview.SourcePort);
        Assert.Equal(window.Key.Id, tool.Preview.SourceId);
    }

    [Fact]
    public void PointerMove_NearTargetPort_SnapsMagnetically()
    {
        var doc = new AnnotationDocument();
        var selection = new CanvasSelection();
        var w1 = new CanvasSelectionItem(
            new CanvasItemId(CanvasItemKind.Window, Guid.NewGuid()),
            new CanvasRect(100, 100, 400, 300),
            "Source Window");
        var w2 = new CanvasSelectionItem(
            new CanvasItemId(CanvasItemKind.Window, Guid.NewGuid()),
            new CanvasRect(700, 100, 400, 300),
            "Target Window");

        var tool = new ConnectionCanvasTool(doc, () => Desktop, () => [w1, w2], selection);
        selection.Replace([w1.Key]);

        // Start from Right port of w1 (500, 250)
        tool.PointerDown(Pointer(500, 250, CanvasPointerButton.Left));

        // Drag to free space
        tool.PointerMove(Pointer(600, 250));
        Assert.Null(tool.SnapTarget);
        Assert.Equal(600, tool.Preview!.B.X);

        // Drag near Left port of w2: (700, 250) - within 20 DIPs
        tool.PointerMove(Pointer(692, 252));
        Assert.NotNull(tool.SnapTarget);
        Assert.Equal(w2.Key, tool.SnapTarget.Item.Key);
        Assert.Equal(ConnectionPort.Left, tool.SnapTarget.Port);
        Assert.Equal(700, tool.Preview.B.X);
        Assert.Equal(250, tool.Preview.B.Y);
    }

    [Fact]
    public void PointerMove_InsideTargetBounds_SnapsToAutoPortFacingSource()
    {
        var doc = new AnnotationDocument();
        var selection = new CanvasSelection();
        var w1 = new CanvasSelectionItem(
            new CanvasItemId(CanvasItemKind.Window, Guid.NewGuid()),
            new CanvasRect(100, 100, 400, 300),
            "Source Window");
        var w2 = new CanvasSelectionItem(
            new CanvasItemId(CanvasItemKind.Window, Guid.NewGuid()),
            new CanvasRect(700, 100, 400, 300),
            "Target Window");

        var tool = new ConnectionCanvasTool(doc, () => Desktop, () => [w1, w2], selection);
        selection.Replace([w1.Key]);

        // Start from w1 Right port
        tool.PointerDown(Pointer(500, 250, CanvasPointerButton.Left));

        // Move inside w2 center: (900, 250)
        tool.PointerMove(Pointer(900, 250));
        Assert.NotNull(tool.SnapTarget);
        Assert.Equal(w2.Key, tool.SnapTarget.Item.Key);
        Assert.Equal(ConnectionPort.Left, tool.SnapTarget.Port);
        Assert.Equal(700, tool.Preview!.B.X);
        Assert.Equal(250, tool.Preview.B.Y);
    }

    [Fact]
    public void PointerUp_OverTarget_CreatesCurvedConnectedArrowAndFiresEvent()
    {
        var doc = new AnnotationDocument();
        var selection = new CanvasSelection();
        var w1 = new CanvasSelectionItem(
            new CanvasItemId(CanvasItemKind.Window, Guid.NewGuid()),
            new CanvasRect(100, 100, 400, 300),
            "Source Window");
        var w2 = new CanvasSelectionItem(
            new CanvasItemId(CanvasItemKind.Window, Guid.NewGuid()),
            new CanvasRect(700, 100, 400, 300),
            "Target Window");

        var tool = new ConnectionCanvasTool(doc, () => Desktop, () => [w1, w2], selection);
        selection.Replace([w1.Key]);

        CanvasAnnotation? createdArrow = null;
        tool.ConnectionCreated += arrow => createdArrow = arrow;

        // Start drag from w1
        tool.PointerDown(Pointer(500, 250, CanvasPointerButton.Left));
        // Move over w2 Left port
        tool.PointerMove(Pointer(695, 250));
        // Release
        tool.PointerUp(Pointer(695, 250, CanvasPointerButton.Left));

        Assert.False(tool.HasPendingInteraction);
        Assert.NotNull(createdArrow);
        Assert.Equal(AnnotationKind.Arrow, createdArrow.Kind);
        Assert.Equal(ConnectorRouting.Curved, createdArrow.Routing);
        Assert.Equal(w1.Key.Id, createdArrow.SourceId);
        Assert.Equal(ConnectionPort.Right, createdArrow.SourcePort);
        Assert.Equal(w2.Key.Id, createdArrow.TargetId);
        Assert.Equal(ConnectionPort.Left, createdArrow.TargetPort);
        Assert.Equal(500, createdArrow.A.X);
        Assert.Equal(250, createdArrow.A.Y);
        Assert.Equal(700, createdArrow.B.X);
        Assert.Equal(250, createdArrow.B.Y);

        // Verify arrow exists in document
        var docArrow = Assert.Single(doc.Items);
        Assert.Equal(createdArrow.Id, docArrow.Id);
    }

    [Fact]
    public void PointerUp_MicroDrag_RejectsAccidentalClick()
    {
        var doc = new AnnotationDocument();
        var selection = new CanvasSelection();
        var w1 = new CanvasSelectionItem(
            new CanvasItemId(CanvasItemKind.Window, Guid.NewGuid()),
            new CanvasRect(100, 100, 400, 300),
            "Source Window");

        var tool = new ConnectionCanvasTool(doc, () => Desktop, () => [w1], selection);
        selection.Replace([w1.Key]);

        CanvasAnnotation? createdArrow = null;
        tool.ConnectionCreated += arrow => createdArrow = arrow;

        // Start drag at (500, 250)
        tool.PointerDown(Pointer(500, 250, CanvasPointerButton.Left));
        // Only moved 2px
        tool.PointerMove(Pointer(502, 251));
        tool.PointerUp(Pointer(502, 251, CanvasPointerButton.Left));

        Assert.False(tool.HasPendingInteraction);
        Assert.Null(createdArrow);
        Assert.Empty(doc.Items);
    }

    [Fact]
    public void Cancel_ClearsPendingGestureAndPreview()
    {
        var doc = new AnnotationDocument();
        var selection = new CanvasSelection();
        var w1 = new CanvasSelectionItem(
            new CanvasItemId(CanvasItemKind.Window, Guid.NewGuid()),
            new CanvasRect(100, 100, 400, 300),
            "Source Window");

        var tool = new ConnectionCanvasTool(doc, () => Desktop, () => [w1], selection);
        selection.Replace([w1.Key]);

        tool.PointerDown(Pointer(500, 250, CanvasPointerButton.Left));
        tool.PointerMove(Pointer(600, 250));
        Assert.True(tool.HasPendingInteraction);
        Assert.NotNull(tool.Preview);

        tool.Cancel();
        Assert.False(tool.HasPendingInteraction);
        Assert.Null(tool.Preview);
        Assert.Null(tool.SnapTarget);
        Assert.Empty(doc.Items);
    }
}
